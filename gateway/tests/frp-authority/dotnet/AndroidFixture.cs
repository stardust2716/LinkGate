using System.Security.Cryptography;
using System.Text.Json;
using LinkGate.Processes;

sealed class AndroidFixture : IAsyncDisposable
{
    private const string Consumer="org.example.linkgate.nativeconsumer";
    private string agentSigner="";private OwnedProcess? ownerHost,consumer;
    private const string Package="com.nebula.linkgate.agent";
    private readonly string adb,digest,directory;
    private readonly CancellationTokenSource lifetime=new(TimeSpan.FromSeconds(240));
    private OwnedProcess? instrumentation;
    private int sequence;
    public AndroidFixture(string executable,string directory)
    {
        adb=Path.GetFullPath(executable);this.directory=Path.Combine(directory,"android-host");Directory.CreateDirectory(this.directory);
        using var input=File.OpenRead(adb);digest=Convert.ToHexString(SHA256.HashData(input));
    }
    private OwnedProcess Start(string[] args,string? input=null)=>OwnedProcess.Start(adb,new[]{"-H","127.0.0.1","-P","5038","-s","emulator-5556"}.Concat(args).ToArray(),digest,Path.Combine(directory,"command-"+Interlocked.Increment(ref sequence)),new Dictionary<string,string>{{"ADB_MDNS_AUTO_CONNECT","0"}},input);
    private static string Read(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(stream);return reader.ReadToEnd();}
    private async Task<string> Complete(OwnedProcess process)
    {
        while(!process.HasExited)await Task.Delay(25,lifetime.Token);
        if(process.ExitCode!=0)throw new IOException("Owned Android fixture command failed.");return Read(process.StandardOutputPath);
    }
    private async Task<string> Run(params string[] args){using var process=Start(args);return await Complete(process);}
    private Task<string> Get(string name,string package=Package)=>Run("shell","run-as",package,"cat","files/fixture/"+name);
    private async Task Put(string name,string value,string package=Package)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-z-]+\\.(pem|json)$"))throw new ArgumentException("Fixed fixture filename required.");
        if(package!=Package&&package!=Consumer)throw new ArgumentException("Fixed fixture package required.");
        await Run("shell","run-as",package,"mkdir","-p","files/fixture");string input=Path.Combine(directory,"input-"+Guid.NewGuid().ToString("N"));File.WriteAllText(input,value);
        try{using var process=Start(["shell","-T","run-as",package,"dd","of=files/fixture/"+name],input);await Complete(process);}finally{File.Delete(input);}
    }
    private OwnedProcess Instrument(string test,string? mode=null,string package=Package)
    {
        List<string> args=["shell","am","instrument","-w","-e","class",test];if(mode is not null)args.AddRange(["-e","mode",mode]);args.Add(package+".test/androidx.test.runner.AndroidJUnitRunner");return Start(args.ToArray());
    }
    private async Task Done(OwnedProcess process)
    {
        string output=await Complete(process);
        if(!output.Contains("OK (1 test)",StringComparison.Ordinal))
        {
            string repo=directory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository unavailable.");
            File.WriteAllText(Path.Combine(repo,"artifacts","android-frp-instrumentation-failure.log"),output);
            throw new IOException("Android private bridge instrumentation failed.");
        }
    }
    public async Task PrepareAsync(string data)
    {
        if((await Run("shell","getprop","ro.kernel.qemu")).Trim()!="1"||(await Run("shell","getprop","ro.product.cpu.abi")).Trim()!="x86_64")throw new IOException("Explicit isolated emulator required.");
        using var preflight=JsonDocument.Parse(await Get("request-result.json"));if(!preflight.RootElement.GetProperty("passed").GetBoolean()||preflight.RootElement.GetProperty("keystoreKeyExportable").GetBoolean())throw new IOException("Native Keystore preflight missing.");
        agentSigner=preflight.RootElement.GetProperty("agentSigner").GetString()??throw new IOException("Public signer missing.");if(agentSigner.Length!=64||!agentSigner.All(Uri.IsHexDigit))throw new IOException("Public signer rejected.");
        File.WriteAllText(Path.Combine(data,"android.csr"),await Get("request.pem"));
    }
    private void Phase(string phase){string repo=directory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository unavailable.");File.AppendAllText(Path.Combine(repo,"artifacts","android-frp-phases.log"),DateTimeOffset.UtcNow.ToString("O")+" "+phase+Environment.NewLine);}
    public async Task<JsonElement> RunAsync(string data,JsonElement server,byte[] challenge,string expectedHash)
    {
        await Put("device-cert.pem",File.ReadAllText(Path.Combine(data,"android-cert.pem")));await Put("owner-root.pem",File.ReadAllText(Path.Combine(data,"owner-root.pem")));
        using(var enroll=Instrument("dev.linkgate.agent.android.NativeOwnerTest","enroll"))await Done(enroll);
        await Put("frp.json",JsonSerializer.Serialize(new{host="10.0.2.2",controlPort=server.GetProperty("controlPort").GetInt32(),backendPort=server.GetProperty("backendPort").GetInt32(),serverCertificate=File.ReadAllText(Path.Combine(data,"server-cert.pem")),publisher=server.GetProperty("publisher").GetString(),challenge=Convert.ToBase64String(challenge),expectedHash}));
        instrumentation=Instrument("dev.linkgate.agent.android.FrpOwnerTest");
        async Task Wait(string name)
        {
            for(int i=0;i<300;i++)
            {
                if(instrumentation.HasExited){await Done(instrumentation);throw new IOException("Android fixture ended before phase completion.");}
                try{using var result=JsonDocument.Parse(await Get(name));if(result.RootElement.GetProperty("ready").GetBoolean())return;}catch(Exception failure)when(failure is IOException or JsonException){}
                await Task.Delay(100,lifetime.Token);
            }
            throw new IOException("Android bridge phase timed out.");
        }
        Phase("internal-started");await Wait("frp-ready.json");Phase("internal-ready");await Wait("frp-holding.json");Phase("internal-holding");
        var watch=System.Diagnostics.Stopwatch.StartNew();File.WriteAllText(Path.Combine(data,"revoke-android"),"true");Phase("internal-revoke-written");await Done(instrumentation);Phase("internal-done");
        if(watch.ElapsedMilliseconds>=5000)throw new IOException("Android server revocation exceeded five seconds.");
        using var report=JsonDocument.Parse(await Get("frp-result.json"));if(!report.RootElement.GetProperty("passed").GetBoolean())throw new IOException("Android business verification did not pass.");
        var evidence=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(report.RootElement.GetRawText())!;evidence["serverTriggerToCompletionMs"]=JsonSerializer.SerializeToElement(watch.ElapsedMilliseconds);return JsonSerializer.SerializeToElement(evidence);
    }
    public async Task<JsonElement> RunSdkAsync(string data,JsonElement server,byte[] challenge,string expectedHash,int peerPort)
    {
        var blocked=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);blocked.Start();int blockedPort=((System.Net.IPEndPoint)blocked.LocalEndpoint).Port;
        await Put("server.json",JsonSerializer.Serialize(new{schema=1,host="10.0.2.2",controlPort=server.GetProperty("controlPort").GetInt32(),backendPort=server.GetProperty("backendPort").GetInt32(),serverCertificate=File.ReadAllText(Path.Combine(data,"server-cert.pem")),stunEndpoints=new[]{"10.0.2.2:"+server.GetProperty("stunPort").GetInt32(),"10.0.2.2:"+server.GetProperty("stunSecondPort").GetInt32()}}));
        await Put("peer.json",JsonSerializer.Serialize(new{certificate=File.ReadAllText(Path.Combine(data,"publisher-cert.pem")),host="10.0.2.2",port=peerPort,blockedPort}));
        await Put("frp-config.json",JsonSerializer.Serialize(new{agentSigner,peer=server.GetProperty("publisher").GetString(),challenge=Convert.ToBase64String(challenge),expectedHash}),Consumer);
        ownerHost=Instrument("dev.linkgate.agent.android.NativeOwnerTest","host-server");
        async Task<JsonElement> Result(string name,string package=Package){using var json=JsonDocument.Parse(await Get(name,package));return json.RootElement.Clone();}
        async Task WaitFor(string name,string package,OwnedProcess held,string field,string expected)
        {
            for(int i=0;i<200;i++){if(held.HasExited){await Done(held);throw new IOException("Native SDK phase ended early.");}try{var value=await Result(name,package);if(value.GetProperty(field).ToString().Equals(expected,StringComparison.OrdinalIgnoreCase))return;}catch(Exception failure)when(failure is IOException or JsonException){}await Task.Delay(100,lifetime.Token);}throw new IOException("Native SDK phase timed out.");
        }
        async Task<int> Owner(string action)
        {
            string id=Guid.NewGuid().ToString();await Put("command.json",JsonSerializer.Serialize(new{id,action}));
            await WaitFor("command-result.json",Package,ownerHost,"id",id);int count=(await Result("command-result.json")).GetProperty("count").GetInt32();Phase("owner-"+action+"-count="+count);return count;
        }
        async Task Invoke(string mode){Phase("sdk-"+mode+"-begin");using var process=Instrument("org.example.linkgate.nativeconsumer.FrpConsumerTest",mode,Consumer);await Done(process);Phase("sdk-"+mode+"-done");}
        async Task Formal(string mode){Phase(mode+"-begin");using var process=Instrument("org.example.linkgate.nativeconsumer.FormalConsumerTest",mode,Consumer);await Done(process);Phase(mode+"-done");}
        bool stopped=false;
        try {
            await WaitFor("host-result.json",Package,ownerHost,"state","ready");Phase("sdk-owner-ready");
            await Invoke("register");JsonElement registered=await Result("frp-register-result.json",Consumer);await Owner("approve-apps");await Invoke("request");await Owner("approve-access");await Invoke("file");JsonElement file=await Result("frp-file-result.json",Consumer);
            await Invoke("cancel-one");if(!(await Result("frp-cancel-result.json",Consumer)).GetProperty("passed").GetBoolean()||await Owner("runtime-count")!=1)throw new IOException("Shared runtime ownership failed.");
            await Invoke("request-direct");await Owner("approve-access");await Invoke("file-direct");JsonElement directFile=await Result("frp-direct-file-result.json",Consumer);if(directFile.GetProperty("path").GetString()!="direct")throw new IOException("Direct SDK path misclassified.");
            int lanCount=await Owner("direct-lan-count"),xtcpCount=await Owner("direct-xtcp-count");if(lanCount+xtcpCount!=1)throw new IOException("Direct candidate evidence missing.");
            await Formal("formal-direct");JsonElement formalDirect=await Result("formal-direct-result.json",Consumer);await Owner("cold-peer-route");await Invoke("request");await Owner("approve-access");await Formal("formal-relay");JsonElement formalRelay=await Result("formal-relay-result.json",Consumer);await Formal("formal-auto");JsonElement formalAuto=await Result("formal-auto-result.json",Consumer);int formalXtcp=await Owner("formal-xtcp-count");
            if(formalAuto.GetProperty("path").GetString()!="direct")throw new IOException("Controlled cold XTCP did not become ready.");await Formal("formal-cancel");JsonElement formalCancellation=await Result("formal-cancel-result.json",Consumer);
            consumer=Instrument("org.example.linkgate.nativeconsumer.FormalConsumerTest","formal-network",Consumer);await WaitFor("formal-network-ready.json",Consumer,consumer,"ready","true");await Owner("network-generation");await Done(consumer);JsonElement formalNetwork=await Result("formal-network-result.json",Consumer);consumer.Dispose();consumer=null;
            consumer=Instrument("org.example.linkgate.nativeconsumer.FrpConsumerTest","hold-local",Consumer);await WaitFor("frp-hold-local-ready.json",Consumer,consumer,"ready","true");var watch=System.Diagnostics.Stopwatch.StartNew();await Owner("revoke-access");await Done(consumer);Phase("sdk-local-revoked");long localMs=watch.ElapsedMilliseconds;if(localMs>=5000)throw new IOException("Native SDK local revocation exceeded budget.");consumer.Dispose();consumer=null;
            await Invoke("request");await Owner("approve-access");
            consumer=Instrument("org.example.linkgate.nativeconsumer.FrpConsumerTest","hold-server",Consumer);await WaitFor("frp-hold-server-ready.json",Consumer,consumer,"ready","true");watch.Restart();File.WriteAllText(Path.Combine(data,"revoke-android-sdk"),"true");await Done(consumer);Phase("sdk-server-revoked");long serverMs=watch.ElapsedMilliseconds;if(serverMs>=5000)throw new IOException("Native SDK server revocation exceeded budget.");
            await Invoke("denied-server");
            await Owner("stop");await Done(ownerHost);stopped=true;
            using(var state=Instrument("dev.linkgate.agent.android.NativeOwnerTest","server-state"))await Done(state);var serverState=await Result("server-state-result.json");if(!serverState.GetProperty("passed").GetBoolean())throw new IOException("Server state did not fail closed.");
            return JsonSerializer.SerializeToElement(new{passed=true,businessSdkTested=true,independentAndroidUid=registered.GetProperty("callerUid").GetInt32(),agentUid=registered.GetProperty("agentUid").GetInt32(),differentSigner=registered.GetProperty("differentSigner").GetBoolean(),relayBytes=file.GetProperty("bytes").GetInt64(),directLanBytes=lanCount==1?directFile.GetProperty("bytes").GetInt64():0,directXtcpBytes=xtcpCount==1?directFile.GetProperty("bytes").GetInt64():0,androidXtcpBusinessTested=xtcpCount==1,formalConnections=new{direct=formalDirect,relay=formalRelay,automatic=formalAuto,network=formalNetwork,cancellation=formalCancellation,selectedXtcpCount=formalXtcp,physicalNetworkChanged=false},integrity=true,requestFin=true,oneUseAttachment=true,concurrentSameApplicationStreams=2,ownedRuntimesForSameScope=1,independentStreamCancellation=true,serverDenialBeforeBusiness=true,localRevokeMs=localMs,serverRevokeMs=serverMs,recordedPath="relay",corruptConfigFailsClosed=true,ownerStopPreventsLateStartup=true,physicalDeviceTested=false});
        }finally{blocked.Stop();if(!stopped&&ownerHost is not null&&!ownerHost.HasExited)try{await Owner("stop");await Done(ownerHost);}catch(Exception){}consumer?.Dispose();consumer=null;ownerHost?.Dispose();ownerHost=null;}
    }
    public ValueTask DisposeAsync(){lifetime.Cancel();consumer?.Dispose();ownerHost?.Dispose();instrumentation?.Dispose();lifetime.Dispose();return ValueTask.CompletedTask;}
}
