package com.nebula.linkgate;

import org.junit.Test;
import static org.junit.Assert.assertEquals;

public class FrpLogObservationTest {
    @Test public void recognizesPreparationFailureWithoutDeclaringDirectness() {
        assertEquals(FrpLogObservation.Event.STUN_OBSERVATIONS_INSUFFICIENT,
            FrpLogObservation.classify("[W] nathole prepare error: discover error: not enough addresses"));
        ConnectionDiagnostics d=new ConnectionDiagnostics();d.natPreparationFailed();
        assertEquals("stun_insufficient_observations",d.snapshot().optString("natPreparationFailure"));
        assertEquals(false,d.snapshot().optBoolean("businessDirectObserved"));
        d.natHoleObserved();assertEquals("none",d.snapshot().optString("natPreparationFailure"));
    }
    @Test public void recognizesActualLoginAndHistoricalNatHoleInfo() {
        assertEquals(FrpLogObservation.Event.COORDINATOR_LOGIN,
                FrpLogObservation.classify("[I] [client/service.go:295] login to server success, get run id [redacted]"));
        assertEquals(FrpLogObservation.Event.NAT_HOLE_OBSERVED,
                FrpLogObservation.classify("[I] [xtcp.go:90] establishing nat hole connection successful"));
    }
    @Test public void fallbackNamesErrorsAndTransferAttemptsRemainUnknown() {
        for (String line : new String[]{
                "[I] [visitor/visitor.go] [aabbccddeeff-demo-fallback] start visitor success",
                "[D] try to transfer connection to visitor: aabbccddeeff-demo-fallback",
                "[W] open tunnel error: timeout; fallback to aabbccddeeff-demo-fallback",
                "[E] start fallback failed",
                "[I] nat hole make hole success",
                "[I] establish quic connection success",
                "[I] start visitor success"
        }) assertEquals(FrpLogObservation.Event.NONE, FrpLogObservation.classify(line));
    }
}
