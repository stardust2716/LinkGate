package dev.linkgate.client;

import dev.linkgate.contracts.ApplicationManifest;
import dev.linkgate.contracts.ControlHello;
import dev.linkgate.contracts.ControlReply;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.RegistrationReply;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.junit.Test;
import static org.junit.Assert.*;

/** Tests the production portable engine, not Android Binder or PackageManager. */
public final class ControlSessionTest {
    private static final UUID OP=UUID.fromString("00000000-0000-4000-8000-000000000002");
    private static final String RESULT="{\"applicationRef\":\"00000000-0000-4000-8000-000000000003\",\"permissionDiff\":{\"addedExports\":[\"files\"],\"removedExports\":[],\"changedExports\":[],\"addedImports\":[],\"removedImports\":[],\"changedImports\":[],\"declarationChanged\":true},\"approvalRequired\":true}";
    private static final Pattern ID=Pattern.compile("\"requestId\":\"([^\"]+)\"");
    private static ApplicationManifest manifest(){return ManifestContract.parse(bytes("{\"manifestVersion\":1,\"applicationId\":\"test.sdk\",\"displayName\":\"SDK\",\"exports\":[{\"serviceId\":\"files\",\"label\":\"Files\",\"protocol\":\"tcp\",\"localTarget\":{\"host\":\"127.0.0.1\",\"port\":18481}}],\"imports\":[]}"));}
    private static byte[] bytes(String value){return value.getBytes(StandardCharsets.UTF_8);}
    private static void await(CountDownLatch latch)throws Exception{assertTrue("Worker did not reach barrier",latch.await(5,TimeUnit.SECONDS));}
    private static Throwable failure(CompletableFuture<?> future)throws Exception{
        try{future.get(5,TimeUnit.SECONDS);throw new AssertionError("Expected failure");}catch(ExecutionException e){return e.getCause();}
    }
    private static void unknown(OperationFuture<?> future)throws Exception{
        Throwable error=failure(future);assertTrue(error.toString(),error instanceof MutationOutcomeUnknownException);
        assertEquals(OP,((MutationOutcomeUnknownException)error).operationId);assertEquals(OP,future.operationId);
        assertTrue(future.mayHaveBeenSubmitted());assertNull(error.getCause());
    }
    private static final class Wire implements ControlSession.Transport {
        final ControlHello hello=new ControlHello();
        final AtomicInteger mutations=new AtomicInteger(),queries=new AtomicInteger(),verifications=new AtomicInteger();
        volatile boolean advertise=true,drop,malformed,wrongId;
        volatile String error;
        volatile int failVerifyAt;
        volatile CountDownLatch entered,release,verified,verifyRelease;
        @Override public void verify()throws Exception{
            int current=verifications.incrementAndGet();
            if(verified!=null){verified.countDown();verifyRelease.await(5,TimeUnit.SECONDS);}
            if(current==failVerifyAt)throw new IOException("private-marker");
        }
        @Override public byte[] request(byte[] request)throws Exception{
            String input=new String(request,StandardCharsets.UTF_8);
            if(input.contains("\"method\":\"hello\"")){
                String response=new String(hello.respond(request),StandardCharsets.UTF_8);
                if(advertise)response=response.replace("\"capabilities\":[","\"capabilities\":[{\"name\":\"app.register.pending\",\"state\":\"experimental\",\"reasonCode\":\"PENDING_ONLY\"},");
                return bytes(response);
            }
            Matcher matcher=ID.matcher(input);assertTrue(matcher.find());String id=matcher.group(1);
            boolean mutation=input.contains("\"method\":\"app.register\"");
            if(mutation){mutations.incrementAndGet();assertTrue(input.contains("\"operationId\":\""+OP+"\""));}
            else{queries.incrementAndGet();assertTrue(input.contains("\"method\":\"operation.status\""));}
            if(entered!=null){entered.countDown();release.await(5,TimeUnit.SECONDS);}
            if(drop)throw new IOException("private-marker after committed mutation");
            String envelope="{\"kind\":\"response\",\"apiVersion\":1,\"requestId\":\""+(wrongId?OP:id)+"\",";
            if(error!=null)return bytes(envelope+"\"error\":{\"code\":\""+error+"\",\"stage\":\"storage\",\"retryable\":false,\"message\":\"private-marker\",\"details\":{}}}");
            String result=malformed?RESULT.replace("\"approvalRequired\":true","\"approvalRequired\":false"):RESULT;
            if(!mutation)result="{\"operationId\":\""+OP+"\",\"status\":\"succeeded\",\"result\":"+result+"}";
            return bytes(envelope+"\"result\":"+result+"}");
        }
    }
    private static ControlSession session(ClientRuntime runtime,Wire wire,AtomicInteger closes){return new ControlSession(runtime,wire,closes::incrementAndGet);}
    private static void hello(ControlSession session)throws Exception{session.helloAsync().get(5,TimeUnit.SECONDS);}

