using System.Text.Json;

namespace LinkGate.Contracts;

public sealed class ConnectionOperation
{
    public Guid OperationId {get;}
    public Guid? ConnectionId {get;}
    public Guid? StreamRef {get;}
    public string Status {get;}
    public string? ErrorCode {get;}
    private readonly string encoded;
    public static ConnectionOperation Parse(ReadOnlySpan<byte> input,Guid expectedOperation,string? expectedKind=null)
    {
        try{using var document=ManifestContract.Read(input,16384);return new ConnectionOperation(document.RootElement,expectedOperation,expectedKind);}
        catch(ContractException){throw;}catch(Exception e)when(e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException){throw new ContractException();}
    }
    private ConnectionOperation(JsonElement value,Guid expectedOperation,string? expectedKind)
    {
        if(expectedKind is not (null or "connectionId" or "streamRef"))throw new ContractException();
        OperationId=ConnectionStatus.Uuid(value.GetProperty("operationId"));if(OperationId!=expectedOperation)throw new ContractException();Status=ConnectionStatus.Text(value.GetProperty("status"),16,"pending|reconciling|succeeded|failed");
        if(Status is "pending" or "reconciling")ConnectionStatus.Fields(value,"operationId","status");
        else if(Status=="failed"){ConnectionStatus.Fields(value,"operationId","status","error");ErrorCode=ConnectionStatus.Error(value.GetProperty("error"));}
        else
        {
            ConnectionStatus.Fields(value,"operationId","status","result");var result=value.GetProperty("result");
            if(result.TryGetProperty("connectionId",out var connection)&&(expectedKind is null or "connectionId")){ConnectionStatus.Fields(result,"connectionId");ConnectionId=ConnectionStatus.Uuid(connection);}
            else if(result.TryGetProperty("streamRef",out var stream)&&(expectedKind is null or "streamRef")){ConnectionStatus.Fields(result,"streamRef");StreamRef=ConnectionStatus.Uuid(stream);}
            else throw new ContractException();
        }
        encoded=value.GetRawText();
    }
    public string ToJson()=>encoded;
    public JsonElement Wire(){using var document=JsonDocument.Parse(encoded);return document.RootElement.Clone();}
}
