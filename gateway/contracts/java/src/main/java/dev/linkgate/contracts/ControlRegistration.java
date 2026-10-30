package dev.linkgate.contracts;

import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.function.Supplier;

public final class ControlRegistration {
    private final ControlHello hello;
    private final Supplier<PendingRegistry> registry;
    public ControlRegistration(ControlHello hello,Supplier<PendingRegistry> registry) {
        if(hello==null||registry==null)throw new IllegalArgumentException("Host configuration required.");this.hello=hello;this.registry=registry;
    }
    public byte[] respond(byte[] input,String caller) {
        ControlRequest request=ControlRequest.parse(input);
        if(request.version!=1)return request.error("VERSION_UNSUPPORTED","validation");
        if(request.method.equals("hello")) {
            byte[] response=hello.respond(input);
            Map<String,Object> envelope=ControlRequest.object(StrictJson.parse(response,65536));
            if(!envelope.containsKey("result"))return response;
            Map<String,Object> result=ControlRequest.object(envelope.get("result"));
            @SuppressWarnings("unchecked") List<Object> capabilities=(List<Object>)result.get("capabilities");
            capabilities.add(ControlRequest.map("name","app.register.pending","state","experimental","reasonCode","PENDING_ONLY"));
            return ControlRequest.encode(envelope);
        }
        if(!request.method.equals("app.register")&&!request.method.equals("operation.status"))return request.error("CAPABILITY_UNSUPPORTED","validation");
        if(caller==null||caller.isEmpty())return request.error("AUTH_REQUIRED","authorization");
        ApplicationManifest manifest=null;UUID lookup=null;
        try {
            if(request.method.equals("app.register")) {
                ControlRequest.fields(request.params,"manifest");
                if(request.operation==null||request.manifestBytes==null)throw new ContractException();
                manifest=ManifestContract.parse(request.manifestBytes);
            } else {
                ControlRequest.fields(request.params,"operationId");
                if(request.operation!=null)throw new ContractException();lookup=ControlRequest.uuid(request.params.get("operationId"));
            }
        } catch(ContractException e){return request.error("INVALID_REQUEST","validation");}
        try {
            PendingRegistry store=registry.get();
            String result=manifest!=null?store.register(caller,request.operation,manifest):store.status(caller,lookup);
            return request.result(StrictJson.parse(result.getBytes(java.nio.charset.StandardCharsets.UTF_8),65536));
        } catch(PendingRegistry.Failure e){return request.error(e.code,e.code.equals("OPERATION_UNKNOWN")?"authorization":"storage");}
        catch(RuntimeException e){return request.error("STORAGE_UNAVAILABLE","storage");}
    }
}
