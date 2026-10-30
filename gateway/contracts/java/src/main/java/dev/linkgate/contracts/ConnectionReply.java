package dev.linkgate.contracts;

import java.util.Map;
import java.util.UUID;

public final class ConnectionReply {
    private ConnectionReply(){}
    public static final class Operation {
        public final UUID operationId,connectionId,streamRef;public final String status,errorCode;private final String encoded;
        public String toJson(){return encoded;}
        public Map<String,Object> wire(){return WireJson.read(encoded.getBytes(java.nio.charset.StandardCharsets.UTF_8),16384);}
        private Operation(Map<String,Object> value,UUID expected,String expectedKind){
            if(expectedKind!=null&&!expectedKind.equals("connectionId")&&!expectedKind.equals("streamRef"))throw new ContractException();operationId=canonicalUuid(value.get("operationId"));if(!operationId.equals(expected))throw new ContractException();status=WireJson.text(value.get("status"),16);UUID connection=null,stream=null;String error=null;
            if(status.equals("pending")||status.equals("reconciling"))WireJson.fields(value,"operationId","status");
            else if(status.equals("succeeded")){WireJson.fields(value,"operationId","status","result");Map<String,Object> result=WireJson.object(value.get("result"));if(result.containsKey("connectionId")&&(expectedKind==null||expectedKind.equals("connectionId"))){WireJson.fields(result,"connectionId");connection=canonicalUuid(result.get("connectionId"));}else if(result.containsKey("streamRef")&&(expectedKind==null||expectedKind.equals("streamRef"))){WireJson.fields(result,"streamRef");stream=canonicalUuid(result.get("streamRef"));}else throw new ContractException();}
            else if(status.equals("failed")){WireJson.fields(value,"operationId","status","error");Map<String,Object> failure=WireJson.object(value.get("error"));WireJson.fields(failure,"code","stage","retryable","message","details");error=WireJson.text(failure.get("code"),64);if(!error.matches("[A-Z][A-Z0-9_]{0,63}")||!"connection".equals(failure.get("stage"))||!Boolean.FALSE.equals(failure.get("retryable"))||!"Connection ended.".equals(failure.get("message"))||!WireJson.object(failure.get("details")).isEmpty())throw new ContractException();}
            else throw new ContractException();connectionId=connection;streamRef=stream;errorCode=error;encoded=new String(WireJson.write(value,16384),java.nio.charset.StandardCharsets.UTF_8);
        }
    }
    private static UUID canonicalUuid(Object raw){UUID value=WireJson.uuid(raw);if(!value.toString().equals(raw))throw new ContractException();return value;}
    public static Operation parseOperation(byte[] input,UUID operation,String expectedKind){try{return new Operation(WireJson.read(input,16384),operation,expectedKind);}catch(ContractException failure){throw failure;}catch(RuntimeException failure){throw new ContractException();}}
    public static byte[] open(UUID request,UUID operation,ConnectionOpenRequest value){if(value==null)throw new IllegalArgumentException("Validated connection request required.");return request(request,"connection.open",WireJson.read(value.toJson().getBytes(java.nio.charset.StandardCharsets.UTF_8),16384),operation);}
    public static byte[] action(UUID request,UUID operation,UUID connection,boolean attach){if(connection==null||operation==null)throw new IllegalArgumentException("Retained operation and connection required.");return request(request,attach?"connection.attach":"connection.close",WireJson.map("connectionId",connection.toString()),operation);}
    public static byte[] status(UUID request,UUID connection){return request(request,"connection.status",WireJson.map("connectionId",connection.toString()),null);}
    public static byte[] operation(UUID request,UUID operation){return request(request,"operation.status",WireJson.map("operationId",operation.toString()),null);}
    public static Operation readOperation(byte[] input,UUID request,UUID operation,String expectedKind){return new Operation(ControlReply.readResult(input,request),operation,expectedKind);}
    public static ConnectionStatus readStatus(byte[] input,UUID request,UUID connection){ConnectionStatus value=ConnectionStatus.parse(WireJson.write(ControlReply.readResult(input,request),16384));if(!value.connectionId.equals(connection.toString()))throw new ContractException();return value;}
    private static byte[] request(UUID request,String method,Object params,UUID operation){if(request==null)throw new IllegalArgumentException("Request required.");Map<String,Object> value=WireJson.map("kind","request","apiVersion",1,"requestId",request.toString(),"method",method,"params",params);if(operation!=null)value.put("operationId",operation.toString());return WireJson.write(value,65536);}
}
