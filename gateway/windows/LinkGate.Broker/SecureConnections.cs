using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using LinkGate.Control;
using Microsoft.Data.Sqlite;
using LinkGate.Authority;

namespace LinkGate.Broker;

internal sealed record LocalSecureScope(string Caller,Guid Application,long Revision,long Decision,string Client,string Alias,string ImportJson,ImportService Import,SecurePeer Peer,bool Relay=false);

// Trusted Agent composition only; no ordinary application can submit an address
// or construct a runtime lifetime through RPC.
public sealed record SecureRelayRoute(string ClientApplication,string ImportAlias,string PublisherDevice,string PublisherApplication,string Service,int LoopbackPort,CancellationToken RuntimeLifetime);

public sealed class SecureConnections : IRelaySecureConnections,IFormalConnections,IAsyncDisposable
{
    private readonly FormalConnections? formal;
    public bool SupportsFormal=>formal is not null;
    public string FormalCapabilityReason{get;}="OWNER_CONFIGURED_ENDPOINTS_ONLY";
    public JsonElement OpenConnection(string caller,Guid operation,ConnectionOpenRequest request)=>Formal.Open(caller,operation,request);
    public JsonElement GetConnectionStatus(string caller,Guid reference)=>Formal.Status(caller,reference);
    public JsonElement AttachConnection(string caller,Guid operation,Guid reference)=>Formal.Attach(caller,operation,reference);
    public JsonElement CloseConnection(string caller,Guid operation,Guid reference)=>Formal.Close(caller,operation,reference);
    public void NotifyNetworkChanged()=>formal?.NetworkChanged();
    private FormalConnections Formal=>formal??throw new RegistrationException("CAPABILITY_UNSUPPORTED");
    private readonly RegistrationStore store;
    private readonly SecureIdentity identity;
    private readonly ControlClient? serverAuthority;
    private readonly SecureRelayRoute[] relayRoutes;
    public bool SupportsRelay=>serverAuthority is not null&&relayRoutes.Length>0;
    private readonly Guid boot=Guid.NewGuid();
    private readonly CancellationTokenSource lifetime=new();
    private readonly ConcurrentDictionary<Guid,Connection> connections=new();
    private readonly object admission=new();
    private readonly string sid;
    private readonly Task monitor;
    private readonly ConnectionPlanner<PeerConnection> planner=new();
    private readonly FileStream hostLock;
    public SecureConnections(RegistrationStore store,SecureIdentity identity,ControlClient? serverAuthority=null,IEnumerable<SecureRelayRoute>? relayRoutes=null,IConnectionRoutes? scopedRoutes=null)
    {
        if(scopedRoutes is not null&&(serverAuthority is null||scopedRoutes.DeviceId!=identity.DeviceId))throw new ArgumentException("Scoped route identity mismatch.");
        if(scopedRoutes is not null)FormalCapabilityReason="SCOPED_AUTHENTICATED_RUNTIME";
        if(serverAuthority is not null&&serverAuthority.DeviceID!=identity.DeviceId)throw new ArgumentException("Visitor authority identity mismatch.");
        this.relayRoutes=(relayRoutes??[]).Take(129).ToArray();
        if(this.relayRoutes.Length>128||this.relayRoutes.Any(r=>r is null||r.LoopbackPort is <1 or >65535||!r.RuntimeLifetime.CanBeCanceled)||this.relayRoutes.GroupBy(r=>(r.ClientApplication,r.ImportAlias,r.PublisherDevice,r.PublisherApplication,r.Service)).Any(g=>g.Count()>1))throw new ArgumentException("Invalid owned relay routes.");
        if(this.relayRoutes.Length>0&&serverAuthority is null)throw new ArgumentException("Server authority required for relay.");
        this.store=store;this.identity=identity;this.serverAuthority=serverAuthority;hostLock=store.AcquireSecureHostLock();
        try
        {
            store.BindSecureIdentity(identity);store.ClosePreviousSecureOpens(boot);store.RecoverConnections(boot,DateTimeOffset.UtcNow);
            using var current=WindowsIdentity.GetCurrent();sid=current.User?.Value??throw new UnauthorizedAccessException();if(serverAuthority is not null)formal=new FormalConnections(store,identity,serverAuthority,this.relayRoutes,boot,scopedRoutes);monitor=MonitorAsync();
        }
        catch{hostLock.Dispose();lifetime.Dispose();throw;}
    }
    public JsonElement Open(string caller,Guid operation,Guid application,string alias,string peer,string publisher,string service)
        =>OpenCore(caller,operation,application,alias,peer,publisher,service,false);
    public JsonElement OpenRelay(string caller,Guid operation,Guid application,string alias,string peer,string publisher,string service)
        =>OpenCore(caller,operation,application,alias,peer,publisher,service,true);
    private JsonElement OpenCore(string caller,Guid operation,Guid application,string alias,string peer,string publisher,string service,bool relay)
    {
        long admitted=ConnectionPlanner<PeerConnection>.Now();
        _=new PendingGrantRequest(application,alias,peer,publisher,service);
        string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{method=relay?"secure.relay.open":"secure.connection.open",application,alias,peer,publisher,service})));
        lock(admission)
        {
            if(lifetime.IsCancellationRequested)throw new RegistrationException("CONNECTION_CLOSED");
            // A retained operation takes precedence over capacity and never dials again.
            if(store.ExistingSecureOpen(caller,operation,digest))return Status(caller,operation);
            if(connections.Count>=1024 || connections.Values.Count(x=>x.Scope.Caller==caller)>=128)throw new RegistrationException("RESOURCE_EXHAUSTED");
            var scope=store.ResolveSecureImport(caller,application,alias,peer,relay);
            SecureRelayRoute? route=null;
            if(relay)
            {
                if(!SupportsRelay)throw new RegistrationException("CAPABILITY_UNSUPPORTED");
                route=relayRoutes.SingleOrDefault(r=>r.ClientApplication==scope.Client&&r.ImportAlias==alias&&r.PublisherDevice==peer&&r.PublisherApplication==publisher&&r.Service==service);
                if(route is null||route.RuntimeLifetime.IsCancellationRequested)throw new RegistrationException("AUTH_REQUIRED");
            }
            Guid reference=Guid.NewGuid();var connection=new Connection(operation,reference,scope,new SecureOpenScope(scope.Client,publisher,service,scope.Import.Protocol,scope.Import.LogicalAuthority),lifetime.Token,route,admitted);
            store.BeginSecureOpen(caller,operation,digest,reference,boot,relay?"relay":"direct");
            if(!connections.TryAdd(reference,connection))throw new InvalidOperationException("Connection reference collision.");
            connection.Work=Task.Run(()=>RunAsync(connection));return Status(caller,operation);
        }
    }
    public JsonElement Status(string caller,Guid operation)
    {
        var status=store.SecureOpenStatus(caller,operation);
        string? dataPipe=null;
        if(status.State=="ready"&&connections.TryGetValue(status.Connection,out var connection))dataPipe=connection.PipeName;
        return JsonSerializer.SerializeToElement(new{connectionId=status.Connection,operationId=operation,state=status.State,error=status.Error,dataPipe,path=status.Path});
    }
    public JsonElement Close(string caller,Guid operation)
    {
        var status=store.SecureOpenStatus(caller,operation);
        if(connections.TryGetValue(status.Connection,out var value))value.Stop();
        store.SetSecureOpenState(caller,operation,boot,"closed","CONNECTION_CLOSED");return Status(caller,operation);
    }
    private async Task RunAsync(Connection connection)
    {
        string code="CONNECTION_CLOSED";
        try
        {
            if(!store.SecureImportsCurrent([connection.Scope])[0])throw new UnauthorizedAccessException();
            var policy=connection.Scope.Import.ConnectionPolicy;
            if(connection.Route is not null)policy=ManifestContract.NormalizePolicy(JsonSerializer.SerializeToUtf8Bytes(new{mode="fast-connect",connectTimeoutMs=policy.ConnectTimeoutMs,directWaitMs=0,allowRelay=true}));
            async Task<PeerConnection> Dial(ConnectionPlanner<PeerConnection>.Attempt attempt,bool relay)
            {
                attempt.RemainingMs();if(!store.SecureImportsCurrent([connection.Scope])[0])throw new UnauthorizedAccessException();
                var route=relay?connection.Route:null;if(relay&&route is null)throw new PlanningException("CAPABILITY_UNSUPPORTED");
                var opened=await PeerConnection.ConnectAsync(identity,connection.Scope.Peer,connection.RemoteScope,attempt.Token,serverAuthority,route is null?null:new IPEndPoint(IPAddress.Loopback,route.LoopbackPort));
                try{attempt.Track(opened.Abort);return opened;}catch{await opened.DisposeAsync();throw;}
            }
            await using var plan=await planner.OpenAsync(policy,connection.Admitted,connection.Lifetime.Token,attempt=>Dial(attempt,false),attempt=>Dial(attempt,true));
            var remote=plan.Value;
            if(!store.SecureImportsCurrent([connection.Scope])[0])throw new UnauthorizedAccessException();
            string name="LinkGate.data.v1."+Guid.NewGuid().ToString("N");
            using var pipe=PipeHost.CreateProtectedPipe(name,sid);
            connection.PipeName=name;
            store.SetSecureOpenState(connection.Scope.Caller,connection.Operation,boot,"ready",null);
            using(var attach=CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token,remote.Lifetime.Token))
            {
                attach.CancelAfter(TimeSpan.FromSeconds(10));await pipe.WaitForConnectionAsync(attach.Token);
                // Capture after the client writes one bounded attachment header, as on the control pipe.
                var header=await SecureWire.ReadAsync(pipe,attach.Token);SecureWire.Fields(header,"kind");
                if(header.GetProperty("kind").GetString()!="attach" || CallerIdentity.Capture(pipe,sid)!=connection.Scope.Caller)throw new UnauthorizedAccessException();
                if(!store.SecureImportsCurrent([connection.Scope])[0])throw new UnauthorizedAccessException();
                await SecureWire.WriteAsync(pipe,new{kind="ready"},attach.Token);
            }
            using var local=new BusinessStream(pipe);using var dataLifetime=CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token,remote.Lifetime.Token);
            await BridgeAsync(local,remote.Stream,dataLifetime.Token);
        }
        catch(Exception e) when(e is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException or RegistrationException or ApplicationReviewException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or LeaseRejectedException or ControlRefusedException)
        {code=e is PlanningException planning?planning.Code is "AUTH_REQUIRED" or "RESOURCE_EXHAUSTED" or "CAPABILITY_UNSUPPORTED"?planning.Code:planning.Code=="CLOCK_UNTRUSTED"?"AUTH_REQUIRED":planning.Code=="CANCELLED"?"CONNECTION_CLOSED":connection.Route is null?"DIRECT_FAILED":"RELAY_FAILED":e is PeerRefusalException refusal?refusal.Code:e is ControlRefusedException refused?refused.Code=="RESOURCE_EXHAUSTED"?refused.Code:"AUTH_REQUIRED":e is OperationCanceledException?"CONNECTION_CLOSED":e is UnauthorizedAccessException or LeaseRejectedException?"AUTH_REQUIRED":connection.Route is null?"DIRECT_FAILED":"RELAY_FAILED";}
        finally
        {
            connection.Stop();
            try{store.SetSecureOpenState(connection.Scope.Caller,connection.Operation,boot,"closed",code);}catch(RegistrationException){ }
            connections.TryRemove(connection.Reference,out _);
            connection.Lifetime.Dispose();
        }
    }
    private static async Task BridgeAsync(BusinessStream left,BusinessStream right,CancellationToken token)
    {
        async Task Pump(BusinessStream from,BusinessStream to)
        {
            byte[] buffer=new byte[16384];int n;
            while((n=await from.ReadAsync(buffer,token))!=0)await to.WriteAsync(buffer.AsMemory(0,n),token);
            await to.CompleteWritesAsync(token);
        }
        await SecurePeerServer.AwaitPairAsync(()=>Pump(left,right),()=>Pump(right,left),()=>{left.Dispose();right.Dispose();});
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await Task.WhenAll(left.DrainWriteCompletionAsync(deadline.Token),right.DrainWriteCompletionAsync(deadline.Token));
    }
    private async Task MonitorAsync()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while(await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var snapshot=connections.Values.ToArray();var valid=store.SecureImportsCurrent(snapshot.Select(x=>x.Scope).ToArray());
                for(int i=0;i<snapshot.Length;i++)if(!valid[i] || DateTime.UtcNow>=identity.Certificate.NotAfter.ToUniversalTime())snapshot[i].Stop();
            }
        }
        catch(OperationCanceledException){ }
    }
    public async ValueTask DisposeAsync()
    {
        lock(admission){lifetime.Cancel();foreach(var connection in connections.Values)connection.Stop();}
        try{if(formal is not null)await formal.DisposeAsync();await monitor;await Task.WhenAll(connections.Values.Select(x=>x.Work??Task.CompletedTask).ToArray());}
        finally{lifetime.Dispose();hostLock.Dispose();}
    }
    private sealed class Connection(Guid operation,Guid reference,LocalSecureScope scope,SecureOpenScope remote,CancellationToken token,SecureRelayRoute? route,long admitted)
    {
        public Guid Operation{get;}=operation;public Guid Reference{get;}=reference;
        public LocalSecureScope Scope{get;}=scope;public SecureOpenScope RemoteScope{get;}=remote;
        public SecureRelayRoute? Route{get;}=route;
        public long Admitted{get;}=admitted;
        public CancellationTokenSource Lifetime{get;}=CancellationTokenSource.CreateLinkedTokenSource(token,route?.RuntimeLifetime??CancellationToken.None);
        public string? PipeName{get;set;}public Task? Work{get;set;}
        public void Stop(){try{Lifetime.Cancel();}catch(ObjectDisposedException){}}
    }
}

