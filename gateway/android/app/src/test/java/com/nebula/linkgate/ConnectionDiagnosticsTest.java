package com.nebula.linkgate;
import org.junit.Test;
import static org.junit.Assert.*;
public class ConnectionDiagnosticsTest {
    @Test public void stunReadinessIsSeparateFromBusinessAndStrictlyValidated() throws Exception {
        ConnectionDiagnostics d=new ConnectionDiagnostics();
        String valid="LINKGATE_STUN {\"v\":1,\"status\":\"ready\",\"observations\":2,\"attemptedEndpoints\":2,\"fallbackUsed\":true}";
        assertTrue(d.observeStun(valid));assertEquals(2,d.snapshot().getInt("stunObservations"));
        assertFalse(d.snapshot().getBoolean("natHoleObserved"));assertFalse(d.snapshot().getBoolean("businessDirectObserved"));
        for(String bad:new String[]{valid.replace("\"observations\":2","\"observations\":1"),valid.replace("\"v\":1","\"v\":1,\"v\":1"),valid.replace("\"attemptedEndpoints\":2","\"attemptedEndpoints\":-1"),valid.replace("ready","unknown"),valid.replace("true","\"true\"")}) assertFalse(d.observeStun(bad));
        assertTrue(d.observeStun(valid.replace("ready","unreachable").replace("\"observations\":2","\"observations\":0")));assertEquals("stun_unreachable",d.snapshot().getString("natPreparationFailure"));
        assertTrue(d.observeStun(valid.replace("ready","insufficient").replace("\"observations\":2","\"observations\":1")));assertEquals("stun_insufficient_observations",d.snapshot().getString("natPreparationFailure"));
        assertTrue(d.observeStun(valid));assertEquals("none",d.snapshot().getString("natPreparationFailure"));
    }
    private String metric(int id,String path,String phase,int tx,int rx,int elapsed,String reason){
        return "LINKGATE_METRIC {\"v\":1,\"id\":"+id+",\"role\":\"visitor\",\"path\":\""+path+"\",\"phase\":\""+phase+"\",\"tx\":"+tx+",\"rx\":"+rx+",\"setupMs\":5,\"elapsedMs\":"+elapsed+",\"reason\":\""+reason+"\"}";
    }
    @Test public void countsCumulativeDataAndSeparatesNat() throws Exception {
        ConnectionDiagnostics d=new ConnectionDiagnostics(); d.natHoleObserved();
        assertTrue(d.snapshot().getBoolean("natHoleObserved"));assertFalse(d.snapshot().getBoolean("businessDirectObserved"));
        assertTrue(d.observe(metric(1,"direct","open",0,0,5,"")));assertFalse(d.snapshot().getBoolean("businessDirectObserved"));
        String sample=metric(1,"direct","open",100,200,10,"");assertTrue(d.observe(sample));assertTrue(d.observe(sample));
        assertEquals(200,d.snapshot().getLong("directRx"));assertTrue(d.snapshot().getBoolean("businessDirectObserved"));
        assertTrue(d.observe(metric(2,"relay","open",30,40,30,"direct_tunnel_unavailable")));
        assertEquals("direct+relay",d.snapshot().getString("currentPath"));
        assertTrue(d.observe(metric(1,"direct","closed",100,210,40,"")));
        assertFalse(d.observe(metric(1,"direct","open",101,211,50,"")));
        d.endActive();assertEquals("idle",d.snapshot().getString("currentPath"));assertEquals(210,d.snapshot().getLong("directRx"));
    }
    @Test public void rejectsMalformedAndStaleCounters(){
        ConnectionDiagnostics d=new ConnectionDiagnostics();
        assertFalse(d.observe(metric(1,"unknown","open",0,0,10,"")));
        assertFalse(d.observe(metric(1,"relay","open",-1,0,10,"")));
        assertTrue(d.observe(metric(1,"relay","open",5,6,10,"")));
        assertFalse(d.observe(metric(1,"relay","open",4,6,20,"")));
        assertFalse(d.observe(metric(2,"relay","open",5,6,10,"").replace("\"v\":1","\"v\":1,\"v\":1")));
        assertFalse(d.observe(metric(2,"relay","open",5,6,10,"").replace("\"tx\":5","\"tx\":5.5")));
        assertTrue(d.observe(metric(3,"unknown","failed",0,0,20,"connection_setup_failed")));
    }
}
