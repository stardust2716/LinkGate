package dev.linkgate.client;

import dev.linkgate.contracts.ApplicationManifest;
import dev.linkgate.contracts.ContractException;
import dev.linkgate.contracts.ControlReply;
import dev.linkgate.contracts.RegistrationReply;
import dev.linkgate.contracts.SecureReply;
import dev.linkgate.contracts.ConnectionOpenRequest;
import dev.linkgate.contracts.ConnectionReply;
import dev.linkgate.contracts.ConnectionStatus;
import java.io.IOException;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeoutException;

final class ControlSession implements AutoCloseable {
    interface Transport { void verify()throws Exception;byte[] request(byte[] input)throws Exception; }
    private interface Decoder<T>{T decode(byte[] input);}
    interface NativeCall<T>{T call()throws Exception;}
    interface Discard<T>{void discard(T value);}
    private final ClientRuntime runtime;
    private final Transport transport;
    private final Runnable onClose;
    private final Object lock=new Object();
    private boolean closed;
    private ControlReply.Hello negotiated;
    private Active<?> active;
    ControlSession(ClientRuntime runtime,Transport transport,Runnable onClose){this.runtime=runtime;this.transport=transport;this.onClose=onClose;}
    CompletableFuture<ControlReply.Hello> helloAsync(){
        UUID id=UUID.randomUUID();return exchange(new CompletableFuture<>(),ControlReply.helloRequest(id),null,
            bytes->ControlReply.readHello(bytes,id),true);
    }
    OperationFuture<RegistrationReply.Pending> registerPendingAsync(ApplicationManifest manifest,UUID operationId,boolean allowExperimental){
        OperationFuture<RegistrationReply.Pending> future=new OperationFuture<>(operationId);
        synchronized(lock){if(!pending()||!allowExperimental){future.completeExceptionally(new IllegalStateException("Negotiate and explicitly enable pending registration first."));return future;}}
        UUID id=UUID.randomUUID();
        final byte[] payload;
        try{payload=RegistrationReply.registerRequest(id,operationId,manifest);}catch(RuntimeException e){future.completeExceptionally(new IllegalArgumentException("Invalid registration request."));return future;}
        return exchange(future,payload,operationId,bytes->RegistrationReply.readPending(bytes,id),false);
    }
    CompletableFuture<RegistrationReply.Operation> getOperationStatusAsync(UUID operationId){
        if(operationId==null)throw new IllegalArgumentException("Operation ID required.");
        synchronized(lock){if(!pending())return failed(new IllegalStateException("Negotiate pending capability before querying operations."));}
        UUID id=UUID.randomUUID();return exchange(new CompletableFuture<>(),RegistrationReply.statusRequest(id,operationId),null,
            bytes->RegistrationReply.readStatus(bytes,id,operationId),false);
    }
    private boolean pending(){if(closed||negotiated==null)return false;ControlReply.Capability value=negotiated.capabilities.get("app.register.pending");return value!=null&&(value.state.equals("supported")||value.state.equals("experimental"));}
    private boolean capability(String name){if(closed||negotiated==null)return false;ControlReply.Capability value=negotiated.capabilities.get(name);return value!=null&&(value.state.equals("supported")||value.state.equals("experimental"));}
    OperationFuture<ConnectionReply.Operation> openConnectionAsync(ConnectionOpenRequest request,UUID operation,boolean allowExperimental){OperationFuture<ConnectionReply.Operation> future=new OperationFuture<>(operation);synchronized(lock){if(!allowExperimental||!capability("connection.api")){future.completeExceptionally(new IllegalStateException("Formal connections unavailable."));return future;}}UUID id=UUID.randomUUID();return exchange(future,ConnectionReply.open(id,operation,request),operation,bytes->ConnectionReply.readOperation(bytes,id,operation,"connectionId"),false);}
    OperationFuture<ConnectionReply.Operation> connectionActionAsync(UUID connection,UUID operation,boolean attach,boolean allowExperimental){OperationFuture<ConnectionReply.Operation> future=new OperationFuture<>(operation);synchronized(lock){if(!allowExperimental||!capability("connection.api")){future.completeExceptionally(new IllegalStateException("Formal connections unavailable."));return future;}}UUID id=UUID.randomUUID();return exchange(future,ConnectionReply.action(id,operation,connection,attach),operation,bytes->ConnectionReply.readOperation(bytes,id,operation,attach?"streamRef":"connectionId"),false);}
    CompletableFuture<ConnectionReply.Operation> connectionOperationAsync(UUID operation){synchronized(lock){if(!capability("connection.api"))return failed(new IllegalStateException("Formal connections unavailable."));}UUID id=UUID.randomUUID();return exchange(new CompletableFuture<>(),ConnectionReply.operation(id,operation),null,bytes->ConnectionReply.readOperation(bytes,id,operation,null),false);}
    CompletableFuture<ConnectionStatus> connectionStatusAsync(UUID connection){synchronized(lock){if(!capability("connection.api"))return failed(new IllegalStateException("Formal connections unavailable."));}UUID id=UUID.randomUUID();return exchange(new CompletableFuture<>(),ConnectionReply.status(id,connection),null,bytes->ConnectionReply.readStatus(bytes,id,connection),false);}
    OperationFuture<SecureReply.Connection> openSecureAsync(SecureReply.Scope scope,UUID operation,boolean allowExperimental) {
        return openSecureAsync(scope,operation,allowExperimental,false);
    }
    OperationFuture<SecureReply.Connection> openRelayAsync(SecureReply.Scope scope,UUID operation,boolean allowExperimental) {return openSecureAsync(scope,operation,allowExperimental,true);}
    private OperationFuture<SecureReply.Connection> openSecureAsync(SecureReply.Scope scope,UUID operation,boolean allowExperimental,boolean relay) {
        OperationFuture<SecureReply.Connection> future=new OperationFuture<>(operation);
        synchronized(lock){if(!allowExperimental||!capability(relay?"secure.stream.relay":"secure.stream.direct")){future.completeExceptionally(new IllegalStateException("Negotiate and explicitly enable secure streams first."));return future;}}
        UUID id=UUID.randomUUID();return exchange(future,relay?SecureReply.relay(id,operation,scope):SecureReply.open(id,operation,scope),operation,bytes->connection(bytes,id,operation,relay?"relay":"direct"),false);
    }
    CompletableFuture<SecureReply.Connection> secureStatusAsync(UUID operation,boolean close) {
        synchronized(lock){if(!capability("secure.stream.direct"))return failed(new IllegalStateException("Secure streams unavailable."));}
        UUID id=UUID.randomUUID();return exchange(new CompletableFuture<>(),SecureReply.status(id,operation,close),null,bytes->connection(bytes,id,operation,null),false);
    }
    private SecureReply.Connection connection(byte[] bytes,UUID request,UUID operation,String expected) {
        SecureReply.Connection result=SecureReply.readConnection(bytes,request,operation);
        synchronized(lock){if(expected!=null&&!expected.equals(result.path)||result.path.equals("relay")&&!result.state.equals("closed")&&!capability("secure.stream.relay"))throw new ContractException();}return result;
    }
    OperationFuture<SecureReply.GrantRequest> requestAccessAsync(SecureReply.Scope scope,UUID operation,String reason,boolean allowExperimental) {
        OperationFuture<SecureReply.GrantRequest> future=new OperationFuture<>(operation);
        synchronized(lock){if(!allowExperimental||!capability("grant.request.pending")){future.completeExceptionally(new IllegalStateException("Negotiate and explicitly enable grant requests first."));return future;}}
        UUID id=UUID.randomUUID();return exchange(future,SecureReply.grant(id,operation,scope,reason),operation,bytes->SecureReply.readGrant(bytes,id),false);
    }
    <T> OperationFuture<T> nativeAsync(UUID operation,boolean allowExperimental,NativeCall<T> work,Discard<T> discard) {
        return nativeAsync(operation,allowExperimental,work,discard,false);
    }
    <T> OperationFuture<T> nativeConnectionAsync(UUID operation,boolean allowExperimental,NativeCall<T> work,Discard<T> discard){return nativeAsync(operation,allowExperimental,work,discard,true);}
    private <T> OperationFuture<T> nativeAsync(UUID operation,boolean allowExperimental,NativeCall<T> work,Discard<T> discard,boolean formal) {
        OperationFuture<T> future=new OperationFuture<>(operation);
        synchronized(lock){if(!allowExperimental||!capability(formal?"connection.api":"secure.stream.direct")){future.completeExceptionally(new IllegalStateException("Secure streams unavailable."));return future;}}
        return run(future,0,operation,work,false,discard);
    }
    private <T,F extends CompletableFuture<T>> F exchange(F future,byte[] payload,UUID mutation,Decoder<T> decoder,boolean hello){
        return run(future,payload.length,mutation,()->decoder.decode(transport.request(payload)),hello,value->{});
    }
    private <T,F extends CompletableFuture<T>> F run(F future,int payloadBytes,UUID mutation,NativeCall<T> work,boolean hello,Discard<T> discard){
        final Active<T> request=new Active<>(future,mutation);
        synchronized(lock){
            if(closed){future.completeExceptionally(new IOException("Broker connection unavailable."));return future;}
            if(active!=null){future.completeExceptionally(new IllegalStateException("RESOURCE_EXHAUSTED"));return future;}
            if(negotiated!=null&&payloadBytes>negotiated.maxRequestBytes){future.completeExceptionally(new IllegalArgumentException("Request exceeds negotiated limit."));return future;}
            active=request;
        }
        ScheduledFuture<?> deadline=runtime.after(2000,()->request.fail(new TimeoutException("Broker call timed out.")));
        future.whenComplete((value,error)->{
            deadline.cancel(false);synchronized(lock){if(active==request)active=null;}
            if(error!=null&&!(error instanceof ControlReply.BrokerError)&&!(error instanceof NativeRefusalException))close();
        });
        if(!runtime.execute(()->{
            T result=null;boolean delivered=false;
            try{
                if(future.isDone())return;
                transport.verify();
                synchronized(lock){
                    if(closed||future.isDone())return;
                    if(future instanceof OperationFuture<?> && !((OperationFuture<?>)future).tryMarkAttempted())return;
                    request.attempted=true;
                }
                result=work.call();transport.verify();delivered=request.succeed(result,hello);
            }catch(ControlReply.BrokerError e){request.fail(e);}
            catch(NativeRefusalException e){request.fail(e);}
            catch(ContractException e){request.fail(new IOException("Invalid control response."));}
            catch(Exception e){request.fail(new IOException("Broker call failed."));}
            finally{if(!delivered&&result!=null)discard.discard(result);}
        }))request.fail(new IllegalStateException("RESOURCE_EXHAUSTED"));
        return future;
    }
    @Override public void close(){Active<?> request;synchronized(lock){if(closed)return;closed=true;request=active;}if(request!=null)request.fail(new IOException("Broker connection closed."));onClose.run();}
    private final class Active<T>{
        final CompletableFuture<T> future;final UUID mutation;boolean attempted,terminal;
        Active(CompletableFuture<T> future,UUID mutation){this.future=future;this.mutation=mutation;}
        boolean succeed(T value,boolean hello){
            synchronized(lock){
                if(terminal||closed||future.isDone())return false;
                terminal=true;if(active==this)active=null;if(hello)negotiated=(ControlReply.Hello)value;
            }
            return future.complete(value);
        }
        void fail(Exception error){
            boolean unknown,notifyClose;
            synchronized(lock){
                if(terminal||future.isDone())return;
                terminal=true;if(active==this)active=null;
                boolean known=error instanceof ControlReply.BrokerError||error instanceof NativeRefusalException;
                unknown=mutation!=null&&attempted&&!known;
                notifyClose=!known&&!closed;if(notifyClose)closed=true;
            }
            try{future.completeExceptionally(unknown?new MutationOutcomeUnknownException(mutation):error);}
            finally{if(notifyClose)onClose.run();}
        }
    }
    private static <T> CompletableFuture<T> failed(Exception error){CompletableFuture<T> result=new CompletableFuture<>();result.completeExceptionally(error);return result;}
}
