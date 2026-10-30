package dev.linkgate.agent.android;

import android.content.Context;
import android.net.Credentials;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.os.Build;
import android.os.SystemClock;
import android.system.Os;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.transport.DeviceIdentity;
import java.io.Closeable;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Proxy;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.cert.X509Certificate;
import java.util.Collections;
import java.util.Date;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.Semaphore;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import javax.net.ssl.SSLParameters;
import javax.net.ssl.SSLSocket;

/** Agent-only composition. No Binder entry accepts these paths or parameters. */
final class FrpRuntimeBridge implements Closeable {
    private final AgentRegistry registry;
    private final DeviceIdentity identity;
    private final X509Certificate serverCertificate,root;
    private final InetAddress serverAddress;
    private final int port;
    private final String application,applicationId,serverKey,namespace;
    private final long revision,epoch;
    private final File socketPath;
    private final LocalSocket boundSocket;
    private final LocalServerSocket listener;
    private final Object gate=new Object(),clockGate=new Object();
    private final Set<LocalSocket> local=new HashSet<>();
    private final Set<Socket> remote=new HashSet<>();
    private final AtomicBoolean stopped=new AtomicBoolean();
    private final Semaphore peers=new Semaphore(16),handshakes=new Semaphore(8);
    private final ThreadFactory factory=work->{Thread thread=new Thread(work,"LinkGate-Owned-Transport");thread.setDaemon(true);return thread;};
    private final ThreadPoolExecutor workers=new ThreadPoolExecutor(0,32,30,TimeUnit.SECONDS,new SynchronousQueue<>(),factory);
    private final ScheduledThreadPoolExecutor timers=new ScheduledThreadPoolExecutor(1,factory);
    private volatile Process child;
    private volatile int childPid;
    private volatile String reason="";
    private final AtomicInteger openedNetworks=new AtomicInteger(),authenticatedConnections=new AtomicInteger();
    private volatile String lastFailure="";
    private long effectiveUtc=System.currentTimeMillis(),lastMono=SystemClock.elapsedRealtimeNanos(),remainderNanos;

    private FrpRuntimeBridge(Context context,AgentRegistry registry,DeviceIdentity identity,String application,InetAddress address,int port,X509Certificate serverCertificate,X509Certificate root)throws Exception {
        if(Build.VERSION.SDK_INT<29)throw new UnsupportedOperationException("STANDARD_ALPN_REQUIRED");
        if(address==null||address.getAddress().length!=4||port<1||port>65535)throw new IllegalArgumentException("Explicit IPv4 server required.");
        this.registry=registry;this.identity=identity;this.application=WireJson.uuid(application).toString();this.serverAddress=InetAddress.getByAddress(address.getAddress());this.port=port;this.serverCertificate=serverCertificate;this.root=root;
        DeviceIdentity.validateDevice(serverCertificate,root);serverKey=DeviceIdentity.keyId(serverCertificate.getPublicKey());
        AgentRegistry.Review review=registry.review(application);
        if(review.approved==null||review.approvedRevision==null)throw new SecurityException("AUTH_REQUIRED");
        var manifest=ManifestContract.parse(review.approved.getBytes(StandardCharsets.UTF_8));
        if(manifest.imports.isEmpty())throw new SecurityException("AUTH_REQUIRED");
        applicationId=manifest.applicationId;revision=review.approvedRevision;epoch=review.authorityEpoch;
        namespace="lg1_"+digest(("namespace\n"+identity.deviceId()+"\n"+applicationId+"\naccess").getBytes(StandardCharsets.UTF_8));
        File directory=new File(context.getNoBackupFilesDir().getCanonicalFile(),"transport-v1");
        if((!directory.isDirectory()&&!directory.mkdir())||!directory.getCanonicalFile().equals(directory))throw new IOException("Private runtime directory unavailable.");
        Os.chmod(directory.getPath(),0700);socketPath=new File(directory,UUID.randomUUID().toString().replace("-","")+".sock");
        if(socketPath.getPath().getBytes(StandardCharsets.UTF_8).length>100)throw new IOException("Private socket path exceeds bound.");
        boundSocket=new LocalSocket(LocalSocket.SOCKET_STREAM);
        LocalServerSocket opened=null;
        try{boundSocket.bind(new LocalSocketAddress(socketPath.getPath(),LocalSocketAddress.Namespace.FILESYSTEM));Os.chmod(socketPath.getPath(),0600);opened=new LocalServerSocket(boundSocket.getFileDescriptor());listener=opened;}
        catch(Exception failure){quiet(opened);quiet(boundSocket);socketPath.delete();throw failure;}
        timers.setRemoveOnCancelPolicy(true);
    }

