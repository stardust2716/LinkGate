package dev.linkgate.contracts;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

final class ControlRequest {
    final String id,method;
    final int version;
    final UUID operation;
    final Map<String,Object> params;
    final byte[] manifestBytes;
    private ControlRequest(String id,String method,int version,UUID operation,Map<String,Object> params,String manifest) {
        this.id=id;this.method=method;this.version=version;this.operation=operation;this.params=params;
        manifestBytes=manifest==null?null:manifest.getBytes(StandardCharsets.UTF_8);
    }
    static ControlRequest parse(byte[] input) {
        StrictJson.RequestDocument document=StrictJson.parseRequest(input,65536);
        Map<String,Object> root=object(document.value);
        fields(root,"kind","apiVersion","requestId","method","params","operationId");
        if(!"request".equals(root.get("kind")))throw invalid();
        uuid(root.get("requestId"));
        String id=(String)root.get("requestId");
        return new ControlRequest(id,text(root.get("method"),64),integer(root.get("apiVersion")),
            root.containsKey("operationId")?uuid(root.get("operationId")):null,object(root.get("params")),document.manifestSource);
    }
    byte[] error(String code,String stage) { return encode(map("kind","response","apiVersion",1,"requestId",id,"error",map("code",code,"stage",stage,"retryable",false,"message","Control request rejected.","details",map()))); }
    byte[] result(Object value) { return encode(map("kind","response","apiVersion",1,"requestId",id,"result",value)); }
    static UUID uuid(Object raw) { String value=text(raw,36);if(!value.matches("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"))throw invalid();return UUID.fromString(value); }
    static int integer(Object raw) { if(!(raw instanceof StrictJson.NumberToken))throw invalid();String value=((StrictJson.NumberToken)raw).raw;if(!value.matches("0|[1-9][0-9]*"))throw invalid();try{return Integer.parseInt(value);}catch(NumberFormatException e){throw invalid();} }
    static String text(Object raw,int max) { if(!(raw instanceof String))throw invalid();String s=(String)raw;if(s.isEmpty()||s.length()>max)throw invalid();for(int i=0;i<s.length();i++)if(s.charAt(i)<32||s.charAt(i)==127)throw invalid();return s; }
    @SuppressWarnings("unchecked") static Map<String,Object> object(Object value) { if(!(value instanceof Map<?,?>))throw invalid();return (Map<String,Object>)value; }
    static void fields(Map<String,Object> value,String... allowed) { Set<String> names=new HashSet<>(Arrays.asList(allowed));for(String key:value.keySet())if(!names.contains(key))throw invalid(); }
    static Map<String,Object> map(Object... pairs) { Map<String,Object> out=new LinkedHashMap<>();for(int i=0;i<pairs.length;i+=2)out.put((String)pairs[i],pairs[i+1]);return out; }
    static byte[] encode(Object value) { return StrictJson.write(value).getBytes(StandardCharsets.UTF_8); }
    private static ContractException invalid() { return new ContractException(); }
}
