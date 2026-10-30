using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;

namespace LinkGate.Runtime;

public sealed class FrpRoutes : IConnectionRoutes
{
    private readonly RegistrationStore store;private readonly SecureIdentity identity;private readonly IPEndPoint server;private readonly string serverKey,executable,digest,root;
    private readonly IPEndPoint[] stun;private readonly OwnedRuntimePool<string,Session> pool=new();private readonly Dictionary<string,ConnectionRouteScope> scopes=[];private readonly object gate=new();
    private readonly CancellationTokenSource stop=new();private readonly Task maintenance;private Task? disposal;private bool closed;
    public string DeviceId=>identity.DeviceId;
    public bool SupportsDirect=>stun.Length>=2;
    public bool SupportsRelay=>true;
    public int RuntimeCount=>pool.Count;
    public int LeaseCount=>pool.LeaseCount;
    public FrpRoutes(RegistrationStore store,SecureIdentity identity,IPEndPoint server,string serverKey,string executable,string sha256,string privateDirectory,IReadOnlyList<IPEndPoint>? stunEndpoints=null)
    {
        if(server.AddressFamily!=AddressFamily.InterNetwork||server.Port is <1 or >65535||!Key(serverKey)||sha256.Length!=64||!sha256.All(Uri.IsHexDigit))throw new ArgumentException("Explicit authenticated runtime required.");
        stun=(stunEndpoints??[]).Take(5).Select(x=>x is null?throw new ArgumentException():new IPEndPoint(new IPAddress(x.Address.GetAddressBytes()),x.Port)).ToArray();
        if(stun.Length>4||stun.Any(x=>x.AddressFamily!=AddressFamily.InterNetwork||x.Port is <1 or >65535||!Canonical(x.Address))||stun.Select(x=>x.ToString()).Distinct(StringComparer.Ordinal).Count()!=stun.Length)throw new ArgumentException("Explicit STUN endpoints required.");
        this.store=store;this.identity=identity;store.BindSecureIdentity(identity);this.server=new(server.Address,server.Port);this.serverKey=serverKey;this.executable=Path.GetFullPath(executable);digest=sha256;
        root=Path.GetFullPath(privateDirectory);SecureIdentity.MakePrivate(root);maintenance=Maintain();
    }
    public async ValueTask<IConnectionRoute> OpenAsync(ConnectionRouteScope scope,bool direct,long deadlineMilliseconds,CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(scope);if(direct&&!SupportsDirect||!direct&&!scope.AllowRelay)throw new ConnectionRouteException("CAPABILITY_UNSUPPORTED");
        if(!store.IsConnectionRouteScopeCurrent(scope))throw new UnauthorizedAccessException();
        ValueTask<OwnedRuntimePool<string,Session>.Lease> opening;
        lock(gate){if(closed)throw new IOException("Routes stopped.");if(!scopes.ContainsKey(scope.Key)&&scopes.Count>=32)throw new ConnectionRouteException("RESOURCE_EXHAUSTED");scopes[scope.Key]=scope;opening=pool.Acquire(scope.Key,creation=>Create(scope,creation),deadlineMilliseconds,cancellation);}
        var lease=await opening;
        try{return new Route(lease,direct);}catch{lease.Dispose();throw;}
    }
    private async Task<Session> Create(ConnectionRouteScope scope,OwnedRuntimePool<string,Session>.Creation creation)
    {
        var session=new Session(root);creation.Own(session);creation.Cancellation.ThrowIfCancellationRequested();
        if(!store.IsConnectionRouteScopeCurrent(scope))throw new UnauthorizedAccessException();
        long deadline=checked(creation.Started+scope.CreationTimeoutMs);string ns=Namespace(identity.DeviceId,scope.ClientApplication,"access"),publisher=Namespace(scope.PublisherDevice,scope.PublisherApplication,"publish"),name="lg1_"+Hash("service\n"+scope.PublisherDevice+"\n"+scope.PublisherApplication+"\n"+scope.Service);
        using(var relay=scope.AllowRelay?Reserve():null)using(var direct=SupportsDirect?Reserve():null){session.RelayPort=relay is null?0:((IPEndPoint)relay.LocalEndpoint).Port;session.DirectPort=direct is null?0:((IPEndPoint)direct.LocalEndpoint).Port;}
        if(session.RelayPort==0&&session.DirectPort==0)throw new ConnectionRouteException("CAPABILITY_UNSUPPORTED");
        var visitors=new List<object>();
        if(session.RelayPort>0)visitors.Add(new{name="stream-relay",type="stcp",serverUser=publisher,serverName=name+"_stcp",secretKey=publisher+"."+name+"_stcp",bindAddr="127.0.0.1",bindPort=session.RelayPort});
        if(session.DirectPort>0)visitors.Add(new{name="stream-direct",type="xtcp",serverUser=publisher,serverName=name+"_xtcp",secretKey=publisher+"."+name+"_xtcp",bindAddr="127.0.0.1",bindPort=session.DirectPort,protocol="quic",keepTunnelOpen=false,fallbackTo=""});
        session.Prepare();SecureIdentity.WriteNew(session.Profile,JsonSerializer.Serialize(new{user=ns,clientID=ns+"."+Guid.NewGuid().ToString("N"),serverAddr="127.0.0.1",serverPort=1,natHoleStunServer=SupportsDirect?stun[0].ToString():"127.0.0.1:1",loginFailExit=true,transport=new{protocol="tcp",wireProtocol="v2",tcpMux=true,heartbeatInterval=1,heartbeatTimeout=15},log=new{to="console",level="error"},visitors}));
        creation.Cancellation.ThrowIfCancellationRequested();
        FrpRuntimeBridge.Start(store,identity,scope.ApplicationRef,FrpRuntimeRole.Access,server,serverKey,executable,digest,["-c",session.Profile],session.Output,stun,session.OwnBridge);
        while(OwnedRuntimePool<string,Session>.Now()<deadline)
        {
            creation.Cancellation.ThrowIfCancellationRequested();if(!session.Alive)throw new IOException("Private runtime unavailable.");
            using var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);using var budget=CancellationTokenSource.CreateLinkedTokenSource(creation.Cancellation);budget.CancelAfter(100);
            try{await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback,session.RelayPort>0?session.RelayPort:session.DirectPort),budget.Token);return session;}
            catch(Exception failure)when(failure is SocketException or OperationCanceledException){creation.Cancellation.ThrowIfCancellationRequested();await Task.Delay(25,creation.Cancellation);}
        }
        throw new TimeoutException("Private runtime startup elapsed.");
    }
    private sealed class Session(string root):OwnedFrpSession(root){internal int RelayPort,DirectPort;}
    private sealed class Route : IConnectionRoute
    {
        private readonly OwnedRuntimePool<string,Session>.Lease lease;public int LoopbackPort{get;}public CancellationToken Cancellation{get;}
        internal Route(OwnedRuntimePool<string,Session>.Lease lease,bool direct){this.lease=lease;var session=lease.Value;LoopbackPort=direct?session.DirectPort:session.RelayPort;if(LoopbackPort<1)throw new IOException("Transport unavailable.");Cancellation=session.Token;}
        public void Dispose()=>lease.Dispose();
    }
    private async Task Maintain()
    {
        try{using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));while(await timer.WaitForNextTickAsync(stop.Token))
        {Dictionary<string,ConnectionRouteScope> current;lock(gate)current=new(scopes);pool.Maintain(key=>current.TryGetValue(key,out var scope)&&store.IsConnectionRouteScopeCurrent(scope),session=>session.Alive);
            lock(gate){foreach(string key in scopes.Keys.ToArray())if(!pool.Contains(key))scopes.Remove(key);}}}catch(OperationCanceledException){}
    }
    public void Invalidate()=>pool.Invalidate();
    public ValueTask DisposeAsync(){lock(gate){closed=true;return new ValueTask(disposal??=DisposeOwned());}}
    private async Task DisposeOwned(){stop.Cancel();try{await maintenance;await pool.DisposeAsync();}finally{stop.Dispose();}}
    internal static void DeleteOwnedFiles(string root,string directory)=>OwnedFrpSession.DeleteOwnedFiles(root,directory);
    private static TcpListener Reserve(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start(1);return listener;}
    private static bool Canonical(IPAddress address){byte[] ip=address.GetAddressBytes();return !address.Equals(IPAddress.Any)&&!address.Equals(IPAddress.Broadcast)&&!(ip[0] is >=224 and <=239)&&!(ip[0]==198&&ip[1] is 18 or 19)&&!(ip[0]==169&&ip[1]==254);}
    private static bool Key(string value)=>value.Length==64&&value.All(x=>x is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Namespace(string device,string app,string role)=>"lg1_"+Hash("namespace\n"+device+"\n"+app+"\n"+role);
}
