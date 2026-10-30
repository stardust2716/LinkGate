using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;
using LinkGate.Control;
using LinkGate.Authority;

internal static class SecureStreamTests
{
    internal static SecureIdentity Identity(string directory,X509Certificate2 owner)
    {
        string csr=SecureIdentity.CreateDeviceRequest(directory);
        SecureIdentity.WriteNew(Path.Combine(directory,"device-cert.pem"),SecureIdentity.IssueDeviceCertificate(owner,csr));
        SecureIdentity.WriteNew(Path.Combine(directory,"owner-root.pem"),owner.ExportCertificatePem());return SecureIdentity.Load(directory);
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new Exception("Repository not found");
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-secure-stream-tests"));string directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            using var owner=SecureIdentity.CreateOwnerRoot();using var a=Identity(Path.Combine(directory,"a/identity"),owner);using var b=Identity(Path.Combine(directory,"b/identity"),owner);using var unknown=Identity(Path.Combine(directory,"unknown/identity"),owner);
            var one=new RegistrationStore(Path.Combine(directory,"a"));var two=new RegistrationStore(Path.Combine(directory,"b"));one.BindSecureIdentity(a);two.BindSecureIdentity(b);
            var trustA=two.TrustSecurePeer(b,a.Certificate.ExportCertificatePem(),0,Guid.NewGuid());one.TrustSecurePeer(a,b.Certificate.ExportCertificatePem(),0,Guid.NewGuid(),"127.0.0.1",17443);
            check(a.DeviceId!=b.DeviceId && a.DeviceId.Length==64 && one.IsSecurePeerActive(b.DeviceId) && two.IsSecurePeerActive(a.DeviceId),"separate locally generated device keys receive explicit owner trust");
            await Tls(a,b,two,b.DeviceId,true,check);await Tls(unknown,b,two,b.DeviceId,false,check);await Tls(a,b,two,new string('0',64),false,check);
            using var wrongRoot=SecureIdentity.CreateOwnerRoot();using var wrongIdentity=new SecureIdentity(X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(directory,"a/identity/device-cert.pem")),File.ReadAllText(Path.Combine(directory,"a/identity/device-key.pem"))),X509Certificate2.CreateFromPem(wrongRoot.ExportCertificatePem()));
            await Tls(wrongIdentity,b,two,b.DeviceId,false,check);
            await Framing(check);
            Authority(two,b,a,trustA,check);
            await OperationLifecycle(one,a,b,check);
            await TicketLifecycle(Path.Combine(directory,"tickets"),a,b,check);
        }
        finally
        {
            string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe cleanup path");
            if(Directory.Exists(target))Directory.Delete(target,true);
        }
    }
    private static async Task Tls(SecureIdentity clientIdentity,SecureIdentity serverIdentity,RegistrationStore store,string expected,bool shouldPass,Action<bool,string> check)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(6));var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        async Task Server()
        {
            try
            {
                using var socket=await listener.AcceptTcpClientAsync(stop.Token);using var ssl=new SslStream(socket.GetStream(),false);
                await ssl.AuthenticateAsServerAsync(serverIdentity.ServerOptions(store.IsSecurePeerActive),stop.Token);
                var frame=await LinkGate.Control.FrameCodec.ReadAsync(ssl,stop.Token);if(frame is not null)await LinkGate.Control.FrameCodec.WriteAsync(ssl,frame,stop.Token);
            }
            catch(Exception e)when(e is IOException or AuthenticationException or OperationCanceledException){}
        }
        Task serving=Server();bool passed=false;
        try
        {
            using var socket=new TcpClient();await socket.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port,stop.Token);
            using var ssl=new SslStream(socket.GetStream(),false);await ssl.AuthenticateAsClientAsync(clientIdentity.ClientOptions(expected),stop.Token);
            byte[] sent=Encoding.UTF8.GetBytes("authenticated peer");await LinkGate.Control.FrameCodec.WriteAsync(ssl,sent,stop.Token);
            byte[]? received=await LinkGate.Control.FrameCodec.ReadAsync(ssl,stop.Token);passed=received is not null&&received.SequenceEqual(sent);
        }
        catch(Exception e)when(e is IOException or AuthenticationException or OperationCanceledException){}
        finally{stop.Cancel();listener.Stop();await serving;}
        check(passed==shouldPass,shouldPass?"actual mutual TLS validates both approved device identities":"mutual TLS rejects unknown device, wrong pin or wrong root");
    }
    private static async Task Framing(Action<bool,string> check)
    {
        await using var wire=new MemoryStream();await using(var writer=new BusinessStream(new LeaveOpen(wire)))
        {await writer.WriteAsync(new byte[40000]);await writer.CompleteWritesAsync();}
        wire.Position=0;await using var reader=new BusinessStream(new LeaveOpen(wire));byte[] buffer=new byte[113];int count=0;int got;
        while((got=await reader.ReadAsync(buffer))>0)count+=got;
        check(count==40000&&reader.PayloadBytesRead==40000,"bounded business framing preserves fragmented reads and directional FIN");
        await using var truncated=new BusinessStream(new MemoryStream());try{_=await truncated.ReadAsync(buffer);throw new Exception("Transport EOF mistaken for clean FIN");}catch(IOException){check(true,"transport loss is distinct from clean half-close");}
        byte[] oversized=new byte[65540];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oversized,65536);
        using var oversizedWire=new MemoryStream(oversized);await using var bounded=new BusinessStream(oversizedWire);
        try{_=await bounded.ReadAsync(buffer);throw new Exception("Oversized business frame accepted");}catch(IOException){check(oversizedWire.Position==4,"oversized business frame is rejected from its header before allocating or reading its payload");}
    }
    private sealed class LeaveOpen(Stream inner):Stream
    {
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>inner.Length;
        public override long Position{get=>inner.Position;set=>inner.Position=value;}public override void Flush()=>inner.Flush();
        public override int Read(byte[] b,int o,int n)=>inner.Read(b,o,n);public override void Write(byte[] b,int o,int n)=>inner.Write(b,o,n);
        public override ValueTask<int> ReadAsync(Memory<byte>b,CancellationToken t=default)=>inner.ReadAsync(b,t);public override ValueTask WriteAsync(ReadOnlyMemory<byte>b,CancellationToken t=default)=>inner.WriteAsync(b,t);
        public override long Seek(long n,SeekOrigin o)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
    private static void Authority(RegistrationStore store,SecureIdentity publisher,SecureIdentity client,SecureAuthorityResult trust,Action<bool,string> check)
    {
        const string manifest="""
            {"manifestVersion":1,"applicationId":"org.example.secure-service","displayName":"Secure service","exports":[{"serviceId":"files","label":"Files","protocol":"https","logicalAuthority":"files.example.invalid","localTarget":{"host":"127.0.0.1","port":18480}}],"imports":[]}
            """;
        Guid application=Guid.Parse(store.Register("publisher",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(manifest))).GetProperty("applicationRef").GetString()!);
        store.DecideReview(store.GetReview(application),ApplicationReviewDecision.Approved,Guid.NewGuid());
        bool Allowed(string app="org.example.secure-client",string service="files")
        {try{store.AuthorizeSecureOpen(client.DeviceId,app,"org.example.secure-service",service,"https","files.example.invalid");return true;}catch(RegistrationException){return false;}}
        check(!Allowed(),"trusted device without service grant cannot deliver business data");
        var proposal=store.ProposeSecureGrant(client.DeviceId,"org.example.secure-client",application,"files");Guid operation=Guid.NewGuid();var grant=store.ApproveSecureGrant(proposal,operation);
        check(Allowed()&&!Allowed("org.example.other")&&!Allowed(service:"other"),"publisher authority enforces exact device application and service scope");
        var scope=store.AuthorizeSecureOpen(client.DeviceId,"org.example.secure-client","org.example.secure-service","files","https","files.example.invalid");
        check(scope.Port==18480&&scope.GrantId==Guid.Parse(grant.Reference)&&store.GetJournalCapacity().ReservedRevocations==3,"grant resolves only reviewed fixed loopback target and reserves its revocation");
        var beforeDenial=store.GetReview(application);var staleProposal=store.ProposeSecureGrant(client.DeviceId,"org.example.secure-client",application,"files");
        store.Register("publisher",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(manifest.Replace("Secure service","Changed pending name",StringComparison.Ordinal))));
        store.DecideReview(store.GetReview(application),ApplicationReviewDecision.Denied,Guid.NewGuid());var afterDenial=store.GetReview(application);
        check(afterDenial.DecisionVersion==beforeDenial.DecisionVersion+1&&afterDenial.AuthorityEpoch==beforeDenial.AuthorityEpoch&&Allowed()&&store.SecureAuthorizationCurrent(scope,"org.example.secure-client","org.example.secure-service","files"),"denying a pending update preserves the existing grant and active authority epoch");
        try{store.ApproveSecureGrant(staleProposal,Guid.NewGuid());throw new Exception("Pre-denial proposal accepted.");}catch(ApplicationReviewException e){check(e.Code=="REVIEW_STALE","denial invalidates old review proposals without revoking active authority");}
        store.RevokeSecureGrant(scope.GrantId,grant.Revision,Guid.NewGuid());
        check(!Allowed()&&!store.SecureAuthorizationCurrent(scope,"org.example.secure-client","org.example.secure-service","files"),"committed grant revocation rejects new and existing authorization checks");
        check(store.ApproveSecureGrant(proposal,operation)==grant&&!Allowed(),"old grant approval outcome cannot resurrect revoked authority");
        check(store.GetSecureAuthorityOperation(operation)==grant,"owner authority lookup recovers the original retained grant outcome");
        try{store.ApproveSecureGrant(proposal,Guid.NewGuid());throw new Exception("Stale grant proposal accepted");}catch(ApplicationReviewException e){check(e.Code=="REVIEW_STALE","a new approval operation cannot reuse stale grant versions");}
        var newGrant=store.ApproveSecureGrant(store.ProposeSecureGrant(client.DeviceId,"org.example.secure-client",application,"files"),Guid.NewGuid());check(Allowed(),"new owner decision can issue a new grant revision");
        store.RevokeSecurePeer(client.DeviceId,trust.Revision,Guid.NewGuid());check(!Allowed()&&!store.IsSecurePeerActive(client.DeviceId),"device revocation disables associated grants");
        store.TrustSecurePeer(publisher,client.Certificate.ExportCertificatePem(),trust.Revision+1,Guid.NewGuid());check(!Allowed(),"re-trusting a device does not reactivate its revoked grants");
        store.SecureClock=new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-10));
        check(!store.IsSecurePeerActive(client.DeviceId),"observed clock rollback fails closed for device authorization");store.SecureClock=TimeProvider.System;
        try{store.BindSecureIdentity(client);throw new Exception("Changed database identity accepted");}catch(ApplicationReviewException e){check(e.Code=="IDENTITY_CHANGED","database binding rejects replacement device identity");}
    }
    private sealed class FixedClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
    private static async Task TicketLifecycle(string directory,SecureIdentity client,SecureIdentity publisher,Action<bool,string> check)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var target=new TcpListener(IPAddress.Loopback,0);target.Start();
        var store=new RegistrationStore(directory);store.BindSecureIdentity(publisher);store.TrustSecurePeer(publisher,client.Certificate.ExportCertificatePem(),0,Guid.NewGuid());
        var manifest=ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new{manifestVersion=1,applicationId="org.example.ticket-publisher",displayName="Tickets",exports=new[]{new{serviceId="raw",label="Raw",protocol="tcp",localTarget=new{host="127.0.0.1",port=((IPEndPoint)target.LocalEndpoint).Port}}},imports=Array.Empty<object>()}));
        Guid app=store.Register("ticket-publisher",Guid.NewGuid(),manifest).GetProperty("applicationRef").GetGuid();store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
        var grant=store.ApproveSecureGrant(store.ProposeSecureGrant(client.DeviceId,"org.example.ticket-client",app,"raw"),Guid.NewGuid());
        await using var server=new SecurePeerServer(publisher,store,IPAddress.Loopback,0);server.Start();
        async Task<(TcpClient Socket,SslStream Ssl)> Dial()
        {
            var socket=new TcpClient();SslStream? ssl=null;
            try{await socket.ConnectAsync(IPAddress.Loopback,server.Port,stop.Token);ssl=new SslStream(socket.GetStream(),false);await ssl.AuthenticateAsClientAsync(client.ClientOptions(publisher.DeviceId),stop.Token);return(socket,ssl);}
            catch{ssl?.Dispose();socket.Dispose();throw;}
        }
        try
        {
            await using(var scoped=new SecurePeerServer(publisher,store,IPAddress.Loopback,0,boundService:new ServiceTarget(publisher.DeviceId,"org.example.ticket-publisher","other")))
            {
                scoped.Start();using var socket=new TcpClient();await socket.ConnectAsync(IPAddress.Loopback,scoped.Port,stop.Token);using var tls=new SslStream(socket.GetStream(),false);await tls.AuthenticateAsClientAsync(client.ClientOptions(publisher.DeviceId),stop.Token);
                await SecureWire.WriteAsync(tls,new{kind="open",client="org.example.ticket-client",publisher="org.example.ticket-publisher",service="raw",protocol="tcp",authority=(string?)null},stop.Token);
                bool refused=false;try{SecureWire.CheckRefusal(await SecureWire.ReadAsync(tls,stop.Token));}catch(PeerRefusalException failure){refused=failure.Code=="AUTH_REQUIRED";}
                check(refused&&!target.Pending(),"a valid local grant cannot cross a different transport service boundary");
            }
            var control=await Dial();using var controlSocket=control.Socket;using var controlTls=control.Ssl;
            await SecureWire.WriteAsync(controlTls,new{kind="open",client="org.example.ticket-client",publisher="org.example.ticket-publisher",service="raw",protocol="tcp",authority=(string?)null},stop.Token);
            var result=await SecureWire.ReadAsync(controlTls,stop.Token);string ticket=result.GetProperty("ticket").GetString()!;
            using var renewal=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            async Task Renew()
            {
                try{while(true){await Task.Delay(500,renewal.Token);await SecureWire.WriteAsync(controlTls,new{kind="renew",ticket},renewal.Token);_=await SecureWire.ReadAsync(controlTls,renewal.Token);}}
                catch(Exception e) when(e is IOException or OperationCanceledException){ }
            }
            Task renewing=Renew();
            try
            {
                var first=await Dial();using var firstSocket=first.Socket;using var firstTls=first.Ssl;
                await SecureWire.WriteAsync(firstTls,new{kind="data",ticket},stop.Token);_=await SecureWire.ReadAsync(firstTls,stop.Token);
                using var backend=await target.AcceptTcpClientAsync(stop.Token);
                var duplicate=await Dial();using var duplicateSocket=duplicate.Socket;using var duplicateTls=duplicate.Ssl;
                await SecureWire.WriteAsync(duplicateTls,new{kind="data",ticket},stop.Token);bool rejected=false;
                try{var refused=await SecureWire.ReadAsync(duplicateTls,stop.Token);SecureWire.CheckRefusal(refused);}catch(IOException){rejected=true;}
                check(rejected&&!target.Pending(),"one-use peer ticket cannot create a second backend connection");
                var watch=System.Diagnostics.Stopwatch.StartNew();store.RevokeSecureGrant(Guid.Parse(grant.Reference),grant.Revision,Guid.NewGuid());
                using var stream=new BusinessStream(firstTls);bool revoked=false;
                try{_=await stream.ReadAsync(new byte[1],stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(4));}catch(IOException){revoked=true;}
                check(revoked&&watch.ElapsedMilliseconds<5000,"publisher revocation cancels a blocked business read within five seconds");
                check(await backend.GetStream().ReadAsync(new byte[1],stop.Token)==0,"revocation also closes the approved backend socket");
            }
            finally{renewal.Cancel();controlSocket.Dispose();await renewing;}
        }
        finally{stop.Cancel();target.Stop();}
    }
    private static async Task OperationLifecycle(RegistrationStore store,SecureIdentity identity,SecureIdentity peer,Action<bool,string> check)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int accepted=0;
        async Task Accept()
        {try{using var socket=await listener.AcceptTcpClientAsync(stop.Token);Interlocked.Increment(ref accepted);}catch(OperationCanceledException){}}
        Task accepting=Accept();
        try
        {
            store.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),1,Guid.NewGuid(),"127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port);
            const string manifest="""
                {"manifestVersion":1,"applicationId":"org.example.secure-client","displayName":"Secure client","exports":[],"imports":[{"alias":"files","protocol":"https","logicalAuthority":"files.example.invalid","endpointMode":"brokered","connectionPolicy":{"mode":"direct-only","allowRelay":false,"connectTimeoutMs":1000}}]}
                """;
            Guid app=store.Register("local",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(manifest))).GetProperty("applicationRef").GetGuid();
            store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
            Guid operation=Guid.NewGuid();Guid reference;
            await using(var provider=new SecureConnections(store,identity))
            {
                try{await using var collision=new SecureConnections(store,identity);throw new Exception("Second state host accepted");}catch(IOException){check(true,"exclusive secure host lease rejects concurrent state recovery");}
                var protocol=new ControlProtocol(store,false,provider);
                byte[] Request(string method,object parameters,Guid? op=null)=>JsonSerializer.SerializeToUtf8Bytes(op is null?new Dictionary<string,object>{{"kind","request"},{"apiVersion",1},{"requestId",Guid.NewGuid()},{"method",method},{"params",parameters}}:new Dictionary<string,object>{{"kind","request"},{"apiVersion",1},{"requestId",Guid.NewGuid()},{"method",method},{"params",parameters},{"operationId",op.Value}});
                string Error(byte[] request,string? caller){using var response=JsonDocument.Parse(protocol.Dispatch(request,caller));return response.RootElement.GetProperty("error").GetProperty("code").GetString()!;}
                using(var hello=JsonDocument.Parse(protocol.Dispatch(Request("hello",new{supportedApiVersions=new[]{1}}))))
                    check(hello.RootElement.GetProperty("result").GetProperty("capabilities").EnumerateArray().Single(x=>x.GetProperty("name").GetString()=="secure.stream.direct").GetProperty("state").GetString()=="experimental","secure direct capability requires an explicit provider and remains experimental");
                var parameters=new{applicationRef=app,importAlias="files",peer=peer.DeviceId,publisher="org.example.secure-service",service="files"};
                check(Error(Request("secure.connection.open",parameters,Guid.NewGuid()),null)=="AUTH_REQUIRED","secure RPC rejects requests without actual caller identity");
                check(Error(Request("secure.connection.open",parameters,Guid.NewGuid()),"other")=="AUTH_REQUIRED","secure RPC cannot open another caller's reviewed application");
                check(Error(Request("secure.connection.open",new{applicationRef=app,importAlias="files",peer=peer.DeviceId,publisher="org.example.secure-service",service="files",target="127.0.0.1:1"},Guid.NewGuid()),"local")=="INVALID_REQUEST","secure RPC rejects caller-selected target addresses");
                check(Error(Request("connection.open",parameters,Guid.NewGuid()),"local")=="CAPABILITY_UNSUPPORTED","direct preview does not silently replace the unfinished product connection API");
                foreach(string limit in new[]{"\"relayBudgetBytes\":\"100\",\"relayBudgetAction\":\"stop-relay\"","\"relayWarningBytes\":\"100\""})
                {
                    string budgetDeclaration=manifest.Replace("org.example.secure-client","org.example.budget-client").Replace("\"allowRelay\":false","\"allowRelay\":false,"+limit);
                    Guid budgetApp=store.Register("local",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(budgetDeclaration))).GetProperty("applicationRef").GetGuid();store.DecideReview(store.GetReview(budgetApp),ApplicationReviewDecision.Approved,Guid.NewGuid());
                    check(Error(Request("secure.connection.open",new{applicationRef=budgetApp,importAlias="files",peer=peer.DeviceId,publisher="org.example.secure-service",service="files"},Guid.NewGuid()),"local")=="CAPABILITY_UNSUPPORTED"&&accepted==0,"unsupported declared relay limits fail before any peer IO");
                }
                var result=provider.Open("local",operation,app,"files",peer.DeviceId,"org.example.secure-service","files");reference=result.GetProperty("connectionId").GetGuid();
                var repeated=provider.Open("local",operation,app,"files",peer.DeviceId,"org.example.secure-service","files");
                check(repeated.GetProperty("connectionId").GetGuid()==reference,"duplicate secure open retains its original reference");
                while(provider.Status("local",operation).GetProperty("state").GetString()!="closed")await Task.Delay(20,stop.Token);
                await accepting;
                check(accepted==1,"failed secure open is never automatically redialed");
                try{provider.Open("local",operation,app,"files",peer.DeviceId,"org.example.secure-service","other");throw new Exception("Conflicting open accepted");}catch(RegistrationException e){check(e.Code=="OPERATION_CONFLICT","secure operation UUID cannot change service scope");}
                try{provider.Status("other",operation);throw new Exception("Other caller read operation");}catch(RegistrationException e){check(e.Code=="OPERATION_UNKNOWN","secure connection outcomes remain caller scoped");}
                check(provider.Close("local",operation).GetProperty("state").GetString()=="closed","closing an already failed connection remains idempotent");
            }
            Guid interrupted=Guid.NewGuid(),interruptedReference=Guid.NewGuid();
            string digest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{method="secure.connection.open",application=app,alias="files",peer=peer.DeviceId,publisher="org.example.secure-service",service="files"})));
            store.BeginSecureOpen("local",interrupted,digest,interruptedReference,Guid.NewGuid());
            Guid relayOperation=Guid.NewGuid(),relayReference=Guid.NewGuid();store.BeginSecureOpen("local",relayOperation,"owned-relay-test",relayReference,Guid.NewGuid(),"relay");
            await using(var restarted=new SecureConnections(store,identity))
            {
                var recovered=restarted.Open("local",interrupted,app,"files",peer.DeviceId,"org.example.secure-service","files");
                check(recovered.GetProperty("connectionId").GetGuid()==interruptedReference&&recovered.GetProperty("state").GetString()=="closed"&&recovered.GetProperty("error").GetString()=="AGENT_RESTARTED","restart closes an interrupted journaled open without recreating its business stream");
                check(restarted.Status("local",operation).GetProperty("connectionId").GetGuid()==reference,"closed connection lookup survives Agent restart");
                var relay=restarted.Status("local",relayOperation);check(relay.GetProperty("path").GetString()=="relay"&&relay.GetProperty("state").GetString()=="closed"&&relay.GetProperty("error").GetString()=="AGENT_RESTARTED","retained relay outcome is not relabeled as direct after restart without relay routes");
            }
        }
        finally{stop.Cancel();listener.Stop();await accepting;}
    }
}
