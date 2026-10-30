package dev.linkgate.client;

import dev.linkgate.contracts.WireJson;
import java.io.IOException;
import java.util.Arrays;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;
import static org.junit.Assert.*;

public class SecureSessionTest {
    @Test public void knownNativeRefusalKeepsVerifiedSessionUsable()throws Exception {
        try(ClientRuntime runtime=new ClientRuntime()) {
            AtomicInteger closed=new AtomicInteger();ControlSession session=new ControlSession(runtime,transport(true,new AtomicInteger()),closed::incrementAndGet);session.helloAsync().get(3,TimeUnit.SECONDS);
            try{session.nativeAsync(UUID.randomUUID(),true,()->{throw new NativeRefusalException(1);},value->{}).get(3,TimeUnit.SECONDS);fail();}catch(java.util.concurrent.ExecutionException expected){assertTrue(expected.getCause() instanceof NativeRefusalException);}
            assertEquals(0,closed.get());assertEquals("usable",session.nativeAsync(UUID.randomUUID(),true,()->"usable",value->{}).get(3,TimeUnit.SECONDS));session.close();
        }
    }
    private static ControlSession.Transport transport(boolean supported,AtomicInteger checks){return new ControlSession.Transport(){
        @Override public void verify()throws IOException{if(checks.incrementAndGet()<0)throw new IOException("simulated private diagnostic");}
        @Override public byte[] request(byte[] input){Map<String,Object> request=WireJson.read(input,65536);return WireJson.write(WireJson.map("kind","response","apiVersion",1,"requestId",request.get("requestId"),"result",WireJson.map("apiVersion",1,"agentVersion","fixture","bootId",UUID.randomUUID().toString(),"capabilities",Arrays.asList(WireJson.map("name","secure.stream.direct","state",supported?"experimental":"unsupported","reasonCode","fixture")),"limits",WireJson.map("controlFrameBytes",65536,"controlRequestsPerCaller",8,"controlGlobalRequests",32,"identityPackageLimit",1))),65536);}
    };}
    @Test public void nativeEntryRequiresCapabilityAndExplicitOptIn()throws Exception {
        try(ClientRuntime runtime=new ClientRuntime()){
            ControlSession session=new ControlSession(runtime,transport(false,new AtomicInteger()),()->{});session.helloAsync().get(3,TimeUnit.SECONDS);AtomicInteger entered=new AtomicInteger();
            try{session.nativeAsync(UUID.randomUUID(),true,()->{entered.incrementAndGet();return new Object();},value->{}).get();fail();}catch(java.util.concurrent.ExecutionException expected){assertEquals(0,entered.get());}
            session.close();
        }
    }
    @Test public void cancelledNativeAttachmentDisposesLateResult()throws Exception {
        try(ClientRuntime runtime=new ClientRuntime()){
            ControlSession session=new ControlSession(runtime,transport(true,new AtomicInteger()),()->{});session.helloAsync().get(3,TimeUnit.SECONDS);CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1),discarded=new CountDownLatch(1);
            UUID operation=UUID.randomUUID();OperationFuture<Object> future=session.nativeAsync(operation,true,()->{entered.countDown();release.await(3,TimeUnit.SECONDS);return new Object();},value->discarded.countDown());
            assertTrue(entered.await(1,TimeUnit.SECONDS));assertTrue(future.cancel(false));assertTrue(future.mayHaveBeenSubmitted());assertEquals(operation,future.operationId);release.countDown();assertTrue(discarded.await(1,TimeUnit.SECONDS));session.close();
        }
    }
    @Test public void failedPostCallPinClosesResourceAndRetainsOriginalOperation()throws Exception {
        try(ClientRuntime runtime=new ClientRuntime()){
            AtomicInteger checks=new AtomicInteger();ControlSession session=new ControlSession(runtime,transport(true,checks),()->{});session.helloAsync().get(3,TimeUnit.SECONDS);CountDownLatch discarded=new CountDownLatch(1);UUID operation=UUID.randomUUID();
            OperationFuture<Object> future=session.nativeAsync(operation,true,()->{checks.set(-10);return new Object();},value->discarded.countDown());
            try{future.get(3,TimeUnit.SECONDS);fail();}catch(java.util.concurrent.ExecutionException expected){assertTrue(expected.getCause() instanceof MutationOutcomeUnknownException);assertEquals(operation,((MutationOutcomeUnknownException)expected.getCause()).operationId);}
            assertTrue(discarded.await(1,TimeUnit.SECONDS));session.close();
        }
    }
    private static final class RelayWire implements ControlSession.Transport {
        boolean advertise;String state="ready",path="relay";int opens;
        public void verify(){}
        public byte[] request(byte[] input){
            Map<String,Object> request=WireJson.read(input,65536);String method=(String)request.get("method");Object result;
            if(method.equals("hello")){
                java.util.List<Object> capabilities=new java.util.ArrayList<>();capabilities.add(WireJson.map("name","secure.stream.direct","state","experimental","reasonCode","fixture"));if(advertise)capabilities.add(WireJson.map("name","secure.stream.relay","state","experimental","reasonCode","fixture"));
                result=WireJson.map("apiVersion",1,"agentVersion","fixture","bootId",UUID.randomUUID().toString(),"capabilities",capabilities,"limits",WireJson.map("controlFrameBytes",65536,"controlRequestsPerCaller",8,"controlGlobalRequests",32,"identityPackageLimit",1));
            }else{
                UUID operation;if(method.endsWith("open")){opens++;operation=UUID.fromString((String)request.get("operationId"));assertEquals("secure.relay.open",method);}else operation=UUID.fromString((String)WireJson.object(request.get("params")).get("operationId"));
                result=WireJson.map("connectionId","00000000-0000-4000-8000-000000000001","operationId",operation.toString(),"state",state,"error",state.equals("closed")?"RELAY_FAILED":null,"dataPipe",null,"path",path);
            }
            return WireJson.write(WireJson.map("kind","response","apiVersion",1,"requestId",request.get("requestId"),"result",result),65536);
        }
    }
    private static dev.linkgate.contracts.SecureReply.Scope relayScope(){return new dev.linkgate.contracts.SecureReply.Scope(UUID.randomUUID(),"files",String.join("",java.util.Collections.nCopies(64,"1")),"org.example.publisher","files");}
    @Test public void relayNeedsItsOwnNegotiationAndExplicitOptIn()throws Exception {
        try(ClientRuntime runtime=new ClientRuntime()) {RelayWire wire=new RelayWire();try(ControlSession session=new ControlSession(runtime,wire,()->{})){
            session.helloAsync().get(3,TimeUnit.SECONDS);
            try{session.openRelayAsync(relayScope(),UUID.randomUUID(),true).get();fail();}catch(java.util.concurrent.ExecutionException expected){assertEquals(0,wire.opens);}
            wire.advertise=true;session.helloAsync().get(3,TimeUnit.SECONDS);
            try{session.openRelayAsync(relayScope(),UUID.randomUUID(),false).get();fail();}catch(java.util.concurrent.ExecutionException expected){assertEquals(0,wire.opens);}
            var opened=session.openRelayAsync(relayScope(),UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);assertEquals("relay",opened.path);assertEquals(1,wire.opens);
        }}
    }
    @Test public void unnegotiatedLiveRelayIsRejectedButRetainedClosedRelayIsQueryable()throws Exception {
        try(ClientRuntime runtime=new ClientRuntime()) {RelayWire wire=new RelayWire();UUID operation=UUID.randomUUID();try(ControlSession session=new ControlSession(runtime,wire,()->{})){
            session.helloAsync().get(3,TimeUnit.SECONDS);wire.state="closed";assertEquals("relay",session.secureStatusAsync(operation,false).get(3,TimeUnit.SECONDS).path);
            wire.state="ready";try{session.secureStatusAsync(operation,false).get(3,TimeUnit.SECONDS);fail();}catch(java.util.concurrent.ExecutionException expected){assertTrue(expected.getCause() instanceof java.io.IOException);}
        }}
    }

}
