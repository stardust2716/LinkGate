package dev.linkgate.agent.android;

import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import androidx.test.platform.app.InstrumentationRegistry;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.descriptors.DescriptorStream;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.KeyStore;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import org.junit.Test;
import static org.junit.Assert.*;

public final class NativeOwnerTest {
    private final Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
    private final File directory=new File(context.getFilesDir(),"fixture");
    private void write(String name,Object value)throws Exception{Files.write(new File(directory,name).toPath(),WireJson.write(value,65536));}
    private Map<String,Object> read(String name)throws Exception{return WireJson.read(Files.readAllBytes(new File(directory,name).toPath()),65536);}
    @Test public void fixture()throws Exception {
        assertTrue(directory.isDirectory()||directory.mkdir());AgentRepository repository=AgentRepository.get(context);
        Bundle arguments=InstrumentationRegistry.getArguments();String mode=arguments.getString("mode","");
        if(mode.equals("request")) {
            Files.write(new File(directory,"request.pem").toPath(),repository.identity.request().getBytes(StandardCharsets.UTF_8));
            KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);assertNull(store.getKey("LinkGate.device.v1",null).getEncoded());
            descriptors();write("request-result.json",WireJson.map("passed",true,"keystoreKeyExportable",false,"deviceId",repository.identity.deviceId(),"agentSigner",signer(),"descriptorHalfClose",true,"blockedReadInterrupted",true));return;
        }
        if(mode.equals("enroll")) {
            String leaf=new String(Files.readAllBytes(new File(directory,"device-cert.pem").toPath()),StandardCharsets.UTF_8),root=new String(Files.readAllBytes(new File(directory,"owner-root.pem").toPath()),StandardCharsets.UTF_8);
            repository.identity.install(leaf,root);assertEquals(repository.identity.deviceId(),repository.identity.load().deviceId());
            write("enroll-result.json",WireJson.map("passed",true,"deviceId",repository.identity.deviceId(),"rootFingerprint",AndroidIdentity.fingerprint(repository.identity.root())));return;
        }
        if(mode.equals("server-state")){
            ServerSettings selected=ServerSettings.parse(Files.readAllBytes(new File(directory,"server.json").toPath()),repository.identity);selected.install(context);
            File saved=new File(context.getNoBackupFilesDir(),"server-v1.json");Files.write(saved.toPath(),"{}".getBytes(StandardCharsets.UTF_8));
            var owner=InstrumentationRegistry.getInstrumentation().startActivitySync(new Intent(context,OwnerActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            try {
                context.getSharedPreferences("owner",Context.MODE_PRIVATE).edit().putBoolean("enabled",true).commit();GatewayService.requestStart(context);
                long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);while(!GatewayService.state().equals("IDENTITY_STORAGE_OR_SERVER_REQUIRED")&&System.nanoTime()<deadline)Thread.sleep(25);
                assertEquals("IDENTITY_STORAGE_OR_SERVER_REQUIRED",GatewayService.state());assertNull(GatewayService.current());
                context.getSharedPreferences("owner",Context.MODE_PRIVATE).edit().putBoolean("enabled",false).commit();GatewayService.requestStop(context);Thread.sleep(100);selected.install(context);
                context.getSharedPreferences("owner",Context.MODE_PRIVATE).edit().putBoolean("enabled",true).commit();GatewayService.requestStart(context);
                context.getSharedPreferences("owner",Context.MODE_PRIVATE).edit().putBoolean("enabled",false).commit();GatewayService.requestStop(context);Thread.sleep(12000);
                assertNull(GatewayService.current());assertFalse(GatewayService.startingOrRunning());write("server-state-result.json",WireJson.map("passed",true,"corruptConfigFailsClosed",true,"stopPreventsLateStartup",true));
            }finally{context.getSharedPreferences("owner",Context.MODE_PRIVATE).edit().putBoolean("enabled",false).commit();GatewayService.requestStop(context);InstrumentationRegistry.getInstrumentation().runOnMainSync(owner::finish);}return;
        }
        if(mode.equals("host-server")){ServerSettings selected=ServerSettings.parse(Files.readAllBytes(new File(directory,"server.json").toPath()),repository.identity);selected.install(context);assertEquals(selected.stunEndpoints,ServerSettings.load(context,repository.identity).stunEndpoints);mode="host";}
        assertEquals("host",mode);Map<String,Object> config=read("peer.json");String certificate=(String)config.get("certificate");String peer=repository.identity.peerId(certificate);
        repository.registry.trustPeer(peer,certificate,(String)config.get("host"),(int)WireJson.integer(config.get("port"),1,65535),0,UUID.randomUUID());
        Intent activity=new Intent(context,OwnerActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);var owner=InstrumentationRegistry.getInstrumentation().startActivitySync(activity);
        for(int attempt=0;attempt<3;attempt++){GatewayService.requestStart(context);GatewayService.requestStop(context);}
        context.getSharedPreferences("owner",Context.MODE_PRIVATE).edit().putBoolean("enabled",true).commit();GatewayService.requestStart(context);
        try {
            long ready=System.nanoTime()+TimeUnit.SECONDS.toNanos(15);while(GatewayService.current()==null&&System.nanoTime()<ready)Thread.sleep(25);assertNotNull(GatewayService.state(),GatewayService.current());
            write("host-result.json",WireJson.map("state","ready","peer",peer,"deviceId",repository.identity.deviceId()));String last="";long deadline=System.nanoTime()+TimeUnit.MINUTES.toNanos(5);
            while(System.nanoTime()<deadline) {
                File commandFile=new File(directory,"command.json");if(!commandFile.exists()){Thread.sleep(50);continue;}
                Map<String,Object> command=read("command.json");String id=WireJson.uuid(command.get("id")).toString();if(id.equals(last)){Thread.sleep(50);continue;}last=id;
                String action=(String)command.get("action");int count=0;
                if(action.equals("approve-apps"))for(String reference:repository.registry.listApplications(null,20)){AgentRegistry.Review review=repository.registry.review(reference);if(review.pending!=null&&review.pending.contains("org.example.native-consumer")){repository.registry.decide(review,"approve",UUID.randomUUID());count++;}}
                else if(action.equals("approve-access"))for(String reference:repository.registry.listRequests(null,20)){AgentRegistry.RequestReview review=repository.registry.inspectRequest(reference);if(review.state.equals("pending")){repository.registry.approveAccess(reference,review.revision,UUID.randomUUID());count++;}}
                else if(action.equals("revoke-access"))for(String reference:repository.registry.listRequests(null,20)){AgentRegistry.RequestReview review=repository.registry.inspectRequest(reference);if(review.state.equals("active")){repository.registry.revokeAccess(reference,review.revision,UUID.randomUUID());count++;}}
                else if(action.equals("runtime-count")){AgentConnections runtime=GatewayService.current();count=runtime==null?0:runtime.runtimeCount();}
                else if(action.equals("direct-lan-count")){AgentConnections runtime=GatewayService.current();count=runtime==null?0:runtime.directLanCount();}
                else if(action.equals("direct-xtcp-count")){AgentConnections runtime=GatewayService.current();count=runtime==null?0:runtime.directXtcpCount();}
                else if(action.equals("formal-xtcp-count")){AgentConnections runtime=GatewayService.current();count=runtime==null?0:runtime.formal.xtcpCount();}
                else if(action.equals("network-generation")){AgentConnections runtime=GatewayService.current();if(runtime!=null){runtime.networkChanged();count=1;}}
                else if(action.equals("cold-peer-route")){repository.registry.trustPeer(peer,certificate,(String)config.get("host"),(int)WireJson.integer(config.get("blockedPort"),1,65535),1,UUID.randomUUID());count=1;}
                else if(action.equals("tls-probe")){try(java.net.Socket raw=new java.net.Socket()){raw.connect(new java.net.InetSocketAddress((String)config.get("host"),(int)WireJson.integer(config.get("port"),1,65535)),3000);try(var secured=repository.identity.load().connect(raw,(int)WireJson.integer(config.get("port"),1,65535),peer,3000)){count=1;}}}
                else if(!action.equals("stop"))throw new IllegalArgumentException("Unknown fixture command.");
                write("command-result.json",WireJson.map("id",id,"count",count,"action",action));if(action.equals("stop"))break;
            }
        } finally {context.getSharedPreferences("owner",Context.MODE_PRIVATE).edit().putBoolean("enabled",false).commit();GatewayService.requestStop(context);InstrumentationRegistry.getInstrumentation().runOnMainSync(owner::finish);}
    }
    private String signer()throws Exception {var info=context.getPackageManager().getPackageInfo(context.getPackageName(),android.content.pm.PackageManager.GET_SIGNING_CERTIFICATES);StringBuilder output=new StringBuilder();for(byte value:java.security.MessageDigest.getInstance("SHA-256").digest(info.signingInfo.getApkContentsSigners()[0].toByteArray())){output.append(Character.forDigit((value>>>4)&15,16));output.append(Character.forDigit(value&15,16));}return output.toString();}
    private void descriptors()throws Exception {
        var workers=Executors.newFixedThreadPool(2);
        try {
            ParcelFileDescriptor[] pair=ParcelFileDescriptor.createReliableSocketPair();
            try(DescriptorStream left=new DescriptorStream(pair[0],new Object());DescriptorStream right=new DescriptorStream(pair[1],new Object())) {
                left.output().write(42);left.finishWrites();assertEquals(42,right.input().read());assertEquals(-1,right.input().read());right.output().write(99);right.finishWrites();assertEquals(99,left.input().read());assertEquals(-1,left.input().read());
            }
            pair=ParcelFileDescriptor.createReliableSocketPair();final DescriptorStream blocked=new DescriptorStream(pair[0],new Object());
            try(DescriptorStream other=new DescriptorStream(pair[1],new Object())){var task=workers.submit(()->{try{return blocked.input().read();}catch(java.io.IOException expected){return -2;}});Thread.sleep(50);blocked.close();assertTrue(task.get(2,TimeUnit.SECONDS)<0);}
            pair=ParcelFileDescriptor.createReliableSocketPair();try(DescriptorStream left=new DescriptorStream(pair[0],new Object());DescriptorStream right=new DescriptorStream(pair[1],new Object())){left.abort();try{right.input().read();fail("Abort became clean EOF.");}catch(java.io.IOException expected){assertFalse(expected.toString().contains("PRIVATE"));}}
        } finally{workers.shutdownNow();assertTrue(workers.awaitTermination(2,TimeUnit.SECONDS));}
    }
}
