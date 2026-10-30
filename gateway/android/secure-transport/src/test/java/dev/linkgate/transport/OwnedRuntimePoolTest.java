package dev.linkgate.transport;

import java.io.Closeable;
import java.io.IOException;
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

public final class OwnedRuntimePoolTest {
    private static final class Resource implements Closeable {final AtomicInteger closes=new AtomicInteger();volatile boolean failClose;public void close()throws IOException{boolean failed=failClose;closes.incrementAndGet();if(failed)throw new IOException("fixture close unavailable");}}
    private static final class Harness implements Closeable {
        final ExecutorService creation=Executors.newFixedThreadPool(2),waiters=Executors.newFixedThreadPool(3);final AtomicLong now=new AtomicLong();final OwnedRuntimePool<String,Resource> pool;
        Harness(int capacity){pool=new OwnedRuntimePool<>(capacity,60000,now::get,creation);}
        Future<OwnedRuntimePool<String,Resource>.Lease> open(String key,OwnedRuntimePool.Factory<Resource> factory,BooleanSupplier cancelled){return waiters.submit(()->pool.acquire(key,factory,TimeUnit.SECONDS.toNanos(15),cancelled));}
        public void close(){pool.close();creation.shutdownNow();waiters.shutdownNow();}
    }
    private static void await(CountDownLatch value)throws Exception{assertTrue(value.await(3,TimeUnit.SECONDS));}
    private static void until(BooleanSupplier value)throws Exception{long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);while(!value.getAsBoolean()&&System.nanoTime()<deadline)Thread.sleep(1);assertTrue(value.getAsBoolean());}
    private static void failed(Future<?> value)throws Exception{try{value.get(3,TimeUnit.SECONDS);fail("Acquisition succeeded.");}catch(java.util.concurrent.ExecutionException expected){assertTrue(expected.getCause() instanceof IOException);}}
    @Test public void coalescesAndKeepsAnotherFlowAliveUntilIdleExpiry()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);Resource resource=new Resource();AtomicInteger starts=new AtomicInteger();
        try(Harness h=new Harness(1)){
            OwnedRuntimePool.Factory<Resource> factory=cancelled->{starts.incrementAndGet();entered.countDown();await(release);return resource;};
            var first=h.open("same-scope",factory,()->false);await(entered);var second=h.open("same-scope",factory,()->false);until(()->h.pool.leaseCount()==2);release.countDown();var a=first.get(3,TimeUnit.SECONDS);var b=second.get(3,TimeUnit.SECONDS);assertSame(a.value(),b.value());assertEquals(1,starts.get());
            a.close();h.pool.maintain(key->true,value->true);assertSame(resource,b.value());assertEquals(0,resource.closes.get());b.close();h.now.set(TimeUnit.SECONDS.toNanos(59));h.pool.maintain(key->true,value->true);assertEquals(0,resource.closes.get());h.now.set(TimeUnit.SECONDS.toNanos(60));h.pool.maintain(key->true,value->true);assertEquals(1,resource.closes.get());assertEquals(0,h.pool.size());
        }finally{release.countDown();}
    }
    @Test public void cancellingFirstWaiterDoesNotCancelSharedCreation()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);AtomicBoolean cancelled=new AtomicBoolean();Resource resource=new Resource();
        try(Harness h=new Harness(1)){
            var first=h.open("scope",stop->{entered.countDown();await(release);assertFalse(stop.getAsBoolean());return resource;},cancelled::get);await(entered);
            var second=h.open("scope",stop->{fail("Duplicated factory.");return null;},()->false);until(()->h.pool.leaseCount()==2);cancelled.set(true);failed(first);assertEquals(1,h.pool.size());release.countDown();try(var lease=second.get(3,TimeUnit.SECONDS)){assertSame(resource,lease.value());assertEquals(0,resource.closes.get());}
        }finally{release.countDown();}
    }
    @Test public void closeDuringCreationDisposesLateResourceAndKeepsItsCapacityReserved()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);Resource resource=new Resource();
        try(Harness h=new Harness(1)){
            var first=h.open("scope",stop->{entered.countDown();await(release);assertTrue(stop.getAsBoolean());return resource;},()->false);await(entered);h.pool.close();failed(first);assertEquals(1,h.pool.size());assertEquals(0,resource.closes.get());release.countDown();until(()->h.pool.size()==0);assertEquals(1,resource.closes.get());
        }finally{release.countDown();}
    }
    @Test public void finalWaiterCancellationCannotFreeCapacityBeforeLateCleanup()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);AtomicBoolean cancelled=new AtomicBoolean();Resource resource=new Resource();
        try(Harness h=new Harness(1)){
            var first=h.open("scope",stop->{entered.countDown();await(release);return resource;},cancelled::get);await(entered);cancelled.set(true);failed(first);failed(h.open("other",stop->{fail("Capacity reused before cleanup.");return null;},()->false));assertEquals(1,h.pool.size());release.countDown();until(()->h.pool.size()==0);assertEquals(1,resource.closes.get());try(var lease=h.open("other",stop->new Resource(),()->false).get(3,TimeUnit.SECONDS)){assertNotNull(lease.value());}
        }finally{release.countDown();}
    }
    @Test public void scopeRevocationDoesNotCloseUnrelatedRuntime()throws Exception {
        try(Harness h=new Harness(2)){
            Resource first=new Resource(),second=new Resource();try(var a=h.open("revoked",stop->first,()->false).get(3,TimeUnit.SECONDS);var b=h.open("other",stop->second,()->false).get(3,TimeUnit.SECONDS)){
                h.pool.maintain(key->!key.equals("revoked"),value->true);assertEquals(1,first.closes.get());assertEquals(0,second.closes.get());assertSame(second,b.value());try{a.value();fail();}catch(IOException expected){}
            }
        }
    }
    @Test public void failedCreationAndRejectedExecutorDoNotLeakSlots()throws Exception {
        try(Harness h=new Harness(1)){
            failed(h.open("failed",stop->{throw new IOException("fixture factory failure");},()->false));until(()->h.pool.size()==0);try(var lease=h.open("next",stop->new Resource(),()->false).get(3,TimeUnit.SECONDS)){assertNotNull(lease.value());}
        }
        try(OwnedRuntimePool<String,Resource> pool=new OwnedRuntimePool<>(1,60000,System::nanoTime,work->{throw new java.util.concurrent.RejectedExecutionException();})){
            try{pool.acquire("scope",stop->new Resource(),System.nanoTime()+TimeUnit.SECONDS.toNanos(2),()->false);fail();}catch(IOException expected){assertEquals(0,pool.size());}
        }
    }
    @Test public void failedCloseRetainsCapacityUntilAConfirmedCleanup()throws Exception {
        try(Harness h=new Harness(1)){
            Resource resource=new Resource();resource.failClose=true;var lease=h.open("scope",stop->resource,()->false).get(3,TimeUnit.SECONDS);h.pool.maintain(key->false,value->true);lease.close();assertEquals(1,h.pool.size());failed(h.open("other",stop->{fail("Failed close freed capacity.");return null;},()->false));resource.failClose=false;h.pool.maintain(key->true,value->true);assertEquals(0,h.pool.size());assertEquals(2,resource.closes.get());
        }
    }
    @Test public void blockedAuthorityCheckDoesNotBlockShutdownAndClockRollbackClosesAll()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        try(Harness h=new Harness(1)){
            Resource resource=new Resource();var lease=h.open("scope",stop->resource,()->false).get(3,TimeUnit.SECONDS);var blocked=h.waiters.submit(()->h.pool.maintain(key->{entered.countDown();try{await(release);}catch(Exception failure){throw new IllegalStateException();}return true;},value->true));await(entered);h.pool.close();assertEquals(1,resource.closes.get());release.countDown();blocked.get(3,TimeUnit.SECONDS);lease.close();
        }finally{release.countDown();}
        try(Harness h=new Harness(1)){
            Resource resource=new Resource();var lease=h.open("scope",stop->resource,()->false).get(3,TimeUnit.SECONDS);h.now.set(1);h.pool.maintain(key->true,value->true);h.now.set(0);h.pool.maintain(key->true,value->true);assertEquals(1,resource.closes.get());lease.close();
        }
    }
    @Test public void failedInitializationRetainsEarlyRegisteredResourceUntilCleanup()throws Exception {
        try(Harness h=new Harness(1)){
            Resource resource=new Resource();resource.failClose=true;failed(h.open("scope",creation->{creation.own(resource);throw new IOException("Initialization failed after resource creation.");},()->false));until(()->resource.closes.get()==1);assertEquals(1,h.pool.size());resource.failClose=false;until(()->{h.pool.maintain(key->true,value->true);return h.pool.size()==0;});assertEquals(2,resource.closes.get());
        }
    }

    @Test public void mismatchedFactoryReturnCannotLeakEitherOwnedResource()throws Exception {
        try(Harness h=new Harness(1)){
            Resource registered=new Resource(),unexpected=new Resource();failed(h.open("scope",creation->{creation.own(registered);return unexpected;},()->false));until(()->h.pool.size()==0);assertEquals(1,registered.closes.get());assertEquals(1,unexpected.closes.get());
        }
    }

    @Test public void deadlineEndsWaitWithoutWaitingForUncooperativeCreation()throws Exception {
        CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);ExecutorService creation=Executors.newSingleThreadExecutor();Resource resource=new Resource();
        try(OwnedRuntimePool<String,Resource> pool=new OwnedRuntimePool<>(1,60000,System::nanoTime,creation)){
            long began=System.nanoTime();try{pool.acquire("scope",stop->{entered.countDown();await(release);return resource;},began+TimeUnit.MILLISECONDS.toNanos(100),()->false);fail();}catch(IOException expected){assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-began)<1000);await(entered);assertEquals(0,pool.leaseCount());assertEquals(1,pool.size());}
            release.countDown();until(()->pool.size()==0);assertEquals(1,resource.closes.get());
        }finally{release.countDown();creation.shutdownNow();}
    }

}
