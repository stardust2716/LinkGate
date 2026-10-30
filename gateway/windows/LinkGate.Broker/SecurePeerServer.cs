using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LinkGate.Control;
using LinkGate.Authority;

namespace LinkGate.Broker;

internal sealed record SecureOpenScope(string Client,string Publisher,string Service,string Protocol,string? Authority);

// The same peer mTLS boundary is required over direct and relay transports.
public sealed class SecurePeerServer : IAsyncDisposable
{
    private readonly SecureIdentity identity;
    private readonly RegistrationStore store;
    private readonly LeaseSupervisor? serverAuthority;
    private readonly ServiceTarget? boundService;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim handshakes=new(32,32);
    private readonly SemaphoreSlim sockets=new(288,288),sessionSlots=new(128,128);
    private readonly ConcurrentDictionary<string,Session> sessions=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long,Task> handlers=new();
    private long handlerId;
    private Task? running;
    public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;

    public SecurePeerServer(SecureIdentity identity,RegistrationStore store,IPAddress bind,int port,LeaseSupervisor? serverAuthority=null,ServiceTarget? boundService=null)
    {
        if(bind.AddressFamily!=AddressFamily.InterNetwork || port is <0 or >65535)throw new ArgumentException("IPv4 endpoint required.");
        if(serverAuthority is not null&&serverAuthority.Client.DeviceID!=identity.DeviceId)throw new ArgumentException("Publisher authority identity mismatch.");
        if(boundService is not null&&boundService.PublisherDevice!=identity.DeviceId)throw new ArgumentException("Transport service identity mismatch.");
        this.boundService=boundService;
        this.identity=identity;this.store=store;this.serverAuthority=serverAuthority;listener=new TcpListener(bind,port);
    }
    public void Start()
    {
        if(running is not null)throw new InvalidOperationException("Already started.");
        store.BindSecureIdentity(identity);listener.Start(32);running=RunAsync();
    }
    private async Task RunAsync()
    {
        Task monitor=MonitorAsync();
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                var socket=await listener.AcceptTcpClientAsync(lifetime.Token);
                if(!sockets.Wait(0)){socket.Dispose();continue;}
                if(!handshakes.Wait(0)){sockets.Release();socket.Dispose();continue;}
                long id=Interlocked.Increment(ref handlerId);
                // Assignment happens before removal even if a task fails synchronously.
                var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);handlers[id]=completion.Task;
                _=HandleTrackedAsync(socket,id,completion);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or SocketException or ObjectDisposedException){ }
        finally
        {
            lifetime.Cancel();foreach(var session in sessions.Values)session.Stop();
            await monitor;await Task.WhenAll(handlers.Values.ToArray());
        }
    }
    private async Task HandleTrackedAsync(TcpClient socket,long id,TaskCompletionSource completion)
    {
        using var handshake=new Slot(handshakes);
        try{await HandleAsync(socket,handshake);}
        catch(Exception e) when(e is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException or RegistrationException or ApplicationReviewException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or LeaseRejectedException or ControlRefusedException){ }
        finally{socket.Dispose();sockets.Release();completion.TrySetResult();handlers.TryRemove(id,out _);}
    }
    private async Task HandleAsync(TcpClient socket,Slot handshake)
    {
        socket.NoDelay=true;
        using var ssl=new SslStream(socket.GetStream(),false);
        using var opening=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);opening.CancelAfter(TimeSpan.FromSeconds(5));
        await ssl.AuthenticateAsServerAsync(identity.ServerOptions(store.IsSecurePeerActive),opening.Token);
        using var remote=new X509Certificate2(ssl.RemoteCertificate??throw new AuthenticationException());
        string peer=SecureIdentity.KeyId(remote);
        // Recheck after TLS: resumption and in-flight revocations must not skip current trust.
        if(!store.IsSecurePeerActive(peer))throw new AuthenticationException();
        var header=await SecureWire.ReadAsync(ssl,opening.Token);string kind=header.GetProperty("kind").GetString()??"";
        if(kind=="data")
        {
            SecureWire.Fields(header,"kind","ticket");string ticket=SecureWire.Token(header,"ticket");
            if(!sessions.TryGetValue(ticket,out var session)||session.Authorization.Peer!=peer||!session.TryAttach()){await SecureWire.RefuseAsync(ssl,"AUTH_REQUIRED",opening.Token);return;}
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,session.Lifetime.Token);
            if(!store.SecureAuthorizationCurrent(session.Authorization,session.Scope.Client,session.Scope.Publisher,session.Scope.Service)||session.ServerLease?.Active==false){await SecureWire.RefuseAsync(ssl,"AUTH_REQUIRED",opening.Token);return;}
            handshake.Dispose();
            using var target=new TcpClient{NoDelay=true};
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));await target.ConnectAsync(IPAddress.Loopback,session.Authorization.Port,deadline.Token);
                await SecureWire.WriteAsync(ssl,new{kind="ready"},deadline.Token);
            }
            try{await BridgeTargetAsync(new BusinessStream(ssl),target,linked.Token);}
            catch{session.Stop();throw;}
            return;
        }
        if(kind!="open")throw new InvalidDataException("Invalid peer request.");
        if(serverAuthority is null)SecureWire.Fields(header,"kind","client","publisher","service","protocol","authority");
        else {if(!header.TryGetProperty("lease",out _)){await SecureWire.RefuseAsync(ssl,"AUTH_REQUIRED",opening.Token);return;}SecureWire.Fields(header,"kind","client","publisher","service","protocol","authority","lease");}
        var scope=new SecureOpenScope(SecureWire.Text(header,"client"),SecureWire.Text(header,"publisher"),SecureWire.Text(header,"service"),SecureWire.Text(header,"protocol"),header.GetProperty("authority").ValueKind==JsonValueKind.Null?null:SecureWire.Text(header,"authority"));
        if(boundService is not null&&(scope.Publisher!=boundService.PublisherApp||scope.Service!=boundService.Service)){await SecureWire.RefuseAsync(ssl,"AUTH_REQUIRED",opening.Token);return;}
        SecureAuthorization authorization;
        try{authorization=store.AuthorizeSecureOpen(peer,scope.Client,scope.Publisher,scope.Service,scope.Protocol,scope.Authority);}
        catch(RegistrationException e){await SecureWire.RefuseAsync(ssl,e.Code=="RESOURCE_EXHAUSTED"?"RESOURCE_EXHAUSTED":"AUTH_REQUIRED",opening.Token);return;}
        LeaseGuard? serverLease=null;
        if(serverAuthority is not null)
        {
            try
            {
                var verified=await serverAuthority.Client.ValidateAsync(LeaseToken(header),new AccessScope(peer,scope.Client,new ServiceTarget(identity.DeviceId,scope.Publisher,scope.Service)),opening.Token);
                if(verified.Receipt.Authority.Protocol!=scope.Protocol||verified.Receipt.Authority.Authority!=(scope.Authority??""))throw new LeaseRejectedException();
                serverLease=serverAuthority.Register(verified);
            }
            catch(Exception failure)when(failure is LeaseRejectedException or ControlRefusedException){await SecureWire.RefuseAsync(ssl,failure is ControlRefusedException{Code:"RESOURCE_EXHAUSTED"}?"RESOURCE_EXHAUSTED":"AUTH_REQUIRED",opening.Token);return;}
        }
        if(!sessionSlots.Wait(0)){serverLease?.Dispose();await SecureWire.RefuseAsync(ssl,"RESOURCE_EXHAUSTED",opening.Token);return;}
        string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var created=new Session(scope,authorization,remote.NotAfter.ToUniversalTime(),serverLease);
        if(!sessions.TryAdd(token,created)){sessionSlots.Release();created.Stop();created.Lifetime.Dispose();throw new IOException("Stream capacity reached.");}
        try
        {
            await SecureWire.WriteAsync(ssl,new{ticket=token,grant=authorization.GrantId,revision=authorization.GrantRevision},opening.Token);
            handshake.Dispose();
            while(!created.Lifetime.IsCancellationRequested)
            {
                using var renewal=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,created.Lifetime.Token);renewal.CancelAfter(TimeSpan.FromSeconds(12));
                var request=await SecureWire.ReadAsync(ssl,renewal.Token);
                if(serverAuthority is null)SecureWire.Fields(request,"kind","ticket");else SecureWire.Fields(request,"kind","ticket","lease");
                if(request.GetProperty("kind").GetString()!="renew" || SecureWire.Token(request,"ticket")!=token)throw new AuthenticationException();
                if(created.ServerLease is { } guard)
                {
                    string next=LeaseToken(request);var previous=guard.Current;
                    if(next!=previous.Token)
                    {
                        var verified=await serverAuthority!.Client.ValidateAsync(next,previous.Receipt.Authority.Scope,renewal.Token);
                        guard.Renew(verified);
                    }
                }
                created.Renew();await SecureWire.WriteAsync(ssl,new{kind="lease",seconds=120},renewal.Token);
            }
        }
        finally{sessions.TryRemove(token,out _);created.Stop();sessionSlots.Release();/* CTS remains alive until all data handlers have left. */}
    }
    private static string LeaseToken(JsonElement value)=>value.GetProperty("lease").GetString() is {Length:>0 and <=16384} token?token:throw new InvalidDataException("Invalid server lease.");
    private async Task MonitorAsync()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while(await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var snapshot=sessions.Values.ToArray();
                // One transaction bounds a busy/corrupt storage failure for the entire batch.
                bool[] valid=store.SecureBatchCurrent(snapshot.Select(x=>(x.Authorization,x.Scope)).ToArray());
                bool certificateValid=DateTime.UtcNow<identity.Certificate.NotAfter.ToUniversalTime();
                for(int i=0;i<snapshot.Length;i++)if(!valid[i]||!certificateValid||snapshot[i].Expired)snapshot[i].Stop();
            }
        }
        catch(OperationCanceledException){ }
    }
    internal static async Task BridgeTargetAsync(BusinessStream remote,TcpClient target,CancellationToken cancellation)
    {
        NetworkStream targetStream=target.GetStream();
        async Task Receive()
        {
            byte[] buffer=new byte[16384];int n;
            while((n=await remote.ReadAsync(buffer,cancellation))!=0)await targetStream.WriteAsync(buffer.AsMemory(0,n),cancellation);
            target.Client.Shutdown(SocketShutdown.Send);
        }
        async Task Send()
        {
            byte[] buffer=new byte[16384];int n;
            while((n=await targetStream.ReadAsync(buffer,cancellation))!=0)await remote.WriteAsync(buffer.AsMemory(0,n),cancellation);
            await remote.CompleteWritesAsync(cancellation);
        }
        await AwaitPairAsync(Receive,Send,()=>{target.Dispose();remote.Dispose();});
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await remote.DrainWriteCompletionAsync(deadline.Token);
    }
    internal static async Task AwaitPairAsync(Func<Task> first,Func<Task> second,Action abort)
    {
        async Task Guard(Func<Task> action){try{await action();}catch{abort();throw;}}
        await Task.WhenAll(Guard(first),Guard(second));
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();listener.Stop();foreach(var session in sessions.Values)session.Stop();
        if(running is not null)await running;lifetime.Dispose();handshakes.Dispose();sockets.Dispose();sessionSlots.Dispose();
    }
    private sealed class Slot(SemaphoreSlim gate):IDisposable{private int released;public void Dispose(){if(Interlocked.Exchange(ref released,1)==0)gate.Release();}}
    private sealed class Session
    {
        public SecureOpenScope Scope{get;}
        public SecureAuthorization Authorization{get;}
        public CancellationTokenSource Lifetime{get;}
        public LeaseGuard? ServerLease{get;}
        private readonly DateTime certificateExpiry;
        public Session(SecureOpenScope scope,SecureAuthorization authorization,DateTime certificateExpiry,LeaseGuard? serverLease){Scope=scope;Authorization=authorization;this.certificateExpiry=certificateExpiry;ServerLease=serverLease;Lifetime=serverLease is null?new():CancellationTokenSource.CreateLinkedTokenSource(serverLease.Cancellation);}
        private readonly long created=Stopwatch.GetTimestamp();
        private long renewed=Stopwatch.GetTimestamp();private int attached;
        public bool Expired=>ServerLease?.Active==false || DateTime.UtcNow>=certificateExpiry || Stopwatch.GetElapsedTime(Volatile.Read(ref renewed))>TimeSpan.FromSeconds(120) || (Volatile.Read(ref attached)==0&&Stopwatch.GetElapsedTime(created)>TimeSpan.FromSeconds(10));
        public bool TryAttach()=>!Lifetime.IsCancellationRequested&&!Expired&&Interlocked.CompareExchange(ref attached,1,0)==0;
        public void Renew()=>Volatile.Write(ref renewed,Stopwatch.GetTimestamp());
        public void Stop(){try{Lifetime.Cancel();}catch(ObjectDisposedException){}finally{ServerLease?.Dispose();}}
    }
}

