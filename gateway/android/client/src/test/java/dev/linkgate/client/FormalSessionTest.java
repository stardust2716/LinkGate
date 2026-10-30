package dev.linkgate.client;

import dev.linkgate.contracts.*;
import java.nio.charset.StandardCharsets;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;
import static org.junit.Assert.*;

public class FormalSessionTest {
    private static final UUID OP=UUID.randomUUID(),REF=UUID.randomUUID();
    private static ConnectionOpenRequest request(){return ConnectionOpenRequest.parse(WireJson.write(WireJson.map("applicationRef",UUID.randomUUID().toString(),"importAlias","files","deviceId","0".repeat(64),"publisherAppId","org.example.publisher","remoteServiceId","files","connectionPolicy",WireJson.map("mode","direct-only","allowRelay",false)),16384));}
    private static final class Wire implements ControlSession.Transport {
        final AtomicInteger calls=new AtomicInteger();boolean advertise=true,drop,wrongOperation;
        public void verify(){}
        public byte[] request(byte[] input)throws Exception{Map<String,Object> call=WireJson.read(input,65536);if(call.get("method").equals("hello")){Map<String,Object> response=WireJson.read(new ControlHello().respond(input),65536);if(advertise)WireJson.object(response.get("result")).put("capabilities",java.util.List.of(WireJson.map("name","connection.api","state","experimental","reasonCode","TEST_ONLY")));return WireJson.write(response,65536);}calls.incrementAndGet();if(drop)throw new java.io.IOException("SIMULATED_PRIVATE_VALUE");Object result=WireJson.map("operationId",wrongOperation?UUID.randomUUID().toString():OP.toString(),"status","succeeded","result",WireJson.map(call.get("method").equals("connection.attach")?"streamRef":"connectionId",REF.toString()));return WireJson.write(WireJson.map("kind","response","apiVersion",1,"requestId",call.get("requestId"),"result",result),65536);}
    }
    private static Throwable failure(CompletableFuture<?> result)throws Exception{try{result.get(4,TimeUnit.SECONDS);throw new AssertionError("Expected failure.");}catch(java.util.concurrent.ExecutionException expected){return expected.getCause();}}
    @Test public void negotiationAndOptInPrecedeFormalMutations()throws Exception{try(ClientRuntime runtime=new ClientRuntime()){Wire wire=new Wire();try(ControlSession session=new ControlSession(runtime,wire,()->{})){assertTrue(failure(session.openConnectionAsync(request(),OP,true)) instanceof IllegalStateException);session.helloAsync().get(3,TimeUnit.SECONDS);assertTrue(failure(session.openConnectionAsync(request(),OP,false)) instanceof IllegalStateException);assertEquals(0,wire.calls.get());var result=session.openConnectionAsync(request(),OP,true).get(3,TimeUnit.SECONDS);assertEquals(REF,result.connectionId);assertEquals(REF,session.connectionActionAsync(REF,OP,true,true).get(3,TimeUnit.SECONDS).streamRef);assertEquals(2,wire.calls.get());}}}
    @Test public void droppedOrMisCorrelatedMutationsStayUnknownWithoutReplay()throws Exception{for(boolean malformed:new boolean[]{false,true})try(ClientRuntime runtime=new ClientRuntime()){Wire wire=new Wire();try(ControlSession session=new ControlSession(runtime,wire,()->{})){session.helloAsync().get(3,TimeUnit.SECONDS);wire.drop=!malformed;wire.wrongOperation=malformed;var result=session.openConnectionAsync(request(),OP,true);Throwable error=failure(result);assertTrue(error instanceof MutationOutcomeUnknownException);assertEquals(OP,((MutationOutcomeUnknownException)error).operationId);assertEquals(1,wire.calls.get());assertFalse(error.toString().contains("SIMULATED_PRIVATE_VALUE"));}}}
    @Test public void originalNestedPolicySizeIsNotErasedByEnvelopeParsing(){String parameters=request().toJson();String old="{\"mode\":\"direct-only\"";String source=parameters.substring(parameters.indexOf("\"connectionPolicy\":")+19,parameters.length()-1);String enlarged=source.substring(0,source.length()-1)+" ".repeat(8193-source.getBytes(StandardCharsets.UTF_8).length)+"}";String changed=parameters.replace(source,enlarged);byte[] original=changed.getBytes(StandardCharsets.UTF_8);byte[] envelope=("{\"kind\":\"request\",\"apiVersion\":1,\"requestId\":\""+UUID.randomUUID()+"\",\"operationId\":\""+OP+"\",\"method\":\"connection.open\",\"params\":"+changed+"}").getBytes(StandardCharsets.UTF_8);assertArrayEquals(original,WireJson.originalConnectionParams(envelope));try{ConnectionOpenRequest.parse(WireJson.originalConnectionParams(envelope));fail("Oversized original policy accepted.");}catch(ContractException expected){assertNull(expected.getCause());}}
}
