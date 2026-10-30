package dev.linkgate.contracts;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.UUID;

/** Bounded protocol primitives; interpretation and authorization belong to the owning protocol. */
public final class WireJson {
    private WireJson() { }
    public static Map<String,Object> read(byte[] input,int limit) { return object(StrictJson.parse(input,limit)); }
    public static byte[] originalManifest(byte[] request) {
        ControlRequest parsed=ControlRequest.parse(request);
        if(!parsed.method.equals("app.register")||parsed.manifestBytes==null||parsed.manifestBytes.length>49152)throw new ContractException();return parsed.manifestBytes.clone();
    }
    public static byte[] originalConnectionParams(byte[] request){ControlRequest parsed=ControlRequest.parse(request);if(!parsed.method.equals("connection.open"))throw new ContractException();StrictJson.RequestDocument document=StrictJson.parseRequest(request,65536);if(document.paramsSource==null)throw new ContractException();return document.paramsSource.getBytes(StandardCharsets.UTF_8);}
    public static byte[] write(Object value,int limit) {
        validate(value,0,new int[]{0});
        byte[] encoded=StrictJson.write(value).getBytes(StandardCharsets.UTF_8);
        if(encoded.length==0||encoded.length>limit)throw new ContractException();
        StrictJson.parse(encoded,limit);return encoded;
    }
    public static Map<String,Object> map(Object... pairs) {
        if(pairs.length%2!=0)throw new IllegalArgumentException("Field pairs required.");
        Map<String,Object> result=new LinkedHashMap<>();
        for(int i=0;i<pairs.length;i+=2){String key=(String)pairs[i];if(result.containsKey(key))throw new ContractException();result.put(key,pairs[i+1]);}
        return result;
    }
    @SuppressWarnings("unchecked") public static Map<String,Object> object(Object value) {
        if(!(value instanceof Map<?,?>))throw new ContractException();return (Map<String,Object>)value;
    }
    public static void fields(Map<String,Object> value,String... fields) {
        if(!value.keySet().equals(new HashSet<>(Arrays.asList(fields))))throw new ContractException();
    }
    public static String text(Object value,int max) {
        if(!(value instanceof String))throw new ContractException();String text=(String)value;
        if(text.isEmpty()||StrictJson.scalars(text)>max||text.codePoints().anyMatch(Character::isISOControl))throw new ContractException();return text;
    }
    public static UUID uuid(Object value) {String text=text(value,36);if(!text.matches("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"))throw new ContractException();return UUID.fromString(text);}
    public static long integer(Object value,long min,long max) {
        if(!(value instanceof StrictJson.NumberToken))throw new ContractException();String raw=((StrictJson.NumberToken)value).raw;
        if(!raw.matches("0|[1-9][0-9]*"))throw new ContractException();
        try{long n=Long.parseLong(raw);if(n<min||n>max)throw new ContractException();return n;}catch(NumberFormatException e){throw new ContractException();}
    }
    public static String key(Object value) {String key=text(value,64);if(!key.matches("[0-9a-f]{64}"))throw new ContractException();return key;}
    private static void validate(Object value,int depth,int[] count) {
        if(++count[0]>10000||depth>16)throw new ContractException();
        if(value instanceof String){StrictJson.scalars((String)value);return;}
        if(value instanceof Map<?,?>){for(Map.Entry<?,?> entry:((Map<?,?>)value).entrySet()){if(!(entry.getKey() instanceof String))throw new ContractException();StrictJson.scalars((String)entry.getKey());validate(entry.getValue(),depth+1,count);}}
        else if(value instanceof java.util.List<?>)for(Object child:(java.util.List<?>)value)validate(child,depth+1,count);
    }
}
