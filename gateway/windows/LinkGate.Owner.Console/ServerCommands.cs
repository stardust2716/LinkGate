using System.Text.Json;
using LinkGate.Authority;
using LinkGate.Broker;
using LinkGate.Runtime;

internal static class ServerCommands
{
    internal static async Task<int> Run(string[] args)
    {
        bool query=args[0]=="server-operation-status";string? method=args[0] switch{"server-device-set"=>"owner.device.set","server-service-set"=>"owner.service.set","server-grant-set"=>"owner.grant.set",_=>null};
        if(query?args.Length!=5:method is null||args.Length!=6){Console.Error.WriteLine("server-{device,service,grant}-set <admin-identity> <server.json> <confirmed-server-key> <intent.json> <operation-UUID> | server-operation-status <admin-identity> <server.json> <confirmed-server-key> <operation-UUID>");return 2;}
        try
        {
            using var identity=SecureIdentity.Load(args[1]);using var settings=AuthenticatedServerSettings.Parse(Read(args[2]),identity.Root,args[3]);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client=await ControlClient.ConnectAsync(settings.Address,settings.ControlPort,identity.Certificate,settings.Certificate,identity.Root,cancellation:timeout.Token);
            ServerOwnerOutcome result=query?await client.GetOwnerOperationAsync(Guid.ParseExact(args[4],"D"),timeout.Token):await client.SetOwnerAuthorityAsync(method!,Read(args[4]),Guid.ParseExact(args[5],"D"),timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(result));return 0;
        }
        catch(OwnerOutcomeUnknownException error){Console.Error.WriteLine(JsonSerializer.Serialize(new{code="OUTCOME_UNKNOWN",operationId=error.OperationId}));return 3;}
        catch(ControlRefusedException error){Console.Error.WriteLine(JsonSerializer.Serialize(new{code=error.Code}));return 1;}
        catch(Exception){Console.Error.WriteLine("Server owner command failed; keep the original operation UUID.");return 1;}
    }
    private static byte[] Read(string path)
    {path=Path.GetFullPath(path);for(var item=new FileInfo(path).Directory;item is not null;item=item.Parent)if(item.Exists&&(item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException();var file=new FileInfo(path);if(!file.Exists||file.Length is <1 or >65536||(file.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException();return File.ReadAllBytes(path);}
}
