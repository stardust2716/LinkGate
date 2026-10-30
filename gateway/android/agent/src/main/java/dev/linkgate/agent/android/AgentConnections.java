package dev.linkgate.agent.android;

import android.os.IBinder;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.authority.ControlClient;
import dev.linkgate.authority.PeerLeaseSource;
import dev.linkgate.authority.AuthoritySnapshot;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.contracts.ManifestContract;
import dev.linkgate.contracts.ConnectionPolicy;
import dev.linkgate.transport.ConnectionPlanner;
import dev.linkgate.descriptors.DescriptorStream;
import dev.linkgate.descriptors.DescriptorWire;
import dev.linkgate.transport.DeviceIdentity;
import dev.linkgate.transport.PeerClient;
import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

final class AgentConnections implements Closeable {
    private final AgentRegistry registry;private final DeviceIdentity identity;private final UUID boot=UUID.randomUUID();
    private final ConcurrentHashMap<String,Entry> active=new ConcurrentHashMap<>();private final Object admission=new Object();private final AtomicBoolean closed=new AtomicBoolean();
    private final ThreadFactory threads=work->{Thread thread=new Thread(work,"LinkGate-Agent");thread.setDaemon(true);return thread;};
    private final ScheduledThreadPoolExecutor timers=new ScheduledThreadPoolExecutor(1,threads);
    private final ThreadPoolExecutor setup=new ThreadPoolExecutor(0,4,30,TimeUnit.SECONDS,new SynchronousQueue<>(),threads),copy=new ThreadPoolExecutor(0,64,30,TimeUnit.SECONDS,new SynchronousQueue<>(),threads),control=new ThreadPoolExecutor(0,8,30,TimeUnit.SECONDS,new SynchronousQueue<>(),threads);
    private final ThreadPoolExecutor directIo=new ThreadPoolExecutor(0,4,30,TimeUnit.SECONDS,new SynchronousQueue<>(),threads),relayIo=new ThreadPoolExecutor(0,4,30,TimeUnit.SECONDS,new SynchronousQueue<>(),threads);
    private final ConnectionPlanner<Opened> planner=new ConnectionPlanner<>(SystemClock::elapsedRealtimeNanos,directIo,relayIo);
    private final ThreadPoolExecutor writes=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(128),threads);
    private final ControlClient authority;private final FrpRoutes routes;final FormalConnections formal;private NetworkObserver networkObserver;private volatile boolean networkChanging;
    private final AtomicInteger directLanReady=new AtomicInteger(),directXtcpReady=new AtomicInteger();
    AgentConnections(AgentRegistry registry,DeviceIdentity identity){this(registry,identity,null,null);}
    AgentConnections(AgentRegistry registry,DeviceIdentity identity,ControlClient authority,FrpRoutes routes){this(null,registry,identity,authority,routes);}
    AgentConnections(android.content.Context context,AgentRegistry registry,DeviceIdentity identity,ControlClient authority,FrpRoutes routes){this.registry=registry;this.identity=identity;this.authority=authority;this.routes=routes;registry.recoverOpens(boot);formal=new FormalConnections(registry,identity,authority,routes,boot,timers,setup,copy,control,writes,directIo,relayIo);timers.setRemoveOnCancelPolicy(true);timers.scheduleWithFixedDelay(this::monitor,500,500,TimeUnit.MILLISECONDS);if(context!=null)try{networkObserver=new NetworkObserver(context,this::networkChanged);}catch(RuntimeException failure){close();throw failure;}}
    Map<String,Object> open(String caller,UUID operation,String application,String alias,String device,String publisher,String service) {
        return open(caller,operation,application,alias,device,publisher,service,false);
    }
    boolean relayAvailable(){return routes!=null&&!closed.get();}
    Map<String,Object> openRelay(String caller,UUID operation,String application,String alias,String device,String publisher,String service){return open(caller,operation,application,alias,device,publisher,service,true);}
    private Map<String,Object> open(String caller,UUID operation,String application,String alias,String device,String publisher,String service,boolean relay) {
        long admitted=SystemClock.elapsedRealtimeNanos();
        synchronized(admission) {
            if(relay&&routes==null)throw new AgentRegistry.Failure("CAPABILITY_UNSUPPORTED");
            if(closed.get())throw new IllegalStateException("GATEWAY_STOPPED");if(networkChanging)throw new AgentRegistry.Failure("RESOURCE_EXHAUSTED");AgentRegistry.Open saved=relay?registry.beginRelayOpen(caller,operation,application,alias,device,publisher,service,boot):registry.beginOpen(caller,operation,application,alias,device,publisher,service,boot);
            if(saved.created) {
                long own=0,total=0;for(Entry entry:active.values())if(!entry.closed.get()){total++;if(entry.scope.caller.equals(caller))own++;}
                if(total>=32||own>=16){registry.setOpen(caller,operation,boot,"closed","RESOURCE_EXHAUSTED");return status(caller,operation);}
                Entry entry=new Entry(saved,admitted);active.put(key(caller,operation),entry);
                try{setup.execute(()->initialize(entry));}catch(RuntimeException e){entry.abort("RESOURCE_EXHAUSTED");}
            }
            return status(caller,operation);
        }
    }
    Map<String,Object> status(String caller,UUID operation) {
        AgentRegistry.Open saved=registry.openStatus(caller,operation);Entry entry=active.get(key(caller,operation));String state=saved.state,error=saved.error;
        if(entry!=null&&entry.closed.get()){state="closed";error=entry.error;}else if(entry==null&&state.equals("ready")){state="closed";error="CONNECTION_CLOSED";}
        return WireJson.map("connectionId",saved.reference,"operationId",operation.toString(),"state",state,"error",error,"dataPipe",null,"path",saved.path);
    }
    Map<String,Object> close(String caller,UUID operation){registry.openStatus(caller,operation);Entry entry=active.get(key(caller,operation));if(entry!=null)entry.abort("CONNECTION_CLOSED");registry.setOpen(caller,operation,boot,"closed","CONNECTION_CLOSED");return status(caller,operation);}
    ParcelFileDescriptor attach(String caller,UUID operation,IBinder token)throws Exception {
        if(token==null||!token.isBinderAlive())throw new SecurityException("AUTH_REQUIRED");AgentRegistry.Open saved=registry.openStatus(caller,operation);Entry entry=active.get(key(caller,operation));
        if(entry==null||entry.closed.get()||!saved.state.equals("ready")||entry.peer==null||!registry.currentScopes(java.util.Collections.singletonList(entry.scope))[0]||!entry.attached.compareAndSet(false,true))throw new SecurityException("AUTH_REQUIRED");
        ParcelFileDescriptor[] pair=null;DescriptorStream descriptor=null;
        try {
            pair=ParcelFileDescriptor.createReliableSocketPair();descriptor=new DescriptorStream(pair[0],entry);entry.descriptor=descriptor;
            IBinder.DeathRecipient death=()->entry.abort("CONNECTION_CLOSED");entry.token=token;entry.death=death;token.linkToDeath(death,0);
            if(entry.closed.get())throw new IOException("Connection closed.");
            copy.execute(()->serveDescriptor(entry));return pair[1];
        }catch(Exception e){quiet(descriptor);if(pair!=null){quiet(pair[0]);quiet(pair[1]);}entry.abort("CONNECTION_CLOSED");throw e;}
    }
    private static final class Opened implements Closeable {
        final PeerClient peer;final String kind;Opened(PeerClient peer,String kind){this.peer=peer;this.kind=kind;}
        @Override public void close(){peer.abort();quiet(peer);}
    }
    private void initialize(Entry entry) {
        ConnectionPlanner<Opened>.Result result=null;
        try {
            if(entry.closed.get()||!registry.currentScopes(java.util.Collections.singletonList(entry.scope))[0])throw new SecurityException("AUTH_REQUIRED");
            long deadline=Math.addExact(entry.admitted,TimeUnit.MILLISECONDS.toNanos(entry.scope.timeoutMs)),left=deadline-SystemClock.elapsedRealtimeNanos();if(left<=0)throw new IOException("Open deadline elapsed.");
            entry.openDeadline=timers.schedule(()->entry.abort(entry.path.equals("relay")?"RELAY_FAILED":"DIRECT_FAILED"),left,TimeUnit.NANOSECONDS);
            ConnectionPolicy policy=entry.path.equals("relay")?ManifestContract.normalizePolicy(WireJson.write(WireJson.map("mode","fast-connect","connectTimeoutMs",entry.scope.timeoutMs,"directWaitMs",0,"allowRelay",true),8192)):entry.scope.policy;
            result=planner.connect(policy,entry.admitted,entry.closed::get,context->openDirect(entry,context),context->openTransport(entry,context,"relay",Long.MAX_VALUE));
            if(!entry.path.equals(result.path)||!registry.currentScopes(java.util.Collections.singletonList(entry.scope))[0])throw new SecurityException("AUTH_REQUIRED");
            synchronized(entry){if(entry.closed.get()||closed.get())throw new IOException("Open cancelled.");Opened selected=result.value();entry.peer=selected.peer;entry.planned=result;if(selected.kind.equals("lan"))directLanReady.incrementAndGet();else if(selected.kind.equals("xtcp"))directXtcpReady.incrementAndGet();result=null;}
            entry.openDeadline.cancel(false);entry.attachDeadline=timers.schedule(()->entry.abort("CONNECTION_CLOSED"),10,TimeUnit.SECONDS);registry.setOpen(entry.scope.caller,entry.operation,boot,"ready",null);
        }catch(Exception failure){entry.abort(failure(failure,entry.path));}finally{if(result!=null)result.close();}
    }
    private Opened openDirect(Entry entry,ConnectionPlanner.Context context)throws Exception {
        boolean xtcp=authority!=null&&routes!=null&&routes.canDirect();
        long lanEnd=xtcp?Math.addExact(SystemClock.elapsedRealtimeNanos(),TimeUnit.MILLISECONDS.toNanos(Math.min(1000,Math.max(250,context.remainingMillis()/4)))):context.deadlineNanos();
        try{return openTransport(entry,context,"lan",lanEnd);}
        catch(ConnectionPlanner.Failure failure){if(!xtcp||failure.code!=ConnectionPlanner.Code.DIRECT_FAILED&&failure.code!=ConnectionPlanner.Code.CONNECT_TIMEOUT)throw failure;}
        return openTransport(entry,context,"xtcp",context.deadlineNanos());
    }
    private Opened openTransport(Entry entry,ConnectionPlanner.Context context,String path,long candidateDeadline)throws Exception {
        synchronized(entry){if(entry.closed.get()||context.cancelled())throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CANCELLED);entry.opening=context;}
        PeerLeaseSource lease=null;FrpRoutes.Route route=null;PeerClient.Cancellation cancellation=new PeerClient.Cancellation();context.track(cancellation);
        try {
            context.remainingMillis();if(!registry.currentScopes(java.util.Collections.singletonList(entry.scope))[0])throw new SecurityException("AUTH_REQUIRED");
            if(authority!=null){lease=PeerLeaseSource.acquire(authority,entry.scope.client,new AuthoritySnapshot.Target(entry.scope.device,entry.scope.publisher,entry.scope.service),entry.scope.protocol,entry.scope.authority);context.track(lease);}
            String host=entry.scope.host;int port=entry.scope.port;
            if(!path.equals("lan")){if(routes==null)throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CAPABILITY_UNSUPPORTED);route=path.equals("xtcp")?routes.openDirect(entry.scope,context.deadlineNanos(),context::cancelled):routes.openRelay(entry.scope,context.deadlineNanos(),context::cancelled);context.track(route);host="127.0.0.1";port=route.port;}
            int remaining=(int)Math.min(context.remainingMillis(),Math.max(0,TimeUnit.NANOSECONDS.toMillis(candidateDeadline-SystemClock.elapsedRealtimeNanos())));if(remaining<1)throw new java.net.SocketTimeoutException("Candidate deadline elapsed.");
            String[] fields=host.split("\\.");byte[] bytes=new byte[4];for(int i=0;i<4;i++)bytes[i]=(byte)Integer.parseInt(fields[i]);
            PeerClient peer=PeerClient.open(identity,entry.scope.device,InetAddress.getByAddress(bytes),port,entry.scope.client,entry.scope.publisher,entry.scope.service,entry.scope.protocol,entry.scope.authority,remaining,timers,control,SystemClock::elapsedRealtimeNanos,PeerClient::connectTcp,cancellation,lease);lease=null;context.track(peer);return new Opened(peer,path);
        }catch(Exception failure){cancellation.close();if(lease!=null)lease.close();if(route!=null)route.close();throw planningFailure(failure,path);}
    }
    private static ConnectionPlanner.Failure planningFailure(Exception failure,String path){
        if(failure instanceof ConnectionPlanner.Failure)return (ConnectionPlanner.Failure)failure;
        String code=failure(failure,path.equals("relay")?"relay":"direct");
        if(code.equals("AUTH_REQUIRED"))return new ConnectionPlanner.Failure(ConnectionPlanner.Code.AUTH_REQUIRED);
        if(code.equals("RESOURCE_EXHAUSTED"))return new ConnectionPlanner.Failure(ConnectionPlanner.Code.RESOURCE_EXHAUSTED);
        return new ConnectionPlanner.Failure(path.equals("relay")?ConnectionPlanner.Code.RELAY_FAILED:ConnectionPlanner.Code.DIRECT_FAILED);
    }
    private static String failure(Exception failure,String path){
        if(failure instanceof ConnectionPlanner.Failure){ConnectionPlanner.Code code=((ConnectionPlanner.Failure)failure).code;if(code==ConnectionPlanner.Code.AUTH_REQUIRED||code==ConnectionPlanner.Code.CLOCK_UNTRUSTED)return "AUTH_REQUIRED";if(code==ConnectionPlanner.Code.RESOURCE_EXHAUSTED)return "RESOURCE_EXHAUSTED";}
        if(failure instanceof PeerClient.Refused)return ((PeerClient.Refused)failure).code;
        if(failure instanceof SecurityException||failure instanceof java.security.GeneralSecurityException)return "AUTH_REQUIRED";
        if(failure instanceof dev.linkgate.transport.OwnedRuntimePool.Exhausted)return "RESOURCE_EXHAUSTED";
        if(failure instanceof ControlClient.Refused){String code=((ControlClient.Refused)failure).code;if(java.util.Arrays.asList("AUTH_REQUIRED","IDENTITY_CHANGED","REVIEW_STALE","CLOCK_UNTRUSTED").contains(code))return "AUTH_REQUIRED";if(code.equals("RESOURCE_EXHAUSTED"))return code;}
        return path.equals("relay")?"RELAY_FAILED":"DIRECT_FAILED";
    }
    private void serveDescriptor(Entry entry) {
        try {
            DescriptorStream descriptor=entry.descriptor;Map<String,Object> request=DescriptorWire.read(descriptor.input());WireJson.fields(request,"kind");
            if(!"attach".equals(request.get("kind"))||entry.closed.get()||!registry.currentScopes(java.util.Collections.singletonList(entry.scope))[0])throw new SecurityException("AUTH_REQUIRED");
            DescriptorWire.write(descriptor.output(),WireJson.map("kind","ready"));ScheduledFuture<?> deadline=entry.attachDeadline;if(deadline!=null)deadline.cancel(false);
            copy.execute(()->pumpInbound(entry));
            pump(descriptor.input(),entry.peer.stream.output);entry.peer.stream.finishWrites();entry.finished();
        }catch(Exception e){entry.abort(e instanceof SecurityException?"AUTH_REQUIRED":"CONNECTION_CLOSED");}
    }
    private void pumpInbound(Entry entry) {
        try{pump(entry.peer.stream.input,entry.descriptor.output());entry.descriptor.finishWrites();entry.finished();}
        catch(Exception e){entry.abort("CONNECTION_CLOSED");}
    }
    private static void pump(InputStream input,OutputStream output)throws IOException {byte[] buffer=new byte[16384];int n;while((n=input.read(buffer))!=-1){if(n==0)throw new IOException("Stream made no progress.");output.write(buffer,0,n);}}
    private void monitor() {
        if(closed.get())return;
        try {formal.monitor();
            List<Entry> entries=new ArrayList<>();for(Entry entry:active.values())if(!entry.closed.get())entries.add(entry);
            List<AgentRegistry.Scope> scopes=new ArrayList<>();for(Entry entry:entries)scopes.add(entry.scope);boolean[] valid=registry.currentScopes(scopes);
            for(int i=0;i<entries.size();i++){Entry entry=entries.get(i);if(!valid[i])entry.abort("AUTH_REQUIRED");else if(entry.peer!=null&&entry.peer.isClosed())entry.abort("CONNECTION_CLOSED");}
        }catch(RuntimeException e){for(Entry entry:active.values())entry.abort("AUTH_REQUIRED");}
    }
    private final class Entry {
        final AgentRegistry.Scope scope;final UUID operation;final String reference,path;final long admitted;
        final AtomicBoolean attached=new AtomicBoolean(),closed=new AtomicBoolean();final AtomicInteger completed=new AtomicInteger();
        volatile ConnectionPlanner.Context opening;volatile ConnectionPlanner<Opened>.Result planned;volatile ScheduledFuture<?> openDeadline;volatile PeerClient peer;volatile DescriptorStream descriptor;volatile ScheduledFuture<?> attachDeadline;volatile IBinder token;volatile IBinder.DeathRecipient death;volatile String error="CONNECTION_CLOSED";
        Entry(AgentRegistry.Open saved,long admitted){this.admitted=admitted;scope=saved.scope;path=saved.path;operation=UUID.fromString(saved.operation);reference=saved.reference;}
        void finished(){if(completed.incrementAndGet()==2){ScheduledFuture<?> deadline=timers.schedule(peer::abort,2,TimeUnit.SECONDS);try{peer.stream.drainWriteCompletion();}catch(IOException ignored){}finally{deadline.cancel(false);abort("CONNECTION_CLOSED");quiet(peer);}}}
        void abort(String code) {
            if(!closed.compareAndSet(false,true))return;error=code;ConnectionPlanner.Context pending;ConnectionPlanner<Opened>.Result selected;synchronized(this){pending=opening;selected=planned;}if(pending!=null)pending.close();if(selected!=null)selected.close();ScheduledFuture<?> opening=openDeadline;if(opening!=null)opening.cancel(false);PeerClient current=peer;if(current!=null)current.abort();quiet(descriptor);ScheduledFuture<?> deadline=attachDeadline;if(deadline!=null)deadline.cancel(false);
            IBinder owner=token;IBinder.DeathRecipient recipient=death;if(owner!=null&&recipient!=null)try{owner.unlinkToDeath(recipient,0);}catch(RuntimeException ignored){}
            Runnable persist=()->{try{registry.setOpen(scope.caller,operation,boot,"closed",code);}catch(RuntimeException ignored){}finally{active.remove(key(scope.caller,operation),this);}};
            try{writes.execute(persist);}catch(RuntimeException e){active.remove(key(scope.caller,operation),this);}
        }
    }
    void networkChanged(){synchronized(admission){if(closed.get()||networkChanging)return;networkChanging=true;}try{formal.networkChanged();for(Entry entry:active.values())entry.abort("CONNECTION_CLOSED");if(routes!=null)routes.invalidate();}finally{formal.networkReady();networkChanging=false;}}
    int runtimeCount(){return routes==null?0:routes.runtimeCount();}
    int directLanCount(){return directLanReady.get();}
    int directXtcpCount(){return directXtcpReady.get();}
    int activeCount(){int count=0;for(Entry entry:active.values())if(!entry.closed.get())count++;return count;}
    @Override public void close(){synchronized(admission){if(!closed.compareAndSet(false,true))return;if(networkObserver!=null)networkObserver.close();for(Entry entry:active.values())entry.abort("GATEWAY_STOPPED");formal.close();if(routes!=null)routes.close();if(authority!=null)authority.close();timers.shutdownNow();setup.shutdownNow();directIo.shutdownNow();relayIo.shutdownNow();copy.shutdownNow();control.shutdownNow();writes.shutdown();}}
    private static String key(String caller,UUID operation){return caller+"/"+operation;}
    private static void quiet(Closeable value){if(value!=null)try{value.close();}catch(IOException ignored){}}
}
