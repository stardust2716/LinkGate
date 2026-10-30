using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Client;
using LinkGate.Contracts;

if(args.Length<3||args[0] is not ("publish" or "register" or "request" or "fetch")){Console.Error.WriteLine("publish <trusted-bootstrap.json> <output-dir> | request <bootstrap> <output-dir> <peer-key> | fetch <bootstrap> <output-dir> <peer-key> <direct|relay>");return 2;}
string output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;timeout.Cancel();};var token=timeout.Token;
using var bootstrap=JsonDocument.Parse(File.ReadAllBytes(args[1]));var settings=bootstrap.RootElement;
using var agent=Process.GetProcessById(settings.GetProperty("processId").GetInt32());
string image=agent.MainModule?.FileName??throw new IOException("Agent image unavailable.");using(var file=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.Read))
{string hash=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();if(hash!=settings.GetProperty("imageSha256").GetString()||agent.StartTime.ToUniversalTime().Ticks!=settings.GetProperty("creationUtcTicks").GetInt64())throw new IOException("Trusted Agent differs.");}
string pipe=settings.GetProperty("pipe").GetString()!;VerifiedPipeClient? client=null;int calls=0;
async Task<VerifiedPipeClient> Session(){if(client is null||calls++>=12){client?.Dispose();client=await VerifiedPipeClient.ConnectAsync(pipe,agent.SafeHandle,token);await client.HelloAsync(token);calls=0;}return client;}
void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name),JsonSerializer.Serialize(value));
try
{
    if(args[0]=="publish")
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var manifest=ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new{manifestVersion=1,applicationId="org.example.trial.publisher",displayName="LinkGate sample publisher",exports=new[]{new{serviceId="sample",label="Deterministic sample",protocol="tcp",localTarget=new{host="127.0.0.1",port}}},imports=Array.Empty<object>()}));
        var registered=await(await Session()).RegisterPendingAsync(manifest,Guid.NewGuid(),true,token);Save("publisher.json",new{applicationRef=registered.GetProperty("applicationRef").GetGuid(),service="sample",deviceId=settings.GetProperty("deviceId").GetString()});
        while(!token.IsCancellationRequested){using var socket=await listener.AcceptTcpClientAsync(token);var stream=socket.GetStream();byte[] request=new byte[1];await stream.ReadExactlyAsync(request,token);if(request[0]!=1)continue;byte[] block=Enumerable.Range(0,65536).Select(x=>(byte)(x%251)).ToArray();for(int i=0;i<32;i++)await stream.WriteAsync(block,token);socket.Client.Shutdown(SocketShutdown.Send);}
    }
    else
    {
        if(args.Length<4)throw new ArgumentException();string record=Path.Combine(output,"client.json");Guid app;
        if(File.Exists(record)){using var saved=JsonDocument.Parse(File.ReadAllBytes(record));app=saved.RootElement.GetProperty("applicationRef").GetGuid();}
        else
        {var manifest=ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new{manifestVersion=1,applicationId="org.example.trial.client",displayName="LinkGate sample client",exports=Array.Empty<object>(),imports=new[]{new{alias="sample",protocol="tcp",endpointMode="brokered",connectionPolicy=new{mode="fast-connect",allowRelay=true,directWaitMs=0,connectTimeoutMs=15000}}}}));var registered=await(await Session()).RegisterPendingAsync(manifest,Guid.NewGuid(),true,token);app=registered.GetProperty("applicationRef").GetGuid();Save("client.json",new{applicationRef=app});}
        if(args[0]=="register"){Console.WriteLine("Declaration pending owner approval.");return 0;}
        if(args[0]=="request")
        {var requested=await(await Session()).RequestGrantPendingAsync(new PendingGrantRequest(app,"sample",args[3],"org.example.trial.publisher","sample"),Guid.NewGuid(),true,token);Save("access.json",new{grantRequestId=requested.GetProperty("grantRequestId").GetGuid()});}
        else
        {
            if(args.Length!=5||args[4] is not ("direct" or "relay"))throw new ArgumentException();bool direct=args[4]=="direct";var request=ConnectionOpenRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(new{applicationRef=app,importAlias="sample",deviceId=args[3],publisherAppId="org.example.trial.publisher",remoteServiceId="sample",connectionPolicy=direct?(object)new{mode="direct-only",allowRelay=false,connectTimeoutMs=15000}:new{mode="fast-connect",allowRelay=true,directWaitMs=0,connectTimeoutMs=15000}}));
            Guid operation=Guid.NewGuid();Save("open-operation.json",new{operationId=operation});var opened=await(await Session()).OpenConnectionAsync(request,operation,true,token);while(opened.Status is "pending" or "reconciling"){await Task.Delay(80,token);opened=await(await Session()).GetConnectionOperationAsync(operation,token);}if(opened.Status!="succeeded")throw new IOException("Open failed: "+opened.ErrorCode);
            Guid reference=opened.ConnectionId!.Value,attach=Guid.NewGuid();var claim=await(await Session()).AttachConnectionAsync(reference,attach,true,token);await using var stream=await(await Session()).AttachConnectionStreamAsync(claim.StreamRef!.Value,attach,true,token);await stream.WriteAsync(new byte[]{1},token);await stream.CompleteWritesAsync(token);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[32768];long bytes=0;int n;while((n=await stream.ReadAsync(buffer,token))!=0){bytes+=n;hash.AppendData(buffer,0,n);}await stream.DrainWriteCompletionAsync(token);using var expected=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] block=Enumerable.Range(0,65536).Select(x=>(byte)(x%251)).ToArray();for(int i=0;i<32;i++)expected.AppendData(block);if(bytes!=2097152||!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),expected.GetHashAndReset()))throw new IOException("Sample integrity failed.");
            client?.Dispose();client=null;calls=0;
            var status=await(await Session()).GetConnectionStatusAsync(reference,token);for(int i=0;i<25&&status.ClosedUtc is null;i++){await Task.Delay(20,token);status=await(await Session()).GetConnectionStatusAsync(reference,token);}if(!status.Complete||status.SelectedPath!=(direct?"direct":"relay"))throw new IOException("Sample completion differs.");Save("result-"+(direct?"direct":"relay")+".json",new{passed=true,bytes,path=status.SelectedPath,complete=status.Complete});await(await Session()).CloseConnectionAsync(reference,Guid.NewGuid(),true,token);
        }
    }
    return 0;
}
catch(Exception error){Console.Error.WriteLine("Sample failed: "+error.GetType().Name);return 1;}
finally{client?.Dispose();}
