package dev.linkgate.contracts;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

/** Read-only API1 negotiation; identity admission belongs to the platform boundary. */
public final class ControlHello {
    public static final int MAX_BYTES = 65536;
    private final String bootId = UUID.randomUUID().toString();
    private static final String[] CAPABILITIES = {"publish.tcp", "visit.tcp", "access.brokered", "access.loopback",
        "protocol.http", "protocol.https", "protocol.ws", "protocol.wss", "policy.direct-first", "policy.direct-only",
        "network.ipv4", "network.lan", "network.ipv6", "identity.device-grant", "identity.revoke"};

    public byte[] respond(byte[] input) {
        try {
            ControlRequest request = ControlRequest.parse(input);
            String id = request.id;
            Map<String,Object> params = request.params;
            if (request.version != 1) return error(id, "VERSION_UNSUPPORTED");
            if (!"hello".equals(request.method)) return error(id, "CAPABILITY_UNSUPPORTED");
            if (request.operation != null) return error(id, "INVALID_REQUEST");
            try {
                fields(params, "supportedApiVersions");
                Object raw = params.get("supportedApiVersions");
                if (!(raw instanceof List<?>)) throw invalid();
                List<?> versions = (List<?>)raw;
                if (versions.isEmpty() || versions.size() > 8) throw invalid();
                boolean supported = false;
                for (Object candidate : versions) {
                    int item = integer(candidate); if (item <= 0) throw invalid(); supported |= item == 1;
                }
                if (!supported) return error(id, "VERSION_UNSUPPORTED");
            } catch (ContractException e) { return error(id, "INVALID_REQUEST"); }
            List<Object> capabilities = new ArrayList<>();
            for (String name : CAPABILITIES) capabilities.add(map("name", name, "state", "unsupported", "reasonCode", "NOT_IMPLEMENTED"));
            return encode(map("kind", "response", "apiVersion", 1, "requestId", id, "result",
                map("apiVersion", 1, "agentVersion", "0.3.0-dev", "bootId", bootId, "capabilities", capabilities,
                    "limits", map("controlFrameBytes", MAX_BYTES, "controlRequestsPerCaller", 8, "controlGlobalRequests", 32, "identityPackageLimit", 1))));
        } catch (ContractException e) { throw e; }
        catch (RuntimeException e) { throw invalid(); }
    }

    private static byte[] error(String id, String code) {
        return encode(map("kind", "response", "apiVersion", 1, "requestId", id,
            "error", map("code", code, "stage", "validation", "retryable", false, "message", "Control request rejected.", "details", map())));
    }
    private static byte[] encode(Object value) { return StrictJson.write(value).getBytes(StandardCharsets.UTF_8); }
    private static Map<String,Object> map(Object... pairs) {
        Map<String,Object> result = new LinkedHashMap<>();
        for (int i = 0; i < pairs.length; i += 2) result.put((String)pairs[i], pairs[i + 1]);
        return result;
    }
    private static void fields(Map<String,Object> value, String... allowed) {
        Set<String> names = new HashSet<>(Arrays.asList(allowed));
        for (String key : value.keySet()) if (!names.contains(key)) throw invalid();
    }
    private static int integer(Object raw) {
        if (!(raw instanceof StrictJson.NumberToken)) throw invalid();
        String token = ((StrictJson.NumberToken)raw).raw;
        if (!token.matches("0|[1-9][0-9]*")) throw invalid();
        try { return Integer.parseInt(token); } catch (NumberFormatException e) { throw invalid(); }
    }
    private static ContractException invalid() { return new ContractException(); }
}
