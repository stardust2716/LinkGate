package dev.linkgate.contracts;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

public final class ControlReply {
    private ControlReply() { }
    public static byte[] helloRequest(UUID requestId) {
        if (requestId == null) throw new IllegalArgumentException("Request ID required.");
        return StrictJson.write(map("kind","request","apiVersion",1,"requestId",requestId.toString(),"method","hello",
            "params",map("supportedApiVersions",Arrays.asList(1)))).getBytes(StandardCharsets.UTF_8);
    }
    public static Hello readHello(byte[] utf8, UUID expectedId) {
        try {
            Map<String,Object> value=readResult(utf8,expectedId); fields(value,"apiVersion","agentVersion","bootId","capabilities","limits");
            if (number(value.get("apiVersion"))!=1) throw invalid();
            String version=text(value.get("agentVersion"),64), boot=text(value.get("bootId"),36);
            if (!boot.matches("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")) throw invalid();
            if (!(value.get("capabilities") instanceof java.util.List<?>)) throw invalid();
            java.util.List<?> list=(java.util.List<?>)value.get("capabilities"); if(list.size()>64) throw invalid();
            Map<String,Capability> capabilities=new LinkedHashMap<>();
            for(Object raw:list) {
                Map<String,Object> item=object(raw); fields(item,"name","state","reasonCode");
                String name=text(item.get("name"),64), state=text(item.get("state"),16);
                if (!Arrays.asList("supported","experimental","unsupported").contains(state)) throw invalid();
                String reason=item.get("reasonCode")==null?null:text(item.get("reasonCode"),64);
                if(capabilities.put(name,new Capability(name,state,reason))!=null) throw invalid();
            }
            Map<String,Object> limits=object(value.get("limits")); fields(limits,"controlFrameBytes","controlRequestsPerCaller","controlGlobalRequests","identityPackageLimit");
            int bytes=number(limits.get("controlFrameBytes")), caller=number(limits.get("controlRequestsPerCaller")), global=number(limits.get("controlGlobalRequests"));
            if(bytes<1||bytes>65536||caller<1||global<caller||global>65536||number(limits.get("identityPackageLimit"))!=1) throw invalid();
            return new Hello(version,boot,capabilities,bytes,caller,global);
        } catch (ContractException | BrokerError e) { throw e; }
        catch(RuntimeException e) { throw invalid(); }
    }
    static Map<String,Object> readResult(byte[] utf8,UUID expectedId) {
        try {
            Map<String,Object> root = object(StrictJson.parse(utf8,65536));
            boolean result = root.containsKey("result");
            if (result == root.containsKey("error")) throw invalid();
            fields(root,"kind","apiVersion","requestId",result?"result":"error");
            if (!"response".equals(root.get("kind")) || number(root.get("apiVersion")) != 1 || expectedId == null || !expectedId.toString().equals(root.get("requestId"))) throw invalid();
            if (!result) {
                Map<String,Object> error = object(root.get("error")); fields(error,"code","stage","retryable","message","details");
                String code=text(error.get("code"),64), stage=text(error.get("stage"),32);
                if (!code.matches("[A-Z][A-Z0-9_]+") || !Arrays.asList("validation","authorization","discovery","direct","relay","attach","runtime","storage").contains(stage) || !(error.get("retryable") instanceof Boolean)) throw invalid();
                text(error.get("message"),512); object(error.get("details"));
                throw new BrokerError(code,stage,(Boolean)error.get("retryable"));
            }
            return object(root.get("result"));
        } catch(ContractException | BrokerError e) { throw e; }
        catch(RuntimeException e) { throw invalid(); }
    }
    public static final class Hello {
        public final String agentVersion, bootId;
        public final Map<String,Capability> capabilities;
        public final int maxRequestBytes, requestsPerCaller, globalRequests;
        private Hello(String version,String boot,Map<String,Capability> caps,int bytes,int caller,int global) {
            agentVersion=version; bootId=boot; capabilities=Collections.unmodifiableMap(new LinkedHashMap<>(caps));
            maxRequestBytes=bytes; requestsPerCaller=caller; globalRequests=global;
        }
    }
    public static final class Capability {
        public final String name,state,reasonCode;
        private Capability(String name,String state,String reason) { this.name=name;this.state=state;reasonCode=reason; }
    }
    public static final class BrokerError extends RuntimeException {
        private static final long serialVersionUID=1L;
        public final String code,stage;
        public final boolean retryable;
        private BrokerError(String code,String stage,boolean retryable) { super("Broker request rejected.");this.code=code;this.stage=stage;this.retryable=retryable; }
    }
    private static Map<String,Object> map(Object... pairs) { Map<String,Object> m=new LinkedHashMap<>();for(int i=0;i<pairs.length;i+=2)m.put((String)pairs[i],pairs[i+1]);return m; }
    @SuppressWarnings("unchecked") private static Map<String,Object> object(Object value) { if(!(value instanceof Map<?,?>))throw invalid();return (Map<String,Object>)value; }
    private static void fields(Map<String,Object> m,String... names) { Set<String> expected=new HashSet<>(Arrays.asList(names));if(!m.keySet().equals(expected))throw invalid(); }
    private static String text(Object value,int max) { if(!(value instanceof String))throw invalid();String s=(String)value;if(s.isEmpty()||s.length()>max)throw invalid();for(int i=0;i<s.length();i++)if(s.charAt(i)<32||s.charAt(i)==127)throw invalid();return s; }
    private static int number(Object value) { if(!(value instanceof StrictJson.NumberToken))throw invalid();String raw=((StrictJson.NumberToken)value).raw;if(!raw.matches("0|[1-9][0-9]*"))throw invalid();try{return Integer.parseInt(raw);}catch(NumberFormatException e){throw invalid();} }
    private static ContractException invalid() { return new ContractException(); }
}
