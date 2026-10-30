using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LinkGate.Client;
using LinkGate.Contracts;
using LinkGate.Control;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

return await Sample.RunAsync(args);

internal static class Sample
{
    private const string Publisher="org.example.secure-publisher",Client="org.example.secure-consumer",Authority="files.example.invalid";
    private static readonly byte[] Block=Enumerable.Range(0,65536).Select(x=>(byte)(x%251)).ToArray();
    private const int Blocks=1024;
    private static Agent? agent;
    private static string journal="";
    private static Guid application;
    public static async Task<int> RunAsync(string[] args)
    {
        if(args.Length!=7 || args[0] is not ("publish" or "consume")){Console.Error.WriteLine("Usage: SecureApp <publish|consume> <Agent.exe> <Agent-sha256> <state> <identity> <application-dir> <public-application-certificate>");return 2;}
        using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(10));
        WebApplication? web=null;TcpListener? raw=null;Task rawTask=Task.CompletedTask;
        try
        {
            Directory.CreateDirectory(args[5]);journal=Path.Combine(args[5],"operations.jsonl");
            agent=await Agent.StartAsync(args[1],args[2],args[3],args[4],stop.Token);
            string secret=Environment.GetEnvironmentVariable("LINKGATE_SAMPLE_TOKEN")??throw new InvalidOperationException("Application token required.");
            if(secret.Length!=64)throw new InvalidOperationException("Invalid application token.");
            int httpsPort=0,tcpPort=0;
            if(args[0]=="publish")
            {
                using var pem=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(args[5],"application-cert.pem")),File.ReadAllText(Path.Combine(args[5],"application-key.pem")));
                using var certificate=X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.UserKeySet);
                var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options=>options.Listen(IPAddress.Loopback,0,listen=>listen.UseHttps(certificate)));
                web=builder.Build();web.UseWebSockets();
                web.Use(async(context,next)=>
                {
                    if(context.Request.Headers.Authorization!="Bearer "+secret){context.Response.StatusCode=401;return;}
                    await next(context);
                });
                web.MapGet("/file",async context=>
                {
                    context.Response.ContentLength=(long)Block.Length*Blocks;
                    for(int i=0;i<Blocks;i++)await context.Response.Body.WriteAsync(Block,context.RequestAborted);
                });
                web.Map("/ws",async context=>
                {
                    if(!context.WebSockets.IsWebSocketRequest){context.Response.StatusCode=400;return;}
                    using var socket=await context.WebSockets.AcceptWebSocketAsync();byte[] buffer=new byte[4096];
                    try
                    {
                        while(socket.State==WebSocketState.Open)
                        {
                            var received=await socket.ReceiveAsync(buffer.AsMemory(),context.RequestAborted);
                            if(received.MessageType==WebSocketMessageType.Close)break;
                            await socket.SendAsync(buffer.AsMemory(0,received.Count),received.MessageType,received.EndOfMessage,context.RequestAborted);
                        }
                    }
                    catch(Exception e) when(e is IOException or OperationCanceledException or WebSocketException){ }
                });
                await web.StartAsync(stop.Token);httpsPort=new Uri(web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
                raw=new TcpListener(IPAddress.Loopback,0);raw.Start();tcpPort=((IPEndPoint)raw.LocalEndpoint).Port;rawTask=ServeRawAsync(raw,secret,stop.Token);
                // The native TLS key can be disposed after the web host stops; retain it through this publish loop.
                application=await RegisterAsync(Manifest(httpsPort,tcpPort),stop.Token);Ready();
                while(await Console.In.ReadLineAsync(stop.Token) is { } command)if(command=="stop")break;
                stop.Cancel();await web.StopAsync(CancellationToken.None);return 0;
            }
            application=await RegisterAsync(Manifest(0,0),stop.Token);Ready();
            while(await Console.In.ReadLineAsync(stop.Token) is { } line && line!="stop")
            {
                using var command=JsonDocument.Parse(line);string action=command.RootElement.GetProperty("action").GetString()??"";
                string peer=command.RootElement.GetProperty("peer").GetString()??"";
                if(action=="deny")
                {
                    bool denied=false;try{await using var stream=await OpenAsync(peer,"files",stop.Token);}catch(IOException){denied=true;}
                    Ensure(denied,"Ungrant access was accepted");Emit(new{eventName="denied",passed=true});
                }
                else if(action=="transfer")
                {
                    using var http=Http(peer,args[6]);
                    using(var response=await http.GetAsync("https://"+Authority+"/file",stop.Token))Ensure(response.StatusCode==HttpStatusCode.Unauthorized,"Application authentication was bypassed");
                    http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",secret);
                    var watch=Stopwatch.StartNew();using var responseFile=await http.GetAsync("https://"+Authority+"/file",HttpCompletionOption.ResponseHeadersRead,stop.Token);responseFile.EnsureSuccessStatusCode();
                    using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[32768];long count=0;await using(var body=await responseFile.Content.ReadAsStreamAsync(stop.Token))
                    {int n;while((n=await body.ReadAsync(buffer,stop.Token))!=0){digest.AppendData(buffer,0,n);count+=n;}}
                    using var expected=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);for(int i=0;i<Blocks;i++)expected.AppendData(Block);
                    Ensure(count==(long)Block.Length*Blocks&&CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(),expected.GetHashAndReset()),"File integrity failed");
                    using var ws=new ClientWebSocket();ws.Options.SetRequestHeader("Authorization","Bearer "+secret);await ws.ConnectAsync(new Uri("wss://"+Authority+"/ws"),http,stop.Token);
                    byte[] sent=Encoding.UTF8.GetBytes("SDK WebSocket actual payload");await ws.SendAsync(sent,WebSocketMessageType.Binary,true,stop.Token);var got=await ws.ReceiveAsync(buffer,stop.Token);
                    Ensure(got.Count==sent.Length&&buffer.AsSpan(0,got.Count).SequenceEqual(sent),"WebSocket integrity failed");
                    await RawAsync(peer,secret,stop.Token);Emit(new{eventName="transferred",passed=true,fileBytes=count,webSocketBytes=got.Count,halfClose=true,applicationAuthentication=true,elapsedMs=watch.ElapsedMilliseconds});
                }
                else if(action=="hold")
                {
                    using var http=Http(peer,args[6]);using var ws=new ClientWebSocket();ws.Options.SetRequestHeader("Authorization","Bearer "+secret);
                    await ws.ConnectAsync(new Uri("wss://"+Authority+"/ws"),http,stop.Token);Emit(new{eventName="holding"});
                    var watch=Stopwatch.StartNew();bool ended=false;
                    try{byte[] buffer=new byte[1];_=await ws.ReceiveAsync(buffer,stop.Token);ended=true;}
                    catch(Exception e) when(e is IOException or WebSocketException){ended=true;}
                    Ensure(ended,"Revocation did not close WebSocket");Emit(new{eventName="revoked",elapsedMs=watch.ElapsedMilliseconds});
                }
                else if(action=="after-revoke")
                {
                    bool denied=false;try{await using var stream=await OpenAsync(peer,"files",stop.Token);}catch(IOException){denied=true;}
                    Ensure(denied,"Revoked grant reopened");await RawAsync(peer,secret,stop.Token);Emit(new{eventName="isolated",passed=true,revokedOpenDenied=true,unrelatedServiceWorks=true});
                }
                else throw new InvalidOperationException("Unknown sample command.");
            }
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine("Secure application verification failed: "+e.GetType().Name);return 1;}
        finally
        {
            stop.Cancel();raw?.Stop();try{await rawTask;}catch(OperationCanceledException){ }
            if(web is not null)await web.DisposeAsync();if(agent is not null)await agent.DisposeAsync();
        }
    }
    private static void Ready()=>Emit(new{eventName="ready",applicationRef=application,peerPort=agent!.Port,deviceId=agent.Device});
    private static void Emit(object value)=>Console.WriteLine(JsonSerializer.Serialize(value));
    private static void Ensure(bool condition,string message){if(!condition)throw new IOException(message);}
    private static AppManifest Manifest(int https,int tcp)
    {
        object[] exports=https==0?[]:[new{serviceId="files",label="Files",protocol="https",logicalAuthority=Authority,localTarget=new{host="127.0.0.1",port=https}},new{serviceId="raw",label="Half close",protocol="tcp",localTarget=new{host="127.0.0.1",port=tcp}}];
        object[] imports=https==0?[new{alias="files",protocol="https",logicalAuthority=Authority,endpointMode="brokered",connectionPolicy=new{mode="direct-only",allowRelay=false,connectTimeoutMs=5000}},new{alias="raw",protocol="tcp",endpointMode="brokered",connectionPolicy=new{mode="direct-only",allowRelay=false,connectTimeoutMs=5000}}]:[];
        return ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new{manifestVersion=1,applicationId=https==0?Client:Publisher,displayName="Independent secure sample",exports,imports}));
    }
    private static async Task<Guid> RegisterAsync(AppManifest manifest,CancellationToken ct)
    {
        using var client=await agent!.ClientAsync(ct);await client.HelloAsync(ct);Guid operation=SaveOperation();
        var result=await client.RegisterPendingAsync(manifest,operation,true,ct);return result.GetProperty("applicationRef").GetGuid();
    }
    private static Guid SaveOperation()
    {
        Guid op=Guid.NewGuid();using var file=new FileStream(journal,FileMode.Append,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);
        file.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{operationId=op})+Environment.NewLine));file.Flush(true);return op;
    }
    private static async Task<BusinessStream> OpenAsync(string peer,string alias,CancellationToken ct)
    {
        using var client=await agent!.ClientAsync(ct);await client.HelloAsync(ct);Guid operation=SaveOperation();
        var status=await client.OpenSecureDirectAsync(new PendingGrantRequest(application,alias,peer,Publisher,alias),operation,true,ct);
        for(int i=0;i<25;i++)
        {
            string state=status.GetProperty("state").GetString()??"";
            if(state=="ready")return await client.AttachSecureStreamAsync(status,ct);
            if(state=="closed")throw new IOException("Secure direct open rejected.");
            await Task.Delay(100,ct);status=await client.GetSecureConnectionStatusAsync(operation,ct);
        }
        throw new IOException("Secure direct open did not become ready; inspect the saved operation instead of retrying it.");
    }
    private static HttpClient Http(string peer,string certificatePath)
    {
        var certificate=X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
        var policy=new X509ChainPolicy{TrustMode=X509ChainTrustMode.CustomRootTrust,RevocationMode=X509RevocationMode.NoCheck,DisableCertificateDownloads=true};policy.CustomTrustStore.Add(certificate);policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,SslOptions=new SslClientAuthenticationOptions{CertificateChainPolicy=policy},ConnectCallback=async(_,ct)=>await OpenAsync(peer,"files",ct)};
        return new HttpClient(handler){Timeout=TimeSpan.FromSeconds(30)};
    }
    private static async Task RawAsync(string peer,string secret,CancellationToken ct)
    {
        await using var stream=await OpenAsync(peer,"raw",ct);await stream.WriteAsync(Encoding.ASCII.GetBytes(secret),ct);await stream.WriteAsync(Block,ct);await stream.CompleteWritesAsync(ct);
        byte[] digest=new byte[32];await stream.ReadExactlyAsync(digest,ct);Ensure(CryptographicOperations.FixedTimeEquals(digest,SHA256.HashData(Block)),"Half-close response lost");
        Ensure(await stream.ReadAsync(new byte[1],ct)==0,"Half-close did not reach EOF");
    }
    private static async Task ServeRawAsync(TcpListener listener,string secret,CancellationToken ct)
    {
        var tasks=new List<Task>();
        try
        {
            while(!ct.IsCancellationRequested)
            {
                var socket=await listener.AcceptTcpClientAsync(ct);
                tasks.RemoveAll(t=>t.IsCompleted);tasks.Add(Handle(socket));
            }
        }
        catch(Exception e) when(e is OperationCanceledException or SocketException){ }
        finally{await Task.WhenAll(tasks);}
        async Task Handle(TcpClient socket)
        {
            using(socket)
            try
            {
                byte[] token=new byte[64];await socket.GetStream().ReadExactlyAsync(token,ct);if(!CryptographicOperations.FixedTimeEquals(token,Encoding.ASCII.GetBytes(secret)))return;
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[16384];int n;
                while((n=await socket.GetStream().ReadAsync(buffer,ct))!=0)hash.AppendData(buffer,0,n);
                await socket.GetStream().WriteAsync(hash.GetHashAndReset(),ct);socket.Client.Shutdown(SocketShutdown.Send);
            }
            catch(Exception e) when(e is IOException or OperationCanceledException or SocketException){ }
        }
    }
    private sealed class Agent : IAsyncDisposable
    {
        private readonly Process process;private readonly FileStream executable;
        private readonly string pipe;
        public int Port{get;}public string Device{get;}
        private Agent(Process process,FileStream executable,JsonElement ready)
        {this.process=process;this.executable=executable;pipe=ready.GetProperty("pipe").GetString()!;Port=ready.GetProperty("peerPort").GetInt32();Device=ready.GetProperty("deviceId").GetString()!;}
        public static async Task<Agent> StartAsync(string path,string expected,string state,string identity,CancellationToken ct)
        {
            path=Path.GetFullPath(path);var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);Process? child=null;
            try
            {
                if(!Convert.ToHexString(SHA256.HashData(file)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Agent digest mismatch.");
                var start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.Environment.Remove("LINKGATE_SAMPLE_TOKEN");
                foreach(string value in new[]{state,identity,Guid.NewGuid().ToString("N"),"127.0.0.1","0"})start.ArgumentList.Add(value);
                child=Process.Start(start)??throw new IOException("Agent launch failed.");
                _=child.StandardError.ReadToEndAsync(ct);string line=await child.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10),ct)??throw new IOException("Agent readiness missing.");
                using var document=JsonDocument.Parse(line);return new Agent(child,file,document.RootElement);
            }
            catch{if(child is not null){if(!child.HasExited)child.Kill(true);child.Dispose();}file.Dispose();throw;}
        }
        public Task<VerifiedPipeClient> ClientAsync(CancellationToken ct)=>VerifiedPipeClient.ConnectAsync(pipe,process.SafeHandle,ct);
        public async ValueTask DisposeAsync()
        {
            if(!process.HasExited)
            {
                process.StandardInput.Close();try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){process.Kill(true);await process.WaitForExitAsync();}
            }
            process.Dispose();executable.Dispose();
        }
    }
}
