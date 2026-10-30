using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Client;
using LinkGate.Contracts;

internal static class FormalApiTests
{
    private sealed class Provider:ISecureConnections,IFormalConnections
    {
        internal int Opens;internal bool Malformed;internal readonly Guid Reference=Guid.NewGuid();public bool SupportsFormal=>true;
        public JsonElement OpenConnection(string caller,Guid operation,ConnectionOpenRequest request){Opens++;return Malformed?JsonSerializer.SerializeToElement(new{operationId=operation,status="pending",result=new{connectionId=Reference}}):JsonSerializer.SerializeToElement(new{operationId=operation,status="pending"});}
        public JsonElement GetConnectionStatus(string caller,Guid reference)=>new ConnectionMeter(Reference,new string('a',64),new string('b',64),"org.example.publisher","files",Guid.NewGuid(),1,DateTimeOffset.UtcNow,0,()=>0).Read().Status.Wire();
        public JsonElement AttachConnection(string caller,Guid operation,Guid reference)=>JsonSerializer.SerializeToElement(new{operationId=operation,status="succeeded",result=new{streamRef=Guid.NewGuid()}});
        public JsonElement CloseConnection(string caller,Guid operation,Guid reference)=>JsonSerializer.SerializeToElement(new{operationId=operation,status="succeeded",result=new{connectionId=reference}});
        public JsonElement Open(string caller,Guid operation,Guid application,string alias,string peer,string publisher,string service)=>throw new NotSupportedException();
        public JsonElement Status(string caller,Guid operation)=>throw new NotSupportedException();public JsonElement Close(string caller,Guid operation)=>throw new NotSupportedException();
    }
    private static ConnectionOpenRequest Request()=>ConnectionOpenRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(new{applicationRef=Guid.NewGuid(),importAlias="files",deviceId=new string('b',64),publisherAppId="org.example.publisher",remoteServiceId="files",connectionPolicy=new{mode="direct-only",allowRelay=false}}));
    public static async Task Run(Action<bool,string> check)
    {
        var provider=new Provider();var protocol=new ControlProtocol(null,false,provider);string request=Request().ToJson();Guid operation=Guid.NewGuid();
        byte[] Wire(string method,string parameters,bool mutation=true)=>Encoding.UTF8.GetBytes("{\"kind\":\"request\",\"apiVersion\":1,\"requestId\":\""+Guid.NewGuid()+"\",\"method\":\""+method+"\",\"params\":"+parameters+(mutation?",\"operationId\":\""+operation+"\"":"")+"}");
        string Error(byte[] wire){using var document=JsonDocument.Parse(protocol.Dispatch(wire,"caller"));return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;}
        check(Error(Wire("connection.open",request,false))=="INVALID_REQUEST"&&provider.Opens==0,"formal open requires retained operation UUID");
        check(Error(Wire("connection.open",request.Replace("\"connectionPolicy\":{","\"connectionPolicy\":{"+new string(' ',9000),StringComparison.Ordinal)))=="INVALID_REQUEST"&&provider.Opens==0,"original nested policy bytes are bounded before provider invocation");
        check(Error(Wire("connection.status",JsonSerializer.Serialize(new{connectionId=provider.Reference})))=="INVALID_REQUEST","read-only status rejects mutation UUID");
        check(Error(Wire("connection.attach",JsonSerializer.Serialize(new{connectionId=provider.Reference}),false))=="INVALID_REQUEST","attachment requires its own mutation UUID");
        check(Error(Wire("access.approve","{}"))=="CAPABILITY_UNSUPPORTED","formal connection mode does not dispatch owner approvals");
        string repo=AppContext.BaseDirectory;while(!Directory.Exists(System.IO.Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException();string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-formal-api-tests")),state=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            using var host=new PipeHost(Guid.NewGuid().ToString("N"),new RegistrationStore(state),false,provider);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));var running=host.RunAsync(stop.Token);
            try
            {
                using var process=Process.GetCurrentProcess();using var client=await VerifiedPipeClient.ConnectAsync(host.PipeName,process.SafeHandle,stop.Token);await client.HelloAsync(stop.Token);
                try{await client.OpenConnectionAsync(Request(),operation);throw new Exception("Implicit formal opt-in.");}catch(InvalidOperationException){check(provider.Opens==0,"public SDK requires explicit experimental opt-in before writing");}
                var pending=await client.OpenConnectionAsync(Request(),operation,true,stop.Token);check(pending.Status=="pending"&&pending.OperationId==operation,"typed SDK preserves pending operation correlation");
                var status=await client.GetConnectionStatusAsync(provider.Reference,stop.Token);check(status.ConnectionId==provider.Reference&&status.BusinessPath=="none","SDK validates immutable status without inventing delivery");
                var attach=await client.AttachConnectionAsync(provider.Reference,Guid.NewGuid(),true,stop.Token);check(attach.StreamRef is not null,"SDK validates typed stream claim");
                provider.Malformed=true;Guid unknown=Guid.NewGuid();try{await client.OpenConnectionAsync(Request(),unknown,true,stop.Token);throw new Exception("Malformed mutation reply accepted.");}catch(MutationOutcomeUnknownException e){check(e.OperationId==unknown&&provider.Opens==2,"malformed post-write formal reply retains original unknown operation without replay");}
            }
            finally{stop.Cancel();try{await running;}catch(OperationCanceledException){}}
        }
        finally{string target=Path.GetFullPath(state);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup.");if(Directory.Exists(target))Directory.Delete(target,true);}
    }
}
