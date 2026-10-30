package org.example.linkgate.nativeconsumer;

import android.content.ComponentName;
import android.content.Context;
import android.os.Binder;
import android.os.Process;
import androidx.test.platform.app.InstrumentationRegistry;
import dev.linkgate.client.BrokerClient;
import dev.linkgate.contracts.ApplicationManifest;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.SecureReply;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.streams.StreamClient;
import java.io.File;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.Collections;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.TimeUnit;
import org.junit.Test;
import static org.junit.Assert.*;

public final class NativeConsumerTest {
    private final Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
    private final File directory=new File(context.getFilesDir(),"fixture");
    private void write(String name,Object value)throws Exception{Files.write(new File(directory,name).toPath(),WireJson.write(value,65536));}
    @Test public void fixture()throws Exception {
        Map<String,Object> config=WireJson.read(Files.readAllBytes(new File(directory,"config.json").toPath()),65536);String mode=InstrumentationRegistry.getArguments().getString("mode","");
        ComponentName component=new ComponentName("com.nebula.linkgate.agent","dev.linkgate.agent.android.ControlService");
        if(mode.equals("register")) {
            try{BrokerClient.bindAsync(context,component,Collections.singleton(String.join("",Collections.nCopies(64,"0")))).get(5,TimeUnit.SECONDS);fail("Wrong signer accepted.");}catch(java.util.concurrent.ExecutionException expected){}
        }
        try(BrokerClient client=BrokerClient.bindAsync(context,component,Collections.singleton((String)config.get("agentSigner"))).get(5,TimeUnit.SECONDS)) {
            client.helloAsync().get(3,TimeUnit.SECONDS);
            if(mode.equals("register")) {
                ApplicationManifest manifest=ManifestContract.parse(("{\"manifestVersion\":1,\"applicationId\":\"org.example.native-consumer\",\"displayName\":\"Native consumer fixture\",\"exports\":[],\"imports\":[{\"alias\":\"file\",\"protocol\":\"tcp\",\"endpointMode\":\"brokered\",\"connectionPolicy\":{\"mode\":\"direct-only\",\"allowRelay\":false,\"connectTimeoutMs\":5000}}]}").getBytes(StandardCharsets.UTF_8));
                var result=client.registerPendingAsync(manifest,UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);context.getSharedPreferences("fixture",0).edit().putString("application",result.applicationRef.toString()).commit();
                var agentInfo=context.getPackageManager().getApplicationInfo(component.getPackageName(),0);assertNotEquals(Process.myUid(),agentInfo.uid);
                var ownInfo=context.getPackageManager().getPackageInfo(context.getPackageName(),android.content.pm.PackageManager.GET_SIGNING_CERTIFICATES);String ownSigner=hex(MessageDigest.getInstance("SHA-256").digest(ownInfo.signingInfo.getApkContentsSigners()[0].toByteArray()));assertNotEquals(config.get("agentSigner"),ownSigner);
                write("register-result.json",WireJson.map("passed",true,"applicationRef",result.applicationRef.toString(),"callerUid",Process.myUid(),"agentUid",agentInfo.uid,"differentSigner",true,"wrongSignerDenied",true));return;
            }
            String reference=context.getSharedPreferences("fixture",0).getString("application",null);assertNotNull(reference);
            SecureReply.Scope scope=new SecureReply.Scope(UUID.fromString(reference),"file",(String)config.get("peer"),"org.example.native-publisher","file");
            if(mode.equals("request")) {
                try{client.openSecureDirectAsync(scope,UUID.randomUUID(),true).get(3,TimeUnit.SECONDS);fail("Unapproved local access accepted.");}catch(java.util.concurrent.ExecutionException expected){}
                var requested=client.requestAccessAsync(scope,UUID.randomUUID(),"Explicit native integration fixture",true).get(3,TimeUnit.SECONDS);
                write("request-result.json",WireJson.map("passed",true,"localGrantRequired",true,"request",requested.requestId.toString()));return;
            }
            UUID operation=UUID.randomUUID();SecureReply.Connection state=client.openSecureDirectAsync(scope,operation,true).get(3,TimeUnit.SECONDS);
            long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(8);while(state.state.equals("pending")&&System.nanoTime()<deadline){Thread.sleep(25);state=client.getSecureConnectionStatusAsync(operation).get(3,TimeUnit.SECONDS);}
            if(mode.equals("denied-remote")){assertEquals("closed",state.state);assertEquals("AUTH_REQUIRED",state.error);write("remote-denial-result.json",WireJson.map("passed",true,"error",state.error));return;}assertEquals(state.error,"ready",state.state);
            try(Socket socket=StreamClient.attachSocketAsync(client,operation,true).get(4,TimeUnit.SECONDS)) {
                try{var duplicate=client.attachStreamAsync(operation,new Binder(),true).get(3,TimeUnit.SECONDS);duplicate.close();fail("Descriptor attached twice.");}catch(java.util.concurrent.ExecutionException expected){}
                socket.setSoTimeout(15000);
                if(mode.equals("file")) {
                    socket.getOutputStream().write(((String)config.get("token")).getBytes(StandardCharsets.US_ASCII));socket.shutdownOutput();
                    MessageDigest digest=MessageDigest.getInstance("SHA-256");byte[] block=new byte[65536];long bytes=0;int n;while((n=socket.getInputStream().read(block))!=-1){digest.update(block,0,n);bytes+=n;}assertEquals(67108864,bytes);
                    String hash=hex(digest.digest());assertEquals(config.get("expectedHash"),hash);
                    write("file-result.json",WireJson.map("passed",true,"bytes",Long.toString(bytes),"sha256",hash,"requestFin",true,"oneUseAttachment",true,"callerUid",Process.myUid()));
                }else {
                    assertEquals("hold",mode);write("holding.json",WireJson.map("state","ready","operation",operation.toString()));long start=System.nanoTime();
                    try{assertEquals(-1,socket.getInputStream().read());}catch(java.io.IOException expected){}long elapsed=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-start);
                    write("hold-result.json",WireJson.map("passed",true,"closed",true,"blockedReadMs",elapsed));
                }
            }finally{client.closeSecureConnectionAsync(operation).get(3,TimeUnit.SECONDS);}
        }
    }
    private static String hex(byte[] bytes){StringBuilder text=new StringBuilder();for(byte value:bytes){text.append(Character.forDigit((value>>>4)&15,16));text.append(Character.forDigit(value&15,16));}return text.toString();}
}
