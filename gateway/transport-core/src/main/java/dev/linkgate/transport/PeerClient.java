package dev.linkgate.transport;

import dev.linkgate.contracts.WireJson;
import java.io.Closeable;
import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.security.GeneralSecurityException;
import java.util.Map;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.Executor;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.SSLSocket;

public final class PeerClient implements Closeable {
    public static final class Refused extends IOException {public final String code;private Refused(String code){super("Authenticated peer refused the connection.");this.code=code;}}
    public interface Clock { long nanos(); }
    public interface LeaseSource extends AutoCloseable { String token()throws IOException; @Override void close(); }
    public interface SocketConnector { Socket connect(InetAddress address,int port,int remainingMs,Cancellation cancellation)throws IOException; }
    public static final class Cancellation implements Closeable {
        private final java.util.List<Socket> sockets=new java.util.ArrayList<>();private boolean closed;
        public synchronized void track(Socket socket)throws IOException {if(closed){socket.close();throw new IOException("Peer opening cancelled.");}sockets.add(socket);}
        public synchronized boolean isClosed(){return closed;}
        @Override public synchronized void close(){if(closed)return;closed=true;for(Socket socket:sockets)quiet(socket);sockets.clear();}
    }
    private final SSLSocket control,data;
    private final Socket rawControl,rawData;
    private final Cancellation cancellation;
    private final LeaseSource leaseSource;
    public final BusinessChannel stream;
    private final AtomicBoolean closed=new AtomicBoolean();
    private final AtomicBoolean disposed=new AtomicBoolean();
    private volatile ScheduledFuture<?> renewal;
    private PeerClient(SSLSocket control,SSLSocket data,Socket rawControl,Socket rawData,String ticket,DeviceIdentity identity,ScheduledExecutorService scheduler,Executor controlIo,Cancellation cancellation,LeaseSource leaseSource) throws IOException {
        this.control=control;this.data=data;this.rawControl=rawControl;this.rawData=rawData;this.cancellation=cancellation;this.leaseSource=leaseSource;stream=new BusinessChannel(data.getInputStream(),data.getOutputStream(),data);
        AtomicBoolean renewing=new AtomicBoolean();
        renewal=scheduler.scheduleWithFixedDelay(()->{
            if(closed.get()||!renewing.compareAndSet(false,true))return;
            try{controlIo.execute(()->{
              ScheduledFuture<?> deadline=scheduler.schedule(this::abort,8,TimeUnit.SECONDS);
              try {
                if(System.currentTimeMillis()>=identity.expiresUtcMs())throw new IOException("Device certificate expired.");
                write(control,leaseSource==null?WireJson.map("kind","renew","ticket",ticket):WireJson.map("kind","renew","ticket",ticket,"lease",leaseToken(leaseSource)));Map<String,Object> lease=read(control);WireJson.fields(lease,"kind","seconds");
                if(!"lease".equals(lease.get("kind")))throw new IOException("Invalid authorization renewal.");WireJson.integer(lease.get("seconds"),1,120);
              }catch(Exception e){try{close();}catch(IOException ignored){ }}
              finally{deadline.cancel(false);renewing.set(false);}
            });}catch(RuntimeException e){try{close();}catch(IOException ignored){ }}
        },1,1,TimeUnit.SECONDS);
        if(closed.get())renewal.cancel(false);
    }
    public boolean isClosed(){return closed.get();}
    public static PeerClient open(DeviceIdentity identity,String peer,InetAddress address,int port,String client,String publisher,String service,String protocol,String authority,int budgetMs,ScheduledExecutorService scheduler,Executor controlIo,Clock clock,SocketConnector connector,Cancellation cancellation)throws IOException,GeneralSecurityException {
        return open(identity,peer,address,port,client,publisher,service,protocol,authority,budgetMs,scheduler,controlIo,clock,connector,cancellation,null);
    }
    public static PeerClient open(DeviceIdentity identity,String peer,InetAddress address,int port,String client,String publisher,String service,String protocol,String authority,int budgetMs,ScheduledExecutorService scheduler,Executor controlIo,Clock clock,SocketConnector connector,Cancellation cancellation,LeaseSource leaseSource)throws IOException,GeneralSecurityException {
        if(address==null||address.getAddress().length!=4||port<1||port>65535||budgetMs<1||budgetMs>60000||scheduler==null||controlIo==null||clock==null||connector==null)throw new IllegalArgumentException("Explicit IPv4 endpoint and bounded budget required.");
        WireJson.key(peer);
        if(client==null||client.length()>128||!client.matches("[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+")||publisher==null||publisher.length()>128||!publisher.matches("[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+")||service==null||!service.matches("[a-z][a-z0-9-]{0,31}")||!java.util.Arrays.asList("tcp","http","https","ws","wss").contains(protocol))throw new IllegalArgumentException("Invalid service scope.");
        if(authority!=null)WireJson.text(authority,253);
        long started=clock.nanos();Socket rawControl=null,rawData=null;SSLSocket control=null,data=null;
        if(cancellation==null||cancellation.isClosed())throw new IOException("Peer opening cancelled.");
        ScheduledFuture<?> deadline=scheduler.schedule(cancellation::close,budgetMs,TimeUnit.MILLISECONDS);
        try {
            rawControl=connector.connect(address,port,remaining(started,budgetMs,clock),cancellation);
            remaining(started,budgetMs,clock);
            control=identity.connect(rawControl,port,peer,remaining(started,budgetMs,clock));
            write(control,leaseSource==null?WireJson.map("kind","open","client",client,"publisher",publisher,"service",service,"protocol",protocol,"authority",authority):WireJson.map("kind","open","client",client,"publisher",publisher,"service",service,"protocol",protocol,"authority",authority,"lease",leaseToken(leaseSource)));
            control.setSoTimeout(remaining(started,budgetMs,clock));Map<String,Object> result=read(control);WireJson.fields(result,"ticket","grant","revision");String ticket=WireJson.key(result.get("ticket"));WireJson.uuid(result.get("grant"));WireJson.integer(result.get("revision"),1,Long.MAX_VALUE-1);
            rawData=connector.connect(address,port,remaining(started,budgetMs,clock),cancellation);data=identity.connect(rawData,port,peer,remaining(started,budgetMs,clock));
            write(data,WireJson.map("kind","data","ticket",ticket));data.setSoTimeout(remaining(started,budgetMs,clock));Map<String,Object> ready=read(data);WireJson.fields(ready,"kind");if(!"ready".equals(ready.get("kind")))throw new IOException("Data attachment rejected.");
            remaining(started,budgetMs,clock);if(cancellation.isClosed())throw new IOException("Peer opening cancelled.");control.setSoTimeout(8000);data.setSoTimeout(0);return new PeerClient(control,data,rawControl,rawData,ticket,identity,scheduler,controlIo,cancellation,leaseSource);
        }catch(IOException|GeneralSecurityException|RuntimeException e){cancellation.close();quiet(rawData);quiet(rawControl);quiet(data);quiet(control);if(leaseSource!=null)leaseSource.close();throw e;}
        finally{deadline.cancel(false);}
    }
    public static Socket connectTcp(InetAddress address,int port,int timeoutMs,Cancellation cancellation)throws IOException {
        Socket socket=new Socket();try{cancellation.track(socket);socket.setTcpNoDelay(true);socket.connect(new InetSocketAddress(address,port),timeoutMs);return socket;}catch(IOException e){socket.close();throw e;}
    }
    private static String leaseToken(LeaseSource source)throws IOException {String value=source.token();if(value==null||value.isEmpty()||value.length()>16384)throw new IOException("Invalid server authority.");return value;}
    private static int remaining(long started,int budget,Clock clock)throws IOException {
        long elapsed=clock.nanos()-started;if(elapsed<0)throw new IOException("Clock unavailable.");long left=budget-TimeUnit.NANOSECONDS.toMillis(elapsed);if(left<=0)throw new java.net.SocketTimeoutException("Peer opening deadline elapsed.");return (int)left;
    }
    private static Map<String,Object> read(SSLSocket socket)throws IOException {
        try{Map<String,Object> value=WireJson.read(BusinessChannel.readFrame(socket.getInputStream(),65536),65536);if("refused".equals(value.get("kind"))){WireJson.fields(value,"kind","code");Object code=value.get("code");if(!"AUTH_REQUIRED".equals(code)&&!"RESOURCE_EXHAUSTED".equals(code))throw new IllegalArgumentException();throw new Refused((String)code);}return value;}catch(RuntimeException e){throw new IOException("Invalid peer message.");}
    }
    private static void write(SSLSocket socket,Object value)throws IOException {BusinessChannel.writeFrame(socket.getOutputStream(),WireJson.write(value,65536),65536);}
    private static void quiet(Closeable value){if(value!=null)try{value.close();}catch(IOException ignored){ }}
    @Override public void close()throws IOException {
        abort();if(!disposed.compareAndSet(false,true))return;quiet(control);quiet(data);
    }
    public void abort(){closed.set(true);ScheduledFuture<?> job=renewal;if(job!=null)job.cancel(false);cancellation.close();quiet(rawControl);quiet(rawData);if(leaseSource!=null)leaseSource.close();}
}
