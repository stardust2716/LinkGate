using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Client;
using LinkGate.Contracts;
using LinkGate.Control;
using LinkGate.Runtime;

internal static class FormalWindowsFixture
{
    internal static async Task<JsonElement> Run(RegistrationStore store,SecureConnections connections,LocalAgent agent,Guid application,string peer,EchoFixture backend,FrpRoutes? dynamicRoutes=null)
    {
        int checks=0,before=backend.Accepted;void Phase(string value)=>Console.WriteLine("formal-windows-stage="+value);Phase("authorize");void Check(bool good){if(!good)throw new IOException("Windows public SDK verification failed.");checks++;}
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));var token=timeout.Token;
        var access=new Dictionary<string,SecureAuthorityResult>();
        foreach(string alias in new[]{"stream","stream-direct"})
        {await agent.Rebind();var request=new PendingGrantRequest(application,alias,peer,"org.example.publisher","stream");var pending=await agent.Client.RequestGrantPendingAsync(request,Guid.NewGuid(),true,token);access[alias]=store.ApproveLocalAccess(store.ProposeLocalAccess(pending.GetProperty("grantRequestId").GetGuid()),Guid.NewGuid());}
        ConnectionOpenRequest Request(string mode)=>ConnectionOpenRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(new{applicationRef=application,importAlias=mode=="direct"?"stream-direct":"stream",deviceId=peer,publisherAppId="org.example.publisher",remoteServiceId="stream",connectionPolicy=mode=="direct"?(object)new{mode="direct-only",allowRelay=false,connectTimeoutMs=15000}:mode=="relay"?new{mode="fast-connect",allowRelay=true,connectTimeoutMs=5000,directWaitMs=0}:new{mode="direct-first",allowRelay=true,connectTimeoutMs=5000,directWaitMs=4000}}));
        async Task<(Guid Reference,Guid Operation)> Open(string mode)
        {
            await agent.Rebind();Guid operation=Guid.NewGuid();var input=Request(mode);var result=await agent.Client.OpenConnectionAsync(input,operation,true,token);var watch=Stopwatch.StartNew();int polls=0;
            while(result.Status=="pending"&&watch.Elapsed<TimeSpan.FromSeconds(18)){await Task.Delay(40,token);if(++polls%10==0)await agent.Rebind();result=await agent.Client.GetConnectionOperationAsync(operation,token);}
            Check(result.Status=="succeeded"&&result.ConnectionId is not null);Guid reference=result.ConnectionId!.Value;var state=await agent.Client.GetConnectionStatusAsync(reference,token);Check(state.FlowState=="attaching"&&state.BusinessPath=="none"&&state.DirectRx==0&&state.RelayRx==0);
            Check((await agent.Client.OpenConnectionAsync(input,operation,true,token)).ConnectionId==reference);return(reference,operation);
        }
        async Task<BusinessStream> Attach(Guid reference,bool verify=false)
        {
            await agent.Rebind();Guid operation=Guid.NewGuid();var claim=await agent.Client.AttachConnectionAsync(reference,operation,true,token);Check(claim.Status=="succeeded"&&claim.StreamRef is not null);var repeated=await agent.Client.AttachConnectionAsync(reference,operation,true,token);Check(claim.StreamRef==repeated.StreamRef);
            var stream=await agent.Client.AttachConnectionStreamAsync(claim.StreamRef!.Value,operation,true,token);
            if(verify){bool refused=false;try{await using var duplicate=await agent.Client.AttachConnectionStreamAsync(claim.StreamRef.Value,operation,true,token);}catch(Exception failure)when(failure is IOException or OperationCanceledException){refused=true;}Check(refused);}return stream;
        }
        var transfers=new List<object>();using var expected=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] block=Enumerable.Range(0,65536).Select(x=>(byte)(x%251)).ToArray();for(int i=0;i<1024;i++)expected.AppendData(block);byte[] expectedHash=expected.GetHashAndReset();
        foreach(string mode in new[]{"direct","relay","automatic"})
        {
            Phase("file-"+mode);
            var opened=await Open(mode);await using var stream=await Attach(opened.Reference,mode=="direct");await stream.WriteAsync(new byte[]{1},token);await stream.WriteAsync(backend.Challenge,token);await stream.CompleteWritesAsync(token);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[32768];long bytes=0;int n;while((n=await stream.ReadAsync(buffer,token))!=0){bytes+=n;hash.AppendData(buffer,0,n);}await stream.DrainWriteCompletionAsync(token);Check(bytes==67108864&&CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),expectedHash));
            await agent.Rebind();var status=await agent.Client.GetConnectionStatusAsync(opened.Reference,token);int polls=0;while(status.ClosedUtc is null&&polls++<50){await Task.Delay(20,token);if(polls%10==0)await agent.Rebind();status=await agent.Client.GetConnectionStatusAsync(opened.Reference,token);}
            string path=mode=="relay"?"relay":"direct";Check(status.Complete&&status.SelectedPath==path&&status.BusinessPath==path&&(path=="relay"?status.RelayTx:status.DirectTx)==33&&(path=="relay"?status.RelayRx:status.DirectRx)==67108864);Check((await agent.Client.GetConnectionOperationAsync(opened.Operation,token)).Status=="succeeded");
            if(mode=="direct")
            {
                using var historyHost=new PipeHost(Guid.NewGuid().ToString("N"),store);using var historyStop=new CancellationTokenSource(TimeSpan.FromSeconds(5));var serving=historyHost.RunAsync(historyStop.Token);
                try{using var process=Process.GetCurrentProcess();using var history=await VerifiedPipeClient.ConnectAsync(historyHost.PipeName,process.SafeHandle,historyStop.Token);await history.HelloAsync(historyStop.Token);var retained=await history.GetConnectionOperationAsync(opened.Operation,historyStop.Token);Check(retained.Status=="succeeded"&&retained.ConnectionId==opened.Reference);}
                finally{historyStop.Cancel();try{await serving;}catch(OperationCanceledException){}}
            }
            transfers.Add(new{mode,path,bytes,exactTx="33",exactRx="67108864",complete=status.Complete});await agent.Client.CloseConnectionAsync(opened.Reference,Guid.NewGuid(),true,token);
        }
        async Task<(Guid Reference,BusinessStream Stream)> Hold(string mode)
        {var opened=await Open(mode);var stream=await Attach(opened.Reference);await stream.WriteAsync(new byte[]{2},token);await stream.WriteAsync(backend.Challenge,token);await stream.ReadExactlyAsync(new byte[1],token);return(opened.Reference,stream);}
        async Task Ended(BusinessStream stream){try{Check(await stream.ReadAsync(new byte[1],token).AsTask().WaitAsync(TimeSpan.FromSeconds(4))==0);}catch(IOException){Check(true);}}
        Phase("cancel");var one=await Hold("direct");var two=await Hold("direct");await using(var first=one.Stream)await using(var second=two.Stream)
        {
            if(dynamicRoutes is not null)Check(dynamicRoutes.RuntimeCount==2&&dynamicRoutes.LeaseCount==2);await agent.Rebind();await agent.Client.CloseConnectionAsync(one.Reference,Guid.NewGuid(),true,token);await Ended(first);var held=second.ReadAsync(new byte[1],token).AsTask();Check(await Task.WhenAny(held,Task.Delay(500,token))!=held);Check((await agent.Client.GetConnectionStatusAsync(two.Reference,token)).FlowState=="open");await agent.Client.CloseConnectionAsync(two.Reference,Guid.NewGuid(),true,token);try{await held.WaitAsync(TimeSpan.FromSeconds(4));}catch(IOException){}
        }
        Phase("network");one=await Hold("relay");two=await Hold("relay");await using(var first=one.Stream)await using(var second=two.Stream)
        {
            await agent.Rebind();ulong generation=(await agent.Client.GetConnectionStatusAsync(one.Reference,token)).NetworkGeneration;connections.NotifyNetworkChanged();await Ended(first);await Ended(second);Check((await agent.Client.GetConnectionStatusAsync(one.Reference,token)).ErrorCode=="NETWORK_CHANGED");var next=await Open("relay");Check((await agent.Client.GetConnectionStatusAsync(next.Reference,token)).NetworkGeneration>generation);await agent.Client.CloseConnectionAsync(next.Reference,Guid.NewGuid(),true,token);
        }
        Phase("revoke");var revoke=await Hold("relay");await using(var stream=revoke.Stream)
        {
            var grant=access["stream"];store.RevokeLocalAccess(Guid.Parse(grant.Reference),grant.Revision,Guid.NewGuid());await Ended(stream);await agent.Rebind();var status=await agent.Client.GetConnectionStatusAsync(revoke.Reference,token);Check(status.ErrorCode=="AUTH_REQUIRED"&&!status.Complete);
        }
        if(dynamicRoutes is not null)
        {
            var wait=Stopwatch.StartNew();while(dynamicRoutes.RuntimeCount!=0&&wait.Elapsed<TimeSpan.FromSeconds(4))await Task.Delay(25,token);Check(dynamicRoutes.RuntimeCount==0);
        }
        Check(backend.Accepted-before==9);return JsonSerializer.SerializeToElement(new{passed=true,checks,businessSockets=9,transfers,oneUseAttachment=true,retainedOpen=true,independentCancellation=true,localVisitorRevoke=true,networkGenerationInjected=true,physicalNetworkChanged=false,independentWindowsProcess=false,dynamicScopedRuntime=dynamicRoutes is not null});
    }
}
