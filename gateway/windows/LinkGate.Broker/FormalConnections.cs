using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Security.Principal;
using System.Text.Json;
using LinkGate.Authority;
using LinkGate.Contracts;

namespace LinkGate.Broker;

internal sealed class FormalConnections : IAsyncDisposable
{
    private readonly RegistrationStore store;private readonly SecureIdentity identity;private readonly ControlClient authority;private readonly SecureRelayRoute[] routes;private readonly IConnectionRoutes? scopedRoutes;private readonly Guid boot;
    private readonly string sid;private readonly CancellationTokenSource lifetime=new();private readonly object admission=new();private readonly ConcurrentDictionary<Guid,Entry> entries=new();private readonly ConnectionPlanner<Opened> planner=new();private readonly Task monitor;
    private ulong generation=1;private bool closed;
    internal FormalConnections(RegistrationStore store,SecureIdentity identity,ControlClient authority,SecureRelayRoute[] routes,Guid boot,IConnectionRoutes? scopedRoutes=null)
    {this.store=store;this.identity=identity;this.authority=authority;this.routes=routes;this.boot=boot;this.scopedRoutes=scopedRoutes;using var current=WindowsIdentity.GetCurrent();sid=current.User?.Value??throw new UnauthorizedAccessException();monitor=Monitor();}
    internal JsonElement Open(string caller,Guid operation,ConnectionOpenRequest request)
    {
        long admitted=ConnectionPlanner<Opened>.Now();var utc=DateTimeOffset.UtcNow;
        lock(admission)
        {
            if(closed)throw new RegistrationException("GATEWAY_STOPPED");
            if(store.RetainedConnectionOpen(caller,operation,request))return store.Status(caller,operation);
            if(entries.Count>=32||entries.Values.Count(x=>x.Scope.Caller==caller)>=16)throw new RegistrationException("RESOURCE_EXHAUSTED");
            if(request.ConnectionPolicy.Prewarm||request.ConnectionPolicy.RelayBudgetBytes is not null||request.ConnectionPolicy.RelayWarningBytes is not null)throw new RegistrationException("CAPABILITY_UNSUPPORTED");
            if(request.ConnectionPolicy.AllowRelay&&scopedRoutes is not {SupportsRelay:true}&&!routes.Any(x=>x.ImportAlias==request.ImportAlias&&x.PublisherDevice==request.DeviceId&&x.PublisherApplication==request.PublisherAppId&&x.Service==request.RemoteServiceId&&!x.RuntimeLifetime.IsCancellationRequested))throw new RegistrationException("CAPABILITY_UNSUPPORTED");
            var accepted=store.BeginConnection(caller,operation,request,boot,generation,utc,admitted,ConnectionPlanner<Opened>.Now);
            if(accepted.Created){var entry=new Entry(this,operation,request,accepted,admitted,utc);if(!entries.TryAdd(entry.Reference,entry))throw new InvalidOperationException();entry.Work=Task.Run(()=>Run(entry));}
            return store.Status(caller,operation);
        }
    }
    internal JsonElement Status(string caller,Guid reference)
    {var saved=store.ConnectionStatus(caller,reference);return entries.TryGetValue(reference,out var entry)&&entry.Scope.Caller==caller?entry.Meter.Read().Status.Wire():saved.Wire();}
    internal JsonElement Attach(string caller,Guid operation,Guid reference)
    {
        var result=store.AttachConnection(caller,operation,reference,boot);Guid stream=result.GetProperty("result").GetProperty("streamRef").GetGuid();
        if(entries.TryGetValue(reference,out var entry)&&entry.Scope.Caller==caller&&!entry.Stopped)entry.Claim.TrySetResult(stream);return result;
    }
    internal JsonElement Close(string caller,Guid operation,Guid reference)
    {var result=store.PrepareConnectionClose(caller,operation,reference);if(entries.TryGetValue(reference,out var entry)&&entry.Scope.Caller==caller)entry.Stop(false,"CONNECTION_CLOSED");else store.TerminateConnection(caller,reference,"CONNECTION_CLOSED",DateTimeOffset.UtcNow);return result;}
    internal void NetworkChanged(){lock(admission){if(closed)return;if(generation==ulong.MaxValue){closed=true;foreach(var entry in entries.Values)entry.Stop(false,"NETWORK_CHANGED");return;}generation++;foreach(var entry in entries.Values)entry.Stop(false,"NETWORK_CHANGED");scopedRoutes?.Invalidate();}}
    private void Current(Entry entry){if(entry.Stopped||lifetime.IsCancellationRequested)throw new OperationCanceledException();lock(admission){if(entry.Generation!=generation)throw new RegistrationException("NETWORK_CHANGED");}if(!store.LocalAccessCurrent([entry.Scope])[0])throw new UnauthorizedAccessException();}
    private sealed class Opened(PeerConnection peer,CancellationTokenRegistration route,IConnectionRoute? lease=null):IAsyncDisposable
    {internal PeerConnection Peer{get;}=peer;public async ValueTask DisposeAsync(){route.Dispose();try{await Peer.DisposeAsync();}finally{lease?.Dispose();}}}
    private async Task<Opened> Dial(Entry entry,ConnectionPlanner<Opened>.Attempt attempt,bool relay)
    {
        Current(entry);attempt.RemainingMs();
        if(scopedRoutes is not null)
        {
            if(!relay&&scopedRoutes.SupportsDirect)
            {
                if(entry.Scope.Peer.Host is not null&&entry.Scope.Peer.Port is not null)
                {
                    using var lan=CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);lan.CancelAfter(Math.Min(1000,Math.Max(250,attempt.RemainingMs()/4)));
                    try{return await OpenPeer(entry,attempt,null,default,lan.Token);}
                    catch(OperationCanceledException)when(!attempt.Token.IsCancellationRequested){}
                    catch(IOException failure)when(failure is not PeerRefusalException&&failure is not PlanningException&&failure is not ConnectionRouteException){}
                    attempt.RemainingMs();Current(entry);
                }
                return await OpenScoped(entry,attempt,true);
            }
            if(relay)return await OpenScoped(entry,attempt,false);
        }
        SecureRelayRoute? route=null;
        if(relay){route=routes.SingleOrDefault(x=>x.ClientApplication==entry.Scope.Client&&x.ImportAlias==entry.Scope.Alias&&x.PublisherDevice==entry.Scope.Peer.DeviceId&&x.PublisherApplication==entry.Scope.Publisher&&x.Service==entry.Scope.Service);if(route is null||route.RuntimeLifetime.IsCancellationRequested)throw new UnauthorizedAccessException();}
        using var stop=route is null?CancellationTokenSource.CreateLinkedTokenSource(attempt.Token):CancellationTokenSource.CreateLinkedTokenSource(attempt.Token,route.RuntimeLifetime);
        return await OpenPeer(entry,attempt,route is null?null:new IPEndPoint(IPAddress.Loopback,route.LoopbackPort),route?.RuntimeLifetime??default,stop.Token);
    }
    private async Task<Opened> OpenScoped(Entry entry,ConnectionPlanner<Opened>.Attempt attempt,bool direct)
    {
        IConnectionRoute? route=await scopedRoutes!.OpenAsync(new ConnectionRouteScope(entry.Scope),direct,attempt.Deadline,attempt.Token);
        try
        {
            attempt.Track(route.Dispose);attempt.RemainingMs();using var stop=CancellationTokenSource.CreateLinkedTokenSource(attempt.Token,route.Cancellation);
            var opened=await OpenPeer(entry,attempt,new IPEndPoint(IPAddress.Loopback,route.LoopbackPort),route.Cancellation,stop.Token,route);route=null;return opened;
        }finally{route?.Dispose();}
    }
    private async Task<Opened> OpenPeer(Entry entry,ConnectionPlanner<Opened>.Attempt attempt,IPEndPoint? endpoint,CancellationToken routeLifetime,CancellationToken token,IConnectionRoute? route=null)
    {
        Current(entry);attempt.RemainingMs();
        var scope=new SecureOpenScope(entry.Scope.Client,entry.Scope.Publisher,entry.Scope.Service,entry.Scope.Import.Protocol,entry.Scope.Import.LogicalAuthority);
        var peer=await PeerConnection.ConnectAsync(identity,entry.Scope.Peer,scope,token,authority,endpoint);CancellationTokenRegistration routeStop=default;
        try{attempt.Track(peer.Abort);if(routeLifetime.CanBeCanceled)routeStop=routeLifetime.Register(peer.Abort);token.ThrowIfCancellationRequested();Current(entry);if(peer.Lifetime.IsCancellationRequested)throw new IOException("Transport closed during setup.");return new Opened(peer,routeStop,route);}
        catch{routeStop.Dispose();await peer.DisposeAsync();throw;}
    }
    private async Task Run(Entry entry)
    {
        ConnectionPlanner<Opened>.Result? plan=null;
        try
        {
            Current(entry);plan=await planner.OpenAsync(entry.Request.ConnectionPolicy,entry.Admitted,entry.Lifetime.Token,a=>Dial(entry,a,false),a=>Dial(entry,a,true));Current(entry);
            lock(entry.Gate){if(entry.Stopped)throw new OperationCanceledException();entry.Peer=plan.Value.Peer;entry.Meter.Ready(plan.Path,plan.SetupMs);}
            store.PublishConnection(entry.Scope.Caller,boot,entry.Meter.Read());
            using var attach=CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime.Token,plan.Value.Peer.Lifetime.Token);attach.CancelAfter(TimeSpan.FromSeconds(10));
            Guid stream=await entry.Claim.Task.WaitAsync(attach.Token);using var pipe=PipeHost.CreateProtectedPipe("LinkGate.stream.v1."+stream.ToString("N"),sid);
            lock(entry.Gate){if(entry.Stopped)throw new OperationCanceledException();entry.Pipe=pipe;}
            await pipe.WaitForConnectionAsync(attach.Token);using(var header=CancellationTokenSource.CreateLinkedTokenSource(attach.Token))
            {
                header.CancelAfter(TimeSpan.FromSeconds(2));var value=await SecureWire.ReadAsync(pipe,header.Token);SecureWire.Fields(value,"kind","streamRef");if(value.GetProperty("kind").GetString()!="attach"||value.GetProperty("streamRef").GetString()!=stream.ToString()||CallerIdentity.Capture(pipe,sid)!=entry.Scope.Caller)throw new UnauthorizedAccessException();
                Current(entry);if(store.ConsumeConnectionAttachment(entry.Scope.Caller,stream,boot)!=entry.Reference)throw new UnauthorizedAccessException();entry.Meter.Attached();await SecureWire.WriteAsync(pipe,new{kind="ready"},header.Token);
            }
            using var local=new LinkGate.Control.BusinessStream(pipe);using var data=CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime.Token,plan.Value.Peer.Lifetime.Token);
            await Bridge(entry,local,plan.Value.Peer.Stream,data.Token);entry.Stop(true,null);
        }
        catch(Exception failure){entry.Stop(false,Code(failure));}
        finally
        {
            if(plan is not null)await plan.DisposeAsync();entry.Finished=true;entry.Persist();entry.Lifetime.Dispose();
        }
    }
    private static async Task Bridge(Entry entry,LinkGate.Control.BusinessStream local,LinkGate.Control.BusinessStream remote,CancellationToken token)
    {
        async Task Pump(LinkGate.Control.BusinessStream from,LinkGate.Control.BusinessStream to,bool outbound)
        {byte[] buffer=new byte[16384];int count;while((count=await from.ReadAsync(buffer,token))!=0){await to.WriteAsync(buffer.AsMemory(0,count),token);if(!entry.Meter.Bytes(outbound,(ulong)count))throw new IOException();}await to.CompleteWritesAsync(token);entry.Meter.Fin(outbound);}
        await SecurePeerServer.AwaitPairAsync(()=>Pump(local,remote,true),()=>Pump(remote,local,false),()=>{local.Dispose();remote.Dispose();});
        using var drain=CancellationTokenSource.CreateLinkedTokenSource(token);drain.CancelAfter(TimeSpan.FromSeconds(2));await Task.WhenAll(local.DrainWriteCompletionAsync(drain.Token),remote.DrainWriteCompletionAsync(drain.Token));
    }
    private async Task Monitor()
    {
        try{using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));while(await timer.WaitForNextTickAsync(lifetime.Token))
        {var all=entries.Values.ToArray();var valid=store.LocalAccessCurrent(all.Select(x=>x.Scope).ToArray());for(int i=0;i<all.Length;i++){var entry=all[i];if(!entry.Stopped){if(!valid[i])entry.Stop(false,"AUTH_REQUIRED");else if(!entry.Meter.ClockValid)entry.Stop(false,"CLOCK_UNTRUSTED");else if(DateTime.UtcNow>=identity.Certificate.NotAfter.ToUniversalTime())entry.Stop(false,"AUTH_REQUIRED");}entry.Persist();}}}catch(OperationCanceledException){}
    }
    private sealed class Entry
    {
        private readonly FormalConnections owner;internal readonly object Gate=new();internal readonly LocalAccessScope Scope;internal readonly Guid Reference,Operation;internal readonly ConnectionOpenRequest Request;internal readonly long Admitted;internal readonly ulong Generation;internal readonly ConnectionMeter Meter;internal readonly CancellationTokenSource Lifetime=new();internal readonly TaskCompletionSource<Guid> Claim=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int stopped,persisting;internal bool Stopped=>Volatile.Read(ref stopped)!=0;internal volatile bool Finished;internal Task? Work;internal PeerConnection? Peer;internal NamedPipeServerStream? Pipe;
        internal Entry(FormalConnections owner,Guid operation,ConnectionOpenRequest request,ConnectionAccepted accepted,long admitted,DateTimeOffset utc)
        {this.owner=owner;Scope=accepted.Scope!;Reference=accepted.Status.ConnectionId;Operation=operation;Request=request;Admitted=admitted;Generation=accepted.Status.NetworkGeneration;Meter=new ConnectionMeter(Reference,accepted.Status.OwnerId,Scope.Peer.DeviceId,Scope.Publisher,Scope.Service,owner.boot,Generation,utc,admitted,ConnectionPlanner<Opened>.Now);}
        internal void Stop(bool clean,string? code)
        {if(Interlocked.Exchange(ref stopped,1)!=0)return;Meter.End(clean,code);try{Lifetime.Cancel();}catch(ObjectDisposedException){}lock(Gate){Peer?.Abort();Pipe?.Dispose();}Persist();}
        internal void Persist()
        {if(Interlocked.Exchange(ref persisting,1)!=0)return;try{var snapshot=Meter.Read();owner.store.PublishConnection(Scope.Caller,owner.boot,snapshot);if(Finished&&snapshot.Status.ClosedUtc is not null)owner.entries.TryRemove(new KeyValuePair<Guid,Entry>(Reference,this));}catch(RegistrationException){}finally{Volatile.Write(ref persisting,0);}}
    }
    private static string Code(Exception failure)=>failure switch{PlanningException p=>p.Code=="CANCELLED"?"CONNECTION_CLOSED":p.Code,ConnectionRouteException r=>r.Code,RegistrationException r=>r.Code,UnauthorizedAccessException=>"AUTH_REQUIRED",System.Security.Authentication.AuthenticationException=>"AUTH_REQUIRED",LeaseRejectedException=>"AUTH_REQUIRED",ControlRefusedException c=>c.Code=="RESOURCE_EXHAUSTED"?c.Code:"AUTH_REQUIRED",PeerRefusalException p=>p.Code,_=>"CONNECTION_CLOSED"};
    public async ValueTask DisposeAsync()
    {Entry[] all;lock(admission){closed=true;all=entries.Values.ToArray();foreach(var entry in all)entry.Stop(false,"GATEWAY_STOPPED");lifetime.Cancel();}try{await monitor;await Task.WhenAll(all.Select(x=>x.Work??Task.CompletedTask));foreach(var entry in all)entry.Persist();}finally{try{if(scopedRoutes is not null)await scopedRoutes.DisposeAsync();}finally{lifetime.Dispose();}}}
}
