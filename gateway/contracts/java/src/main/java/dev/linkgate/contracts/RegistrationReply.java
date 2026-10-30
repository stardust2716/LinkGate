package dev.linkgate.contracts;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.UUID;

public final class RegistrationReply {
    private RegistrationReply() { }
    public static byte[] registerRequest(UUID requestId,UUID operationId,ApplicationManifest manifest) {
        if(requestId==null||operationId==null||manifest==null)throw new IllegalArgumentException("Registration arguments required.");
        Object value=StrictJson.parse(manifest.toJson().getBytes(StandardCharsets.UTF_8),49152);
        return encode(ControlRequest.map("kind","request","apiVersion",1,"requestId",requestId.toString(),"method","app.register",
            "params",ControlRequest.map("manifest",value),"operationId",operationId.toString()));
    }
    public static byte[] statusRequest(UUID requestId,UUID operationId) {
        if(requestId==null||operationId==null)throw new IllegalArgumentException("Operation ID required.");
        return encode(ControlRequest.map("kind","request","apiVersion",1,"requestId",requestId.toString(),"method","operation.status",
            "params",ControlRequest.map("operationId",operationId.toString())));
    }
    public static Pending readPending(byte[] bytes,UUID requestId) {
        try{return parsePending(ControlReply.readResult(bytes,requestId));}
        catch(ContractException|ControlReply.BrokerError e){throw e;}catch(RuntimeException e){throw invalid();}
    }
    public static Operation readStatus(byte[] bytes,UUID requestId,UUID operationId) {
        try {
            Map<String,Object> value=ControlReply.readResult(bytes,requestId);fields(value,"operationId","status","result");
            if(operationId==null||!operationId.toString().equals(value.get("operationId"))||!"succeeded".equals(value.get("status")))throw invalid();
            return new Operation(operationId,parsePending(ControlRequest.object(value.get("result"))));
        }catch(ContractException|ControlReply.BrokerError e){throw e;}catch(RuntimeException e){throw invalid();}
    }
    static Pending parsePending(Map<String,Object> value) {
        fields(value,"applicationRef","permissionDiff","approvalRequired");
        if(!Boolean.TRUE.equals(value.get("approvalRequired")))throw invalid();
        UUID reference=ControlRequest.uuid(value.get("applicationRef"));Map<String,Object> diff=ControlRequest.object(value.get("permissionDiff"));
        fields(diff,"addedExports","removedExports","changedExports","addedImports","removedImports","changedImports","declarationChanged");
        if(!(diff.get("declarationChanged") instanceof Boolean))throw invalid();
        return new Pending(reference,new Diff(ids(diff,"addedExports"),ids(diff,"removedExports"),ids(diff,"changedExports"),
            ids(diff,"addedImports"),ids(diff,"removedImports"),ids(diff,"changedImports"),(Boolean)diff.get("declarationChanged")));
    }
    private static List<String> ids(Map<String,Object> value,String key) {
        Object raw=value.get(key);if(!(raw instanceof List<?>))throw invalid();List<?> list=(List<?>)raw;if(list.size()>16)throw invalid();
        List<String> result=new ArrayList<>();HashSet<String> seen=new HashSet<>();
        for(Object item:list){if(!(item instanceof String)||!((String)item).matches("[a-z][a-z0-9-]{0,31}")||!seen.add((String)item))throw invalid();result.add((String)item);}
        return Collections.unmodifiableList(result);
    }
    private static void fields(Map<String,Object> value,String... names){if(!value.keySet().equals(new HashSet<>(Arrays.asList(names))))throw invalid();}
    private static byte[] encode(Object value){byte[] bytes=StrictJson.write(value).getBytes(StandardCharsets.UTF_8);if(bytes.length>65536)throw invalid();return bytes;}
    private static ContractException invalid(){return new ContractException();}
    public static final class Pending {
        public final UUID applicationRef;
        public final boolean approvalRequired=true;
        public final Diff permissionDiff;
        private Pending(UUID reference,Diff diff){applicationRef=reference;permissionDiff=diff;}
    }
    public static final class Diff {
        public final List<String> addedExports,removedExports,changedExports,addedImports,removedImports,changedImports;
        public final boolean declarationChanged;
        private Diff(List<String> ae,List<String> re,List<String> ce,List<String> ai,List<String> ri,List<String> ci,boolean changed){addedExports=ae;removedExports=re;changedExports=ce;addedImports=ai;removedImports=ri;changedImports=ci;declarationChanged=changed;}
    }
    public static final class Operation {
        public final UUID operationId;
        public final String status="succeeded";
        public final Pending result;
        private Operation(UUID id,Pending result){operationId=id;this.result=result;}
    }
}