internal sealed class PeerConnection : IAsyncDisposable
{
    private readonly TcpClient controlSocket,dataSocket;
    private readonly SslStream control;
    private readonly ControlClient? authorityClient;
    private AuthorizedLease? serverLease;
    private readonly LeaseWindow leaseWindow=new();
    public BusinessStream Stream{get;}
    public CancellationTokenSource Lifetime{get;}=new();
    private readonly Task renew;
    private PeerConnection(TcpClient controlSocket,SslStream control,TcpClient dataSocket,SslStream data,string ticket,ControlClient? authorityClient,AuthorizedLease? serverLease)
    {this.controlSocket=controlSocket;this.control=control;this.dataSocket=dataSocket;this.authorityClient=authorityClient;this.serverLease=serverLease;if(serverLease is not null)leaseWindow.Accept(serverLease.Receipt);Stream=new BusinessStream(data);renew=RenewAsync(ticket);}
    internal static async Task<PeerConnection> ConnectAsync(SecureIdentity identity,SecurePeer peer,SecureOpenScope scope,CancellationToken token,ControlClient? authorityClient=null,IPEndPoint? ownedEndpoint=null)
    {
        if(!peer.Active||(ownedEndpoint is null&&(peer.Host is null||peer.Port is null)))throw new IOException("Peer route unavailable.");
        var endpoint=ownedEndpoint??new IPEndPoint(IPAddress.Parse(peer.Host!),peer.Port!.Value);
        var controlSocket=new TcpClient{NoDelay=true};var dataSocket=new TcpClient{NoDelay=true};SslStream? control=null,data=null;
        using var cancellation=token.Register(()=>{controlSocket.Dispose();dataSocket.Dispose();});
        try
        {
            AuthorizedLease? lease=null;
            if(authorityClient is not null)
            {
                lease=await authorityClient.AcquireAsync(scope.Client,new ServiceTarget(peer.DeviceId,scope.Publisher,scope.Service),token);
                if(lease.Receipt.Authority.Protocol!=scope.Protocol||lease.Receipt.Authority.Authority!=(scope.Authority??""))throw new LeaseRejectedException();
            }
            await controlSocket.ConnectAsync(endpoint,token);control=new SslStream(controlSocket.GetStream(),false);
            await control.AuthenticateAsClientAsync(identity.ClientOptions(peer.DeviceId),token);
            object open=lease is null?new{kind="open",client=scope.Client,publisher=scope.Publisher,service=scope.Service,protocol=scope.Protocol,authority=scope.Authority}:
                new{kind="open",client=scope.Client,publisher=scope.Publisher,service=scope.Service,protocol=scope.Protocol,authority=scope.Authority,lease=lease.Token};
            await SecureWire.WriteAsync(control,open,token);
            var result=await SecureWire.ReadAsync(control,token);SecureWire.CheckRefusal(result);SecureWire.Fields(result,"ticket","grant","revision");
            string ticket=SecureWire.Token(result,"ticket");if(!Guid.TryParseExact(result.GetProperty("grant").GetString(),"D",out _)||result.GetProperty("revision").GetInt64()<1)throw new InvalidDataException("Invalid grant response.");
            await dataSocket.ConnectAsync(endpoint,token);data=new SslStream(dataSocket.GetStream(),false);
            await data.AuthenticateAsClientAsync(identity.ClientOptions(peer.DeviceId),token);
            await SecureWire.WriteAsync(data,new{kind="data",ticket},token);var ready=await SecureWire.ReadAsync(data,token);SecureWire.CheckRefusal(ready);SecureWire.Fields(ready,"kind");
            if(ready.GetProperty("kind").GetString()!="ready")throw new InvalidDataException("Data attachment rejected.");
            return new PeerConnection(controlSocket,control,dataSocket,data,ticket,authorityClient,lease);
        }
        catch{controlSocket.Dispose();dataSocket.Dispose();control?.Dispose();data?.Dispose();throw;}
    }
    private async Task RenewAsync(string ticket)
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
            while(await timer.WaitForNextTickAsync(Lifetime.Token))
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(8));
                if(serverLease is not null)
                {
                    _=leaseWindow.Current;
                    if(serverLease.Receipt.Remaining<=TimeSpan.FromSeconds(60))
                    {
                        var current=serverLease.Receipt.Authority;
                        var next=await authorityClient!.AcquireAsync(current.Scope.VisitorApp,current.Scope.Target,deadline.Token);
                        if(next.Receipt.Authority!=current)throw new LeaseRejectedException();
                        leaseWindow.Accept(next.Receipt);serverLease=next;
                    }
                }
                object heartbeat=serverLease is null?new{kind="renew",ticket}:new{kind="renew",ticket,lease=serverLease.Token};
                await SecureWire.WriteAsync(control,heartbeat,deadline.Token);var value=await SecureWire.ReadAsync(control,deadline.Token);SecureWire.Fields(value,"kind","seconds");
                if(value.GetProperty("kind").GetString()!="lease"||value.GetProperty("seconds").GetInt32() is <1 or >120)throw new IOException("Invalid lease.");
            }
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or LeaseRejectedException or ControlRefusedException){ }
        finally{leaseWindow.Close();Lifetime.Cancel();Stream.Dispose();controlSocket.Dispose();dataSocket.Dispose();}
    }
    internal void Abort(){try{Lifetime.Cancel();}catch(ObjectDisposedException){}controlSocket.Dispose();dataSocket.Dispose();Stream.Dispose();}
    public async ValueTask DisposeAsync()
    {Abort();control.Dispose();await renew;Lifetime.Dispose();}
}