internal static class SecureWire
{
    public static Task RefuseAsync(Stream stream,string code,CancellationToken token)=>WriteAsync(stream,new{kind="refused",code},token);
    public static void CheckRefusal(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty("kind",out var kind)||kind.GetString()!="refused")return;
        Fields(value,"kind","code");string? code=value.GetProperty("code").GetString();if(code is not ("AUTH_REQUIRED" or "RESOURCE_EXHAUSTED"))throw new InvalidDataException("Invalid peer refusal.");throw new PeerRefusalException(code);
    }
    public static async Task<JsonElement> ReadAsync(Stream stream,CancellationToken ct)
    {var bytes=await LinkGate.Control.FrameCodec.ReadAsync(stream,ct)??throw new IOException("Peer disconnected.");using var document=ControlJson.ReadDocument(bytes);return document.RootElement.Clone();}
    public static Task WriteAsync(Stream stream,object value,CancellationToken ct)=>LinkGate.Control.FrameCodec.WriteAsync(stream,JsonSerializer.SerializeToUtf8Bytes(value),ct);
    public static void Fields(JsonElement value,params string[] names)
    {if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=names.Length||value.EnumerateObject().Any(x=>!names.Contains(x.Name,StringComparer.Ordinal)))throw new InvalidDataException("Invalid peer message.");}
    public static string Text(JsonElement value,string name)=>value.GetProperty(name).GetString() is {Length:>0 and <=253} text?text:throw new InvalidDataException("Invalid peer field.");
    public static string Token(JsonElement value,string name)
    {string text=Text(value,name);if(text.Length!=64||text.Any(c=>!((c>='0'&&c<='9')||(c>='a'&&c<='f'))))throw new InvalidDataException("Invalid peer token.");return text;}
}
internal sealed class PeerRefusalException(string code):IOException("Authenticated peer refused the connection."){public string Code{get;}=code;}
