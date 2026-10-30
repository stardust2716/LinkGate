package dev.linkgate.contracts;

import org.junit.Test;
import static org.junit.Assert.*;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.UUID;

public final class ControlReplyTest {
    private static final UUID ID=UUID.fromString("00000000-0000-4000-8000-000000000001");
    private static String valid() { return new String(new ControlHello().respond(ControlReply.helloRequest(ID)),StandardCharsets.UTF_8); }
    private static ControlReply.Hello read(String value) { return ControlReply.readHello(value.getBytes(StandardCharsets.UTF_8),ID); }
    private static void reject(String value) { try { read(value);fail("Invalid response accepted"); }catch(ContractException e){assertEquals("INVALID_REQUEST",e.getMessage());assertNull(e.getCause());} }
    @Test public void validReplyIsImmutableAndAllUnsupported() {
        ControlReply.Hello value=read(valid());assertEquals(65536,value.maxRequestBytes);assertEquals(15,value.capabilities.size());
        for(ControlReply.Capability capability:value.capabilities.values())assertEquals("unsupported",capability.state);
        try { value.capabilities.clear();fail("Mutable result"); } catch(UnsupportedOperationException expected) { }
    }
    @Test public void requestCorrelationAndExclusiveEnvelopeAreRequired() {
        reject(valid().replace(ID.toString(),UUID.randomUUID().toString()));
        reject(valid().replace("\"result\":", "\"error\":{},\"result\":"));
        reject(valid().replace("\"result\":", "\"extra\":true,\"result\":"));
    }
    @Test public void duplicatesUnicodeAndSizeAreRejected() {
        reject(valid().replace("\"kind\":", "\"ki\\u006ed\":\"response\",\"kind\":"));
        reject("\ufeff"+valid());reject(valid()+"{}");reject(valid().replace("0.3.0-dev","\\ud800"));
        try { ControlReply.readHello(new byte[65537],ID);fail("Over limit"); }catch(ContractException expected) { }
        try { ControlReply.readHello(new byte[]{(byte)0xff},ID);fail("Bad encoding"); }catch(ContractException expected) { }
    }
    @Test public void lexicalVersionsAndAndroidLimitsAreValidated() {
        reject(valid().replace("\"apiVersion\":1", "\"apiVersion\":1.0"));
        reject(valid().replace("\"controlFrameBytes\":65536", "\"controlFrameBytes\":65537"));
        reject(valid().replace("\"controlFrameBytes\":65536", "\"controlFrameBytes\":1e4"));
        reject(valid().replace("\"controlGlobalRequests\":32", "\"controlGlobalRequests\":1"));
        reject(valid().replace("\"identityPackageLimit\":1", "\"identityPackageLimit\":2"));
    }
    @Test public void duplicateNamesAndUnknownStatesAreRejected() {
        reject(valid().replace("visit.tcp","publish.tcp"));reject(valid().replace("unsupported","unknown-state"));
        reject(valid().replace("\"bootId\":\"", "\"bootId\":\"not-a-uuid"));
    }
    @Test public void unknownCapabilitiesArePreservedWithoutPromotion() {
        ControlReply.Hello value=read(valid().replace("publish.tcp","future.capability"));
        assertEquals("unsupported",value.capabilities.get("future.capability").state);
    }
    @Test public void remoteErrorsDiscardRawMessagesAndDetails() {
        String error="{\"kind\":\"response\",\"apiVersion\":1,\"requestId\":\""+ID+"\",\"error\":{\"code\":\"VERSION_UNSUPPORTED\",\"stage\":\"validation\",\"retryable\":false,\"message\":\"private-marker\",\"details\":{\"secret\":\"private-marker\"}}}";
        try { read(error);fail("Error accepted as success"); }catch(ControlReply.BrokerError e){assertEquals("VERSION_UNSUPPORTED",e.code);assertEquals("Broker request rejected.",e.getMessage());assertFalse(e.toString().contains("private-marker"));assertNull(e.getCause());}
    }
}
