package com.nebula.linkgate;

import org.json.JSONArray;
import org.json.JSONObject;
import org.json.JSONTokener;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Set;

/** The device import contract. Exceptions deliberately never include supplied values. */
public final class Profile {
    public static final int MAX_BYTES = 128 * 1024;
    public final String namespace, deviceName, host, token, tlsServerName, trustedCaPem;
    public final int serverPort, stunPort;
    public final List<Service> services;
    private final String canonicalJson;

    public static final class Service {
        public final String id, label, secretKey;
        public final int localPort, visitorPort;
        private Service(String id, String label, int localPort, int visitorPort, String secretKey) {
            this.id = id; this.label = label; this.localPort = localPort;
            this.visitorPort = visitorPort; this.secretKey = secretKey;
        }
        public String localAddress() { return "127.0.0.1:" + visitorPort; }
    }

    private Profile(JSONObject value) throws Exception {
        fields(value, "formatVersion", "namespace", "deviceName", "server", "services");
        if (number(value, "formatVersion") != 1) throw invalid();
        namespace = text(value, "namespace", 12);
        if (!namespace.matches("[a-z0-9]{12}")) throw invalid();
        deviceName = text(value, "deviceName", 64);
        JSONObject server = value.getJSONObject("server");
        fields(server, "host", "port", "stunPort", "token", "tlsServerName", "trustedCaPem");
        host = text(server, "host", 253);
        if (!hostname(host)) throw invalid();
        serverPort = port(server, "port");
        stunPort = server.has("stunPort") ? port(server, "stunPort") : 3478;
        token = hex(server, "token");
        tlsServerName = text(server, "tlsServerName", 253);
        if (!hostname(tlsServerName) || tlsServerName.matches("[0-9.]+")) throw invalid();
        trustedCaPem = text(server, "trustedCaPem", 16 * 1024, true);
        validateCa(trustedCaPem);
        JSONArray input = value.getJSONArray("services");
        if (input.length() < 1 || input.length() > 16) throw invalid();
        Set<String> ids = new HashSet<>();
        Set<Integer> ports = new HashSet<>();
        List<Service> parsed = new ArrayList<>();
        for (int i = 0; i < input.length(); i++) {
            JSONObject item = input.getJSONObject(i);
            fields(item, "id", "label", "localPort", "visitorPort", "secretKey");
            String id = text(item, "id", 32);
            if (!id.matches("[a-z][a-z0-9-]{0,31}") || !ids.add(id)) throw invalid();
            int visitorPort = port(item, "visitorPort");
            if (!ports.add(visitorPort)) throw invalid();
            int localPort = port(item, "localPort");
            parsed.add(new Service(id, text(item, "label", 64), localPort,
                    visitorPort, hex(item, "secretKey")));
        }
        services = Collections.unmodifiableList(parsed);
        canonicalJson = value.toString();
    }

