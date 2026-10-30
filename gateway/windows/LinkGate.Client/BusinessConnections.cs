using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinkGate.Contracts;
using LinkGate.Control;

namespace LinkGate.Client;

public sealed partial class VerifiedPipeClient
{
    public Task<JsonElement> OpenSecureDirectAsync(PendingGrantRequest scope,Guid operationId,bool allowExperimental=false,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(scope);RequireSecure(allowExperimental);
        return ExchangeAsync("secure.connection.open",new{applicationRef=scope.ApplicationRef,importAlias=scope.ImportAlias,peer=scope.DeviceId,publisher=scope.PublisherAppId,service=scope.RemoteServiceId},operationId,cancellationToken,operationId);
    }
    public Task<JsonElement> GetSecureConnectionStatusAsync(Guid operationId,CancellationToken cancellationToken=default)
    {RequireSecure(true);return ExchangeAsync("secure.connection.status",new{operationId},null,cancellationToken,operationId);}
    public Task<JsonElement> CloseSecureConnectionAsync(Guid operationId,CancellationToken cancellationToken=default)
    {RequireSecure(true);return ExchangeAsync("secure.connection.close",new{operationId},null,cancellationToken,operationId);}
    public Task<JsonElement> OpenSecureRelayAsync(PendingGrantRequest scope,Guid operationId,bool allowExperimental=false,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(scope);RequireSecure(allowExperimental);if(!relayAvailable)throw new InvalidOperationException("The Agent did not negotiate scoped relay.");
        return ExchangeAsync("secure.relay.open",new{applicationRef=scope.ApplicationRef,importAlias=scope.ImportAlias,peer=scope.DeviceId,publisher=scope.PublisherAppId,service=scope.RemoteServiceId},operationId,cancellationToken,operationId);
    }
    public async Task<BusinessStream> AttachSecureStreamAsync(JsonElement status,CancellationToken cancellationToken=default)
    {
        RequireSecure(true);ValidateConnectionResult(status,null);
        if(status.GetProperty("state").GetString()!="ready")throw new InvalidOperationException("Connection is not ready for attachment.");
        string name=status.GetProperty("dataPipe").GetString()??throw new InvalidOperationException("Connection is no longer attachable.");
        if(!DuplicateHandle(GetCurrentProcess(),expectedProcess,GetCurrentProcess(),out var pin,0,false,2)){pin?.Dispose();throw IdentityError();}
        NamedPipeClientStream? data=null;
        try
        {
            data=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await data.ConnectAsync(timeout.Token);Verify(data,pin);
            await FrameCodec.WriteAsync(data,JsonSerializer.SerializeToUtf8Bytes(new{kind="attach"}),timeout.Token);
            var bytes=await FrameCodec.ReadAsync(data,timeout.Token)??throw ResponseError();using var document=ControlJson.ReadDocument(bytes);
            Fields(document.RootElement,"kind");if(document.RootElement.GetProperty("kind").GetString()!="ready")throw ResponseError();
            // After attachment the pipe kernel handle owns this byte stream; no reconnection occurs on Agent exit.
            return new BusinessStream(data);
        }
        catch{data?.Dispose();throw;}
        finally{pin.Dispose();}
    }
    private void RequireSecure(bool allowExperimental)
    {ObjectDisposedException.ThrowIf(disposed,this);if(!negotiated||!secureAvailable||!allowExperimental)throw new InvalidOperationException("Explicitly enable the negotiated secure direct preview.");}
    private void ValidateConnectionResult(JsonElement result,Guid? operation)
    {
        Fields(result,"connectionId","operationId","state","error","dataPipe","path");
        string path=result.GetProperty("path").GetString()??"";
        string state=result.GetProperty("state").GetString()??"";
        if(!Guid.TryParseExact(result.GetProperty("connectionId").GetString(),"D",out _)||!Guid.TryParseExact(result.GetProperty("operationId").GetString(),"D",out Guid op)||(operation is not null&&op!=operation)||(path!="direct"&&!(path=="relay"&&(relayAvailable||state=="closed"))))throw ResponseError();
        if(state is not ("pending" or "ready" or "closed"))throw ResponseError();
        var error=result.GetProperty("error");if(error.ValueKind!=JsonValueKind.Null && (state!="closed" || error.GetString() is not ("CONNECTION_CLOSED" or "DIRECT_FAILED" or "RELAY_FAILED" or "AUTH_REQUIRED" or "AGENT_RESTARTED" or "RESOURCE_EXHAUSTED")))throw ResponseError();
        var data=result.GetProperty("dataPipe");
        if(data.ValueKind!=JsonValueKind.Null && (state!="ready" || data.GetString() is not string name || !Regex.IsMatch(name,@"\ALinkGate\.data\.v1\.[0-9a-f]{32}\z",RegexOptions.CultureInvariant)))throw ResponseError();
    }
}
