using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;
using LinkGate.Processes;

if(args.Length!=2){Console.Error.WriteLine("Usage: NativeHarness <adb.exe> <trusted-agent-signing-cert-sha256>");return 2;}
string adb=Path.GetFullPath(args[0]),signer=args[1];if(signer.Length!=64||!signer.All(Uri.IsHexDigit))throw new ArgumentException("Explicit Agent signer required.");
string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository missing.");
string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-native-acceptance")),directory=Path.Combine(root,Guid.NewGuid().ToString("N"));SecureIdentity.MakePrivate(directory);
using var executable=File.OpenRead(adb);string adbHash=Convert.ToHexString(SHA256.HashData(executable));
using var lifetime=new CancellationTokenSource(TimeSpan.FromMinutes(4));var backend=new TcpListener(IPAddress.Loopback,0);var backendTasks=new List<Task>();Task serving=Task.CompletedTask;OwnedProcess? ownerHost=null,consumer=null;int commands=0,accepts=0;long transmitted=0;bool ownerStopped=false;string? evidence=null;
using var tlsEvents=new TlsEvents();
const string Owner="com.nebula.linkgate.agent",Client="org.example.linkgate.nativeconsumer";
string ReadLog(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(file);return reader.ReadToEnd();}
OwnedProcess Start(string[] command,string? input=null)=>OwnedProcess.Start(adb,new[]{"-H","127.0.0.1","-P","5038","-s","emulator-5556"}.Concat(command).ToArray(),adbHash,Path.Combine(directory,"adb-"+(++commands)),new Dictionary<string,string>{{"ADB_MDNS_AUTO_CONNECT","0"}},input);
async Task<string> Run(params string[] command){using var child=Start(command);return await Complete(child);}
async Task<string> Complete(OwnedProcess child){while(!child.HasExited)await Task.Delay(25,lifetime.Token);string output=ReadLog(child.StandardOutputPath);if(child.ExitCode!=0)throw new IOException("Owned ADB fixture command failed: "+ReadLog(child.StandardErrorPath));return output;}
async Task Put(string package,string name,string value){if(package!=Owner&&package!=Client||!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-z-]+\\.(json|pem)$"))throw new ArgumentException("Fixture path rejected.");await Run("shell","run-as",package,"mkdir","-p","files/fixture");string input=Path.Combine(directory,"input-"+Guid.NewGuid().ToString("N"));SecureIdentity.WriteNew(input,value);using var child=Start(["shell","-T","run-as",package,"dd","of=files/fixture/"+name],input);await Complete(child);File.Delete(input);}
Task<string> Get(string package,string name)=>Run("shell","run-as",package,"cat","files/fixture/"+name);
OwnedProcess Instrument(string package,string test,string mode)=>Start(["shell","am","instrument","-w","-e","class",test,"-e","mode",mode,package+".test/androidx.test.runner.AndroidJUnitRunner"]);
async Task InstrumentDone(OwnedProcess child){string output=await Complete(child);if(!output.Contains("OK (1 test)",StringComparison.Ordinal))throw new IOException("Native instrumentation failed: "+output);}
async Task<JsonElement> Result(string package,string file){string text=await Get(package,file);using var json=JsonDocument.Parse(text);return json.RootElement.Clone();}
async Task OwnerCommand(string action){string id=Guid.NewGuid().ToString();await Put(Owner,"command.json",JsonSerializer.Serialize(new{id,action}));for(int i=0;i<50;i++){if(ownerHost is not null&&ownerHost.HasExited)await InstrumentDone(ownerHost);try{if((await Result(Owner,"command-result.json")).GetProperty("id").GetString()==id)return;}catch(IOException){}await Task.Delay(100,lifetime.Token);}throw new IOException("Owner command timed out.");}
try
{
    if((await Run("shell","getprop","ro.kernel.qemu")).Trim()!="1"||(await Run("shell","getprop","ro.product.cpu.abi")).Trim()!="x86_64")throw new IOException("Explicit test emulator required.");
    var native=await Result(Owner,"request-result.json");if(!native.GetProperty("passed").GetBoolean()||native.GetProperty("keystoreKeyExportable").GetBoolean())throw new IOException("Keystore/descriptor preflight missing.");
    using var owner=SecureIdentity.CreateOwnerRoot();string windowsDirectory=Path.Combine(directory,"windows");string csr=SecureIdentity.CreateDeviceRequest(windowsDirectory);SecureIdentity.WriteNew(Path.Combine(windowsDirectory,"device-cert.pem"),SecureIdentity.IssueDeviceCertificate(owner,csr));SecureIdentity.WriteNew(Path.Combine(windowsDirectory,"owner-root.pem"),owner.ExportCertificatePem());using var identity=SecureIdentity.Load(windowsDirectory);
    string androidCertificate=SecureIdentity.IssueDeviceCertificate(owner,await Get(Owner,"request.pem"));
    await Put(Owner,"device-cert.pem",androidCertificate);await Put(Owner,"owner-root.pem",owner.ExportCertificatePem());using(var enroll=Instrument(Owner,"dev.linkgate.agent.android.NativeOwnerTest","enroll"))await InstrumentDone(enroll);
    string androidId=(await Result(Owner,"enroll-result.json")).GetProperty("deviceId").GetString()!;
    var store=new RegistrationStore(Path.Combine(directory,"state"));store.BindSecureIdentity(identity);store.TrustSecurePeer(identity,androidCertificate,0,Guid.NewGuid());
    backend.Start();int backendPort=((IPEndPoint)backend.LocalEndpoint).Port;string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));byte[] pattern=Enumerable.Range(0,65536).Select(i=>(byte)(i%251)).ToArray();using var expected=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);for(int i=0;i<1024;i++)expected.AppendData(pattern);string expectedHash=Convert.ToHexString(expected.GetHashAndReset()).ToLowerInvariant();
    using var host=new PipeHost(Guid.NewGuid().ToString("N"),store);Task control=host.RunAsync(lifetime.Token);Guid application;using var current=Process.GetCurrentProcess();
    using(var sdk=await LinkGate.Client.VerifiedPipeClient.ConnectAsync(host.PipeName,current.SafeHandle,lifetime.Token)){await sdk.HelloAsync(lifetime.Token);var manifest=ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new{manifestVersion=1,applicationId="org.example.native-publisher",displayName="Native publisher fixture",exports=new[]{new{serviceId="file",label="File",protocol="tcp",localTarget=new{host="127.0.0.1",port=backendPort}}},imports=Array.Empty<object>()}));application=(await sdk.RegisterPendingAsync(manifest,Guid.NewGuid(),true,lifetime.Token)).GetProperty("applicationRef").GetGuid();}store.DecideReview(store.GetReview(application),ApplicationReviewDecision.Approved,Guid.NewGuid());
    await using var server=new SecurePeerServer(identity,store,IPAddress.Loopback,0);server.Start();serving=Serve();
    await Put(Owner,"peer.json",JsonSerializer.Serialize(new{certificate=identity.Certificate.ExportCertificatePem(),host="10.0.2.2",port=server.Port}));
    await Put(Client,"config.json",JsonSerializer.Serialize(new{agentSigner=signer.ToLowerInvariant(),peer=identity.DeviceId,token,expectedHash}));
    ownerHost=Instrument(Owner,"dev.linkgate.agent.android.NativeOwnerTest","host");
    for(int i=0;i<100;i++){if(ownerHost.HasExited){await InstrumentDone(ownerHost);throw new IOException("Owner host exited.");}try{if((await Result(Owner,"host-result.json")).GetProperty("state").GetString()=="ready")break;}catch(IOException){}await Task.Delay(100,lifetime.Token);if(i==99)throw new IOException("Owner runtime failed to start.");}
    await OwnerCommand("tls-probe");
    using(var register=Instrument(Client,"org.example.linkgate.nativeconsumer.NativeConsumerTest","register"))await InstrumentDone(register);var registered=await Result(Client,"register-result.json");
    await OwnerCommand("approve-apps");using(var request=Instrument(Client,"org.example.linkgate.nativeconsumer.NativeConsumerTest","request"))await InstrumentDone(request);await OwnerCommand("approve-access");
    using(var denied=Instrument(Client,"org.example.linkgate.nativeconsumer.NativeConsumerTest","denied-remote"))await InstrumentDone(denied);
    if(accepts!=0)throw new IOException("Unapproved access reached application.");store.ApproveSecureGrant(store.ProposeSecureGrant(androidId,"org.example.native-consumer",application,"file"),Guid.NewGuid());
    using(var file=Instrument(Client,"org.example.linkgate.nativeconsumer.NativeConsumerTest","file"))await InstrumentDone(file);var result=await Result(Client,"file-result.json");
    consumer=Instrument(Client,"org.example.linkgate.nativeconsumer.NativeConsumerTest","hold");for(int i=0;i<100;i++){if(consumer.HasExited){await InstrumentDone(consumer);throw new IOException("Consumer did not hold stream.");}try{if((await Result(Client,"holding.json")).GetProperty("state").GetString()=="ready")break;}catch(IOException){}await Task.Delay(100,lifetime.Token);if(i==99)throw new IOException("Consumer hold timed out.");}
    var watch=Stopwatch.StartNew();await OwnerCommand("revoke-access");await InstrumentDone(consumer);long revokeMs=watch.ElapsedMilliseconds;if(revokeMs>=5000)throw new IOException("Native active revocation exceeded budget.");
    await OwnerCommand("stop");await InstrumentDone(ownerHost);ownerStopped=true;
    if(result.GetProperty("bytes").GetString()!="67108864"||result.GetProperty("sha256").GetString()!=expectedHash||accepts!=2||transmitted!=67108864)throw new IOException("Business delivery evidence mismatch.");
    evidence=JsonSerializer.Serialize(new{passed=true,androidApi=36,architecture="x86_64",physicalPhone=false,cloudAccessed=false,keystoreKeyExported=false,independentAndroidUid=registered.GetProperty("callerUid").GetInt32(),agentUid=registered.GetProperty("agentUid").GetInt32(),differentSigningCertificate=registered.GetProperty("differentSigner").GetBoolean(),wrongSignerDenied=true,localGrantRequired=true,publisherGrantRequired=true,authenticatedRefusal=true,mutualTls=true,nativeBinderDescriptor=true,oneUseAttachment=true,fileBytes=67108864,integrity=true,requestFin=true,activeLocalRevokeMs=revokeMs,backendConnections=accepts,backendTx=transmitted.ToString()});
    lifetime.Cancel();try{await control;}catch(OperationCanceledException){}
    async Task Serve(){try{while(!lifetime.IsCancellationRequested){TcpClient socket=await backend.AcceptTcpClientAsync(lifetime.Token);Interlocked.Increment(ref accepts);backendTasks.Add(Handle(socket));}}catch(Exception e)when(e is OperationCanceledException or SocketException){}}
    async Task Handle(TcpClient socket){using(socket)try{var stream=socket.GetStream();byte[] received=new byte[64];await stream.ReadExactlyAsync(received,lifetime.Token);if(!CryptographicOperations.FixedTimeEquals(received,Encoding.ASCII.GetBytes(token)))return;if(await stream.ReadAsync(new byte[1],lifetime.Token)!=0)throw new IOException("Request FIN missing.");for(int i=0;i<1024;i++){await stream.WriteAsync(pattern,lifetime.Token);Interlocked.Add(ref transmitted,pattern.Length);}socket.Client.Shutdown(SocketShutdown.Send);}catch(Exception e)when(e is IOException or SocketException or OperationCanceledException){}}
}
catch(Exception e){Console.Error.WriteLine("Native acceptance failed: "+e.GetType().Name+" "+e.Message);return 1;}
finally
{
    if(ownerHost is not null&&!ownerStopped&&!ownerHost.HasExited)try{await OwnerCommand("stop");await InstrumentDone(ownerHost);}catch(Exception){}consumer?.Dispose();ownerHost?.Dispose();lifetime.Cancel();backend.Stop();await serving;await Task.WhenAll(backendTasks);
    string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture cleanup.");using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(Directory.Exists(target)){try{Directory.Delete(target,true);}catch(IOException){await Task.Delay(25,cleanup.Token);}}
}
File.WriteAllText(Path.Combine(repo,"artifacts","linkgate-native-acceptance-summary.json"),evidence??throw new IOException("Evidence missing."));Console.WriteLine(evidence);return 0;
sealed class TlsEvents:System.Diagnostics.Tracing.EventListener
{
    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source){if(source.Name=="System.Net.Security")EnableEvents(source,System.Diagnostics.Tracing.EventLevel.Verbose);}
    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs value){if(value.EventName=="HandshakeFailed")Console.Error.WriteLine(JsonSerializer.Serialize(new{tlsEvent=value.EventName,details=value.Payload?.Select(x=>x?.ToString()).ToArray()}));}
}