public sealed partial class RegistrationStore
{
    internal FileStream AcquireSecureHostLock()
    {
        lock(gate)
        {
            RejectLinks();string path=Path.Combine(directory,"secure-host.lock");
            if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked host lock rejected.");
            if(File.Exists(path))
            {
                using var identity=WindowsIdentity.GetCurrent();var user=identity.User??throw new UnauthorizedAccessException();var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
                foreach(FileSystemAccessRule rule in new FileInfo(path).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)))
                    if(rule.AccessControlType==AccessControlType.Allow&&!rule.IdentityReference.Equals(user)&&!rule.IdentityReference.Equals(system))throw new UnauthorizedAccessException("Private host lock required.");
            }
            return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        }
    }
    internal LocalSecureScope ResolveSecureImport(string caller,Guid application,string alias,string peer,bool relay=false)=>GrantStorage(()=>
    {
        lock(gate){using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);var value=ReadSecureImport(c,t,caller,application,alias,peer,relay);t.Commit();return value;}
    });
    private LocalSecureScope ReadSecureImport(SqliteConnection c,SqliteTransaction t,string caller,Guid application,string alias,string peer,bool relay=false)
    {
        var review=OwnedReview(c,t,caller,application);var import=review.ApprovedManifest?.Imports.SingleOrDefault(x=>x.Alias==alias);
        var route=ReadSecurePeer(c,t,peer);
        if(review.ApprovedRevision is null||import is null||route?.Active!=true||(!relay&&(route.Host is null||route.Port is null))||import.EndpointMode!="brokered")throw new RegistrationException("AUTH_REQUIRED");
        if(import.ConnectionPolicy.Prewarm||import.ConnectionPolicy.RelayBudgetBytes is not null||import.ConnectionPolicy.RelayWarningBytes is not null|| (relay?(!import.ConnectionPolicy.AllowRelay||import.ConnectionPolicy.Mode=="direct-only"):(import.ConnectionPolicy.Mode!="direct-only"||import.ConnectionPolicy.AllowRelay)))throw new RegistrationException("CAPABILITY_UNSUPPORTED");
        return new LocalSecureScope(caller,application,review.ApprovedRevision.Value,review.AuthorityEpoch,review.ApprovedManifest!.ApplicationId,alias,ImportJson(review.ApprovedManifest,alias)!,import,route,relay);
    }
    internal bool[] SecureImportsCurrent(LocalSecureScope[] scopes)
    {
        var valid=new bool[scopes.Length];if(scopes.Length==0)return valid;
        try
        {
            lock(gate)
            {
                using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);CheckSecureTime(c,t);
                for(int i=0;i<scopes.Length;i++)
                {
                    var expected=scopes[i];
                    try{var current=ReadSecureImport(c,t,expected.Caller,expected.Application,expected.Alias,expected.Peer.DeviceId,expected.Relay);valid[i]=current.Revision==expected.Revision&&current.Decision==expected.Decision&&current.ImportJson==expected.ImportJson&&current.Peer==expected.Peer;}
                    catch(RegistrationException){valid[i]=false;}
                }
                t.Commit();return valid;
            }
        }
        catch(Exception){Array.Clear(valid);return valid;}
    }
    internal bool ExistingSecureOpen(string caller,Guid operation,string digest)=>GrantStorage(()=>
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);
            object? prior=Scalar(c,t,"SELECT digest FROM operations WHERE caller=$caller AND operation=$op",("$caller",caller),("$op",operation.ToString()));
            if(prior is null)return false;
            if(prior is not string text || text!=digest)throw new RegistrationException("OPERATION_CONFLICT");
            if(Convert.ToInt64(Scalar(c,t,"SELECT COUNT(*) FROM secure_opens WHERE caller=$caller AND operation=$op",("$caller",caller),("$op",operation.ToString())))!=1)throw new RegistrationException("STORAGE_UNAVAILABLE");return true;
        }
    });
    internal void BeginSecureOpen(string caller,Guid operation,string digest,Guid reference,Guid boot,string path="direct")=>GrantStorage(()=>
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);
            if(Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM secure_opens WHERE caller=$caller AND state!='closed')+(SELECT COUNT(*) FROM connections WHERE caller=$caller AND terminal=0)",("$caller",caller)))>=128||Convert.ToInt64(Scalar(c,t,"SELECT (SELECT COUNT(*) FROM secure_opens WHERE state!='closed')+(SELECT COUNT(*) FROM connections WHERE terminal=0)"))>=1024)throw new RegistrationException("RESOURCE_EXHAUSTED");
            AdmitRegistration(c,t,caller);
            Execute(c,t,"INSERT INTO operations VALUES($caller,$op,$digest,$result)",("$caller",caller),("$op",operation.ToString()),("$digest",digest),("$result",JsonSerializer.Serialize(new{connectionId=reference,path})));
            Execute(c,t,"INSERT INTO secure_opens VALUES($caller,$op,$reference,$boot,'pending',NULL)",("$caller",caller),("$op",operation.ToString()),("$reference",reference.ToString()),("$boot",boot.ToString()));t.Commit();return true;
        }
    });
    internal (Guid Connection,string State,string? Error,string Path) SecureOpenStatus(string caller,Guid operation)=>GrantStorage(()=>
    {
        lock(gate)
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);VerifyReviewSchema(c,t);
            using var command=Command(c,t,"SELECT connection,state,error,(SELECT result FROM operations WHERE caller=$caller AND operation=$op) FROM secure_opens WHERE caller=$caller AND operation=$op",("$caller",caller),("$op",operation.ToString()));using var reader=command.ExecuteReader();
            if(!reader.Read())throw new RegistrationException("OPERATION_UNKNOWN");
            string state=reader.GetString(1);string? error=reader.IsDBNull(2)?null:reader.GetString(2);
            if(state is not ("pending" or "ready" or "closed") || (error is not null&&error is not ("CONNECTION_CLOSED" or "DIRECT_FAILED" or "RELAY_FAILED" or "AUTH_REQUIRED" or "AGENT_RESTARTED" or "RESOURCE_EXHAUSTED")))throw new RegistrationException("STORAGE_UNAVAILABLE");
            using var result=ControlProtocol.ReadDocument(Encoding.UTF8.GetBytes(reader.GetString(3)));var value=result.RootElement;
            SecureWire.Fields(value,value.TryGetProperty("path",out _)?["connectionId","path"]:["connectionId"]);
            string path=value.TryGetProperty("path",out var selected)?selected.GetString()??"":"direct";
            if(path is not ("direct" or "relay")||value.GetProperty("connectionId").GetGuid()!=Guid.ParseExact(reader.GetString(0),"D"))throw new RegistrationException("STORAGE_UNAVAILABLE");
            return(Guid.ParseExact(reader.GetString(0),"D"),state,error,path);
        }
    });
    internal void SetSecureOpenState(string caller,Guid operation,Guid boot,string state,string? error)=>GrantStorage(()=>
    {
        lock(gate)
        {
            using var c=Open();Execute(c,null,"UPDATE secure_opens SET state=$state,error=$error WHERE caller=$caller AND operation=$op AND boot=$boot AND state!='closed'",
                ("$caller",caller),("$op",operation.ToString()),("$boot",boot.ToString()),("$state",state),("$error",(object?)error??DBNull.Value));return true;
        }
    });
    internal void ClosePreviousSecureOpens(Guid boot)=>GrantStorage(()=>
    {
        lock(gate){using var c=Open();Execute(c,null,"UPDATE secure_opens SET state='closed',error='AGENT_RESTARTED' WHERE boot!=$boot AND state!='closed'",("$boot",boot.ToString()));return true;}
    });
}
