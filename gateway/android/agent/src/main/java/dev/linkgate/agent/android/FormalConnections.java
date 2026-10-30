package dev.linkgate.agent.android;

import android.os.IBinder;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import dev.linkgate.agent.AgentRegistry;
import dev.linkgate.agent.ConnectionJournal;
import dev.linkgate.agent.ConnectionMeter;
import dev.linkgate.authority.AuthoritySnapshot;
import dev.linkgate.authority.ControlClient;
import dev.linkgate.authority.PeerLeaseSource;
import dev.linkgate.contracts.ConnectionOpenRequest;
import dev.linkgate.contracts.ConnectionStatus;
import dev.linkgate.contracts.WireJson;
import dev.linkgate.descriptors.DescriptorStream;
import dev.linkgate.descriptors.DescriptorWire;
import dev.linkgate.transport.ConnectionPlanner;
import dev.linkgate.transport.DeviceIdentity;
import dev.linkgate.transport.PeerClient;
import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

final class FormalConnections implements Closeable {
    private final AgentRegistry registry;private final ConnectionJournal journal;private final DeviceIdentity identity;private final ControlClient authority;private final FrpRoutes routes;private final UUID boot;
    private final ScheduledThreadPoolExecutor timers;private final ThreadPoolExecutor setup,copy,control,writes;private final ConnectionPlanner<Opened> planner;
    private final ConcurrentHashMap<String,Entry> entries=new ConcurrentHashMap<>();private final Object admission=new Object();private final AtomicBoolean closed=new AtomicBoolean();private volatile boolean networkChanging;private final AtomicLong generation=new AtomicLong(1);private final AtomicInteger xtcpReady=new AtomicInteger();
    FormalConnections(AgentRegistry registry,DeviceIdentity identity,ControlClient authority,FrpRoutes routes,UUID boot,ScheduledThreadPoolExecutor timers,ThreadPoolExecutor setup,ThreadPoolExecutor copy,ThreadPoolExecutor control,ThreadPoolExecutor writes,ThreadPoolExecutor direct,ThreadPoolExecutor relay){
        this.registry=registry;this.journal=registry.connections();this.identity=identity;this.authority=authority;this.routes=routes;this.boot=boot;this.timers=timers;this.setup=setup;this.copy=copy;this.control=control;this.writes=writes;planner=new ConnectionPlanner<>(SystemClock::elapsedRealtimeNanos,direct,relay);journal.recover(boot,System.currentTimeMillis());
    }
    boolean available(){return authority!=null&&!closed.get();}
    Map<String,Object> open(String caller,UUID operation,ConnectionOpenRequest request){
        long admitted=SystemClock.elapsedRealtimeNanos(),utc=System.currentTimeMillis();
        synchronized(admission){
            if(!available())throw new IllegalStateException("GATEWAY_STOPPED");
            if(networkChanging)throw new AgentRegistry.Failure("NETWORK_CHANGED");
            // Retained requests precede capacity and never redial. A new request
            // cannot bypass a missing route capability through another profile.
            long own=entries.values().stream().filter(e->e.scope.caller.equals(caller)).count();
            if(entries.size()>=32||own>=16){if(journal.retainedOpen(caller,operation,request))return read(registry.operationStatus(caller,operation));throw new AgentRegistry.Failure("RESOURCE_EXHAUSTED");}
            if(request.connectionPolicy.allowRelay&&routes==null&&!journal.retainedOpen(caller,operation,request))throw new AgentRegistry.Failure("CAPABILITY_UNSUPPORTED");
            ConnectionJournal.Accepted accepted=journal.begin(caller,operation,request,boot,Long.toString(generation.get()),utc,admitted,SystemClock::elapsedRealtimeNanos);
            if(accepted.created){Entry entry=new Entry(caller,operation,request,accepted,utc,admitted);entries.put(entry.reference,entry);try{setup.execute(()->initialize(entry));}catch(RuntimeException failure){entry.abort("RESOURCE_EXHAUSTED");}}
            return read(registry.operationStatus(caller,operation));
        }
    }
    Map<String,Object> status(String caller,String reference){ConnectionStatus saved=journal.status(caller,reference);Entry entry=entries.get(reference);return entry!=null&&entry.scope.caller.equals(caller)?entry.meter.snapshot().status.wire():saved.wire();}
    Map<String,Object> attach(String caller,UUID operation,String reference){if(!available())throw new IllegalStateException("GATEWAY_STOPPED");return read(journal.attach(caller,operation,reference,boot));}
    Map<String,Object> close(String caller,UUID operation,String reference){String result=journal.prepareClose(caller,operation,reference);Entry entry=entries.get(reference);if(entry!=null&&entry.scope.caller.equals(caller))entry.abort("CONNECTION_CLOSED");else journal.terminate(caller,reference,"CONNECTION_CLOSED",System.currentTimeMillis());return read(result);}
    ParcelFileDescriptor descriptor(String caller,String stream,IBinder token)throws Exception {
        if(token==null||!token.isBinderAlive())throw new SecurityException("AUTH_REQUIRED");String reference=journal.consume(caller,stream,boot);Entry entry=entries.get(reference);
        if(entry==null||entry.stopped.get()||!entry.scope.caller.equals(caller)||entry.peer==null||!entry.attached.compareAndSet(false,true))throw new SecurityException("AUTH_REQUIRED");ParcelFileDescriptor[] pair=null;DescriptorStream descriptor=null;
        try{pair=ParcelFileDescriptor.createReliableSocketPair();descriptor=new DescriptorStream(pair[0],entry);entry.descriptor=descriptor;entry.token=token;entry.death=()->entry.abort("CONNECTION_CLOSED");token.linkToDeath(entry.death,0);if(entry.stopped.get())throw new IOException("Closed connection.");copy.execute(()->outbound(entry));return pair[1];}
        catch(Exception failure){quiet(descriptor);if(pair!=null){quiet(pair[0]);quiet(pair[1]);}entry.abort("CONNECTION_CLOSED");throw failure;}
    }
    private static final class Opened implements Closeable {final PeerClient peer;final String kind;Opened(PeerClient peer,String kind){this.peer=peer;this.kind=kind;}public void close(){peer.abort();quiet(peer);}}
    private void initialize(Entry entry){ConnectionPlanner<Opened>.Result selected=null;try{
        requireCurrent(entry);long remaining=TimeUnit.MILLISECONDS.toNanos(entry.request.connectionPolicy.connectTimeoutMs)-(SystemClock.elapsedRealtimeNanos()-entry.admitted);if(remaining<=0)throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CONNECT_TIMEOUT);
        entry.openTimer=timers.schedule(()->entry.abort("CONNECT_TIMEOUT"),remaining,TimeUnit.NANOSECONDS);
        selected=planner.connect(entry.request.connectionPolicy,entry.admitted,entry.stopped::get,context->direct(entry,context),context->transport(entry,context,"relay",Long.MAX_VALUE));requireCurrent(entry);
        synchronized(entry){if(entry.stopped.get()||closed.get())throw new IOException("Closed connection.");Opened value=selected.value();entry.peer=value.peer;entry.plan=selected;entry.meter.ready(selected.path,selected.setupMs);if(value.kind.equals("xtcp"))xtcpReady.incrementAndGet();selected=null;}
        if(entry.openTimer!=null)entry.openTimer.cancel(false);entry.attachTimer=timers.schedule(()->entry.abort("CONNECTION_CLOSED"),10,TimeUnit.SECONDS);journal.publish(entry.scope.caller,boot,entry.meter.snapshot());
    }catch(Exception failure){entry.abort(code(failure));}finally{if(selected!=null)selected.close();}}
    private Opened direct(Entry entry,ConnectionPlanner.Context context)throws Exception {
        boolean xtcp=routes!=null&&routes.canDirect();long lan=xtcp?Math.min(context.deadlineNanos(),SystemClock.elapsedRealtimeNanos()+TimeUnit.MILLISECONDS.toNanos(Math.min(1000,Math.max(250,context.remainingMillis()/4)))):context.deadlineNanos();
        try{return transport(entry,context,"lan",lan);}catch(ConnectionPlanner.Failure failure){if(!xtcp||failure.code!=ConnectionPlanner.Code.DIRECT_FAILED&&failure.code!=ConnectionPlanner.Code.CONNECT_TIMEOUT)throw failure;}
        return transport(entry,context,"xtcp",context.deadlineNanos());
    }
    private Opened transport(Entry entry,ConnectionPlanner.Context context,String path,long candidateDeadline)throws Exception {
        synchronized(entry){if(entry.stopped.get()||context.cancelled())throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CANCELLED);entry.opening=context;}
        PeerClient.Cancellation abort=new PeerClient.Cancellation();context.track(abort);PeerLeaseSource lease=null;FrpRoutes.Route route=null;
        try{requireCurrent(entry);context.remainingMillis();lease=PeerLeaseSource.acquire(authority,entry.scope.client,new AuthoritySnapshot.Target(entry.scope.device,entry.scope.publisher,entry.scope.service),entry.scope.protocol,entry.scope.authority);context.track(lease);
            String host=entry.scope.host;int port=entry.scope.port;if(!path.equals("lan")){if(routes==null)throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CAPABILITY_UNSUPPORTED);route=path.equals("xtcp")?routes.openDirect(entry.scope,context.deadlineNanos(),context::cancelled):routes.openRelay(entry.scope,context.deadlineNanos(),context::cancelled);context.track(route);host="127.0.0.1";port=route.port;}
            long left=Math.min(context.remainingMillis(),TimeUnit.NANOSECONDS.toMillis(candidateDeadline-SystemClock.elapsedRealtimeNanos()));if(left<1)throw new java.net.SocketTimeoutException("Candidate timed out.");String[] fields=host.split("\\.");byte[] address=new byte[4];for(int i=0;i<4;i++)address[i]=(byte)Integer.parseInt(fields[i]);
            PeerClient peer=PeerClient.open(identity,entry.scope.device,InetAddress.getByAddress(address),port,entry.scope.client,entry.scope.publisher,entry.scope.service,entry.scope.protocol,entry.scope.authority,(int)left,timers,control,SystemClock::elapsedRealtimeNanos,PeerClient::connectTcp,abort,lease);lease=null;context.track(peer);return new Opened(peer,path);
        }catch(Exception failure){abort.close();if(lease!=null)lease.close();if(route!=null)route.close();if(failure instanceof ConnectionPlanner.Failure)throw (ConnectionPlanner.Failure)failure;String code=code(failure);if(code.equals("AUTH_REQUIRED")||code.equals("PERMISSION_DENIED"))throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.AUTH_REQUIRED);if(code.equals("RESOURCE_EXHAUSTED"))throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.RESOURCE_EXHAUSTED);if(code.equals("CAPABILITY_UNSUPPORTED"))throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CAPABILITY_UNSUPPORTED);if(code.equals("CLOCK_UNTRUSTED"))throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CLOCK_UNTRUSTED);if(code.equals("NETWORK_CHANGED"))throw new ConnectionPlanner.Failure(ConnectionPlanner.Code.CANCELLED);throw new ConnectionPlanner.Failure(path.equals("relay")?ConnectionPlanner.Code.RELAY_FAILED:ConnectionPlanner.Code.DIRECT_FAILED);}
    }
    private void requireCurrent(Entry entry){if(entry.stopped.get()||closed.get()||entry.generation!=generation.get())throw new IllegalStateException("NETWORK_CHANGED");if(!registry.currentScopes(Collections.singletonList(entry.scope))[0])throw new SecurityException("AUTH_REQUIRED");}
    private void outbound(Entry entry){try{
        Map<String,Object> header=DescriptorWire.read(entry.descriptor.input());WireJson.fields(header,"kind");if(!"attach".equals(header.get("kind")))throw new SecurityException("AUTH_REQUIRED");requireCurrent(entry);entry.meter.attached();DescriptorWire.write(entry.descriptor.output(),WireJson.map("kind","ready"));if(entry.attachTimer!=null)entry.attachTimer.cancel(false);copy.execute(()->inbound(entry));pump(entry,entry.descriptor.input(),entry.peer.stream.output,true);entry.peer.stream.finishWrites();entry.meter.fin(true);entry.finished();
    }catch(Exception failure){entry.abort(code(failure));}}
    private void inbound(Entry entry){try{pump(entry,entry.peer.stream.input,entry.descriptor.output(),false);entry.descriptor.finishWrites();entry.meter.fin(false);entry.finished();}catch(Exception failure){entry.abort(code(failure));}}
    private static void pump(Entry entry,InputStream input,OutputStream output,boolean outbound)throws IOException {byte[] buffer=new byte[16384];int n;while((n=input.read(buffer))!=-1){if(n==0)throw new IOException("No stream progress.");output.write(buffer,0,n);if(!entry.meter.bytes(outbound,n))throw new IOException("Connection ended.");}}
    void monitor(){
        ArrayList<Entry> current=new ArrayList<>(entries.values());ArrayList<AgentRegistry.Scope> scopes=new ArrayList<>();for(Entry entry:current)scopes.add(entry.scope);boolean[] valid=registry.currentScopes(scopes);
        for(int i=0;i<current.size();i++){Entry entry=current.get(i);if(!entry.stopped.get()){if(!valid[i])entry.abort("AUTH_REQUIRED");else if(entry.generation!=generation.get())entry.abort("NETWORK_CHANGED");else if(!entry.meter.clockValid())entry.abort("CLOCK_UNTRUSTED");else if(entry.peer!=null&&entry.peer.isClosed())entry.abort("CONNECTION_CLOSED");}entry.persist();}
    }
    void networkChanged(){synchronized(admission){networkChanging=true;generation.incrementAndGet();for(Entry entry:entries.values())entry.abort("NETWORK_CHANGED");}}
    void networkReady(){networkChanging=false;}
    int xtcpCount(){return xtcpReady.get();}
    private final class Entry {
        final AgentRegistry.Scope scope;final ConnectionOpenRequest request;final UUID operation;final String reference;final long admitted,generation;final ConnectionMeter meter;
        final AtomicBoolean stopped=new AtomicBoolean(),attached=new AtomicBoolean(),persisting=new AtomicBoolean();final AtomicInteger finished=new AtomicInteger();volatile ConnectionPlanner.Context opening;volatile ConnectionPlanner<Opened>.Result plan;volatile PeerClient peer;volatile DescriptorStream descriptor;volatile ScheduledFuture<?> openTimer,attachTimer;volatile IBinder token;volatile IBinder.DeathRecipient death;
        Entry(String caller,UUID operation,ConnectionOpenRequest request,ConnectionJournal.Accepted accepted,long utc,long admitted){this.scope=accepted.scope;this.operation=operation;this.request=request;this.admitted=admitted;reference=accepted.status.connectionId;generation=Long.parseLong(accepted.status.networkGeneration);meter=new ConnectionMeter(reference,accepted.status.ownerId,scope.device,scope.publisher,scope.service,boot.toString(),Long.toString(generation),utc,admitted,SystemClock::elapsedRealtimeNanos);}
        void finished(){if(finished.incrementAndGet()!=2)return;ScheduledFuture<?> deadline=timers.schedule(peer::abort,2,TimeUnit.SECONDS);try{peer.stream.drainWriteCompletion();stop(true,null);}catch(IOException failure){abort("CONNECTION_CLOSED");}finally{deadline.cancel(false);}}
        void abort(String code){stop(false,code);}
        void stop(boolean clean,String code){if(!stopped.compareAndSet(false,true))return;ConnectionPlanner.Context pending;ConnectionPlanner<Opened>.Result selected;synchronized(this){pending=opening;selected=plan;}if(pending!=null)pending.close();if(selected!=null)selected.close();if(peer!=null)peer.abort();if(!clean&&descriptor!=null)try{descriptor.abort();}catch(IOException ignored){}else quiet(descriptor);if(openTimer!=null)openTimer.cancel(false);if(attachTimer!=null)attachTimer.cancel(false);if(token!=null&&death!=null)try{token.unlinkToDeath(death,0);}catch(RuntimeException ignored){}meter.end(clean,code);persist();}
        void persist(){if(!persisting.compareAndSet(false,true))return;try{writes.execute(()->{try{ConnectionMeter.Snapshot snapshot=meter.snapshot();journal.publish(scope.caller,boot,snapshot);if(snapshot.status.closedUtc!=null)entries.remove(reference,this);}catch(RuntimeException ignored){}finally{persisting.set(false);}});}catch(RuntimeException rejected){persisting.set(false);}}
    }
    @Override public void close(){synchronized(admission){if(!closed.compareAndSet(false,true))return;for(Entry entry:entries.values())entry.abort("GATEWAY_STOPPED");}}
    private static String code(Exception failure){if(failure instanceof ConnectionPlanner.Failure){ConnectionPlanner.Code value=((ConnectionPlanner.Failure)failure).code;return value==ConnectionPlanner.Code.CANCELLED?"CONNECTION_CLOSED":value.name();}if(failure instanceof AgentRegistry.Failure)return ((AgentRegistry.Failure)failure).code;if(failure instanceof PeerClient.Refused)return ((PeerClient.Refused)failure).code;if(failure instanceof ControlClient.Refused)return ((ControlClient.Refused)failure).code.equals("RESOURCE_EXHAUSTED")?"RESOURCE_EXHAUSTED":"AUTH_REQUIRED";if(failure instanceof SecurityException||failure instanceof java.security.GeneralSecurityException)return "AUTH_REQUIRED";if(failure instanceof dev.linkgate.transport.OwnedRuntimePool.Exhausted||failure instanceof java.util.concurrent.RejectedExecutionException)return "RESOURCE_EXHAUSTED";if(failure instanceof IllegalStateException&&"NETWORK_CHANGED".equals(failure.getMessage()))return "NETWORK_CHANGED";return "CONNECTION_CLOSED";}
    private static Map<String,Object> read(String value){return WireJson.read(value.getBytes(java.nio.charset.StandardCharsets.UTF_8),65536);}
    private static void quiet(Closeable value){if(value!=null)try{value.close();}catch(IOException ignored){}}
}