    public static Profile parse(byte[] bytes) throws Exception {
        if (bytes.length < 2 || bytes.length > MAX_BYTES) throw invalid();
        String json = StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT)
                .decode(java.nio.ByteBuffer.wrap(bytes)).toString();
        if (json.startsWith("\uFEFF")) json = json.substring(1);
        try {
            StrictJson.validate(json);
            JSONTokener parser = new JSONTokener(json);
            Object value = parser.nextValue();
            if (!(value instanceof JSONObject) || parser.nextClean() != 0) throw invalid();
            return new Profile((JSONObject) value);
        }
        catch (Exception error) { throw invalid(); }
    }

    public byte[] encoded() { return canonicalJson.getBytes(StandardCharsets.UTF_8); }

    public String visitorToml(String caPath) {
        StringBuilder out = new StringBuilder();
        out.append("serverAddr = ").append(quote(host)).append('\n');
        out.append("serverPort = ").append(serverPort).append('\n');
        out.append("natHoleStunServer = ").append(quote(host + ":" + stunPort)).append('\n');
        out.append("loginFailExit = false\n");
        out.append("auth.method = \"token\"\nauth.token = ").append(quote(token)).append('\n');
        out.append("transport.tls.enable = true\ntransport.tls.serverName = ")
                .append(quote(tlsServerName)).append('\n');
        out.append("transport.tls.trustedCaFile = ").append(quote(caPath)).append('\n');
        out.append("log.to = \"console\"\nlog.level = \"info\"\nlog.disablePrintColor = true\n");
        for (Service service : services) {
            String base = namespace + "-" + service.id;
            out.append("\n[[visitors]]\nname = ").append(quote(base + "-fallback"))
                    .append("\ntype = \"stcp\"\nserverName = ").append(quote(base + "-stcp"))
                    .append("\nsecretKey = ").append(quote(service.secretKey)).append("\nbindPort = -1\n")
                    .append("transport.useEncryption = true\ntransport.useCompression = false\n");
            out.append("\n[[visitors]]\nname = ").append(quote(base + "-direct"))
                    .append("\ntype = \"xtcp\"\nserverName = ").append(quote(base + "-xtcp"))
                    .append("\nsecretKey = ").append(quote(service.secretKey))
                    .append("\nbindAddr = \"127.0.0.1\"\nbindPort = ").append(service.visitorPort)
                    .append("\nprotocol = \"quic\"\nfallbackTo = ").append(quote(base + "-fallback"))
                    .append("\nfallbackTimeoutMs = 1500\nkeepTunnelOpen = true\nminRetryInterval = 30\nmaxRetriesAnHour = 120\n")
                    .append("transport.useEncryption = true\ntransport.useCompression = false\n");
        }
        return out.toString();
    }

    private static String quote(String value) {
        return "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"")
                .replace("\r", "\\r").replace("\n", "\\n").replace("\t", "\\t") + "\"";
    }
    private static void fields(JSONObject value, String... allowed) throws Exception {
        Set<String> keys = new HashSet<>();
        Collections.addAll(keys, allowed);
        Iterator<String> input = value.keys();
        while (input.hasNext()) if (!keys.contains(input.next())) throw invalid();
    }
    private static String text(JSONObject value, String key, int max) throws Exception {
        return text(value, key, max, false);
    }
    private static String text(JSONObject value, String key, int max, boolean multiline) throws Exception {
        Object supplied = value.get(key);
        if (!(supplied instanceof String)) throw invalid();
        String text = (String) supplied;
        if (text.isEmpty() || text.length() > max) throw invalid();
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c == 127 || (c < 32 && !(multiline && (c == '\n' || c == '\r')))) throw invalid();
        }
        return text;
    }
    private static int number(JSONObject value, String key) throws Exception {
        Object number = value.get(key);
        if (!(number instanceof Integer) && !(number instanceof Long)) throw invalid();
        long parsed = ((Number) number).longValue();
        if (parsed < 0 || parsed > 65535) throw invalid();
        return (int) parsed;
    }
    private static int port(JSONObject value, String key) throws Exception {
        int port = number(value, key);
        if (port < 1024) throw invalid();
        return port;
    }
    private static String hex(JSONObject value, String key) throws Exception {
        String input = text(value, key, 64);
        if (!input.matches("[0-9a-fA-F]{64}")) throw invalid();
        return input;
    }
    private static boolean hostname(String host) {
        if (host.isEmpty() || host.length() > 253 || host.endsWith(".")) return false;
        if (host.matches("[0-9.]+")) {
            String[] parts = host.split("\\.", -1);
            if (parts.length != 4) return false;
            for (String part : parts) {
                if (!part.matches("0|[1-9][0-9]{0,2}")) return false;
                if (Integer.parseInt(part) > 255) return false;
            }
            int first = Integer.parseInt(parts[0]);
            if (host.equals("0.0.0.0") || host.equals("255.255.255.255") || first >= 224) return false;
            return true;
        }
        if (host.matches("(?i)(?:0x[0-9a-f]+|[0-9]+)(?:\\.(?:0x[0-9a-f]+|[0-9]+))*")) return false;
        for (String label : host.split("\\.", -1)) {
            if (!label.matches("[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?")) return false;
        }
        return true;
    }
    private static void validateCa(String pem) throws Exception {
        if (!pem.trim().matches("-----BEGIN CERTIFICATE-----\\r?\\n[A-Za-z0-9+/=\\r\\n]+-----END CERTIFICATE-----") ||
                pem.contains("PRIVATE KEY")) throw invalid();
        X509Certificate ca = (X509Certificate) CertificateFactory.getInstance("X.509")
                .generateCertificate(new ByteArrayInputStream(pem.getBytes(StandardCharsets.US_ASCII)));
        if (ca.getBasicConstraints() < 0) throw invalid();
        ca.checkValidity();
    }
    private static Exception invalid() { return new Exception("导入文件格式或证书无效，请使用电脑端生成的完整配对文件。"); }
}
