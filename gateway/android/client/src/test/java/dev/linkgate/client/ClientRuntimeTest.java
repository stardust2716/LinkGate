package dev.linkgate.client;

import org.junit.Test;
import static org.junit.Assert.*;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.ScheduledFuture;

public final class ClientRuntimeTest {
    @Test public void clientLeasesAreBoundedAndIdempotent() {
        try(ClientRuntime runtime=new ClientRuntime()) {
            List<ClientRuntime.Lease> leases=new ArrayList<>();
            for(int i=0;i<8;i++) { ClientRuntime.Lease lease=runtime.acquire();assertNotNull(lease);leases.add(lease); }
            assertNull(runtime.acquire());for(ClientRuntime.Lease lease:leases){lease.close();lease.close();}
            for(int i=0;i<8;i++) { ClientRuntime.Lease lease=runtime.acquire();assertNotNull(lease);leases.set(i,lease); }
            assertNull(runtime.acquire());for(ClientRuntime.Lease lease:leases)lease.close();
        }
    }
    @Test public void blockedCallsDoNotSpawnReplacementWorkersAndTimersStillRun() throws Exception {
        CountDownLatch entered=new CountDownLatch(2), release=new CountDownLatch(1), ended=new CountDownLatch(2), timed=new CountDownLatch(1);
        try(ClientRuntime runtime=new ClientRuntime()) {
            Runnable blocked=()->{ entered.countDown();try { release.await(); }catch(InterruptedException e){Thread.currentThread().interrupt();}finally{ended.countDown();} };
            assertTrue(runtime.execute(blocked));assertTrue(runtime.execute(blocked));
            assertTrue(entered.await(3,TimeUnit.SECONDS));
            try {
                assertFalse(runtime.execute(()->fail("Replacement worker created")));
                runtime.after(20,timed::countDown);assertTrue(timed.await(3,TimeUnit.SECONDS));assertEquals(2,runtime.activeWorkers());
                assertFalse(runtime.execute(()->fail("Timeout allowed replacement")));
            } finally { release.countDown(); }
            assertTrue(ended.await(3,TimeUnit.SECONDS));
        } finally { release.countDown(); }
    }
    @Test public void canceledTimersLeaveNoQueueHistory() {
        try(ClientRuntime runtime=new ClientRuntime()) {
            for(int i=0;i<1000;i++) { ScheduledFuture<?> timer=runtime.after(60000,()->{});timer.cancel(false); }
            assertEquals(0,runtime.timerTasks());
        }
    }
}
