package dev.linkgate.contracts;

import java.util.Arrays;
import java.util.Map;
import java.util.UUID;

public final class SecureReply {
    private SecureReply() { }
    public static final class Scope {
        public final UUID applicationRef;public final String alias,device,publisher,service;
        public Scope(UUID application,String alias,String device,String publisher,String service) {
            if(application==null||alias==null||!alias.matches("[a-z][a-z0-9-]{0,31}")||publisher==null||publisher.length()>128||!publisher.matches("[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+")||service==null||!service.matches("[a-z][a-z0-9-]{0,31}"))throw new IllegalArgumentException("Exact application and service scope required.");
            applicationRef=application;this.alias=alias;this.device=WireJson.key(device);this.publisher=publisher;this.service=service;
        }
    }
    public static final class Connection {
        public final UUID connectionId,operationId;public final String state,error,path;
        private Connection(Map<String,Object> value,UUID expected) {
            WireJson.fields(value,"connectionId","operationId","state","error","dataPipe","path");connectionId=WireJson.uuid(value.get("connectionId"));operationId=WireJson.uuid(value.get("operationId"));
            if(!operationId.equals(expected)||!Arrays.asList("direct","relay").contains(value.get("path"))||!Arrays.asList("pending","ready","closed").contains(value.get("state")))throw new ContractException();
            state=(String)value.get("state");path=(String)value.get("path");Object failure=value.get("error");
            if(failure!=null&&(!state.equals("closed")||!Arrays.asList("CONNECTION_CLOSED","DIRECT_FAILED","RELAY_FAILED","AUTH_REQUIRED","AGENT_RESTARTED","RESOURCE_EXHAUSTED","GATEWAY_STOPPED").contains(failure)))throw new ContractException();error=(String)failure;
            if(value.get("dataPipe")!=null)throw new ContractException(); // Android uses the Binder descriptor transaction, never a Windows pipe.
        }
    }
    public static final class GrantRequest {
        public final UUID requestId;
        private GrantRequest(Map<String,Object> value){WireJson.fields(value,"grantRequestId","pendingUserApproval");requestId=WireJson.uuid(value.get("grantRequestId"));if(!Boolean.TRUE.equals(value.get("pendingUserApproval")))throw new ContractException();}
    }
    public static byte[] open(UUID request,UUID operation,Scope scope) {
        return open(request,operation,scope,false);
    }
    public static byte[] relay(UUID request,UUID operation,Scope scope) {return open(request,operation,scope,true);}
    private static byte[] open(UUID request,UUID operation,Scope scope,boolean relay) {
        if(scope==null||operation==null)throw new IllegalArgumentException("Saved operation and scope required.");
        return request(request,relay?"secure.relay.open":"secure.connection.open",WireJson.map("applicationRef",scope.applicationRef.toString(),"importAlias",scope.alias,"peer",scope.device,"publisher",scope.publisher,"service",scope.service),operation);
    }
    public static byte[] status(UUID request,UUID operation,boolean close) {
        if(operation==null)throw new IllegalArgumentException("Original operation required.");return request(request,close?"secure.connection.close":"secure.connection.status",WireJson.map("operationId",operation.toString()),null);
    }
    public static byte[] grant(UUID request,UUID operation,Scope scope,String reason) {
        if(scope==null||operation==null)throw new IllegalArgumentException("Saved operation and scope required.");if(reason==null)reason="";
        if(StrictJson.scalars(reason)>256||reason.codePoints().anyMatch(Character::isISOControl))throw new ContractException();
        return request(request,"grant.request",WireJson.map("applicationRef",scope.applicationRef.toString(),"importAlias",scope.alias,"deviceId",scope.device,"publisherAppId",scope.publisher,"remoteServiceId",scope.service,"reason",reason),operation);
    }
    public static Connection readConnection(byte[] input,UUID request,UUID operation){return new Connection(ControlReply.readResult(input,request),operation);}
    public static GrantRequest readGrant(byte[] input,UUID request){return new GrantRequest(ControlReply.readResult(input,request));}
    private static byte[] request(UUID id,String method,Object parameters,UUID operation) {
        if(id==null)throw new IllegalArgumentException("Request ID required.");Map<String,Object> value=WireJson.map("kind","request","apiVersion",1,"requestId",id.toString(),"method",method,"params",parameters);
        if(operation!=null)value.put("operationId",operation.toString());return WireJson.write(value,65536);
    }
}
