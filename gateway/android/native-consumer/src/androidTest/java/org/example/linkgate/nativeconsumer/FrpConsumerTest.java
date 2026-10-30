package org.example.linkgate.nativeconsumer;

import android.content.ComponentName;
import android.content.Context;
import android.os.Binder;
import android.os.Process;
import androidx.test.platform.app.InstrumentationRegistry;
import dev.linkgate.client.BrokerClient;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.SecureReply;
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

public final class FrpConsumerTest {
    private final Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
    private final File directory=new File(context.getFilesDir(),"fixture");
    private void write(String name,Object value)throws Exception{Files.write(new File(directory,name).toPath(),WireJson.write(value,65536));}
    private SecureReply.Connection awaitOpen(BrokerClient client,SecureReply.Scope scope,UUID operation)throws Exception {
        String path=scope.alias.equals("stream-direct")?"direct":"relay";SecureReply.Connection state=(path.equals("direct")?client.openSecureDirectAsync(scope,operation,true):client.openSecureRelayAsync(scope,operation,true)).get(3,TimeUnit.SECONDS);
        long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(18);
        while(state.state.equals("pending")&&System.nanoTime()<deadline){Thread.sleep(25);state=client.getSecureConnectionStatusAsync(operation).get(3,TimeUnit.SECONDS);assertEquals(path,state.path);}
        assertEquals(path,state.path);return state;
    }
    private SecureReply.Connection open(BrokerClient client,SecureReply.Scope scope,UUID operation)throws Exception {var result=awaitOpen(client,scope,operation);assertEquals(result.error,"ready",result.state);return result;}
    @Test public void fixture()throws Exception {
        Map<String,Object> config=WireJson.read(Files.readAllBytes(new File(directory,"frp-config.json").toPath()),65536);
        String mode=InstrumentationRegistry.getArguments().getString("mode","");ComponentName component=new ComponentName("com.nebula.linkgate.agent","dev.linkgate.agent.android.ControlService");
        try(BrokerClient client=BrokerClient.bindAsync(context,component,Collections.singleton((String)config.get("agentSigner"))).get(5,TimeUnit.SECONDS)) {
            var hello=client.helloAsync().get(3,TimeUnit.SECONDS);assertNotNull(hello.capabilities.get("secure.stream.relay"));
            if(mode.equals("register")) {
                String manifest="{\"manifestVersion\":1,\"applicationId\":\"org.example.native-consumer\",\"displayName\":\"Independent FRP SDK fixture\",\"exports\":[],\"imports\":[{\"alias\":\"stream\",\"protocol\":\"tcp\",\"endpointMode\":\"brokered\",\"connectionPolicy\":{\"mode\":\"fast-connect\",\"directWaitMs\":0,\"allowRelay\":true,\"connectTimeoutMs\":15000}},{\"alias\":\"stream-direct\",\"protocol\":\"tcp\",\"endpointMode\":\"brokered\",\"connectionPolicy\":{\"mode\":\"direct-only\",\"allowRelay\":false,\"connectTimeoutMs\":15000}}]}";
                var result=client.registerPendingAsync(ManifestContract.parse(manifest.getBytes(StandardCharsets.UTF_8)),UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);context.getSharedPreferences("frp-fixture",0).edit().putString("application",result.applicationRef.toString()).commit();
                int agentUid=context.getPackageManager().getApplicationInfo(component.getPackageName(),0).uid;assertNotEquals(Process.myUid(),agentUid);
                var info=context.getPackageManager().getPackageInfo(context.getPackageName(),android.content.pm.PackageManager.GET_SIGNING_CERTIFICATES);String signer=hex(MessageDigest.getInstance("SHA-256").digest(info.signingInfo.getApkContentsSigners()[0].toByteArray()));assertNotEquals(config.get("agentSigner"),signer);
                write("frp-register-result.json",WireJson.map("passed",true,"callerUid",Process.myUid(),"agentUid",agentUid,"differentSigner",true));return;
            }
            String reference=context.getSharedPreferences("frp-fixture",0).getString("application",null);assertNotNull(reference);boolean direct=mode.equals("request-direct")||mode.equals("file-direct");SecureReply.Scope scope=new SecureReply.Scope(UUID.fromString(reference),direct?"stream-direct":"stream",(String)config.get("peer"),"org.example.publisher","stream");
            if(mode.equals("request")||mode.equals("request-direct")) {
                try{(direct?client.openSecureDirectAsync(scope,UUID.randomUUID(),true):client.openSecureRelayAsync(scope,UUID.randomUUID(),true)).get(3,TimeUnit.SECONDS);fail("Missing local authorization accepted.");}catch(java.util.concurrent.ExecutionException expected){}
                client.requestAccessAsync(scope,UUID.randomUUID(),"Independent FRP SDK acceptance",true).get(3,TimeUnit.SECONDS);write("frp-request-result.json",WireJson.map("passed",true,"localGrantRequired",true));return;
            }
            if(mode.equals("denied-server")){var refused=awaitOpen(client,scope,UUID.randomUUID());assertEquals("closed",refused.state);assertEquals("AUTH_REQUIRED",refused.error);write("frp-server-denial-result.json",WireJson.map("passed",true,"error",refused.error));return;}
            byte[] challenge=Base64.getDecoder().decode((String)config.get("challenge"));assertEquals(32,challenge.length);
            UUID first=UUID.randomUUID();open(client,scope,first);
            try(Socket socket=StreamClient.attachSocketAsync(client,first,true).get(4,TimeUnit.SECONDS)) {
                socket.setSoTimeout(15000);
                try{var repeated=client.attachStreamAsync(first,new Binder(),true).get(3,TimeUnit.SECONDS);repeated.close();fail("Repeated attachment accepted.");}catch(java.util.concurrent.ExecutionException expected){}
                if(mode.equals("cancel-one")) {
                    UUID second=UUID.randomUUID();open(client,scope,second);
                    try(Socket other=StreamClient.attachSocketAsync(client,second,true).get(4,TimeUnit.SECONDS)){
                        socket.getOutputStream().write(2);socket.getOutputStream().write(challenge);assertEquals(1,socket.getInputStream().read());other.getOutputStream().write(2);other.getOutputStream().write(challenge);assertEquals(1,other.getInputStream().read());
                        client.closeSecureConnectionAsync(first).get(3,TimeUnit.SECONDS);assertEquals("ready",client.getSecureConnectionStatusAsync(second).get(3,TimeUnit.SECONDS).state);other.setSoTimeout(300);
                        try{other.getInputStream().read();fail("Other stream closed after independent cancellation.");}catch(java.net.SocketTimeoutException alive){}
                        write("frp-cancel-result.json",WireJson.map("passed",true,"unrelatedSharedStreamPreserved",true));
                    }finally{client.closeSecureConnectionAsync(second).get(3,TimeUnit.SECONDS);}
                }else if(mode.equals("file")||mode.equals("file-direct")) {
                    socket.getOutputStream().write(1);socket.getOutputStream().write(challenge);socket.shutdownOutput();MessageDigest hash=MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[32768];long bytes=0;int count;while((count=socket.getInputStream().read(buffer))!=-1){bytes+=count;hash.update(buffer,0,count);}assertEquals(67108864,bytes);assertEquals(config.get("expectedHash"),hex(hash.digest()));write(direct?"frp-direct-file-result.json":"frp-file-result.json",WireJson.map("passed",true,"bytes",bytes,"integrity",true,"requestFin",true,"oneUseAttachment",true,"path",direct?"direct":"relay"));
                }else {
                    assertTrue(mode.equals("hold-local")||mode.equals("hold-server"));UUID second=UUID.randomUUID();open(client,scope,second);
                    try(Socket other=StreamClient.attachSocketAsync(client,second,true).get(4,TimeUnit.SECONDS)) {
                        other.setSoTimeout(15000);socket.getOutputStream().write(2);socket.getOutputStream().write(challenge);assertEquals(1,socket.getInputStream().read());other.getOutputStream().write(2);other.getOutputStream().write(challenge);assertEquals(1,other.getInputStream().read());
                        write("frp-"+mode+"-ready.json",WireJson.map("ready",true,"concurrent",2));long began=System.nanoTime();
                        try{assertEquals(-1,socket.getInputStream().read());}catch(java.io.IOException expected){}try{assertEquals(-1,other.getInputStream().read());}catch(java.io.IOException expected){}
                        long elapsed=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-began);
                        SecureReply.Connection closed=client.getSecureConnectionStatusAsync(first).get(3,TimeUnit.SECONDS);assertEquals("relay",closed.path);assertEquals("closed",closed.state);
                        write("frp-"+mode+"-result.json",WireJson.map("passed",true,"twoStreamsClosed",true,"observedMs",elapsed,"path","relay"));
                    }finally{client.closeSecureConnectionAsync(second).get(3,TimeUnit.SECONDS);}
                }
            }finally{var closed=client.closeSecureConnectionAsync(first).get(3,TimeUnit.SECONDS);assertEquals(direct?"direct":"relay",closed.path);}
        }
    }
    private static String hex(byte[] bytes){StringBuilder text=new StringBuilder();for(byte value:bytes){text.append(Character.forDigit((value>>>4)&15,16));text.append(Character.forDigit(value&15,16));}return text.toString();}
}
