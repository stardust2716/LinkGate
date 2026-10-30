package dev.linkgate.agent.android;

import android.content.Context;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.os.SystemClock;
import androidx.test.platform.app.InstrumentationRegistry;
import dev.linkgate.authority.AuthoritySnapshot;
import dev.linkgate.authority.ControlClient;
import dev.linkgate.authority.LeaseClock;
import dev.linkgate.authority.LeaseRejectedException;
import dev.linkgate.authority.PeerLeaseSource;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.PeerClient;
import java.io.File;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.util.Base64;
import java.util.Collections;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import org.junit.Test;
import static org.junit.Assert.*;

public final class FrpOwnerTest {
    private final Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
    private final File directory=new File(context.getFilesDir(),"fixture");
    private void write(String name,Object value)throws Exception{Files.write(new File(directory,name).toPath(),WireJson.write(value,65536));}
    @Test public void actualBridge()throws Exception {
        Map<String,Object> config=WireJson.read(Files.readAllBytes(new File(directory,"frp.json").toPath()),65536);
        AgentRepository repository=AgentRepository.get(context);var identity=repository.identity.load();var root=repository.identity.root();
        KeyStore keys=KeyStore.getInstance("AndroidKeyStore");keys.load(null);assertNull(keys.getKey("LinkGate.device.v1",null).getEncoded());
        String host=(String)config.get("host"),publisher=WireJson.key(config.get("publisher"));
        byte[] address=new byte[4];String[] fields=host.split("\\.");for(int i=0;i<4;i++)address[i]=(byte)Integer.parseInt(fields[i]);
        String clientNamespace="lg1_"+hash(("namespace\n"+identity.deviceId()+"\norg.example.client\naccess").getBytes(StandardCharsets.UTF_8));
        String pubNamespace="lg1_"+hash(("namespace\n"+publisher+"\norg.example.publisher\npublish").getBytes(StandardCharsets.UTF_8));
        String name="lg1_"+hash(("service\n"+publisher+"\norg.example.publisher\nstream").getBytes(StandardCharsets.UTF_8))+"_stcp";
        String manifest="{\"manifestVersion\":1,\"applicationId\":\"org.example.client\",\"displayName\":\"Native FRP owner fixture\",\"exports\":[],\"imports\":[{\"alias\":\"stream\",\"protocol\":\"tcp\",\"endpointMode\":\"brokered\",\"connectionPolicy\":{\"mode\":\"fast-connect\",\"allowRelay\":true}}]}";
        Map<String,Object> registered=WireJson.read(repository.registry.register("native-frp-fixture","Instrumentation-owned fixture",UUID.randomUUID(),ManifestContract.parse(manifest.getBytes(StandardCharsets.UTF_8))).getBytes(StandardCharsets.UTF_8),65536);
        String application=WireJson.uuid(registered.get("applicationRef")).toString();repository.registry.decide(repository.registry.review(application),"approve",UUID.randomUUID());
        int localPort;try(ServerSocket reservation=new ServerSocket(0,1,InetAddress.getByAddress(new byte[]{127,0,0,1}))){localPort=reservation.getLocalPort();}
        File profile=new File(context.getNoBackupFilesDir().getCanonicalFile(),"frp-fixture-"+UUID.randomUUID()+".json");
        Object visitor=WireJson.map("name","stream-relay","type","stcp","serverUser",pubNamespace,"serverName",name,"secretKey",pubNamespace+"."+name,"bindAddr","127.0.0.1","bindPort",localPort);
        Files.write(profile.toPath(),WireJson.write(WireJson.map("user",clientNamespace,"clientID",clientNamespace,"serverAddr","127.0.0.1","serverPort",1,"loginFailExit",true,"transport",WireJson.map("protocol","tcp","wireProtocol","v2","tcpMux",true,"heartbeatInterval",1,"heartbeatTimeout",3),"log",WireJson.map("to","console","level","error"),"visitors",Collections.singletonList(visitor)),65536));
        var scheduler=Executors.newScheduledThreadPool(2);var controlIo=Executors.newFixedThreadPool(2);
        LeaseClock clock=new LeaseClock(new LeaseClock.Source(){public long utcMillis(){return System.currentTimeMillis();}public long nanos(){return SystemClock.elapsedRealtimeNanos();}});
        long localRevokeMs=0,serverRevokeMs=0;
        try(ControlClient authority=ControlClient.connect(address,(int)WireJson.integer(config.get("controlPort"),1,65535),identity,AndroidIdentity.parse((String)config.get("serverCertificate")),root,clock);
            FrpRuntimeBridge bridge=FrpRuntimeBridge.start(context,repository.registry,identity,application,InetAddress.getByAddress(address),(int)WireJson.integer(config.get("backendPort"),1,65535),AndroidIdentity.parse((String)config.get("serverCertificate")),root,profile)) {
            assertEquals(clientNamespace,bridge.namespace());
            scheduler.schedule(bridge::close,90,TimeUnit.SECONDS);
            long listeningDeadline=SystemClock.elapsedRealtime()+5000;boolean listening=false;
            while(SystemClock.elapsedRealtime()<listeningDeadline&&bridge.childAlive()){
                try(java.net.Socket probe=new java.net.Socket(java.net.Proxy.NO_PROXY)){probe.connect(new java.net.InetSocketAddress(InetAddress.getByAddress(new byte[]{127,0,0,1}),localPort),100);listening=true;break;}catch(java.io.IOException pending){Thread.sleep(25);}
            }
            assertTrue("Owned runtime readiness: "+bridge.stopReason()+" / "+bridge.lastFailure(),listening);assertTrue(bridge.authenticatedConnections()>0);
            int before=bridge.openedNetworks();
            try(LocalSocket unrelated=new LocalSocket()){
                unrelated.connect(new LocalSocketAddress(bridge.socketPath(),LocalSocketAddress.Namespace.FILESYSTEM));unrelated.setSoTimeout(2000);
                try{unrelated.getOutputStream().write(1);assertEquals(-1,unrelated.getInputStream().read());}catch(java.net.SocketTimeoutException failure){throw failure;}catch(java.io.IOException expected){}
            }
            Thread.sleep(100);assertEquals(before,bridge.openedNetworks());
            assertTrue(bridge.childAlive());
            write("frp-ready.json",WireJson.map("ready",true,"unrelatedClientRefused",true));
            byte[] challenge=Base64.getDecoder().decode((String)config.get("challenge"));assertEquals(32,challenge.length);
            AuthoritySnapshot.Target target=new AuthoritySnapshot.Target(publisher,"org.example.publisher","stream");
            try{var probe=PeerLeaseSource.acquire(authority,"org.example.client",target,"tcp",null);probe.close();}catch(LeaseRejectedException failure){throw new AssertionError("Standard lease verification stage: "+failure.stage);}
            try(PeerClient peer=PeerClient.open(identity,publisher,InetAddress.getByAddress(new byte[]{127,0,0,1}),localPort,"org.example.client","org.example.publisher","stream","tcp",null,15000,scheduler,controlIo,SystemClock::elapsedRealtimeNanos,PeerClient::connectTcp,new PeerClient.Cancellation(),PeerLeaseSource.acquire(authority,"org.example.client",target,"tcp",null))) {
                peer.stream.output.write(1);peer.stream.output.write(challenge);peer.stream.finishWrites();
                MessageDigest digest=MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[32768];long count=0;int n;while((n=peer.stream.input.read(buffer))!=-1){count+=n;digest.update(buffer,0,n);}
                assertEquals(67108864,count);assertEquals(config.get("expectedHash"),hex(digest.digest()));peer.stream.drainWriteCompletion();
            }
            repository.registry.register("native-frp-fixture","Instrumentation-owned fixture",UUID.randomUUID(),ManifestContract.parse(manifest.replace("Native FRP owner fixture","Pending native fixture").getBytes(StandardCharsets.UTF_8)));
            repository.registry.decide(repository.registry.review(application),"deny",UUID.randomUUID());Thread.sleep(400);assertFalse(bridge.isStopped());
            try(PeerClient held=PeerClient.open(identity,publisher,InetAddress.getByAddress(new byte[]{127,0,0,1}),localPort,"org.example.client","org.example.publisher","stream","tcp",null,15000,scheduler,controlIo,SystemClock::elapsedRealtimeNanos,PeerClient::connectTcp,new PeerClient.Cancellation(),PeerLeaseSource.acquire(authority,"org.example.client",target,"tcp",null))) {
                held.stream.output.write(2);held.stream.output.write(challenge);assertEquals(1,held.stream.input.read());
                write("frp-holding.json",WireJson.map("ready",true));long began=SystemClock.elapsedRealtime();
                try{assertEquals(-1,held.stream.input.read());}catch(java.io.IOException expected){}
                serverRevokeMs=SystemClock.elapsedRealtime()-began;
            }
            long began=SystemClock.elapsedRealtime();repository.registry.decide(repository.registry.review(application),"revoke",UUID.randomUUID());
            while(bridge.childAlive()&&SystemClock.elapsedRealtime()-began<5000)Thread.sleep(20);localRevokeMs=SystemClock.elapsedRealtime()-began;
            assertFalse(bridge.childAlive());assertEquals("AUTHORITY_CHANGED",bridge.stopReason());
            write("frp-result.json",WireJson.map("passed",true,"actualPackagedFrpc",true,"keystoreKeyExported",false,"actualUnixCredentials",true,"unrelatedClientRefused",true,"agentControlMtls",true,"agentDataMtls",true,"serverLease",true,"relayBytes",67108864,"integrity",true,"requestFin",true,"pendingDenialPreservesRuntime",true,"localRuntimeRevokeMs",localRevokeMs,"serverRevokeObservedMs",serverRevokeMs,"businessSdkTested",false,"physicalDeviceTested",false));
        }finally{scheduler.shutdownNow();controlIo.shutdownNow();profile.delete();}
    }
    private static String hash(byte[] value)throws Exception{return hex(MessageDigest.getInstance("SHA-256").digest(value));}
    private static String hex(byte[] value){StringBuilder result=new StringBuilder();for(byte item:value){result.append(Character.forDigit((item>>>4)&15,16));result.append(Character.forDigit(item&15,16));}return result.toString();}
}
