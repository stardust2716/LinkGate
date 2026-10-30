using System.Net;
using System.Text.Json;
using LinkGate.Authority;
using LinkGate.Broker;
using LinkGate.Runtime;

internal static class AuthenticatedHost
{
    internal static async Task<int> Run(string[] args)
    {
        if(args.Length!=9||!IPAddress.TryParse(args[7],out var bind)||bind.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||!int.TryParse(args[8],out int port)||port is <0 or >65535)
        {Console.Error.WriteLine("serve <private-state> <identity> <server.json> <confirmed-server-key> <frpc.exe> <runtime-SHA256> <IPv4-bind> <port-or-zero>");return 2;}
        using var stop=new CancellationTokenSource();ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=cancel;
        try
        {
            using var identity=SecureIdentity.Load(args[2]);var store=new RegistrationStore(args[1]);store.BindSecureIdentity(identity);
            string settingsPath=Path.GetFullPath(args[3]);for(var item=new FileInfo(settingsPath).Directory;item is not null;item=item.Parent)if(item.Exists&&(item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException();var info=new FileInfo(settingsPath);if(!info.Exists||info.Length is <1 or >65536||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException();
            using var settings=AuthenticatedServerSettings.Parse(File.ReadAllBytes(settingsPath),identity.Root,args[4]);
            using var authority=await ControlClient.ConnectAsync(settings.Address,settings.ControlPort,identity.Certificate,settings.Certificate,identity.Root,cancellation:stop.Token);await using var leases=new LeaseSupervisor(authority);
            await using var routes=new FrpRoutes(store,identity,new IPEndPoint(settings.Address,settings.BackendPort),settings.ServerKey,args[5],args[6],Path.Combine(args[1],"visitor-runtimes"),settings.StunEndpoints);
            await using var connections=new SecureConnections(store,identity,authority,scopedRoutes:routes);await using var peer=new SecurePeerServer(identity,store,bind,port,leases);peer.Start();
            await using var publishers=new PublisherRuntimes(store,identity,new IPEndPoint(settings.Address,settings.BackendPort),settings.ServerKey,args[5],args[6],Path.Combine(args[1],"publisher-runtimes"),peer.Port,settings.StunEndpoints);
            using var control=new PipeHost(testSuffix:null,registrations:store,enablePendingGrants:true,secureConnections:connections);var serving=control.RunAsync(stop.Token);
            using var process=System.Diagnostics.Process.GetCurrentProcess();string image=Environment.ProcessPath??throw new IOException();using var imageFile=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.Read);string imageSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageFile)).ToLowerInvariant();
            var descriptor=new{state="local-control-started",pipe=control.PipeName,peerPort=peer.Port,deviceId=identity.DeviceId,publicationState="not-verified",processId=Environment.ProcessId,creationUtcTicks=process.StartTime.ToUniversalTime().Ticks,imageSha256};
            string pending=Path.Combine(args[1],"agent-bootstrap.json.part");File.WriteAllText(pending,JsonSerializer.Serialize(descriptor));File.Move(pending,Path.Combine(args[1],"agent-bootstrap.json"),true);Console.WriteLine(JsonSerializer.Serialize(descriptor));
            var input=Task.Run(()=>StopCommand(stop));_=input.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);await Task.WhenAny(serving,input,Task.Delay(Timeout.Infinite,stop.Token));stop.Cancel();try{await serving;}catch(OperationCanceledException){}return 0;
        }
        catch(Exception){Console.Error.WriteLine("Authenticated Agent stopped; verify explicit configuration and retained owner operations.");return 1;}
        finally{Console.CancelKeyPress-=cancel;}
    }
    private static async Task StopCommand(CancellationTokenSource stop)
    {var token=stop.Token;while(!token.IsCancellationRequested){string? line=await Console.In.ReadLineAsync(token);if(line is null){await Task.Delay(Timeout.Infinite,token);return;}if(line is "stop" or "quit"){stop.Cancel();return;}}}
}
