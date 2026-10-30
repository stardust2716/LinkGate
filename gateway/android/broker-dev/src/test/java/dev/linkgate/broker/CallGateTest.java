package dev.linkgate.broker;

import org.junit.Test;
import static org.junit.Assert.*;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicInteger;

public final class CallGateTest {
    @Test public void uidLimitAndIdempotentRelease() {
        CallGate gate = new CallGate(); List<CallGate.Lease> leases = new ArrayList<>();
        for(int i=0;i<8;i++) { CallGate.Lease lease=gate.acquire(10001); assertNotNull(lease); leases.add(lease); }
        assertNull(gate.acquire(10001)); assertEquals(8,gate.activeCalls());
        for(CallGate.Lease lease:leases) { lease.close(); lease.close(); }
        assertEquals(0,gate.activeCalls()); assertEquals(0,gate.activeCallers());
    }
    @Test public void globalLimitAndRecovery() {
        CallGate gate=new CallGate(); List<CallGate.Lease> leases=new ArrayList<>();
        for(int i=0;i<32;i++) leases.add(gate.acquire(10000+i));
        assertNull(gate.acquire(20000)); assertEquals(32,gate.activeCallers());
        leases.get(0).close(); CallGate.Lease recovered=gate.acquire(20000); assertNotNull(recovered);
        recovered.close(); for(CallGate.Lease lease:leases) lease.close();
        assertEquals(0,gate.activeCalls()); assertEquals(0,gate.activeCallers());
    }
    @Test public void callerChurnLeavesNoHistory() {
        CallGate gate=new CallGate();
        for(int i=0;i<10000;i++) try(CallGate.Lease lease=gate.acquire(i)) { assertNotNull(lease); }
        assertEquals(0,gate.activeCalls()); assertEquals(0,gate.activeCallers()); assertNull(gate.acquire(-1));
    }
    @Test public void concurrentAdmissionNeverExceedsBound() throws Exception {
        CallGate gate=new CallGate(); CountDownLatch start=new CountDownLatch(1), admitted=new CountDownLatch(64), release=new CountDownLatch(1);
        AtomicInteger count=new AtomicInteger(); List<Thread> threads=new ArrayList<>();
        for(int i=0;i<64;i++) { Thread thread=new Thread(()->{ try { start.await(); CallGate.Lease lease=gate.acquire(10001); if(lease!=null) count.incrementAndGet(); admitted.countDown(); release.await(); if(lease!=null) lease.close(); }catch(InterruptedException e){Thread.currentThread().interrupt();} }); threads.add(thread); thread.start(); }
        start.countDown();
        try { assertTrue(admitted.await(5,java.util.concurrent.TimeUnit.SECONDS)); assertEquals(8,count.get()); assertEquals(8,gate.activeCalls()); } finally { release.countDown(); }
        for(Thread thread:threads) { thread.join(5000); assertFalse(thread.isAlive()); }
        assertEquals(0,gate.activeCalls()); assertEquals(0,gate.activeCallers());
    }
}
