package dev.linkgate.agent;

import dev.linkgate.contracts.ConnectionStatus;
import dev.linkgate.contracts.ContractException;
import dev.linkgate.contracts.WireJson;
import java.math.BigInteger;
import java.util.UUID;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;
import org.junit.Test;
import static org.junit.Assert.*;

public class ConnectionMeterTest {
    private final AtomicLong now=new AtomicLong();
    private ConnectionMeter meter(){return new ConnectionMeter(UUID.randomUUID().toString(),"1".repeat(64),"2".repeat(64),"org.example.publisher","files",UUID.randomUUID().toString(),"1",1000000,0,now::get);}
    @Test public void selectionIsNotBusinessAndCleanFinCountsAreExact(){ConnectionMeter meter=meter();assertEquals("none",meter.snapshot().status.selectedPath);now.set(TimeUnit.MILLISECONDS.toNanos(25));meter.ready("direct",25);ConnectionStatus ready=meter.snapshot().status;assertEquals("attaching",ready.flowState);assertEquals("direct",ready.selectedPath);assertEquals("none",ready.businessPath);meter.attached();meter.bytes(true,123);meter.bytes(false,456);meter.fin(true);meter.fin(false);meter.end(true,null);ConnectionStatus done=meter.snapshot().status;assertEquals("closed",done.flowState);assertTrue(done.complete);assertEquals(new BigInteger("123"),done.directTx);assertEquals(new BigInteger("456"),done.directRx);assertEquals(BigInteger.ZERO,done.relayTx);assertEquals("direct",done.businessPath);assertNull(done.errorCode);}
    @Test public void incompleteFinAndLateEventsCannotBecomeComplete(){ConnectionMeter meter=meter();meter.ready("relay",0);meter.attached();meter.bytes(true,10);meter.fin(true);meter.end(true,null);ConnectionMeter.Snapshot closed=meter.snapshot();assertFalse(closed.status.complete);assertFalse(meter.bytes(false,90));meter.ready("direct",0);meter.attached();meter.end(true,null);assertEquals(closed.status.toJson(),meter.snapshot().status.toJson());assertEquals(closed.sequence,meter.snapshot().sequence);}
    @Test public void interruptedRelayRetainsPathAndObservedBytes(){ConnectionMeter meter=meter();meter.ready("relay",0);meter.attached();meter.bytes(false,7);meter.end(false,"NETWORK_CHANGED");ConnectionStatus closed=meter.snapshot().status;assertEquals("failed",closed.flowState);assertFalse(closed.complete);assertEquals("relay",closed.selectedPath);assertEquals(new BigInteger("7"),closed.relayRx);assertEquals("NETWORK_CHANGED",closed.errorCode);}
    @Test public void uint64OverflowFailsWithoutWrappingOrFabricatingBytes(){ConnectionMeter meter=meter();meter.ready("direct",0);meter.attached();assertTrue(meter.bytes(true,Long.MAX_VALUE));assertTrue(meter.bytes(true,Long.MAX_VALUE));assertFalse(meter.bytes(true,2));ConnectionStatus status=meter.snapshot().status;assertEquals("18446744073709551614",status.directTx.toString());assertEquals("COUNTER_OVERFLOW",status.errorCode);assertFalse(status.complete);}
    @Test public void clockRollbackKeepsOriginalUtcAndFailsClosed(){ConnectionMeter meter=meter();now.set(TimeUnit.MILLISECONDS.toNanos(40));meter.ready("direct",40);now.set(TimeUnit.MILLISECONDS.toNanos(30));assertFalse(meter.clockValid());meter.end(false,"DIRECT_FAILED");ConnectionStatus closed=meter.snapshot().status;assertEquals("CLOCK_UNTRUSTED",closed.errorCode);assertEquals(closed.readyUtc,closed.closedUtc);}
    @Test public void publicStateRejectsCrossPathOrImpossibleCombinations(){ConnectionMeter meter=meter();meter.ready("direct",0);var value=meter.snapshot().status.wire();value.put("relayTx","1");invalid(value);value=meter.snapshot().status.wire();value.put("complete",true);invalid(value);value=meter.snapshot().status.wire();value.put("networkGeneration","01");invalid(value);value=meter.snapshot().status.wire();value.put("directTx","18446744073709551616");invalid(value);value=meter.snapshot().status.wire();value.put("privateRoute","SIMULATED_PRIVATE_VALUE");invalid(value);}
    private static void invalid(Object value){try{ConnectionStatus.parse(WireJson.write(value,16384));fail("Invalid public state accepted.");}catch(ContractException expected){assertNull(expected.getCause());assertFalse(expected.toString().contains("SIMULATED_PRIVATE_VALUE"));}}
}
