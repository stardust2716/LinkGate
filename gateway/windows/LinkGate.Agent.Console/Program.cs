using System.Net;
using System.Text.Json;
using LinkGate.Broker;

if(args.Length>0&&args[0]=="serve")return await AuthenticatedHost.Run(args);

if(args.Length!=5||!Guid.TryParseExact(args[2],"N",out _)||!IPAddress.TryParse(args[3],out var bind)||!int.TryParse(args[4],out int port)||port is <0 or >65535)
{Console.Error.WriteLine("Usage: LinkGate.Agent.Console <private-state> <private-identity> <test-suffix> <IPv4-bind> <port-or-zero>");return 2;}
using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(30));
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
try
{
    using var identity=SecureIdentity.Load(args[1]);var store=new RegistrationStore(args[0]);
    await using var connections=new SecureConnections(store,identity);
    await using var peer=new SecurePeerServer(identity,store,bind,port);peer.Start();
    using var host=new PipeHost(args[2],store,false,connections);Task serving=host.RunAsync(stop.Token);
    Console.WriteLine(JsonSerializer.Serialize(new{pipe=host.PipeName,peerPort=peer.Port,deviceId=identity.DeviceId}));
    try{await Task.WhenAny(serving,Console.In.ReadLineAsync(stop.Token).AsTask());}
    finally{stop.Cancel();}
    try{await serving;}catch(OperationCanceledException){ }
    return 0;
}
catch(Exception){Console.Error.WriteLine("Secure direct Agent stopped with an error.");return 1;}
