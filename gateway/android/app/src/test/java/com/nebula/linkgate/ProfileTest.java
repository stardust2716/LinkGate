package com.nebula.linkgate;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import java.nio.charset.StandardCharsets;
import static org.junit.Assert.*;

public class ProfileTest {
    private JSONObject valid() throws Exception {
        String pem;
        try (java.io.InputStream input = getClass().getResourceAsStream("/test-ca.txt")) {
            assertNotNull(input); pem = new String(input.readAllBytes(), StandardCharsets.US_ASCII);
        }
        return new JSONObject().put("formatVersion", 1).put("namespace", "aabbccddeeff")
                .put("deviceName", "测试电脑").put("server", new JSONObject()
                        .put("host", "203.0.113.44").put("port", 17400).put("stunPort", 21116)
                        .put("token", "a".repeat(64)).put("tlsServerName", "gateway.internal").put("trustedCaPem", pem))
                .put("services", new JSONArray().put(new JSONObject().put("id", "demo")
                        .put("label", "测试网页").put("localPort", 18480).put("visitorPort", 18481)
                        .put("secretKey", "b".repeat(64))));
    }
    private Profile parse(JSONObject value) throws Exception { return Profile.parse(value.toString().getBytes(StandardCharsets.UTF_8)); }

    @Test public void importRoundTripsAndGeneratesOnlyLoopbackVisitors() throws Exception {
        Profile profile = parse(valid());
        Profile again = Profile.parse(profile.encoded());
        assertEquals(1, again.services.size());
        assertEquals("127.0.0.1:18481", again.services.get(0).localAddress());
        String config = profile.visitorToml("/private/linkgate/ca.pem");
        assertTrue(config.contains("natHoleStunServer = \"203.0.113.44:21116\""));
        assertTrue(config.contains("fallbackTo = \"aabbccddeeff-demo-fallback\""));
        assertTrue(config.contains("serverName = \"aabbccddeeff-demo-stcp\""));
        assertTrue(config.contains("serverName = \"aabbccddeeff-demo-xtcp\""));
        assertTrue(config.contains("bindAddr = \"127.0.0.1\""));
        assertFalse(config.contains("0.0.0.0"));
        assertTrue(config.contains("trustedCaFile = \"/private/linkgate/ca.pem\""));
        assertTrue(config.contains("transport.useEncryption = true"));
    }
    @Test public void rejectsUnknownFieldsAndWrongTypesAtEveryBoundary() throws Exception {
        JSONObject value = valid(); value.put("command", "dangerous");
        assertThrows(Exception.class, () -> parse(value));
        JSONObject server = valid(); server.getJSONObject("server").put("skipTlsVerify", true);
        assertThrows(Exception.class, () -> parse(server));
        JSONObject service = valid(); service.getJSONArray("services").getJSONObject(0).put("bindAddr", "0.0.0.0");
        assertThrows(Exception.class, () -> parse(service));
        JSONObject version = valid(); version.put("formatVersion", "1");
        assertThrows(Exception.class, () -> parse(version));
        JSONObject number = valid(); number.getJSONObject("server").put("port", 17400.5);
        assertThrows(Exception.class, () -> parse(number));
    }
    @Test public void rejectsNetworkAndTomlInjectionAndInvalidPorts() throws Exception {
        for (String host : new String[]{"https://example.com", "example.com/path", "host:17400", "999.0.0.1", "bad\nname", "-bad.example", "0.0.0.0", "255.255.255.255", "224.0.0.1", "127.1", "0177.0.0.1", "0x7f000001"}) {
            JSONObject value = valid(); value.getJSONObject("server").put("host", host);
            assertThrows(Exception.class, () -> parse(value));
        }
        for (int port : new int[]{0, 80, 1023, 65536}) {
            JSONObject value = valid(); value.getJSONArray("services").getJSONObject(0).put("visitorPort", port);
            assertThrows(Exception.class, () -> parse(value));
        }
        JSONObject secret = valid(); secret.getJSONArray("services").getJSONObject(0).put("secretKey", "\"\n[[proxies]]");
        assertThrows(Exception.class, () -> parse(secret));
    }
    @Test public void rejectsDuplicateServicesAndOversizedOrTrailingDocuments() throws Exception {
        JSONObject duplicate = valid(); duplicate.getJSONArray("services").put(new JSONObject(duplicate.getJSONArray("services").getJSONObject(0).toString()));
        assertThrows(Exception.class, () -> parse(duplicate));
        JSONObject samePort = valid();
        JSONObject second = new JSONObject(samePort.getJSONArray("services").getJSONObject(0).toString()).put("id", "second");
        samePort.getJSONArray("services").put(second);
        assertThrows(Exception.class, () -> parse(samePort));
        byte[] oversized = new byte[Profile.MAX_BYTES + 1];
        assertThrows(Exception.class, () -> Profile.parse(oversized));
        byte[] trailing = (valid() + " {}").getBytes(StandardCharsets.UTF_8);
        assertThrows(Exception.class, () -> Profile.parse(trailing));
        assertThrows(Exception.class, () -> Profile.parse(new byte[]{'{', (byte)0xff, '}'}));
    }
    @Test public void caAndSecretsAreRequiredAndErrorsDoNotEchoCredentials() throws Exception {
        JSONObject value = valid(); value.getJSONObject("server").put("trustedCaPem", "not-a-certificate");
        Exception error = assertThrows(Exception.class, () -> parse(value));
        assertFalse(error.getMessage().contains("a".repeat(64)));
        JSONObject token = valid(); token.getJSONObject("server").put("token", "short");
        assertThrows(Exception.class, () -> parse(token));
        JSONObject defaultStun = valid(); defaultStun.getJSONObject("server").remove("stunPort");
        assertEquals(3478, parse(defaultStun).stunPort);
    }
    @Test public void publisherPortIsRequiredAndTlsServerNameCannotBeAnIp() throws Exception {
        JSONObject missing = valid(); missing.getJSONArray("services").getJSONObject(0).remove("localPort");
        assertThrows(Exception.class, () -> parse(missing));
        JSONObject ip = valid(); ip.getJSONObject("server").put("tlsServerName", "203.0.113.44");
        assertThrows(Exception.class, () -> parse(ip));
        JSONObject dns = valid(); dns.getJSONObject("server").put("tlsServerName", "gateway.internal");
        assertEquals("gateway.internal", parse(dns).tlsServerName);
    }
    @Test public void caCannotHideExtraPemTextOrPrivateKeys() throws Exception {
        for (String suffix : new String[]{"extra text", "\n-----BEGIN " + "PRIVATE KEY-----\nYWJj\n-----END PRIVATE KEY-----", valid().getJSONObject("server").getString("trustedCaPem")}) {
            JSONObject value = valid();
            JSONObject server = value.getJSONObject("server");
            server.put("trustedCaPem", server.getString("trustedCaPem") + suffix);
            assertThrows(Exception.class, () -> parse(value));
        }
    }
}
