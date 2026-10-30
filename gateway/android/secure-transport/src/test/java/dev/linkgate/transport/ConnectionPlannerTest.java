package dev.linkgate.transport;

import dev.linkgate.contracts.ConnectionPolicy;
import dev.linkgate.contracts.ManifestContract;
import java.io.Closeable;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.BooleanSupplier;
import org.junit.Test;
import static org.junit.Assert.*;

public final class ConnectionPlannerTest {
    private static final class Resource implements Closeable {final AtomicInteger closes=new AtomicInteger();public void close(){closes.incrementAndGet();}}
    private static final class Harness implements Closeable {
        final ExecutorService direct=Executors.newSingleThreadExecutor(),relay=Executors.newSingleThreadExecutor(),caller=Executors.newSingleThreadExecutor();final AtomicLong now=new AtomicLong();final ConnectionPlanner<Resource> planner=new ConnectionPlanner<>(now::get,direct,relay);
        public void close(){direct.shutdownNow();relay.shutdownNow();caller.shutdownNow();}
    }
    private static ConnectionPolicy policy(boolean relay,int wait){return policy(relay,wait,"");}
    private static ConnectionPolicy policy(boolean relay,int wait,String extra){return ManifestContract.normalizePolicy(("{\"mode\":\"fast-connect\",\"connectTimeoutMs\":1000,\"directWaitMs\":"+wait+",\"allowRelay\":"+relay+extra+"}").getBytes(StandardCharsets.UTF_8));}
    private static void await(CountDownLatch latch)throws Exception{assertTrue(latch.await(3,TimeUnit.SECONDS));}
    private static void until(BooleanSupplier condition)throws Exception{long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);while(!condition.getAsBoolean()&&System.nanoTime()<deadline)Thread.sleep(1);assertTrue(condition.getAsBoolean());}
    private static void failed(Future<?> future,ConnectionPlanner.Code code)throws Exception{try{future.get(3,TimeUnit.SECONDS);fail("Unexpected transport.");}catch(java.util.concurrent.ExecutionException expected){assertTrue(expected.getCause() instanceof ConnectionPlanner.Failure);assertEquals(code,((ConnectionPlanner.Failure)expected.getCause()).code);}}
    @Test public void readyDirectWinsWithoutStartingRelayOrLaterReplay()throws Exception {
        try(Harness h=new Harness()){
            Resource resource=new Resource();AtomicInteger direct=new AtomicInteger(),relay=new AtomicInteger();
            try(var result=h.planner.connect(policy(true,100),0,()->false,context->{direct.incrementAndGet();h.now.set(TimeUnit.MILLISECONDS.toNanos(40));return resource;},context->{relay.incrementAndGet();return new Resource();})){
                assertEquals("direct",result.path);assertEquals(ConnectionPlanner.Fallback.NONE,result.fallback);assertEquals(40,result.setupMs);assertSame(resource,result.value());assertEquals(0,resource.closes.get());
            }
            assertEquals(1,resource.closes.get());assertEquals(1,direct.get());assertEquals(0,relay.get());
        }
    }
    @Test public void definiteDirectFailureFallsBackOnlyOnce()throws Exception {
        try(Harness h=new Harness()){
            AtomicInteger first=new AtomicInteger(),second=new AtomicInteger();try(var result=h.planner.connect(policy(true,100),0,()->false,context->{first.incrementAndGet();throw new IOException("private diagnostic");},context->{second.incrementAndGet();return new Resource();})){
                assertEquals("relay",result.path);assertEquals(ConnectionPlanner.Fallback.DIRECT_FAILED,result.fallback);
            }assertEquals(1,first.get());assertEquals(1,second.get());
        }
    }
    @Test public void consumedAuthorizationTimeIsNotResetForDirectOrTotalBudget()throws Exception {
        try(Harness h=new Harness()){
            AtomicInteger first=new AtomicInteger();h.now.set(TimeUnit.MILLISECONDS.toNanos(200));try(var result=h.planner.connect(policy(true,100),0,()->false,context->{first.incrementAndGet();return new Resource();},context->{assertEquals(800,context.remainingMillis());return new Resource();})){
                assertEquals("relay",result.path);assertEquals(ConnectionPlanner.Fallback.DIRECT_WINDOW_EXPIRED,result.fallback);assertEquals(200,result.setupMs);assertEquals(0,first.get());
            }
            h.now.set(TimeUnit.MILLISECONDS.toNanos(1000));try{h.planner.connect(policy(true,100),0,()->false,context->{fail();return null;},context->{fail();return null;});fail();}catch(ConnectionPlanner.Failure failure){assertEquals(ConnectionPlanner.Code.CONNECT_TIMEOUT,failure.code);}
        }
    }
    @Test public void forbiddenRelayUsesRemainingTotalForDirect()throws Exception {
        try(Harness h=new Harness()){
            AtomicInteger relay=new AtomicInteger();try(var result=h.planner.connect(policy(false,1),0,()->false,context->{assertEquals(1000,context.remainingMillis());h.now.set(TimeUnit.MILLISECONDS.toNanos(500));return new Resource();},context->{relay.incrementAndGet();return new Resource();})){
                assertEquals("direct",result.path);assertEquals(500,result.setupMs);assertEquals(0,relay.get());
            }
            try{h.planner.connect(policy(false,1),h.now.get(),()->false,context->{throw new IOException("direct unavailable");},context->{relay.incrementAndGet();return new Resource();});fail();}catch(ConnectionPlanner.Failure failure){assertEquals(ConnectionPlanner.Code.DIRECT_FAILED,failure.code);assertEquals(0,relay.get());}
        }
    }
    @Test public void terminalAuthorityAndCapacityNeverTryRelay()throws Exception {
        for(ConnectionPlanner.Code code:new ConnectionPlanner.Code[]{ConnectionPlanner.Code.AUTH_REQUIRED,ConnectionPlanner.Code.RESOURCE_EXHAUSTED,ConnectionPlanner.Code.CAPABILITY_UNSUPPORTED,ConnectionPlanner.Code.CLOCK_UNTRUSTED,ConnectionPlanner.Code.CANCELLED}){
            try(Harness h=new Harness()){
                AtomicInteger relay=new AtomicInteger();try{h.planner.connect(policy(true,100),0,()->false,context->{throw new ConnectionPlanner.Failure(code);},context->{relay.incrementAndGet();return new Resource();});fail();}catch(ConnectionPlanner.Failure failure){assertEquals(code,failure.code);assertNull(failure.getCause());assertEquals(0,relay.get());}
            }
        }
        try(Harness h=new Harness()){
            try{h.planner.connect(policy(true,100),0,()->false,context->{throw new java.security.GeneralSecurityException("private token marker");},context->{fail();return null;});fail();}catch(ConnectionPlanner.Failure failure){assertEquals(ConnectionPlanner.Code.AUTH_REQUIRED,failure.code);assertFalse(failure.getMessage().contains("private token"));}
        }
        try(Harness h=new Harness()){
            try{h.planner.connect(policy(true,100),0,()->false,context->{throw new SecurityException("private authority marker");},context->{fail();return null;});fail();}catch(ConnectionPlanner.Failure failure){assertEquals(ConnectionPlanner.Code.AUTH_REQUIRED,failure.code);assertFalse(failure.getMessage().contains("private authority"));}
        }
    }
    @Test public void directDeadlineCancelsPartialWorkAndClosesLateResult()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);Resource partial=new Resource(),relay=new Resource();
        try(Harness h=new Harness()){
            var pending=h.caller.submit(()->h.planner.connect(policy(true,10),0,()->false,context->{context.track(partial);entered.countDown();await(release);return partial;},context->relay));await(entered);h.now.set(TimeUnit.MILLISECONDS.toNanos(10));
            try(var result=pending.get(3,TimeUnit.SECONDS)){
                assertEquals("relay",result.path);assertEquals(ConnectionPlanner.Fallback.DIRECT_WINDOW_EXPIRED,result.fallback);assertTrue(partial.closes.get()>=1);assertEquals(0,relay.closes.get());release.countDown();until(()->partial.closes.get()>=2);assertSame(relay,result.value());
            }assertEquals(1,relay.closes.get());
        }finally{release.countDown();}
    }
    @Test public void parentCancellationStopsPendingAttemptWithoutRelay()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);AtomicBoolean cancelled=new AtomicBoolean();Resource partial=new Resource();AtomicInteger relay=new AtomicInteger();
        try(Harness h=new Harness()){
            var pending=h.caller.submit(()->h.planner.connect(policy(true,100),0,cancelled::get,context->{context.track(partial);entered.countDown();await(release);return partial;},context->{relay.incrementAndGet();return new Resource();}));await(entered);cancelled.set(true);failed(pending,ConnectionPlanner.Code.CANCELLED);assertTrue(partial.closes.get()>=1);assertEquals(0,relay.get());
        }finally{release.countDown();}
    }
    @Test public void relayTimeoutCancelsItsOwnedPartialTransport()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);Resource partial=new Resource();
        try(Harness h=new Harness()){
            var pending=h.caller.submit(()->h.planner.connect(policy(true,0),0,()->false,context->{fail();return null;},context->{context.track(partial);entered.countDown();await(release);return partial;}));await(entered);h.now.set(TimeUnit.SECONDS.toNanos(1));failed(pending,ConnectionPlanner.Code.CONNECT_TIMEOUT);assertTrue(partial.closes.get()>=1);
        }finally{release.countDown();}
    }
    @Test public void rejectedExecutorAndUnsupportedFeaturesFailBeforeNetworking()throws Exception {
        ConnectionPlanner<Resource> rejected=new ConnectionPlanner<>(()->0,work->{throw new java.util.concurrent.RejectedExecutionException();},Runnable::run);
        try{rejected.connect(policy(true,100),0,()->false,context->{fail();return null;},context->{fail();return null;});fail();}catch(ConnectionPlanner.Failure failure){assertEquals(ConnectionPlanner.Code.RESOURCE_EXHAUSTED,failure.code);}
        try(Harness h=new Harness()){
            for(String extra:new String[]{",\"prewarm\":true",",\"relayBudgetBytes\":\"100\",\"relayBudgetAction\":\"stop-relay\"",",\"relayWarningBytes\":\"100\""}){
                try{h.planner.connect(policy(true,100,extra),0,()->false,context->{fail();return null;},context->{fail();return null;});fail();}catch(ConnectionPlanner.Failure failure){assertEquals(ConnectionPlanner.Code.CAPABILITY_UNSUPPORTED,failure.code);}
            }
        }
    }
    @Test public void clockRollbackIsTerminalAndLateCompletionCannotReviveIt()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);Resource resource=new Resource();AtomicInteger relay=new AtomicInteger();
        try(Harness h=new Harness()){
            h.now.set(TimeUnit.MILLISECONDS.toNanos(100));var pending=h.caller.submit(()->h.planner.connect(policy(true,100),h.now.get(),()->false,context->{context.track(resource);entered.countDown();await(release);return resource;},context->{relay.incrementAndGet();return new Resource();}));await(entered);h.now.set(TimeUnit.MILLISECONDS.toNanos(99));failed(pending,ConnectionPlanner.Code.CLOCK_UNTRUSTED);h.now.set(TimeUnit.MILLISECONDS.toNanos(200));release.countDown();until(()->resource.closes.get()>=2);assertEquals(0,relay.get());
        }finally{release.countDown();}
    }
}
