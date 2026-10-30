package dev.linkgate.agent.android;

import android.app.Service;
import android.content.Intent;
import android.os.Binder;
import android.os.IBinder;
import android.os.ParcelFileDescriptor;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.broker.ILinkGateControl;
import dev.linkgate.contracts.ApplicationManifest;
import dev.linkgate.contracts.ContractException;
import dev.linkgate.contracts.ControlHello;
import dev.linkgate.contracts.ConnectionOpenRequest;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.WireJson;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;

public final class ControlService extends Service {
    public static final String ACTION="dev.linkgate.BIND_CONTROL_V1";
    private final CallerPrincipal.Gate calls=new CallerPrincipal.Gate();
    private final ControlHello hello=new ControlHello();
    private final ILinkGateControl.Stub binder=new ILinkGateControl.Stub(){
        @Override public dev.linkgate.broker.StreamAttachment attachConnection(String streamRef,IBinder lifetimeToken){
            if(streamRef==null||streamRef.length()!=36||lifetimeToken==null)return new dev.linkgate.broker.StreamAttachment(4,null);
            CallerPrincipal.Gate.Lease lease=calls.acquire(Binder.getCallingUid());if(lease==null)return new dev.linkgate.broker.StreamAttachment(3,null);
            try(CallerPrincipal.Gate.Lease ignored=lease){CallerPrincipal caller;try{caller=CallerPrincipal.capture(getPackageManager());}catch(Exception failure){return new dev.linkgate.broker.StreamAttachment(1,null);}AgentConnections current=GatewayService.current();if(current==null)return new dev.linkgate.broker.StreamAttachment(2,null);try{return new dev.linkgate.broker.StreamAttachment(0,current.formal.descriptor(caller.scope,WireJson.uuid(streamRef).toString(),lifetimeToken));}catch(Exception failure){return new dev.linkgate.broker.StreamAttachment(1,null);}}
        }
        @Override public byte[] request(byte[] message){
            if(message==null||message.length==0||message.length>65536)throw new IllegalArgumentException("INVALID_REQUEST");
            CallerPrincipal.Gate.Lease lease=calls.acquire(Binder.getCallingUid());if(lease==null)throw new IllegalStateException("RESOURCE_EXHAUSTED");
            try(CallerPrincipal.Gate.Lease ignored=lease){
                CallerPrincipal caller;
                try{caller=CallerPrincipal.capture(getPackageManager());}catch(Exception e){throw new SecurityException("AUTH_REQUIRED");}
                try{return dispatch(message,caller);}catch(ContractException e){throw new IllegalArgumentException("INVALID_REQUEST");}
            }
        }
        @Override public dev.linkgate.broker.StreamAttachment attachStream(String operationId,IBinder lifetimeToken){
            if(operationId==null||operationId.length()!=36||lifetimeToken==null)return new dev.linkgate.broker.StreamAttachment(4,null);
            CallerPrincipal.Gate.Lease lease=calls.acquire(Binder.getCallingUid());if(lease==null)return new dev.linkgate.broker.StreamAttachment(3,null);
            try(CallerPrincipal.Gate.Lease ignored=lease){
                CallerPrincipal caller;
                try{caller=CallerPrincipal.capture(getPackageManager());}catch(Exception e){return new dev.linkgate.broker.StreamAttachment(1,null);}
                AgentConnections connections=GatewayService.current();if(connections==null)return new dev.linkgate.broker.StreamAttachment(2,null);
                try{return new dev.linkgate.broker.StreamAttachment(0,connections.attach(caller.scope,WireJson.uuid(operationId),lifetimeToken));}catch(Exception e){return new dev.linkgate.broker.StreamAttachment(1,null);}
            }
        }
    };
    private byte[] dispatch(byte[] input,CallerPrincipal caller){
        Map<String,Object> root=WireJson.read(input,65536);boolean mutating=root.containsKey("operationId");
        if(mutating)WireJson.fields(root,"kind","apiVersion","requestId","method","params","operationId");else WireJson.fields(root,"kind","apiVersion","requestId","method","params");
        if(!"request".equals(root.get("kind")))throw new ContractException();WireJson.uuid(root.get("requestId"));String id=(String)root.get("requestId");long version=WireJson.integer(root.get("apiVersion"),0,Integer.MAX_VALUE);String method=WireJson.text(root.get("method"),64);Map<String,Object> params=WireJson.object(root.get("params"));UUID operation=mutating?WireJson.uuid(root.get("operationId")):null;
        if(version!=1)return error(id,"VERSION_UNSUPPORTED","validation");
        if(method.equals("hello")){
            Map<String,Object> response=WireJson.read(hello.respond(input),65536);
            if(response.containsKey("result")){
                Map<String,Object> result=WireJson.object(response.get("result"));Object original=result.get("capabilities");if(!(original instanceof List<?>))throw new ContractException();List<Object> capabilities=new ArrayList<>((List<?>)original);
                for(String name:new String[]{"app.register.pending","grant.request.pending","secure.stream.direct"})capabilities.add(WireJson.map("name",name,"state","experimental","reasonCode","EXPLICIT_OWNER_AUTHORITY"));AgentConnections current=GatewayService.current();if(current!=null&&current.relayAvailable())capabilities.add(WireJson.map("name","secure.stream.relay","state","experimental","reasonCode","EXPLICIT_OWNER_AUTHORITY"));capabilities.add(WireJson.map("name","connection.api","state","experimental","reasonCode","OWNER_STARTED_SERVER_REQUIRED"));result.put("capabilities",capabilities);
            }
            return WireJson.write(response,65536);
        }
        try {
            AgentRegistry registry=AgentRepository.get(this).registry;Object result;
            if(method.equals("app.register")){
                WireJson.fields(params,"manifest");if(operation==null)return error(id,"INVALID_REQUEST","validation");ApplicationManifest manifest=ManifestContract.parse(WireJson.originalManifest(input));if(!manifest.exports.isEmpty())return error(id,"CAPABILITY_UNSUPPORTED","validation");
                result=read(registry.register(caller.scope,caller.provenance,operation,manifest));
            }else if(method.equals("operation.status")){
                WireJson.fields(params,"operationId");if(mutating)return error(id,"INVALID_REQUEST","validation");UUID original=WireJson.uuid(params.get("operationId"));synchronized(GatewayService.class){if(GatewayService.current()==null)try{var historical=registry.connections().atOperation(caller.scope,original);if(historical.closedUtc==null)registry.connections().terminate(caller.scope,historical.connectionId,"GATEWAY_STOPPED",System.currentTimeMillis());}catch(AgentRegistry.Failure missing){if(!missing.code.equals("OPERATION_UNKNOWN"))throw missing;}}result=read(registry.operationStatus(caller.scope,original));
            }else if(method.equals("connection.open")){
                if(operation==null)return error(id,"INVALID_REQUEST","validation");ConnectionOpenRequest request=ConnectionOpenRequest.parse(WireJson.originalConnectionParams(input));AgentConnections current=GatewayService.current();if(current==null)return error(id,"GATEWAY_STOPPED","runtime");if(!current.formal.available())return error(id,"CAPABILITY_UNSUPPORTED","runtime");result=current.formal.open(caller.scope,operation,request);
            }else if(method.equals("connection.status")){
                WireJson.fields(params,"connectionId");if(mutating)return error(id,"INVALID_REQUEST","validation");String reference=WireJson.uuid(params.get("connectionId")).toString();synchronized(GatewayService.class){AgentConnections current=GatewayService.current();if(current!=null)result=current.formal.status(caller.scope,reference);else{var historical=registry.connections().status(caller.scope,reference);if(historical.closedUtc==null)registry.connections().terminate(caller.scope,reference,"GATEWAY_STOPPED",System.currentTimeMillis());result=registry.connections().status(caller.scope,reference).wire();}}
            }else if(method.equals("connection.attach")||method.equals("connection.close")){
                WireJson.fields(params,"connectionId");if(operation==null)return error(id,"INVALID_REQUEST","validation");String reference=WireJson.uuid(params.get("connectionId")).toString();AgentConnections current=GatewayService.current();if(current!=null)result=method.endsWith("attach")?current.formal.attach(caller.scope,operation,reference):current.formal.close(caller.scope,operation,reference);else{if(method.endsWith("attach"))return error(id,"GATEWAY_STOPPED","runtime");result=read(registry.connections().prepareClose(caller.scope,operation,reference));registry.connections().terminate(caller.scope,reference,"CONNECTION_CLOSED",System.currentTimeMillis());}
            }else if(method.equals("grant.request")){
                if(params.containsKey("reason"))WireJson.fields(params,"applicationRef","importAlias","deviceId","publisherAppId","remoteServiceId","reason");else WireJson.fields(params,"applicationRef","importAlias","deviceId","publisherAppId","remoteServiceId");
                if(operation==null)return error(id,"INVALID_REQUEST","validation");Object reason=params.get("reason");if(params.containsKey("reason")&&!(reason instanceof String))throw new ContractException();
                result=read(registry.requestAccess(caller.scope,operation,WireJson.uuid(params.get("applicationRef")).toString(),WireJson.text(params.get("importAlias"),32),WireJson.key(params.get("deviceId")),WireJson.text(params.get("publisherAppId"),128),WireJson.text(params.get("remoteServiceId"),32),reason==null?"":(String)reason));
            }else if((method.equals("secure.connection.open")||method.equals("secure.relay.open"))){
                WireJson.fields(params,"applicationRef","importAlias","peer","publisher","service");if(operation==null)return error(id,"INVALID_REQUEST","validation");AgentConnections connections=GatewayService.current();if(connections==null)return error(id,"GATEWAY_STOPPED","runtime");
                result=method.equals("secure.relay.open")?connections.openRelay(caller.scope,operation,WireJson.uuid(params.get("applicationRef")).toString(),WireJson.text(params.get("importAlias"),32),WireJson.key(params.get("peer")),WireJson.text(params.get("publisher"),128),WireJson.text(params.get("service"),32)):connections.open(caller.scope,operation,WireJson.uuid(params.get("applicationRef")).toString(),WireJson.text(params.get("importAlias"),32),WireJson.key(params.get("peer")),WireJson.text(params.get("publisher"),128),WireJson.text(params.get("service"),32));
            }else if(method.equals("secure.connection.status")||method.equals("secure.connection.close")){
                WireJson.fields(params,"operationId");if(mutating)return error(id,"INVALID_REQUEST","validation");AgentConnections connections=GatewayService.current();UUID original=WireJson.uuid(params.get("operationId"));
                if(connections!=null)result=method.endsWith("close")?connections.close(caller.scope,original):connections.status(caller.scope,original);
                else{AgentRegistry.Open saved=registry.openStatus(caller.scope,original);if(method.endsWith("close")){registry.setOpen(caller.scope,original,UUID.fromString(saved.boot),"closed","CONNECTION_CLOSED");saved=registry.openStatus(caller.scope,original);}result=WireJson.map("connectionId",saved.reference,"operationId",original.toString(),"state","closed","error",saved.error==null?"GATEWAY_STOPPED":saved.error,"dataPipe",null,"path",saved.path);}
            }else return error(id,"CAPABILITY_UNSUPPORTED","validation");
            return WireJson.write(WireJson.map("kind","response","apiVersion",1,"requestId",id,"result",result),65536);
        }catch(AgentRegistry.Failure e){return error(id,e.code,e.code.equals("STORAGE_UNAVAILABLE")?"storage":"authorization");}
        catch(IllegalArgumentException e){return error(id,"INVALID_REQUEST","validation");}
        catch(IllegalStateException e){return error(id,"GATEWAY_STOPPED".equals(e.getMessage())?"GATEWAY_STOPPED":"STORAGE_UNAVAILABLE","runtime");}
        catch(RuntimeException e){return error(id,"STORAGE_UNAVAILABLE","storage");}
    }
    private static Map<String,Object> read(String json){return WireJson.read(json.getBytes(StandardCharsets.UTF_8),65536);}
    private static byte[] error(String id,String code,String stage){return WireJson.write(WireJson.map("kind","response","apiVersion",1,"requestId",id,"error",WireJson.map("code",code,"stage",stage,"retryable",false,"message","Control request rejected.","details",WireJson.map())),65536);}
    @Override public IBinder onBind(Intent intent){return intent!=null&&ACTION.equals(intent.getAction())?binder:null;}
    @Override public int onStartCommand(Intent intent,int flags,int startId){stopSelf(startId);return START_NOT_STICKY;}
}