    static FrpRuntimeBridge start(Context context,AgentRegistry registry,DeviceIdentity identity,String application,InetAddress address,int port,X509Certificate certificate,X509Certificate root,File configuration)throws Exception {
        return start(context,registry,identity,application,address,port,certificate,root,configuration,value->{});
    }
    static FrpRuntimeBridge start(Context context,AgentRegistry registry,DeviceIdentity identity,String application,InetAddress address,int port,X509Certificate certificate,X509Certificate root,File configuration,java.util.function.Consumer<FrpRuntimeBridge> owner)throws Exception {
        return start(context,registry,identity,application,address,port,certificate,root,configuration,java.util.Collections.emptyList(),owner);
    }
    static FrpRuntimeBridge start(Context context,AgentRegistry registry,DeviceIdentity identity,String application,InetAddress address,int port,X509Certificate certificate,X509Certificate root,File configuration,java.util.List<String> endpoints,java.util.function.Consumer<FrpRuntimeBridge> owner)throws Exception {
        String stun=dev.linkgate.transport.StunEndpoints.environment(endpoints);
        File privateRoot=context.getNoBackupFilesDir().getCanonicalFile(),profile=configuration.getCanonicalFile();
        if(!profile.getPath().startsWith(privateRoot.getPath()+File.separator)||!profile.isFile()||profile.length()>65536)throw new IOException("Private bounded configuration required.");
        Os.chmod(profile.getPath(),0400);
        File[] runtime=runtime(context);File executable=runtime[0],launcher=runtime[1];
        FrpRuntimeBridge bridge=new FrpRuntimeBridge(context,registry,identity,application,address,port,certificate,root);
        try {
            owner.accept(bridge);
            ProcessBuilder builder=new ProcessBuilder(launcher.getPath(),Integer.toString(android.os.Process.myPid()),executable.getPath(),"-c",profile.getPath());
            builder.environment().put("LINKGATE_AGENT_PIPE",bridge.socketPath.getPath());builder.environment().put("LINKGATE_AGENT_PID",Integer.toString(android.os.Process.myPid()));builder.environment().put("LINKGATE_STUN_ENDPOINTS",stun);
            for(String name:new String[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy"})builder.environment().remove(name);
            builder.redirectErrorStream(true);
            ScheduledFuture<?> deadline=bridge.timers.schedule(()->bridge.fail("START_TIMEOUT"),3,TimeUnit.SECONDS);
            Process launched=builder.start();
            synchronized(bridge.gate){bridge.child=launched;if(bridge.stopped.get()){stopChild(launched);throw new IOException("Runtime start cancelled.");}}
            String header=line(launched.getInputStream());
            if(!header.matches("LINKGATE_CHILD [1-9][0-9]{0,9}"))throw new IOException("Owned child identity unavailable.");
            long number=Long.parseLong(header.substring(15));if(number<=1||number>Integer.MAX_VALUE||!launched.isAlive())throw new IOException("Owned child exited.");bridge.childPid=(int)number;
            if(!bridge.current())throw new SecurityException("AUTH_REQUIRED");deadline.cancel(false);
            bridge.workers.execute(()->{try{byte[] discard=new byte[2048];while(launched.getInputStream().read(discard)!=-1){}}catch(IOException ignored){}});
            Thread accepting=bridge.factory.newThread(bridge::accept);accepting.start();
            bridge.timers.scheduleWithFixedDelay(()->{try{if(!bridge.childAlive())bridge.fail("RUNTIME_EXITED");else if(!bridge.current())bridge.fail("AUTHORITY_CHANGED");}catch(Exception failure){bridge.fail("AUTHORITY_CHANGED");}},250,250,TimeUnit.MILLISECONDS);
            return bridge;
        }catch(Exception failure){bridge.close();throw failure;}
    }

    static boolean available(Context context)throws Exception {
        if(Build.VERSION.SDK_INT<29)return false;
        File directory=new File(context.getApplicationInfo().nativeLibraryDir);boolean executable=new File(directory,"libfrpc.so").exists(),launcher=new File(directory,"liblinkgate-agent-launcher.so").exists(),lock=false;
        for(String name:context.getAssets().list(""))if(name.equals("agent-runtime.lock.json"))lock=true;
        if(!executable&&!launcher&&!lock)return false;
        runtime(context);return true;
    }
    private static File[] runtime(Context context)throws Exception {
        File directory=new File(context.getApplicationInfo().nativeLibraryDir).getCanonicalFile();
        File executable=new File(directory,"libfrpc.so"),launcher=new File(directory,"liblinkgate-agent-launcher.so");
        Map<String,Object> lock;
        try(InputStream input=context.getAssets().open("agent-runtime.lock.json")){lock=WireJson.read(read(input,8192),8192);}
        WireJson.fields(lock,"schema","sourceCommit","goVersion","abis");WireJson.integer(lock.get("schema"),1,1);
        if(!"d8cf1800dc1abd7fda1828cab105d7a6b5a50eb1".equals(lock.get("sourceCommit")))throw new IOException("Runtime source rejected.");
        Map<String,Object> abis=object(lock.get("abis"));String abi=null;
        for(String supported:Build.SUPPORTED_ABIS)if(abis.containsKey(supported)){abi=supported;break;}
        if(abi==null)throw new UnsupportedOperationException("RUNTIME_ABI_UNSUPPORTED");
        Map<String,Object> hashes=object(abis.get(abi));WireJson.fields(hashes,"frpcSha256","launcherSha256");
        verify(executable,WireJson.key(hashes.get("frpcSha256")),directory);verify(launcher,WireJson.key(hashes.get("launcherSha256")),directory);
        return new File[]{executable,launcher};
    }

    private long now()throws IOException {
        synchronized(clockGate){long mono=SystemClock.elapsedRealtimeNanos(),delta=Math.subtractExact(mono,lastMono);if(delta<0)throw new IOException("Monotonic clock unavailable.");long elapsed=Math.addExact(delta,remainderNanos),floor=Math.addExact(effectiveUtc,elapsed/1000000L),wall=System.currentTimeMillis();remainderNanos=elapsed%1000000L;effectiveUtc=Math.max(floor,wall);if(wall>floor)remainderNanos=0;lastMono=mono;return effectiveUtc;}
    }
    private boolean current()throws Exception {
        if(stopped.get()||child==null||!child.isAlive())return false;
        long time=now();if(time>=identity.expiresUtcMs())return false;
        DeviceIdentity.validateDevice(serverCertificate,root,new Date(time));
        AgentRegistry.Review review=registry.review(application);
        return review.approvedRevision!=null&&review.approvedRevision==revision&&review.authorityEpoch==epoch&&review.approved!=null&&ManifestContract.parse(review.approved.getBytes(StandardCharsets.UTF_8)).applicationId.equals(applicationId);
    }
    private boolean actual(LocalSocket socket)throws IOException {
        Process process=child;if(stopped.get()||process==null||!process.isAlive())return false;
        Credentials peer=socket.getPeerCredentials();return peer.getUid()==android.os.Process.myUid()&&peer.getPid()==childPid&&process.isAlive();
    }
    private void accept() {
        try{while(!stopped.get()){
            LocalSocket socket=listener.accept();
            if(!peers.tryAcquire()){socket.close();continue;}
            synchronized(gate){if(stopped.get()){socket.close();peers.release();break;}local.add(socket);}
            try{workers.execute(()->handle(socket));}catch(RuntimeException failure){synchronized(gate){local.remove(socket);}socket.close();peers.release();}
        }}catch(IOException failure){if(!stopped.get())fail("BRIDGE_FAILED");}
    }
    private void handle(LocalSocket socket) {
        Socket raw=null;boolean opening=false;ScheduledFuture<?> deadline=null;
        try {
            if(!actual(socket)||!current()||!handshakes.tryAcquire())return;opening=true;
            raw=new Socket(Proxy.NO_PROXY);raw.setTcpNoDelay(true);Socket owned=raw;
            synchronized(gate){if(stopped.get())return;remote.add(raw);}
            openedNetworks.incrementAndGet();
            deadline=timers.schedule(()->{quiet(owned);quiet(socket);},3,TimeUnit.SECONDS);
            raw.connect(new InetSocketAddress(serverAddress,port),3000);
            try(SSLSocket secure=(SSLSocket)identity.tls(serverKey).socketFactory.createSocket(raw,"linkgate.device",port,true)) {
                secure.setUseClientMode(true);secure.setSoTimeout(3000);SSLParameters parameters=secure.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");parameters.setApplicationProtocols(new String[]{"linkgate-frp/1"});parameters.setProtocols(new String[]{"TLSv1.3","TLSv1.2"});secure.setSSLParameters(parameters);secure.startHandshake();
                if(!"linkgate-frp/1".equals(secure.getApplicationProtocol())||!actual(socket)||!current())return;
                authenticatedConnections.incrementAndGet();
                byte[] scope=WireJson.write(WireJson.map("kind","linkgate.bridge","apiVersion",1,"applicationId",applicationId,"role","access"),4096);
                secure.getOutputStream().write(ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(scope.length).array());secure.getOutputStream().write(scope);secure.getOutputStream().flush();
                deadline.cancel(false);deadline=null;secure.setSoTimeout(0);socket.setSoTimeout(0);handshakes.release();opening=false;
                workers.execute(()->{try{copy(secure.getInputStream(),socket.getOutputStream());}catch(IOException ignored){}finally{quiet(owned);quiet(socket);}});
                try{copy(socket.getInputStream(),secure.getOutputStream());}finally{quiet(raw);quiet(socket);}
            }
        }catch(Exception failure){lastFailure=failure instanceof javax.net.ssl.SSLException?"TLS_FAILED":failure instanceof SecurityException?"AUTH_REQUIRED":failure instanceof IOException?"TRANSPORT_FAILED":"BRIDGE_FAILED";}
        finally{if(deadline!=null)deadline.cancel(false);quiet(raw);quiet(socket);if(opening)handshakes.release();synchronized(gate){local.remove(socket);if(raw!=null)remote.remove(raw);}peers.release();}
    }
    boolean isStopped(){return stopped.get();}
    boolean childAlive(){Process process=child;return process!=null&&process.isAlive();}
    String namespace(){return namespace;}
    String socketPath(){return socketPath.getPath();}
    String stopReason(){return reason;}
    int openedNetworks(){return openedNetworks.get();}
    int authenticatedConnections(){return authenticatedConnections.get();}
    String lastFailure(){return lastFailure;}
    private void fail(String code){synchronized(gate){if(reason.isEmpty())reason=code;}close();}
    @Override public void close(){
        if(!stopped.compareAndSet(false,true))return;
        synchronized(gate){if(reason.isEmpty())reason="STOPPED";try{Os.shutdown(boundSocket.getFileDescriptor(),android.system.OsConstants.SHUT_RDWR);}catch(Exception ignored){}quiet(listener);quiet(boundSocket);for(Socket socket:remote)quiet(socket);for(LocalSocket socket:local)quiet(socket);stopChild(child);}
        timers.shutdownNow();workers.shutdownNow();socketPath.delete();
    }
    private static void copy(InputStream input,OutputStream output)throws IOException{byte[] buffer=new byte[16384];int n;while((n=input.read(buffer))!=-1){if(n==0)throw new IOException("Transport stalled.");output.write(buffer,0,n);}}
    private static byte[] read(InputStream input,int limit)throws IOException{var output=new java.io.ByteArrayOutputStream();byte[] buffer=new byte[2048];int n;while((n=input.read(buffer))!=-1){if(output.size()+n>limit)throw new IOException("Bounded runtime input required.");output.write(buffer,0,n);}return output.toByteArray();}
    private static String line(InputStream input)throws IOException{var output=new java.io.ByteArrayOutputStream();for(int i=0;i<64;i++){int value=input.read();if(value<0)throw new IOException("Owned child identity unavailable.");if(value==10)return new String(output.toByteArray(),StandardCharsets.US_ASCII);if(value<32||value>126)throw new IOException("Owned child identity invalid.");output.write(value);}throw new IOException("Owned child identity exceeds bound.");}
    @SuppressWarnings("unchecked") private static Map<String,Object> object(Object value)throws IOException{if(!(value instanceof Map))throw new IOException("Runtime manifest unavailable.");return (Map<String,Object>)value;}
    private static String digest(byte[] value)throws Exception{return hex(MessageDigest.getInstance("SHA-256").digest(value));}
    private static String hex(byte[] bytes){StringBuilder result=new StringBuilder();for(byte value:bytes){result.append(Character.forDigit((value>>>4)&15,16));result.append(Character.forDigit(value&15,16));}return result.toString();}
    private static void verify(File file,String hash,File directory)throws Exception{if(!file.getCanonicalFile().getParentFile().equals(directory)||!file.isFile()||!file.canExecute()||file.length()>67108864)throw new IOException("Packaged runtime unavailable.");MessageDigest digest=MessageDigest.getInstance("SHA-256");try(InputStream input=new FileInputStream(file)){byte[] buffer=new byte[65536];int n;while((n=input.read(buffer))!=-1)digest.update(buffer,0,n);}if(!MessageDigest.isEqual(hash.getBytes(StandardCharsets.US_ASCII),hex(digest.digest()).getBytes(StandardCharsets.US_ASCII)))throw new IOException("Packaged runtime integrity rejected.");}
    private static void quiet(Closeable value){if(value!=null)try{value.close();}catch(IOException ignored){}}
    private static void stopChild(Process process){if(process!=null){process.destroyForcibly();quiet(process.getInputStream());quiet(process.getOutputStream());quiet(process.getErrorStream());}}
}
