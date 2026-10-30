package dev.linkgate.client;

import org.junit.Test;
import static org.junit.Assert.*;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;

public final class PeerPinTest {
    private static final String A=repeat('a'), B=repeat('b');
    private static String repeat(char c) { char[] text=new char[64];Arrays.fill(text,c);return new String(text); }
    private static PeerPin.Snapshot snapshot(String pkg,String cls,int uid,long installed,long updated,List<String> signers,boolean enabled,boolean exported,boolean shared) {
        return new PeerPin.Snapshot(pkg,cls,uid,installed,updated,signers,enabled,exported,shared);
    }
    private static PeerPin.Snapshot good() { return snapshot("test.broker","test.broker.Service",10001,10,20,Arrays.asList(A),true,true,false); }
    private static PeerPin pin() { return new PeerPin("test.broker","test.broker.Service",Arrays.asList(A)); }
    private static void denied(PeerPin.Snapshot value) {
        try { pin().verify(value); fail("Identity accepted"); }
        catch(SecurityException e) { assertEquals("Broker identity mismatch.",e.getMessage());assertNull(e.getCause()); }
    }
    @Test public void exactCurrentSignerAndCanonicalComponent() { pin().verify(good()); new PeerPin("test.broker","test.broker.Service",Arrays.asList(A.toUpperCase(java.util.Locale.ROOT))).verify(good()); }
    @Test public void signerSubsetsSupersetsAndRotationAreRejected() {
        denied(snapshot("test.broker","test.broker.Service",10001,10,20,Arrays.asList(B),true,true,false));
        denied(snapshot("test.broker","test.broker.Service",10001,10,20,Arrays.asList(A,B),true,true,false));
        PeerPin multi=new PeerPin("test.broker","test.broker.Service",Arrays.asList(A,B));
        try { multi.verify(good()); fail("Signer subset accepted"); } catch(SecurityException expected) { }
        multi.verify(snapshot("test.broker","test.broker.Service",10001,10,20,Arrays.asList(B,A),true,true,false));
    }
    @Test public void wrongComponentDisabledAndSharedIdentityFailClosed() {
        denied(snapshot("wrong.broker","test.broker.Service",10001,10,20,Arrays.asList(A),true,true,false));
        denied(snapshot("test.broker","wrong.Service",10001,10,20,Arrays.asList(A),true,true,false));
        denied(snapshot("test.broker","test.broker.Service",-1,10,20,Arrays.asList(A),true,true,false));
        denied(snapshot("test.broker","test.broker.Service",10001,0,20,Arrays.asList(A),true,true,false));
        denied(snapshot("test.broker","test.broker.Service",10001,10,20,Arrays.asList(A),false,true,false));
        denied(snapshot("test.broker","test.broker.Service",10001,10,20,Arrays.asList(A),true,false,false));
        denied(snapshot("test.broker","test.broker.Service",10001,10,20,Arrays.asList(A),true,true,true));
    }
    @Test public void installUpdateAndUidChangesInvalidateBinding() {
        assertTrue(PeerPin.same(good(),good()));
        assertFalse(PeerPin.same(good(),snapshot("test.broker","test.broker.Service",10002,10,20,Arrays.asList(A),true,true,false)));
        assertFalse(PeerPin.same(good(),snapshot("test.broker","test.broker.Service",10001,11,20,Arrays.asList(A),true,true,false)));
        assertFalse(PeerPin.same(good(),snapshot("test.broker","test.broker.Service",10001,10,21,Arrays.asList(A),true,true,false)));
    }
    @Test public void pinsAndSnapshotsCopyInputCollections() {
        List<String> values=new ArrayList<>(Arrays.asList(A)); PeerPin pin=new PeerPin("test.broker","test.broker.Service",values);
        PeerPin.Snapshot value=snapshot("test.broker","test.broker.Service",10001,10,20,values,true,true,false);values.clear();
        pin.verify(value);
        try { value.signers.clear(); fail("Mutable snapshot"); } catch(UnsupportedOperationException expected) { }
    }
    @Test public void malformedPinInputDoesNotReflectSecrets() {
        for(List<String> values:Arrays.asList(Collections.<String>emptyList(),Arrays.asList("private-marker"),Arrays.asList(A,A)))
            try { new PeerPin("test.broker","test.broker.Service",values);fail("Bad pin accepted"); } catch(IllegalArgumentException e) { assertFalse(e.toString().contains("private-marker")); }
        try { new PeerPin("test.broker",".Service",Arrays.asList(A));fail("Relative component accepted"); } catch(IllegalArgumentException expected) { }
    }
}
