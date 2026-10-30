using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using LinkGate.Control;

namespace LinkGate.Client;

public sealed partial class VerifiedPipeClient
{
    public async Task<ConnectionOperation> OpenConnectionAsync(ConnectionOpenRequest request,Guid operationId,bool allowExperimental=false,CancellationToken cancellationToken=default)
    {ArgumentNullException.ThrowIfNull(request);RequireFormal(allowExperimental);using var document=JsonDocument.Parse(request.ToJson());var result=await ExchangeAsync("connection.open",document.RootElement,operationId,cancellationToken,operationId);return ConnectionOperation.Parse(Encoding.UTF8.GetBytes(result.GetRawText()),operationId,"connectionId");}
    public async Task<ConnectionOperation> AttachConnectionAsync(Guid connectionId,Guid operationId,bool allowExperimental=false,CancellationToken cancellationToken=default)
    {RequireFormal(allowExperimental);var result=await ExchangeAsync("connection.attach",new{connectionId},operationId,cancellationToken,operationId);return ConnectionOperation.Parse(Encoding.UTF8.GetBytes(result.GetRawText()),operationId,"streamRef");}
    public async Task<ConnectionOperation> CloseConnectionAsync(Guid connectionId,Guid operationId,bool allowExperimental=false,CancellationToken cancellationToken=default)
    {RequireFormal(allowExperimental);var result=await ExchangeAsync("connection.close",new{connectionId},operationId,cancellationToken,operationId);return ConnectionOperation.Parse(Encoding.UTF8.GetBytes(result.GetRawText()),operationId,"connectionId");}
    public async Task<ConnectionOperation> GetConnectionOperationAsync(Guid operationId,CancellationToken cancellationToken=default)
    {ObjectDisposedException.ThrowIf(disposed,this);if(!negotiated)throw new InvalidOperationException("Negotiate before querying operations.");var result=await ExchangeAsync("operation.status",new{operationId},null,cancellationToken,operationId,"connection.operation.status");return ConnectionOperation.Parse(Encoding.UTF8.GetBytes(result.GetRawText()),operationId);}
    public async Task<ConnectionStatus> GetConnectionStatusAsync(Guid connectionId,CancellationToken cancellationToken=default)
    {RequireFormal(true);var result=await ExchangeAsync("connection.status",new{connectionId},null,cancellationToken,connectionId);return ConnectionStatus.Parse(Encoding.UTF8.GetBytes(result.GetRawText()));}
    public async Task<BusinessStream> AttachConnectionStreamAsync(Guid streamRef,Guid attachOperation,bool allowExperimental=false,CancellationToken cancellationToken=default)
    {
        RequireFormal(allowExperimental);if(!DuplicateHandle(GetCurrentProcess(),expectedProcess,GetCurrentProcess(),out var pin,0,false,2)){pin?.Dispose();throw IdentityError();}
        NamedPipeClientStream? data=null;bool attempted=false;
        try
        {
            data=new NamedPipeClientStream(".","LinkGate.stream.v1."+streamRef.ToString("N"),PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(2));await data.ConnectAsync(timeout.Token);Verify(data,pin);timeout.Token.ThrowIfCancellationRequested();attempted=true;
            await FrameCodec.WriteAsync(data,JsonSerializer.SerializeToUtf8Bytes(new{kind="attach",streamRef}),timeout.Token);var bytes=await FrameCodec.ReadAsync(data,timeout.Token)??throw ResponseError();using var document=ControlJson.ReadDocument(bytes);Fields(document.RootElement,"kind");if(document.RootElement.GetProperty("kind").GetString()!="ready")throw ResponseError();Verify(data,pin);
            return new BusinessStream(data);
        }
        catch{data?.Dispose();if(attempted)throw new MutationOutcomeUnknownException(attachOperation);throw;}
        finally{pin.Dispose();}
    }
    private void RequireFormal(bool allowExperimental)
    {ObjectDisposedException.ThrowIf(disposed,this);if(!negotiated||!formalAvailable||!allowExperimental)throw new InvalidOperationException("Explicitly enable the negotiated public connection experiment.");}
}
