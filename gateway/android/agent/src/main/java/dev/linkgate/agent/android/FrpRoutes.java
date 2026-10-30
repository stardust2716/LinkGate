package dev.linkgate.agent.android;

import android.content.Context;
import android.os.SystemClock;
import android.system.Os;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.DeviceIdentity;
import dev.linkgate.transport.OwnedRuntimePool;
import java.io.Closeable;
import java.io.File;
import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Proxy;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.security.cert.X509Certificate;
import java.util.Collections;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;

final class FrpRoutes implements Closeable {
    private final Context context;private final AgentRegistry registry;private final DeviceIdentity identity;private final ServerSettings settings;private final X509Certificate root;
    private final ThreadFactory threads=task->{Thread thread=new Thread(task,"LinkGate-Routes");thread.setDaemon(true);return thread;};
    private final ThreadPoolExecutor creation=new ThreadPoolExecutor(0,4,30,TimeUnit.SECONDS,new SynchronousQueue<>(),threads);
    private final ScheduledThreadPoolExecutor maintenance=new ScheduledThreadPoolExecutor(1,threads);
    private volatile long stopping=-1;
    private final OwnedRuntimePool<Key,Session> pool=new OwnedRuntimePool<>(16,60000,SystemClock::elapsedRealtimeNanos,creation);
    private static final class Key {
        final AgentRegistry.Scope scope;final String value;
        Key(AgentRegistry.Scope scope){this.scope=scope;value=scope.json();}
        @Override public boolean equals(Object other){return other instanceof Key&&value.equals(((Key)other).value);}
        @Override public int hashCode(){return value.hashCode();}
    }
    FrpRoutes(Context context,AgentRegistry registry,DeviceIdentity identity,ServerSettings settings,X509Certificate root){
        this.context=context;this.registry=registry;this.identity=identity;this.settings=settings;this.root=root;
        maintenance.setRemoveOnCancelPolicy(true);maintenance.scheduleWithFixedDelay(this::maintain,500,500,TimeUnit.MILLISECONDS);
    }
    boolean canDirect(){return settings.stunEndpoints.size()>=2;}
    Route openRelay(AgentRegistry.Scope scope,long deadline,BooleanSupplier cancelled)throws Exception{return open(scope,deadline,cancelled,false);}
    Route openDirect(AgentRegistry.Scope scope,long deadline,BooleanSupplier cancelled)throws Exception{return open(scope,deadline,cancelled,true);}
    private Route open(AgentRegistry.Scope scope,long deadline,BooleanSupplier cancelled,boolean direct)throws Exception {
        if(direct&&!canDirect()||!direct&&!scope.allowRelay)throw new dev.linkgate.transport.ConnectionPlanner.Failure(dev.linkgate.transport.ConnectionPlanner.Code.CAPABILITY_UNSUPPORTED);
        if(!registry.currentScopes(Collections.singletonList(scope))[0])throw new SecurityException("AUTH_REQUIRED");
        Key key=new Key(scope);return new Route(pool.acquire(key,stop->create(scope,stop),deadline,cancelled),direct);
    }
    private Session create(AgentRegistry.Scope scope,OwnedRuntimePool.Creation<Session> cancelled)throws Exception {
        Session session=new Session();cancelled.own(session);long deadline=SystemClock.elapsedRealtimeNanos()+TimeUnit.MILLISECONDS.toNanos(scope.timeoutMs);
        {
            String namespace=namespace(identity.deviceId(),scope.client,"access"),publisher=namespace(scope.device,scope.publisher,"publish"),base="lg1_"+hash("service\n"+scope.device+"\n"+scope.publisher+"\n"+scope.service);
            InetAddress loopback=InetAddress.getByAddress(new byte[]{127,0,0,1});
            try(ServerSocket relay=scope.allowRelay?new ServerSocket(0,1,loopback):null;ServerSocket direct=canDirect()?new ServerSocket(0,1,loopback):null){session.relayPort=relay==null?0:relay.getLocalPort();session.directPort=direct==null?0:direct.getLocalPort();}
            if(session.relayPort==0&&session.directPort==0)throw new IOException("No owned transport available.");
            File profile=new File(context.getNoBackupFilesDir().getCanonicalFile(),"route-"+java.util.UUID.randomUUID()+".json");session.profile=profile;
            java.util.List<Object> visitors=new java.util.ArrayList<>();
            if(session.relayPort>0)visitors.add(WireJson.map("name","stream-relay","type","stcp","serverUser",publisher,"serverName",base+"_stcp","secretKey",publisher+"."+base+"_stcp","bindAddr","127.0.0.1","bindPort",session.relayPort));
            if(session.directPort>0)visitors.add(WireJson.map("name","stream-direct","type","xtcp","serverUser",publisher,"serverName",base+"_xtcp","secretKey",publisher+"."+base+"_xtcp","bindAddr","127.0.0.1","bindPort",session.directPort,"protocol","quic","keepTunnelOpen",false,"fallbackTo",""));
            Files.write(profile.toPath(),WireJson.write(WireJson.map("user",namespace,"clientID",namespace+"."+java.util.UUID.randomUUID().toString().replace("-",""),"serverAddr","127.0.0.1","serverPort",1,"natHoleStunServer",canDirect()?settings.stunEndpoints.get(0):"127.0.0.1:1","loginFailExit",true,"transport",WireJson.map("protocol","tcp","wireProtocol","v2","tcpMux",true,"heartbeatInterval",1,"heartbeatTimeout",15),"log",WireJson.map("to","console","level","error"),"visitors",visitors),65536));Os.chmod(profile.getPath(),0400);
            if(cancelled.getAsBoolean())throw new IOException("Route cancelled.");
            session.bridge=FrpRuntimeBridge.start(context,registry,identity,scope.application,settings.address(),settings.backendPort,settings.certificate,root,profile,settings.stunEndpoints,bridge->session.bridge=bridge);
            while(SystemClock.elapsedRealtimeNanos()<deadline&&!cancelled.getAsBoolean()&&!Thread.currentThread().isInterrupted()) {
                if(!session.alive())throw new IOException("Runtime unavailable.");
                try(Socket probe=new Socket(Proxy.NO_PROXY)){probe.connect(new InetSocketAddress(loopback,session.relayPort>0?session.relayPort:session.directPort),100);return session;}catch(IOException pending){Thread.sleep(25);}
            }
            throw new IOException("Route readiness deadline elapsed.");
        }
    }
    private static String namespace(String device,String app,String role)throws Exception{return "lg1_"+hash("namespace\n"+device+"\n"+app+"\n"+role);}
    private static String hash(String value)throws Exception{StringBuilder output=new StringBuilder();for(byte item:MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8))){output.append(Character.forDigit((item>>>4)&15,16));output.append(Character.forDigit(item&15,16));}return output.toString();}
    private static final class Session implements Closeable {
        private boolean closed;private FrpRuntimeBridge bridge;private File profile;int relayPort,directPort;
        synchronized boolean alive(){return !closed&&bridge!=null&&!bridge.isStopped()&&bridge.childAlive();}
        @Override public void close()throws IOException{FrpRuntimeBridge child;File file;synchronized(this){closed=true;child=bridge;file=profile;}if(child!=null)child.close();if(file!=null&&file.exists()&&!file.delete())throw new IOException("Private runtime cleanup pending.");if(child!=null&&child.childAlive())throw new IOException("Runtime cleanup pending.");}
    }
    final class Route implements Closeable {
        private final OwnedRuntimePool<Key,Session>.Lease lease;final int port;
        Route(OwnedRuntimePool<Key,Session>.Lease lease,boolean direct)throws IOException{this.lease=lease;boolean valid=false;try{Session session=lease.value();port=direct?session.directPort:session.relayPort;if(port<1)throw new IOException("Requested transport unavailable.");valid=true;}finally{if(!valid)lease.close();}}
        @Override public void close(){lease.close();}
    }
    private void maintain(){
        if(stopping<0)pool.maintain(key->registry.currentScopes(Collections.singletonList(key.scope))[0],Session::alive);
        else{pool.close();if(pool.size()==0||SystemClock.elapsedRealtimeNanos()-stopping>=TimeUnit.SECONDS.toNanos(3))maintenance.shutdownNow();}
    }
    int runtimeCount(){return pool.size();}
    void invalidate(){pool.maintain(key->false,Session::alive);}
    @Override public void close(){stopping=SystemClock.elapsedRealtimeNanos();pool.close();creation.shutdownNow();if(pool.size()==0)maintenance.shutdownNow();}
}
