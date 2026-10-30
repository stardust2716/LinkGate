package dev.linkgate.authority;

import org.junit.Test;
import static org.junit.Assert.*;

public final class LeaseClockTest {
    private static final class Source implements LeaseClock.Source {long utc=1000,nano;public long utcMillis(){return utc;}public long nanos(){return nano;}}
    @Test public void authenticatedDateOnlyAdvancesTheFence()throws Exception {
        Source source=new Source();LeaseClock clock=new LeaseClock(source);assertEquals(1000,clock.read().utcMillis);
        clock.observeAuthenticatedTime(10000);assertEquals(10000,clock.read().utcMillis);
        source.nano+=500000000;assertEquals(10500,clock.read().utcMillis);
        clock.observeAuthenticatedTime(9000);assertEquals(10500,clock.read().utcMillis);
        source.utc=-100000;source.nano+=999999;assertEquals(10500,clock.read().utcMillis);source.nano++;assertEquals(10501,clock.read().utcMillis);
    }
    @Test public void observationsCannotRecoverMonotonicRollback()throws Exception {
        Source source=new Source();LeaseClock clock=new LeaseClock(source);source.nano=10;clock.read();source.nano=9;
        try{clock.observeAuthenticatedTime(10000);fail();}catch(LeaseRejectedException expected){}
        source.nano=20;try{clock.observeAuthenticatedTime(20000);fail();}catch(LeaseRejectedException expected){}
    }
    @Test public void delayedDateObservationUsesConservativeUpperTime()throws Exception {
        Source source=new Source();LeaseClock clock=new LeaseClock(source);long began=clock.read().nanos;source.nano=350000000;
        clock.observeAuthenticatedResponseDate(10000,began);assertTrue(clock.read().utcMillis>=11350);
        long original=clock.read().utcMillis;clock.observeAuthenticatedResponseDate(9000,source.nano);assertEquals(original,clock.read().utcMillis);
    }
}
