package org.example.linkgate.nativeconsumer;

import android.content.ComponentName;
import android.content.Context;
import android.os.Binder;
import androidx.test.platform.app.InstrumentationRegistry;
import dev.linkgate.client.BrokerClient;
import dev.linkgate.contracts.ConnectionOpenRequest;
import dev.linkgate.contracts.ConnectionReply;
import dev.linkgate.contracts.ConnectionStatus;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.streams.StreamClient;
import java.io.File;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.Base64;
import java.util.Collections;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.TimeUnit;
import org.junit.Test;
import static org.junit.Assert.*;

public final class FormalConsumerTest {
    private final Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
    private final File directory=new File(context.getFilesDir(),"fixture");
    private void write(String name,Object value)throws Exception{Files.write(new File(directory,name).toPath(),WireJson.write(value,65536));}
    private ConnectionOpenRequest request(Map<String,Object> config,boolean direct){String app=context.getSharedPreferences("frp-fixture",0).getString("application",null);assertNotNull(app);return ConnectionOpenRequest.parse(WireJson.write(WireJson.map("applicationRef",app,"importAlias",direct?"stream-direct":"stream","deviceId",config.get("peer"),"publisherAppId","org.example.publisher","remoteServiceId","stream","connectionPolicy",direct?WireJson.map("mode","direct-only","allowRelay",false):WireJson.map("mode","fast-connect","directWaitMs",0)),16384));}
    private UUID open(BrokerClient client,ConnectionOpenRequest request,UUID operation)throws Exception {
        ConnectionReply.Operation result=client.openConnectionAsync(request,operation,true).get(3,TimeUnit.SECONDS);long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(18);
        while(result.status.equals("pending")&&System.nanoTime()<deadline){Thread.sleep(30);result=client.getConnectionOperationAsync(operation).get(3,TimeUnit.SECONDS);}
        assertEquals(result.errorCode,"succeeded",result.status);assertNotNull(result.connectionId);ConnectionStatus status=client.getConnectionStatusAsync(result.connectionId).get(3,TimeUnit.SECONDS);assertEquals("attaching",status.flowState);assertEquals("none",status.businessPath);assertEquals("0",status.directTx.add(status.directRx).add(status.relayTx).add(status.relayRx).toString());
        var retained=client.openConnectionAsync(request,operation,true).get(3,TimeUnit.SECONDS);assertEquals(result.connectionId,retained.connectionId);return result.connectionId;
    }
    private Socket attach(BrokerClient client,UUID connection)throws Exception {UUID operation=UUID.randomUUID();var claim=client.attachConnectionAsync(connection,operation,true).get(3,TimeUnit.SECONDS);assertEquals("succeeded",claim.status);assertNotNull(claim.streamRef);var retained=client.attachConnectionAsync(connection,operation,true).get(3,TimeUnit.SECONDS);assertEquals(claim.streamRef,retained.streamRef);Socket socket=StreamClient.attachConnectionSocketAsync(client,claim.streamRef,operation,true).get(4,TimeUnit.SECONDS);socket.setSoTimeout(15000);try{var repeated=client.attachConnectionDescriptorAsync(claim.streamRef,operation,new Binder(),true).get(3,TimeUnit.SECONDS);repeated.close();fail("Formal stream attached twice.");}catch(java.util.concurrent.ExecutionException expected){}return socket;}
    @Test public void fixture()throws Exception {
        Map<String,Object> config=WireJson.read(Files.readAllBytes(new File(directory,"frp-config.json").toPath()),65536);String mode=InstrumentationRegistry.getArguments().getString("mode","");boolean direct=mode.equals("formal-direct");ConnectionOpenRequest request=request(config,direct);byte[] challenge=Base64.getDecoder().decode((String)config.get("challenge"));
        if(mode.equals("formal-auto")||mode.equals("formal-cancel")){Map<String,Object> parameters=WireJson.read(request.toJson().getBytes(StandardCharsets.UTF_8),16384);parameters.put("connectionPolicy",WireJson.map("mode","direct-first","directWaitMs",8000));request=ConnectionOpenRequest.parse(WireJson.write(parameters,16384));}
        try(BrokerClient client=BrokerClient.bindAsync(context,new ComponentName("com.nebula.linkgate.agent","dev.linkgate.agent.android.ControlService"),Collections.singleton((String)config.get("agentSigner"))).get(5,TimeUnit.SECONDS)){
            assertNotNull(client.helloAsync().get(3,TimeUnit.SECONDS).capabilities.get("connection.api"));UUID operation=UUID.randomUUID(),connection=open(client,request,operation);
            try(Socket socket=attach(client,connection)){
                if(mode.equals("formal-cancel")){
                    UUID second=open(client,request,UUID.randomUUID());try(Socket other=attach(client,second)){assertEquals("direct",client.getConnectionStatusAsync(connection).get(3,TimeUnit.SECONDS).selectedPath);assertEquals("direct",client.getConnectionStatusAsync(second).get(3,TimeUnit.SECONDS).selectedPath);socket.getOutputStream().write(2);socket.getOutputStream().write(challenge);assertEquals(1,socket.getInputStream().read());other.getOutputStream().write(2);other.getOutputStream().write(challenge);assertEquals(1,other.getInputStream().read());client.closeConnectionAsync(connection,UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);assertEquals("open",client.getConnectionStatusAsync(second).get(3,TimeUnit.SECONDS).flowState);other.setSoTimeout(500);try{other.getInputStream().read();fail("Unrelated direct stream closed.");}catch(java.net.SocketTimeoutException alive){}write("formal-cancel-result.json",WireJson.map("passed",true,"twoDirectStreams",true,"unrelatedStreamPreserved",true));}finally{client.closeConnectionAsync(second,UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);}
                }else if(mode.equals("formal-network")){
                    UUID second=open(client,request,UUID.randomUUID());try(Socket other=attach(client,second)){
                        socket.getOutputStream().write(2);socket.getOutputStream().write(challenge);assertEquals(1,socket.getInputStream().read());other.getOutputStream().write(2);other.getOutputStream().write(challenge);assertEquals(1,other.getInputStream().read());String before=client.getConnectionStatusAsync(connection).get(3,TimeUnit.SECONDS).networkGeneration;
                        write("formal-network-ready.json",WireJson.map("ready",true));try{assertEquals(-1,socket.getInputStream().read());}catch(java.io.IOException expected){}try{assertEquals(-1,other.getInputStream().read());}catch(java.io.IOException expected){}
                        ConnectionStatus stopped=client.getConnectionStatusAsync(connection).get(3,TimeUnit.SECONDS);assertEquals("NETWORK_CHANGED",stopped.errorCode);assertEquals("relay",stopped.selectedPath);assertFalse(stopped.complete);UUID next=open(client,request,UUID.randomUUID());ConnectionStatus fresh=client.getConnectionStatusAsync(next).get(3,TimeUnit.SECONDS);assertTrue(new java.math.BigInteger(fresh.networkGeneration).compareTo(new java.math.BigInteger(before))>0);client.closeConnectionAsync(next,UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);write("formal-network-result.json",WireJson.map("passed",true,"oldStreamsClosed",true,"newGeneration",true,"payloadReplayed",false,"incompleteTail",true));
                    }finally{client.closeConnectionAsync(second,UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);}
                }else{
                    socket.getOutputStream().write(1);socket.getOutputStream().write(challenge);socket.shutdownOutput();MessageDigest hash=MessageDigest.getInstance("SHA-256");long bytes=0;byte[] buffer=new byte[32768];int n;while((n=socket.getInputStream().read(buffer))!=-1){bytes+=n;hash.update(buffer,0,n);}ConnectionStatus observed=client.getConnectionStatusAsync(connection).get(3,TimeUnit.SECONDS);assertEquals("path="+observed.selectedPath+";state="+observed.flowState+";error="+observed.errorCode+";rx="+observed.directRx.add(observed.relayRx),67108864,bytes);assertEquals(config.get("expectedHash"),hex(hash.digest()));
                    ConnectionStatus status=client.getConnectionStatusAsync(connection).get(3,TimeUnit.SECONDS);long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);while(status.closedUtc==null&&System.nanoTime()<deadline){Thread.sleep(30);status=client.getConnectionStatusAsync(connection).get(3,TimeUnit.SECONDS);}assertEquals(status.errorCode,"closed",status.flowState);assertTrue(status.complete);if(!mode.equals("formal-auto"))assertEquals(direct?"direct":"relay",status.selectedPath);assertEquals(status.selectedPath,status.businessPath);boolean selectedDirect=status.selectedPath.equals("direct");assertEquals("33",(selectedDirect?status.directTx:status.relayTx).toString());assertEquals("67108864",(selectedDirect?status.directRx:status.relayRx).toString());assertEquals("succeeded",client.getConnectionOperationAsync(operation).get(3,TimeUnit.SECONDS).status);assertEquals(connection,client.openConnectionAsync(request,operation,true).get(3,TimeUnit.SECONDS).connectionId);
                    write(mode+"-result.json",WireJson.map("passed",true,"bytes",bytes,"path",status.selectedPath,"exactTx","33","exactRx","67108864","complete",status.complete,"integrity",true,"retainedOpen",true,"oneUseAttachment",true));
                }
            }finally{client.closeConnectionAsync(connection,UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);}
        }
    }
    private static String hex(byte[] bytes){StringBuilder result=new StringBuilder();for(byte value:bytes){result.append(Character.forDigit((value>>>4)&15,16));result.append(Character.forDigit(value&15,16));}return result.toString();}
}
