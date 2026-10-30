using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Client;
using LinkGate.Contracts;
using LinkGate.Processes;
using LinkGate.Runtime;
using LinkGate.Authority;
using LinkGate.Control;

if (args.Length is not (2 or 3)) return 2;
bool scopedOnly=args.Length==3&&args[2]=="--scoped";
string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository missing.");
string ownedRoot=Path.GetFullPath(Path.Combine(repo,"artifacts","frp-native-authority")),directory=Path.Combine(ownedRoot,Guid.NewGuid().ToString("N")),data=Path.Combine(directory,"data");
string runId=Guid.NewGuid().ToString("N");
void Evidence(string json){string saved=Path.Combine(repo,"artifacts","frp-native-authority-runs");Directory.CreateDirectory(saved);File.WriteAllText(Path.Combine(saved,runId+".json"),json);File.WriteAllText(Path.Combine(repo,"artifacts","frp-native-authority-summary.json"),json);}
Evidence(JsonSerializer.Serialize(new{passed=false,state="running",runId}));
int checks=0;long revokeMs=0,directRevokeMs=0,localStopMs=0,localGrantMs=0;string phase="prepare";
JsonElement? androidEvidence=null,androidSdkEvidence=null,windowsFormalEvidence=null;int formalSockets=0;
string Digest(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
void Check(bool value){if(!value)throw new IOException("Native runtime authority verification failed.");checks++;}
async Task Wait(string path,OwnedProcess? server=null)
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while(!File.Exists(path)||new FileInfo(path).Length==0){if(server?.HasExited==true)throw new IOException("Owned authority fixture exited.");await Task.Delay(20,timeout.Token);}
}
async Task<Guid> Register(RegistrationStore store,AppManifest manifest)
{
    using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var host=new PipeHost(Guid.NewGuid().ToString("N"),store);Task serving=host.RunAsync(lifetime.Token);
    try{using var process=Process.GetCurrentProcess();using var client=await VerifiedPipeClient.ConnectAsync(host.PipeName,process.SafeHandle,lifetime.Token);await client.HelloAsync(lifetime.Token);return (await client.RegisterPendingAsync(manifest,Guid.NewGuid(),true,lifetime.Token)).GetProperty("applicationRef").GetGuid();}
    finally{lifetime.Cancel();try{await serving;}catch(OperationCanceledException){}}
}
AppManifest Manifest(string app,int backendPort)=>ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new
{
    manifestVersion=1,applicationId=app,displayName="Owned native fixture",
    exports=app=="org.example.publisher"?new object[]{new{serviceId="stream",label="Stream",protocol="tcp",localTarget=new{host="127.0.0.1",port=backendPort}}}:Array.Empty<object>(),
    imports=app=="org.example.client"?new object[]{new{alias="stream",protocol="tcp",endpointMode="brokered",connectionPolicy=new{mode="fast-connect",directWaitMs=0,allowRelay=true,connectTimeoutMs=scopedOnly?15000:5000}},new{alias="stream-direct",protocol="tcp",endpointMode="brokered",connectionPolicy=new{mode="direct-only",allowRelay=false,connectTimeoutMs=15000}}}:Array.Empty<object>()
}));
string Namespace(string device,string app,string role)=>"lg1_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("namespace\n"+device+"\n"+app+"\n"+role))).ToLowerInvariant();
try
{
    SecureIdentity.MakePrivate(data);using var pubKey=new TestKey();using var visitorKey=new TestKey();
    await using var android=args.Length==3&&!scopedOnly?new AndroidFixture(args[2],directory):null;
    if(android is not null)await android.PrepareAsync(data);
    foreach(var pair in new[]{("publisher",pubKey.Key),("visitor",visitorKey.Key)})File.WriteAllText(Path.Combine(data,pair.Item1+".csr"),new CertificateRequest("CN=Native owned runtime",pair.Item2,HashAlgorithmName.SHA256).CreateSigningRequestPem());
    string serverExecutable=Path.GetFullPath(args[0]),clientExecutable=Path.GetFullPath(args[1]);
    using var server=OwnedProcess.Start(serverExecutable,[data],Digest(serverExecutable),Path.Combine(directory,"server"));await Wait(Path.Combine(data,"ready.json"),server);
    using var config=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data,"ready.json")));var values=config.RootElement;
    using var root=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data,"owner-root.pem")));using var pubPublic=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data,"publisher-cert.pem")));using var visitorPublic=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data,"visitor-cert.pem")));
    using var pubCertificate=pubPublic.CopyWithPrivateKey(pubKey.Key);using var visitorCertificate=visitorPublic.CopyWithPrivateKey(visitorKey.Key);
    var pubIdentity=new SecureIdentity(pubCertificate,root);var visitorIdentity=new SecureIdentity(visitorCertificate,root);
    using var serverPublic=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data,"server-cert.pem")));
    using var pubControl=await ControlClient.ConnectAsync(IPAddress.Loopback,values.GetProperty("controlPort").GetInt32(),pubCertificate,serverPublic,root);
    using var visitorControl=await ControlClient.ConnectAsync(IPAddress.Loopback,values.GetProperty("controlPort").GetInt32(),visitorCertificate,serverPublic,root);
    await using var supervision=new LeaseSupervisor(pubControl);
    await using var backend=new EchoFixture();
    var pubStore=new RegistrationStore(Path.Combine(directory,"publisher-state"));Guid pubApp=await Register(pubStore,Manifest("org.example.publisher",backend.Port));pubStore.DecideReview(pubStore.GetReview(pubApp),ApplicationReviewDecision.Approved,Guid.NewGuid());
    var visitorStore=new RegistrationStore(Path.Combine(directory,"visitor-state"));Guid visitorApp=await Register(visitorStore,Manifest("org.example.client",0));visitorStore.DecideReview(visitorStore.GetReview(visitorApp),ApplicationReviewDecision.Approved,Guid.NewGuid());
    pubStore.BindSecureIdentity(pubIdentity);pubStore.TrustSecurePeer(pubIdentity,visitorPublic.ExportCertificatePem(),0,Guid.NewGuid());
    visitorStore.BindSecureIdentity(visitorIdentity);
    await using var peer=new SecurePeerServer(pubIdentity,pubStore,IPAddress.Loopback,0,supervision,new ServiceTarget(pubIdentity.DeviceId,"org.example.publisher","stream"));peer.Start();
    string publisher=values.GetProperty("publisher").GetString()!,pubNamespace=Namespace(publisher,"org.example.publisher","publish"),visitorNamespace=Namespace(values.GetProperty("visitor").GetString()!,"org.example.client","access");
    string localName="lg1_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("service\n"+publisher+"\norg.example.publisher\nstream"))).ToLowerInvariant()+"_stcp",proxy=pubNamespace+"."+localName;
    string directName=localName[..^5]+"_xtcp",directProxy=pubNamespace+"."+directName;
    object Common(string name,object[] proxies,object[] visitors)=>new {user=name,clientID=name,serverAddr="127.0.0.1",serverPort=1,natHoleStunServer="127.0.0.1:"+values.GetProperty("stunPort").GetInt32(),loginFailExit=true,transport=new{protocol="tcp",wireProtocol="v2",tcpMux=true,heartbeatInterval=1,heartbeatTimeout=3},log=new{to="console",level="error"},proxies,visitors};
    object Publication(string name,string kind,string key)=>new{name,type=kind,secretKey=key,allowUsers=new[]{"*"},localIP="127.0.0.1",localPort=peer.Port,metadatas=new Dictionary<string,string>{{"linkgate.service","stream"}}};
    string pubProfile=Path.Combine(data,"publisher.json");File.WriteAllBytes(pubProfile,JsonSerializer.SerializeToUtf8Bytes(Common(pubNamespace,[Publication(localName,"stcp",proxy),Publication(directName,"xtcp",directProxy)],[])));
    int visitorPort;using(var reservation=new TcpListener(IPAddress.Loopback,0)){reservation.Start();visitorPort=((IPEndPoint)reservation.LocalEndpoint).Port;}
    int directPort;using(var reservation=new TcpListener(IPAddress.Loopback,0)){reservation.Start();directPort=((IPEndPoint)reservation.LocalEndpoint).Port;}
    visitorStore.TrustSecurePeer(visitorIdentity,pubPublic.ExportCertificatePem(),0,Guid.NewGuid(),"127.0.0.1",directPort);
    string visitorProfile=Path.Combine(data,"visitor.json");File.WriteAllBytes(visitorProfile,JsonSerializer.SerializeToUtf8Bytes(Common(visitorNamespace,[],[new{name="stream-relay",type="stcp",serverUser=pubNamespace,serverName=localName,secretKey=proxy,bindAddr="127.0.0.1",bindPort=visitorPort},new{name="stream-direct",type="xtcp",serverUser=pubNamespace,serverName=directName,secretKey=directProxy,bindAddr="127.0.0.1",bindPort=directPort,protocol="quic",keepTunnelOpen=false,fallbackTo=""}])));
    var endpoint=new IPEndPoint(IPAddress.Loopback,values.GetProperty("backendPort").GetInt32());string serverKey=values.GetProperty("server").GetString()!,clientDigest=Digest(clientExecutable);
    IPEndPoint[] stunEndpoints=[new(IPAddress.Loopback,values.GetProperty("stunPort").GetInt32()),new(IPAddress.Loopback,values.GetProperty("stunSecondPort").GetInt32())];
    phase="stun-options";
    foreach(var invalid in new IPEndPoint[][]{[new(IPAddress.Any,3478)],[new(IPAddress.IPv6Loopback,3478)],[new(IPAddress.Parse("198.18.0.1"),3478)],[new(IPAddress.Loopback,0)],[stunEndpoints[0],stunEndpoints[0]],[new(IPAddress.Loopback,1),new(IPAddress.Loopback,2),new(IPAddress.Loopback,3),new(IPAddress.Loopback,4),new(IPAddress.Loopback,5)]}){
        try{await using var unexpected=FrpRuntimeBridge.Start(pubStore,pubIdentity,pubApp,FrpRuntimeRole.Publish,endpoint,serverKey,clientExecutable,clientDigest,["-c",pubProfile],Path.Combine(directory,"invalid-stun"),invalid);throw new IOException("Invalid STUN list admitted.");}catch(ArgumentException){Check(true);}
    }
    if(scopedOnly)
    {
        phase="dynamic-scoped-runtime";
        await using var automatedPublishers=new PublisherRuntimes(pubStore,pubIdentity,endpoint,serverKey,clientExecutable,clientDigest,Path.Combine(directory,"automated-publishers"),peer.Port,stunEndpoints);
        await Wait(Path.Combine(data,"published.json"),server);Check(automatedPublishers.RuntimeCount==1);
        using var blockedLan=new TcpListener(IPAddress.Loopback,0);blockedLan.Start();
        visitorStore.TrustSecurePeer(visitorIdentity,pubPublic.ExportCertificatePem(),1,Guid.NewGuid(),"127.0.0.1",((IPEndPoint)blockedLan.LocalEndpoint).Port);
        pubStore.ApproveSecureGrant(pubStore.ProposeSecureGrant(visitorIdentity.DeviceId,"org.example.client",pubApp,"stream"),Guid.NewGuid());
        await using var dynamicRoutes=new FrpRoutes(visitorStore,visitorIdentity,endpoint,serverKey,clientExecutable,clientDigest,Path.Combine(directory,"dynamic-routes"),stunEndpoints);
        await using var scopedConnections=new SecureConnections(visitorStore,visitorIdentity,visitorControl,scopedRoutes:dynamicRoutes);
        await using var scopedAgent=new LocalAgent(visitorStore,scopedConnections);
        var result=await FormalWindowsFixture.Run(visitorStore,scopedConnections,scopedAgent,visitorApp,pubIdentity.DeviceId,backend,dynamicRoutes);
        pubStore.DecideReview(pubStore.GetReview(pubApp),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        var retired=Stopwatch.StartNew();while(automatedPublishers.RuntimeCount!=0&&retired.Elapsed<TimeSpan.FromSeconds(4))await Task.Delay(25);Check(automatedPublishers.RuntimeCount==0);
        string dynamicEvidence=JsonSerializer.Serialize(new{passed=true,runId,mode="scoped-runtime-only",sdk=result,actualSourceBuiltFrpc=true,actualMutualTlsBackend=true,noStaticVisitorConfiguration=true,noStaticPublisherConfiguration=true,publisherRevokeCleanup=true,physicalNatTested=false,cloudAccessed=false,installedRuntimeChanged=false});Evidence(dynamicEvidence);Console.WriteLine(dynamicEvidence);return 0;
    }
    phase="publisher-runtime";
    await using var publishing=FrpRuntimeBridge.Start(pubStore,pubIdentity,pubApp,FrpRuntimeRole.Publish,endpoint,serverKey,clientExecutable,clientDigest,["-c",pubProfile],Path.Combine(directory,"publisher-runtime"),stunEndpoints);
    await Wait(Path.Combine(data,"published.json"),server);Check(publishing.Namespace==pubNamespace&&!publishing.HasExited);
    phase="visitor-runtime";
    await using var visiting=FrpRuntimeBridge.Start(visitorStore,visitorIdentity,visitorApp,FrpRuntimeRole.Access,endpoint,serverKey,clientExecutable,clientDigest,["-c",visitorProfile],Path.Combine(directory,"visitor-runtime"),stunEndpoints);
    await Wait(Path.Combine(data,"controlled.json"),server);Check(visiting.Namespace==visitorNamespace&&!visiting.HasExited);
    phase="foreign-pipe";
    using(var unrelated=new NamedPipeClientStream(".",publishing.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous))
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(2));await unrelated.ConnectAsync(limit.Token);
        try{await unrelated.WriteAsync(new byte[]{1,2,3},limit.Token);Check(await unrelated.ReadAsync(new byte[1],limit.Token)==0);}catch(IOException){Check(true);}
    }
    await Task.Delay(200);File.WriteAllText(Path.Combine(data,"observe"),"true");await Wait(Path.Combine(data,"observation.json"),server);using(var observation=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data,"observation.json"))))Check(observation.RootElement.GetProperty("peers").GetInt32()==2);
    Check(!publishing.HasExited&&!visiting.HasExited);
    await using var connections=new SecureConnections(visitorStore,visitorIdentity,visitorControl,[new SecureRelayRoute("org.example.client","stream",pubIdentity.DeviceId,"org.example.publisher","stream",visitorPort,visiting.Cancellation)]);
    await using var agent=new LocalAgent(visitorStore,connections);
    async Task<JsonElement> OpenStatus(string service="stream",bool direct=false)
    {
        await agent.Rebind();Guid operation=Guid.NewGuid();var scope=new PendingGrantRequest(visitorApp,direct?"stream-direct":"stream",pubIdentity.DeviceId,"org.example.publisher",service);
        var status=direct?await agent.Client.OpenSecureDirectAsync(scope,operation,true):await agent.Client.OpenSecureRelayAsync(scope,operation,true);
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(18));int polls=0;while(status.GetProperty("state").GetString()=="pending"){await Task.Delay(40,limit.Token);if(++polls%20==0)await agent.Rebind();status=await agent.Client.GetSecureConnectionStatusAsync(operation,limit.Token);}
        Check(status.GetProperty("path").GetString()==(direct?"direct":"relay"));return status;
    }
    async Task<BusinessStream> Open(bool direct=false){var status=await OpenStatus(direct:direct);Check(status.GetProperty("state").GetString()=="ready");return await agent.Client.AttachSecureStreamAsync(status);}
    phase="missing-local-grant";var denied=await OpenStatus();Check(denied.GetProperty("state").GetString()=="closed"&&denied.GetProperty("error").GetString()=="AUTH_REQUIRED"&&backend.Accepted==0);
    phase="scope-policy";
    try{await OpenStatus("other");throw new IOException("Unowned scope admitted.");}catch(BrokerRequestException failure){Check(failure.Code=="AUTH_REQUIRED"&&backend.Accepted==0);}
    await agent.Rebind();try{await agent.Client.OpenSecureDirectAsync(new PendingGrantRequest(visitorApp,"stream",pubIdentity.DeviceId,"org.example.publisher","stream"),Guid.NewGuid(),true);throw new IOException("Direct method changed transport.");}catch(BrokerRequestException failure){Check(failure.Code=="AUTH_REQUIRED"||failure.Code=="CAPABILITY_UNSUPPORTED");}
    var localGrant=pubStore.ApproveSecureGrant(pubStore.ProposeSecureGrant(visitorIdentity.DeviceId,"org.example.client",pubApp,"stream"),Guid.NewGuid());
    async Task Transfer(bool direct)
    {
        await using var stream=await Open(direct);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));await stream.WriteAsync(new byte[]{1},timeout.Token);await stream.WriteAsync(backend.Challenge,timeout.Token);await stream.CompleteWritesAsync(timeout.Token);
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[32768];long count=0;while(count<67108864){int n=await stream.ReadAsync(buffer,timeout.Token);if(n==0)break;count+=n;digest.AppendData(buffer,0,n);}
        using var expected=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] block=Enumerable.Range(0,65536).Select(value=>(byte)(value%251)).ToArray();for(int i=0;i<1024;i++)expected.AppendData(block);
        Check(count==67108864&&CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(),expected.GetHashAndReset()));
        Check(await stream.ReadAsync(buffer,timeout.Token)==0);await stream.DrainWriteCompletionAsync(timeout.Token);
    }
    phase="relay-transfer";await Transfer(false);
    phase="xtcp-transfer";await Transfer(true);
    File.WriteAllText(Path.Combine(data,"observe-direct"),"true");await Wait(Path.Combine(data,"direct-observation.json"),server);using(var observations=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data,"direct-observation.json"))))Check(observations.RootElement.GetArrayLength()==2&&observations.RootElement[0].GetInt32()>=2&&observations.RootElement[1].GetInt32()>=2);
    if(android is null){phase="windows-public-sdk";windowsFormalEvidence=await FormalWindowsFixture.Run(visitorStore,connections,agent,visitorApp,pubIdentity.DeviceId,backend);formalSockets=windowsFormalEvidence.Value.GetProperty("businessSockets").GetInt32();}
    phase="local-grant-revoke";
    await using(var localHeld=await Open())
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(8));await localHeld.WriteAsync(new byte[]{2},limit.Token);await localHeld.WriteAsync(backend.Challenge,limit.Token);await localHeld.ReadExactlyAsync(new byte[1],limit.Token);
        async Task<bool> EndLocal(){try{return await localHeld.ReadAsync(new byte[1])==0;}catch(IOException){return true;}}
        var ended=EndLocal();var localWatch=Stopwatch.StartNew();pubStore.RevokeSecureGrant(Guid.Parse(localGrant.Reference),localGrant.Revision,Guid.NewGuid());Check(await ended.WaitAsync(TimeSpan.FromSeconds(5)));localGrantMs=localWatch.ElapsedMilliseconds;Check(backend.Accepted==3+formalSockets&&!publishing.HasExited&&!visiting.HasExited);
    }
    pubStore.ApproveSecureGrant(pubStore.ProposeSecureGrant(visitorIdentity.DeviceId,"org.example.client",pubApp,"stream"),Guid.NewGuid());
    phase="approval-lifecycle";
    Guid changed=await Register(pubStore,Manifest("org.example.publisher",backend.Port==65535?65534:backend.Port+1));Check(changed==pubApp);pubStore.DecideReview(pubStore.GetReview(pubApp),ApplicationReviewDecision.Denied,Guid.NewGuid());await Task.Delay(500);Check(!publishing.HasExited&&!publishing.Cancellation.IsCancellationRequested);
    phase="revoke";
    await using var holding=await Open();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));await holding.WriteAsync(new byte[]{2},deadline.Token);await holding.WriteAsync(backend.Challenge,deadline.Token);byte[] ready=new byte[1];await holding.ReadExactlyAsync(ready,deadline.Token);Check(ready[0]==1);
    await using var directHolding=await Open(true);await directHolding.WriteAsync(new byte[]{2},deadline.Token);await directHolding.WriteAsync(backend.Challenge,deadline.Token);await directHolding.ReadExactlyAsync(new byte[1],deadline.Token);
    async Task<bool> Ended(){try{return await holding.ReadAsync(new byte[1])==0;}catch(IOException){return true;}}
    async Task<bool> EndedDirect(){try{return await directHolding.ReadAsync(new byte[1])==0;}catch(IOException){return true;}}
    Task<bool> blocked=Ended(),blockedDirect=EndedDirect();var watch=Stopwatch.StartNew();File.WriteAllText(Path.Combine(data,"revoke"),"true");await Wait(Path.Combine(data,"revoked"),server);Check(await blocked.WaitAsync(TimeSpan.FromSeconds(5)));revokeMs=watch.ElapsedMilliseconds;Check(await blockedDirect.WaitAsync(TimeSpan.FromSeconds(5)));directRevokeMs=watch.ElapsedMilliseconds;Check(revokeMs<5000&&directRevokeMs<5000&&backend.Accepted==5+formalSockets);
    var refused=await OpenStatus();Check(refused.GetProperty("state").GetString()=="closed"&&refused.GetProperty("error").GetString()=="AUTH_REQUIRED");
    var refusedDirect=await OpenStatus(direct:true);Check(refusedDirect.GetProperty("state").GetString()=="closed"&&refusedDirect.GetProperty("error").GetString()=="AUTH_REQUIRED");
    Check(backend.Accepted==5+formalSockets);
    phase="local-stop";watch.Restart();visitorStore.DecideReview(visitorStore.GetReview(visitorApp),ApplicationReviewDecision.Revoked,Guid.NewGuid());while(!visiting.HasExited){if(watch.Elapsed>TimeSpan.FromSeconds(5))throw new IOException("Owned runtime local revoke did not stop.");await Task.Delay(20);}localStopMs=watch.ElapsedMilliseconds;Check(visiting.StopReason=="AUTHORITY_CHANGED");Check(!publishing.HasExited);
    if(android is not null)
    {
        phase="android-private-bridge";
        string certificate=File.ReadAllText(Path.Combine(data,"android-cert.pem"));using var androidPublic=X509Certificate2.CreateFromPem(certificate);string device=SecureIdentity.KeyId(androidPublic);
        pubStore.TrustSecurePeer(pubIdentity,certificate,0,Guid.NewGuid());pubStore.ApproveSecureGrant(pubStore.ProposeSecureGrant(device,"org.example.client",pubApp,"stream"),Guid.NewGuid());pubStore.ApproveSecureGrant(pubStore.ProposeSecureGrant(device,"org.example.native-consumer",pubApp,"stream"),Guid.NewGuid());
        using var expected=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] block=Enumerable.Range(0,65536).Select(value=>(byte)(value%251)).ToArray();for(int i=0;i<1024;i++)expected.AppendData(block);
        string expectedHash=Convert.ToHexString(expected.GetHashAndReset()).ToLowerInvariant();androidEvidence=await android.RunAsync(data,values,backend.Challenge,expectedHash);Check(backend.Accepted==7&&!publishing.HasExited);
        phase="android-public-sdk";androidSdkEvidence=await android.RunSdkAsync(data,values,backend.Challenge,expectedHash,peer.Port);Check(backend.Accepted==23&&!publishing.HasExited);
    }
}
catch(Exception failure){
    object Trace(Exception e)=>new{type=e.GetType().Name,frames=new StackTrace(e,false).GetFrames().Take(12).Select(f=>f.GetMethod()?.DeclaringType?.FullName+"."+f.GetMethod()?.Name),inner=e is AggregateException aggregate?aggregate.InnerExceptions.Select(Trace).ToArray():e.InnerException is null?Array.Empty<object>():new[]{Trace(e.InnerException)}};
    File.WriteAllText(Path.Combine(repo,"artifacts","frp-native-failure-stack.json"),JsonSerializer.Serialize(Trace(failure),new JsonSerializerOptions{WriteIndented=true}));
    Evidence(JsonSerializer.Serialize(new{passed=false,state="failed",runId,phase,failureType=failure.GetType().Name}));Console.Error.WriteLine("native-frp-stage="+phase+";failure="+failure.GetType().Name);return 1;}