    @Test public void registrationRequiresNegotiationCapabilityAndExplicitOptIn()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();try(ControlSession session=session(runtime,wire,new AtomicInteger())){
                OperationFuture<?> before=session.registerPendingAsync(manifest(),OP,true);assertTrue(failure(before) instanceof IllegalStateException);assertFalse(before.mayHaveBeenSubmitted());
                hello(session);assertTrue(failure(session.registerPendingAsync(manifest(),OP,false)) instanceof IllegalStateException);
                wire.advertise=false;hello(session);assertTrue(failure(session.registerPendingAsync(manifest(),OP,true)) instanceof IllegalStateException);
                assertTrue(failure(session.getOperationStatusAsync(OP)) instanceof IllegalStateException);assertEquals(0,wire.mutations.get());
            }
        }
    }
    @Test public void negotiationCanChainImmediatelyIntoRegistrationAndExplicitLookup()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();try(ControlSession session=session(runtime,wire,new AtomicInteger())){
                session.helloAsync().thenCompose(value->session.registerPendingAsync(manifest(),OP,true)).get(5,TimeUnit.SECONDS);
                RegistrationReply.Operation result=session.getOperationStatusAsync(OP).get(5,TimeUnit.SECONDS);
                assertEquals(OP,result.operationId);assertTrue(result.result.approvalRequired);assertEquals(1,wire.mutations.get());assertEquals(1,wire.queries.get());
            }
        }
    }
    @Test public void knownBrokerErrorKeepsSessionAndDoesNotReplay()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();AtomicInteger closes=new AtomicInteger();try(ControlSession session=session(runtime,wire,closes)){
                hello(session);wire.error="OPERATION_CONFLICT";Throwable error=failure(session.registerPendingAsync(manifest(),OP,true));
                assertTrue(error instanceof ControlReply.BrokerError);assertFalse(error.toString().contains("private-marker"));assertEquals(0,closes.get());
                wire.error="OPERATION_UNKNOWN";assertTrue(failure(session.getOperationStatusAsync(OP)) instanceof ControlReply.BrokerError);
                wire.error=null;assertEquals(OP,session.getOperationStatusAsync(OP).get(5,TimeUnit.SECONDS).operationId);assertEquals(1,wire.mutations.get());
            }
        }
    }
    @Test public void lostReplyRequiresNewVerifiedSessionAndExplicitOriginalIdLookup()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();wire.drop=true;
            try(ControlSession first=session(runtime,wire,new AtomicInteger())){
                hello(first);unknown(first.registerPendingAsync(manifest(),OP,true));assertTrue(failure(first.helloAsync()) instanceof IOException);
            }
            wire.drop=false;try(ControlSession recovered=session(runtime,wire,new AtomicInteger())){
                hello(recovered);assertEquals(OP,recovered.getOperationStatusAsync(OP).get(5,TimeUnit.SECONDS).operationId);
                assertEquals(1,wire.mutations.get());assertEquals(1,wire.queries.get());
            }
        }
    }
    @Test public void badOrUncorrelatedReplyAfterEntryIsUnknown()throws Exception{
        for(boolean wrongId:new boolean[]{false,true})try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();wire.wrongId=wrongId;wire.malformed=!wrongId;
            try(ControlSession session=session(runtime,wire,new AtomicInteger())){hello(session);unknown(session.registerPendingAsync(manifest(),OP,true));assertEquals(1,wire.mutations.get());}
        }
    }
    @Test public void timeoutDiscardsLateReplyAndNeverReplays()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();wire.entered=new CountDownLatch(1);wire.release=new CountDownLatch(1);
            try(ControlSession session=session(runtime,wire,new AtomicInteger())){
                hello(session);OperationFuture<?> future=session.registerPendingAsync(manifest(),OP,true);await(wire.entered);
                try{unknown(future);wire.release.countDown();assertTrue(failure(session.helloAsync()) instanceof IOException);assertEquals(1,wire.mutations.get());}
                finally{wire.release.countDown();}
            }
        }
    }
    @Test public void explicitCloseAfterEntryPreservesUnknownOperation()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();wire.entered=new CountDownLatch(1);wire.release=new CountDownLatch(1);AtomicInteger closes=new AtomicInteger();
            try(ControlSession session=session(runtime,wire,closes)){
                hello(session);OperationFuture<?> future=session.registerPendingAsync(manifest(),OP,true);await(wire.entered);
                try{session.close();unknown(future);assertEquals(1,closes.get());assertEquals(1,wire.mutations.get());}finally{wire.release.countDown();}
            }
        }
    }
    @Test public void cancellationAfterEntryIsStandardCancellationWithRecoveryMetadata()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();wire.entered=new CountDownLatch(1);wire.release=new CountDownLatch(1);
            try(ControlSession session=session(runtime,wire,new AtomicInteger())){
                hello(session);OperationFuture<?> future=session.registerPendingAsync(manifest(),OP,true);await(wire.entered);
                try{assertTrue(future.cancel(false));try{future.join();fail("Expected cancellation");}catch(CancellationException expected){}
                    assertEquals(OP,future.operationId);assertTrue(future.mayHaveBeenSubmitted());assertEquals(1,wire.mutations.get());}
                finally{wire.release.countDown();}
            }
        }
    }
    @Test public void cancellationBeforeTransportEntrySendsNoMutation()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();try(ControlSession session=session(runtime,wire,new AtomicInteger())){
                hello(session);wire.verified=new CountDownLatch(1);wire.verifyRelease=new CountDownLatch(1);
                OperationFuture<?> future=session.registerPendingAsync(manifest(),OP,true);await(wire.verified);
                try{assertTrue(future.cancel(false));assertFalse(future.mayHaveBeenSubmitted());assertEquals(0,wire.mutations.get());}
                finally{wire.verifyRelease.countDown();}
            }
        }
    }
    @Test public void concurrentRequestIsRejectedBeforeEntry()throws Exception{
        try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();wire.entered=new CountDownLatch(1);wire.release=new CountDownLatch(1);
            try(ControlSession session=session(runtime,wire,new AtomicInteger())){
                hello(session);OperationFuture<?> first=session.registerPendingAsync(manifest(),OP,true);await(wire.entered);
                try{OperationFuture<?> second=session.registerPendingAsync(manifest(),OP,true);assertEquals("RESOURCE_EXHAUSTED",failure(second).getMessage());assertFalse(second.mayHaveBeenSubmitted());
                    wire.release.countDown();first.get(5,TimeUnit.SECONDS);assertEquals(1,wire.mutations.get());}
                finally{wire.release.countDown();}
            }
        }
    }
    @Test public void peerFailureBeforeAndAfterEntryHaveDifferentOutcome()throws Exception{
        for(boolean after:new boolean[]{false,true})try(ClientRuntime runtime=new ClientRuntime()){
            Wire wire=new Wire();try(ControlSession session=session(runtime,wire,new AtomicInteger())){
                hello(session);wire.failVerifyAt=wire.verifications.get()+(after?2:1);
                OperationFuture<?> future=session.registerPendingAsync(manifest(),OP,true);
                if(after)unknown(future);else{Throwable error=failure(future);assertTrue(error instanceof IOException);assertFalse(error instanceof MutationOutcomeUnknownException);assertFalse(error.toString().contains("private-marker"));assertFalse(future.mayHaveBeenSubmitted());}
                assertEquals(after?1:0,wire.mutations.get());
            }
        }
    }
    @Test public void preEntryCancellationPermanentlyPreventsSubmissionClaim(){
        OperationFuture<?> before=new OperationFuture<>(OP);assertTrue(before.cancel(false));
        assertFalse(before.tryMarkAttempted());assertFalse(before.mayHaveBeenSubmitted());assertTrue(before.isCancelled());
        OperationFuture<?> after=new OperationFuture<>(OP);assertTrue(after.tryMarkAttempted());assertTrue(after.cancel(true));
        assertTrue(after.mayHaveBeenSubmitted());assertTrue(after.isCancelled());assertEquals(OP,after.operationId);
    }
}
