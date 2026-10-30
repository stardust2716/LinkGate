using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;

if(args.Length!=2){Console.Error.WriteLine("Usage: InteropHarness <java.exe> <interop-classpath>");return 2;}
string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository missing.");
string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-java-interop")),directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
using var lifetime=new CancellationTokenSource(TimeSpan.FromMinutes(2));var backend=new TcpListener(IPAddress.Loopback,0);
Process? child=null;Task<string>? childErrors=null;Task serving=Task.CompletedTask;var backendTasks=new List<Task>();int accepts=0;long backendTx=0,backendRx=0;
try
{
    SecureIdentity.MakePrivate(directory);string javaDirectory=Path.Combine(directory,"java"),windowsDirectory=Path.Combine(directory,"windows");SecureIdentity.MakePrivate(javaDirectory);
    string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    using var owner=SecureIdentity.CreateOwnerRoot();string csr=SecureIdentity.CreateDeviceRequest(windowsDirectory);
    SecureIdentity.WriteNew(Path.Combine(windowsDirectory,"device-cert.pem"),SecureIdentity.IssueDeviceCertificate(owner,csr));SecureIdentity.WriteNew(Path.Combine(windowsDirectory,"owner-root.pem"),owner.ExportCertificatePem());
    using var publisher=SecureIdentity.Load(windowsDirectory);
    child=Start("request",javaDirectory);
    JsonElement requested=await Read("request");await child.WaitForExitAsync(lifetime.Token);if(child.ExitCode!=0)throw new IOException("Java CSR generation failed.");child.Dispose();child=null;
    SecureIdentity.WriteNew(Path.Combine(javaDirectory,"device-cert.pem"),SecureIdentity.IssueDeviceCertificate(owner,File.ReadAllText(Path.Combine(javaDirectory,"device-request.pem"))));
    SecureIdentity.WriteNew(Path.Combine(javaDirectory,"owner-root.pem"),owner.ExportCertificatePem());
    string device=requested.GetProperty("deviceId").GetString()??throw new IOException("Missing device identity.");
    var store=new RegistrationStore(Path.Combine(directory,"state"));store.BindSecureIdentity(publisher);store.TrustSecurePeer(publisher,File.ReadAllText(Path.Combine(javaDirectory,"device-cert.pem")),0,Guid.NewGuid());
    backend.Start();int localPort=((IPEndPoint)backend.LocalEndpoint).Port;
    // Use the ordinary local pipe registration boundary, even though this is an owner fixture.
    using var localHost=new PipeHost(Guid.NewGuid().ToString("N"),store);Task controlServing=localHost.RunAsync(lifetime.Token);
    Guid app;
    using var currentProcess=Process.GetCurrentProcess();
    using(var client=await LinkGate.Client.VerifiedPipeClient.ConnectAsync(localHost.PipeName,currentProcess.SafeHandle,lifetime.Token))
    {
        await client.HelloAsync(lifetime.Token);
        var manifest=ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new{manifestVersion=1,applicationId="org.example.interop-publisher",displayName="Interop fixture",exports=new[]{new{serviceId="file",label="File",protocol="tcp",localTarget=new{host="127.0.0.1",port=localPort}}},imports=Array.Empty<object>()}));
        app=(await client.RegisterPendingAsync(manifest,Guid.NewGuid(),true,lifetime.Token)).GetProperty("applicationRef").GetGuid();
    }
    store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
    await using var server=new SecurePeerServer(publisher,store,IPAddress.Loopback,0);server.Start();
    serving=Serve();
    child=Start("run",javaDirectory,publisher.DeviceId,server.Port.ToString());
    JsonElement ready=await Read("ready");if(ready.GetProperty("deviceId").GetString()!=device)throw new IOException("Device identity mismatch.");
    await Command("deny");await Read("deny");if(accepts!=0)throw new IOException("Missing grant reached backend.");
    var grant=store.ApproveSecureGrant(store.ProposeSecureGrant(device,"org.example.java-transport",app,"file"),Guid.NewGuid());
    await Command("wrong-peer");await Read("wrong-peer");if(accepts!=0)throw new IOException("Wrong peer reached backend.");
    await Command("file");var file=await Read("file");
    if(file.GetProperty("bytes").GetInt64()!=67108864||!file.GetProperty("integrity").GetBoolean()||!file.GetProperty("halfClose").GetBoolean())throw new IOException("Incomplete file evidence.");
    await Command("hold");await Read("holding");var watch=Stopwatch.StartNew();store.RevokeSecureGrant(Guid.Parse(grant.Reference),grant.Revision,Guid.NewGuid());await Read("revoked");long revokeMs=watch.ElapsedMilliseconds;
    if(revokeMs>=5000)throw new IOException("Java stream revocation exceeded budget.");
    await Command("after-revoke");await Read("after-revoke");if(accepts!=2||backendTx!=67108864||backendRx!=64)throw new IOException("Unexpected backend delivery.");
    await Command("stop");await child.WaitForExitAsync(lifetime.Token);if(child.ExitCode!=0)throw new IOException("Java peer failed.");
    lifetime.Cancel();try{await controlServing;}catch(OperationCanceledException){ }
    Console.WriteLine(JsonSerializer.Serialize(new{passed=true,windowsJavaMutualTls=true,separateDeviceKeys=true,ownerSignedJavaCsr=true,ungrantedDenied=true,wrongPeerDenied=true,fileBytes=67108864,integrity=true,halfClose=true,activeRevokeMs=revokeMs,revokedOpenDenied=true,backendConnections=accepts,backendTx=backendTx.ToString(),backendRx=backendRx.ToString(),androidKeystoreExecuted=false,androidBinderExecuted=false,physicalDevices=false,cloudAccessed=false}));return 0;

    Process Start(params string[] command)
    {
        var start=new ProcessStartInfo(Path.GetFullPath(args[0])){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in new[]{"-cp",args[1],"dev.linkgate.interop.InteropClient"}.Concat(command))start.ArgumentList.Add(argument);
        if(command[0]=="run")start.Environment["LINKGATE_INTEROP_TOKEN"]=token;
        var result=Process.Start(start)??throw new IOException("Java launch failed.");childErrors=result.StandardError.ReadToEndAsync(lifetime.Token);return result;
    }
    async Task<JsonElement> Read(string expected)
    {
        string text=await child!.StandardOutput.ReadLineAsync(lifetime.Token)??throw new IOException("Java stopped before "+expected+": "+(childErrors is null?"":await childErrors));
        using var document=JsonDocument.Parse(text);if(document.RootElement.GetProperty("event").GetString()!=expected)throw new IOException("Unexpected Java event.");return document.RootElement.Clone();
    }
    Task Command(string command)=>child!.StandardInput.WriteLineAsync(command);
    async Task Serve()
    {
        try{while(!lifetime.IsCancellationRequested){var socket=await backend.AcceptTcpClientAsync(lifetime.Token);Interlocked.Increment(ref accepts);backendTasks.Add(Handle(socket));}}
        catch(Exception e) when(e is OperationCanceledException or SocketException){ }
    }
    async Task Handle(TcpClient socket)
    {
        using(socket)
        try
        {
            NetworkStream stream=socket.GetStream();byte[] received=new byte[64];await stream.ReadExactlyAsync(received,lifetime.Token);
            if(!CryptographicOperations.FixedTimeEquals(received,Encoding.ASCII.GetBytes(token)))return;Interlocked.Add(ref backendRx,received.Length);
            if(await stream.ReadAsync(new byte[1],lifetime.Token)!=0)throw new IOException("Expected request FIN.");
            byte[] block=Enumerable.Range(0,65536).Select(x=>(byte)(x%251)).ToArray();
            for(int i=0;i<1024;i++){await stream.WriteAsync(block,lifetime.Token);Interlocked.Add(ref backendTx,block.Length);}socket.Client.Shutdown(SocketShutdown.Send);
        }
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException){ }
    }
}
catch(Exception e){Console.Error.WriteLine("Java transport conformance failed: "+e.GetType().Name+" "+e.Message);return 1;}
finally
{
    lifetime.Cancel();backend.Stop();await serving;await Task.WhenAll(backendTasks);
    if(child is not null){if(!child.HasExited){child.StandardInput.Close();try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){child.Kill(true);await child.WaitForExitAsync();}}child.Dispose();}
    string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup.");if(Directory.Exists(target))Directory.Delete(target,true);
}