finally
{
    string target=Path.GetFullPath(directory);if(!target.StartsWith(ownedRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture cleanup.");using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(Directory.Exists(target)){try{Directory.Delete(target,true);}catch(IOException){await Task.Delay(25,limit.Token);}}
}
string evidence=JsonSerializer.Serialize(new{passed=true,runId,checks,actualSourceBuiltFrpc=true,actualProtectedNamedPipes=true,actualParentPidVerified=true,unrelatedPipeClientDenied=true,clientKeysExported=false,actualMutualTlsBackend=true,actualWireV2=true,actualTcpMux=true,actualRelayBytes=67108864,actualXtcpOnlyBytes=67108864,xtcpFallbackEnabled=false,loopbackStunTested=true,integrity=true,requestHalfClose=true,actualReportedPaths=new[]{"relay","direct"},missingLocalGrantRejected=true,scopePolicyRejected=true,activeRelayRevokeMs=revokeMs,activeDirectRevokeMs=directRevokeMs,localGrantRevokeMs=localGrantMs,localRuntimeRevokeMs=localStopMs,pendingDenialPreservesRuntime=true,unrelatedRuntimePreserved=true,agentDataMtlsInRelayTested=true,businessSdkInRelayTested=true,androidNativeBridgeTested=androidEvidence is not null,androidNativeBridge=androidEvidence,androidPublicSdk=androidSdkEvidence,windowsPublicSdk=windowsFormalEvidence,physicalNatTested=false,cloudAccessed=false,installedRuntimeChanged=false});Evidence(evidence);Console.WriteLine(evidence);return 0;

sealed class TestKey:IDisposable
{
    private readonly CngKey handle=CngKey.Create(CngAlgorithm.ECDsaP256,"LinkGate.FRP.Test."+Guid.NewGuid().ToString("N"),new CngKeyCreationParameters{ExportPolicy=CngExportPolicies.None,KeyUsage=CngKeyUsages.Signing,Provider=CngProvider.MicrosoftSoftwareKeyStorageProvider});
    public ECDsaCng Key{get;}public TestKey(){Key=new ECDsaCng(handle);}public void Dispose(){try{handle.Delete();}finally{Key.Dispose();handle.Dispose();}}
}
sealed class LocalAgent:IAsyncDisposable
{
    private readonly PipeHost host;private readonly CancellationTokenSource lifetime=new();private readonly Task serving;
    public VerifiedPipeClient Client{get;private set;}=null!;
    public LocalAgent(RegistrationStore store,SecureConnections connections){host=new PipeHost(Guid.NewGuid().ToString("N"),store,true,connections);serving=host.RunAsync(lifetime.Token);}
    public async Task Rebind(){Client?.Dispose();using var process=Process.GetCurrentProcess();Client=await VerifiedPipeClient.ConnectAsync(host.PipeName,process.SafeHandle,lifetime.Token);await Client.HelloAsync(lifetime.Token);}
    public async ValueTask DisposeAsync(){lifetime.Cancel();Client?.Dispose();try{await serving;}catch(OperationCanceledException){}finally{host.Dispose();lifetime.Dispose();}}
}
sealed class EchoFixture:IAsyncDisposable
{
    private readonly TcpListener listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource lifetime=new();private readonly ConcurrentBag<Task> tasks=[];private readonly Task serving;private int accepted;
    public byte[] Challenge{get;}=RandomNumberGenerator.GetBytes(32);public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;public int Accepted=>Volatile.Read(ref accepted);
    public EchoFixture(){listener.Start();serving=Serve();}
    private async Task Serve(){try{while(!lifetime.IsCancellationRequested){var connection=await listener.AcceptTcpClientAsync(lifetime.Token);Interlocked.Increment(ref accepted);tasks.Add(Handle(connection));}}catch(Exception failure)when(failure is OperationCanceledException or SocketException){}}
    private async Task Handle(TcpClient connection)
    {
        using(connection)try{var stream=connection.GetStream();byte[] header=new byte[33];await stream.ReadExactlyAsync(header,lifetime.Token);if(!CryptographicOperations.FixedTimeEquals(header.AsSpan(1),Challenge))return;if(header[0]==1){if(await stream.ReadAsync(new byte[1],lifetime.Token)!=0)return;byte[] block=Enumerable.Range(0,65536).Select(value=>(byte)(value%251)).ToArray();for(int i=0;i<1024;i++)await stream.WriteAsync(block,lifetime.Token);connection.Client.Shutdown(SocketShutdown.Send);}else if(header[0]==2){await stream.WriteAsync(new byte[]{1},lifetime.Token);_=await stream.ReadAsync(new byte[1],lifetime.Token);}}catch(Exception failure)when(failure is IOException or SocketException or OperationCanceledException){}
    }
    public async ValueTask DisposeAsync(){lifetime.Cancel();listener.Stop();await serving;await Task.WhenAll(tasks.ToArray());lifetime.Dispose();}
}
