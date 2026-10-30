package dev.linkgate.contracts;

import org.junit.Test;
import static org.junit.Assert.*;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.List;
import java.util.Map;

public final class ControlHelloTest {
    private static final String ID = "00000000-0000-4000-8000-000000000001";
    private static String request(String method, String params) {
        return "{\"kind\":\"request\",\"apiVersion\":1,\"requestId\":\"" + ID + "\",\"method\":\"" + method + "\",\"params\":" + params + "}";
    }
    private static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }
    @SuppressWarnings("unchecked") private static Map<String,Object> object(Object value) { return (Map<String,Object>)value; }
    private static Map<String,Object> response(ControlHello hello, String value) {
        return object(StrictJson.parse(hello.respond(bytes(value)), 65536));
    }
    private static String code(String value) { return (String)object(response(new ControlHello(), value).get("error")).get("code"); }
    private static void reject(byte[] input) {
        try { new ControlHello().respond(input); fail("Malformed input accepted"); }
        catch (ContractException e) { assertEquals("INVALID_REQUEST", e.getMessage()); assertNull(e.getCause()); }
    }

    @Test public void helloHasStableBootAndNoProductCapabilities() {
        ControlHello hello = new ControlHello();
        Map<String,Object> a = response(hello, request("hello", "{\"supportedApiVersions\":[1]}"));
        Map<String,Object> b = response(hello, request("hello", "{\"supportedApiVersions\":[2,1]}"));
        assertEquals(ID, a.get("requestId")); assertEquals(object(a.get("result")).get("bootId"), object(b.get("result")).get("bootId"));
        List<?> capabilities = (List<?>)object(a.get("result")).get("capabilities");
        assertEquals(15, capabilities.size());
        for (Object capability : capabilities) assertEquals("unsupported", object(capability).get("state"));
        assertFalse(a.containsKey("error")); assertFalse(a.toString().contains("uid"));
    }
    @Test public void unsupportedMethodsNeverMutate() {
        for (String method : new String[] {"app.register", "operation.status", "grant.request", "service.publish", "connection.open", "gateway.stop"})
            assertEquals("CAPABILITY_UNSUPPORTED", code(request(method, "{}")));
    }
    @Test public void versionsMustBePositiveLexicalIntegers() {
        for (String value : new String[] {"{}", "{\"supportedApiVersions\":[]}", "{\"supportedApiVersions\":[1.0]}", "{\"supportedApiVersions\":[1e0]}", "{\"supportedApiVersions\":[true]}", "{\"supportedApiVersions\":[0]}", "{\"supportedApiVersions\":[1,2,3,4,5,6,7,8,9]}"})
            assertEquals("INVALID_REQUEST", code(request("hello", value)));
        assertEquals("VERSION_UNSUPPORTED", code(request("hello", "{\"supportedApiVersions\":[2]}")));
        assertEquals("VERSION_UNSUPPORTED", code(request("hello", "{\"supportedApiVersions\":[1]}").replace("\"apiVersion\":1", "\"apiVersion\":2")));
    }
    @Test public void identityFieldsAndUnknownParametersAreRejected() {
        assertEquals("INVALID_REQUEST", code(request("hello", "{\"supportedApiVersions\":[1],\"uid\":10001}")));
        reject(bytes(request("hello", "{\"supportedApiVersions\":[1]}").replace("\"params\":", "\"packageName\":\"private-marker\",\"params\":")));
    }
    @Test public void malformedEnvelopesAreSanitized() {
        String valid = request("hello", "{\"supportedApiVersions\":[1]}");
        for (String input : new String[] {"{}", "[]", valid + "{}", "\ufeff" + valid, valid.replace(ID, "bad"), valid.replace("\"apiVersion\":1", "\"apiVersion\":1.0"),
            valid.replace("\"kind\":", "\"ki\\u006ed\":\"request\",\"kind\":"), valid.replace("hello", "\\ud800"), valid.replace("hello", "bad\\nmethod") }) reject(bytes(input));
        reject(new byte[] {(byte)0xff}); reject(null);
    }
    @Test public void byteAndContainerLimitsAreEnforced() {
        byte[] valid = bytes(request("hello", "{\"supportedApiVersions\":[1]}"));
        byte[] maximum = Arrays.copyOf(valid, 65536); Arrays.fill(maximum, valid.length, maximum.length, (byte)' ');
        assertNotNull(new ControlHello().respond(maximum)); reject(Arrays.copyOf(maximum, 65537));
        reject(bytes(request("hello", "{\"supportedApiVersions\":[" + repeat("[",17) + "1" + repeat("]",17) + "]}")));
        reject(bytes(request("hello", "{\"supportedApiVersions\":[" + repeat("0,",10001) + "0]}")));
    }
    private static String repeat(String input, int count) { StringBuilder output = new StringBuilder(); for (int i=0;i<count;i++) output.append(input); return output.toString(); }
}
