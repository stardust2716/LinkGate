using System.Text;
using System.Text.Json;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

namespace LinkGate.Broker;

/// <summary>Negotiation and opt-in pending registration. Errors never reflect caller-supplied text.</summary>
public sealed class ControlProtocol
{
    private readonly RegistrationStore? registrations;
    private readonly bool pendingGrants;
    private readonly ISecureConnections? secure;
    public ControlProtocol(RegistrationStore? registrations = null):this(registrations,false) { }
    public ControlProtocol(RegistrationStore? registrations,bool enablePendingGrants):this(registrations,enablePendingGrants,null){ }
    public ControlProtocol(RegistrationStore? registrations,bool enablePendingGrants,ISecureConnections? secureConnections)
    { this.registrations = registrations;pendingGrants=enablePendingGrants;secure=secureConnections; }
    private readonly string bootId = Guid.NewGuid().ToString();
    private static readonly string[] Capabilities = ["publish.tcp", "visit.tcp", "access.brokered", "access.loopback",
        "protocol.http", "protocol.https", "protocol.ws", "protocol.wss", "policy.direct-first",
        "policy.direct-only", "network.ipv4", "network.lan", "network.ipv6", "identity.device-grant", "identity.revoke"];

    public byte[] Dispatch(byte[] input) => Dispatch(input, null);

    internal byte[] Dispatch(byte[] input, string? caller)
    {
        if (input.Length == 0 || input.Length > FrameCodec.MaxBytes) throw Invalid();
        try
        {
            using var document = ReadDocument(input);
            var root = document.RootElement;
            Fields(root, "kind", "apiVersion", "requestId", "method", "params", "operationId");
            if (root.GetProperty("kind").GetString() != "request" ||
                !Guid.TryParseExact(root.GetProperty("requestId").GetString(), "D", out var requestId) ||
                root.GetProperty("method").ValueKind != JsonValueKind.String ||
                root.GetProperty("method").GetString() is not { Length: > 0 and <= 64 } method ||
                root.GetProperty("params").ValueKind != JsonValueKind.Object) throw Invalid();
            if (root.TryGetProperty("operationId", out var op) &&
                !Guid.TryParseExact(op.GetString(), "D", out _)) throw Invalid();
            var version = root.GetProperty("apiVersion");
            if (!Integer(version, out int api)) throw Invalid();
            string id = root.GetProperty("requestId").GetString()!;
            if (api != 1) return Error(id, "VERSION_UNSUPPORTED");
            if(method is "connection.open" or "connection.status" or "connection.attach" or "connection.close")return FormalConnection(root,method,id,caller);
            if(method is "secure.connection.open" or "secure.relay.open" or "secure.connection.status" or "secure.connection.close")return SecureConnection(root,method,id,caller);
            if (method is "grant.request" or "grant.request.status") return PendingGrant(root,method,id,caller);
            if (method is "app.register" or "operation.status") return Registration(root, method, id, caller);
            if (method != "hello") return Error(id, "CAPABILITY_UNSUPPORTED");
            if (root.TryGetProperty("operationId", out _)) return Error(id, "INVALID_REQUEST");
            var parameters = root.GetProperty("params");
            try
            {
                Fields(parameters, "supportedApiVersions");
                var versions = parameters.GetProperty("supportedApiVersions");
                if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() is < 1 or > 8)
                    return Error(id, "INVALID_REQUEST");
                bool supported = false;
                foreach (var item in versions.EnumerateArray())
                {
                    if (!Integer(item, out int v) || v <= 0) return Error(id, "INVALID_REQUEST");
                    supported |= v == 1;
                }
                if (!supported) return Error(id, "VERSION_UNSUPPORTED");
            }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { return Error(id, "INVALID_REQUEST"); }
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "response", apiVersion = 1, requestId = id,
                result = new
                {
                    apiVersion = 1, agentVersion = "0.3.0-dev", bootId,
                    capabilities = Capabilities.Select(name => new { name, state = "unsupported", reasonCode = "NOT_IMPLEMENTED" })
                        .Concat(registrations is null ? [] : new[] { new { name = "app.register.pending", state = "experimental", reasonCode = "PENDING_ONLY" } })
                        .Concat(registrations is null || !pendingGrants ? [] : new[] { new { name = "grant.request.pending", state = "experimental", reasonCode = "PENDING_ONLY" } })
                        .Concat(secure is null?[]:new[]{new{name="secure.stream.direct",state="experimental",reasonCode="KNOWN_IPV4_ENDPOINT_ONLY"}})
                        .Concat(secure is IRelaySecureConnections{SupportsRelay:true}?new[]{new{name="secure.stream.relay",state="experimental",reasonCode="OWNED_FRP_ROUTE_ONLY"}}:[])
                        .Concat(secure is IFormalConnections{SupportsFormal:true}?new[]{new{name="connection.api",state="experimental",reasonCode=((IFormalConnections)secure).FormalCapabilityReason}}:[]),
                    limits = new { controlFrameBytes = FrameCodec.MaxBytes, controlConnections = 1, requestsPerConnection = 32, frameTimeoutMs = 2000 }
                }
            });
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw Invalid(); }
    }

    private byte[] FormalConnection(JsonElement root,string method,string id,string? caller)
    {
        if(secure is not IFormalConnections{SupportsFormal:true} formal)return Error(id,"CAPABILITY_UNSUPPORTED");if(caller is null)return Error(id,"AUTH_REQUIRED","authorization");
        try
        {
            var p=root.GetProperty("params");JsonElement result;
            if(method=="connection.open")
            {
                Guid operation=Guid.ParseExact(root.GetProperty("operationId").GetString()!,"D");var request=ConnectionOpenRequest.Parse(Encoding.UTF8.GetBytes(p.GetRawText()));result=formal.OpenConnection(caller,operation,request);
            }
            else
            {
                Fields(p,"connectionId");Guid reference=Guid.ParseExact(p.GetProperty("connectionId").GetString()!,"D");
                if(method=="connection.status"){if(root.TryGetProperty("operationId",out _))return Error(id,"INVALID_REQUEST");result=formal.GetConnectionStatus(caller,reference);}
                else{Guid operation=Guid.ParseExact(root.GetProperty("operationId").GetString()!,"D");result=method=="connection.attach"?formal.AttachConnection(caller,operation,reference):formal.CloseConnection(caller,operation,reference);}
            }
            return JsonSerializer.SerializeToUtf8Bytes(new{kind="response",apiVersion=1,requestId=id,result});
        }
        catch(RegistrationException e){return Error(id,e.Code,e.Code is "AUTH_REQUIRED" or "PERMISSION_DENIED" or "OPERATION_UNKNOWN"?"authorization":"runtime");}
        catch(Exception e)when(e is ContractException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException){return Error(id,"INVALID_REQUEST");}
    }

    private byte[] SecureConnection(JsonElement root,string method,string id,string? caller)
    {
        if(secure is null)return Error(id,"CAPABILITY_UNSUPPORTED");
        if(caller is null)return Error(id,"AUTH_REQUIRED","authorization");
        try
        {
            var p=root.GetProperty("params");JsonElement result;
            if(method is "secure.connection.open" or "secure.relay.open")
            {
                Fields(p,"applicationRef","importAlias","peer","publisher","service");
                Guid operation=Guid.ParseExact(root.GetProperty("operationId").GetString()!,"D");
                Guid application=Guid.ParseExact(p.GetProperty("applicationRef").GetString()!,"D");string alias=p.GetProperty("importAlias").GetString()!,peer=p.GetProperty("peer").GetString()!,publisher=p.GetProperty("publisher").GetString()!,service=p.GetProperty("service").GetString()!;
                if(method=="secure.relay.open")
                {if(secure is not IRelaySecureConnections{SupportsRelay:true} relay)return Error(id,"CAPABILITY_UNSUPPORTED");result=relay.OpenRelay(caller,operation,application,alias,peer,publisher,service);}
                else result=secure.Open(caller,operation,application,alias,peer,publisher,service);
            }
            else
            {
                Fields(p,"operationId");if(root.TryGetProperty("operationId",out _))return Error(id,"INVALID_REQUEST");
                Guid operation=Guid.ParseExact(p.GetProperty("operationId").GetString()!,"D");
                result=method=="secure.connection.close"?secure.Close(caller,operation):secure.Status(caller,operation);
            }
            return JsonSerializer.SerializeToUtf8Bytes(new{kind="response",apiVersion=1,requestId=id,result});
        }
        catch(RegistrationException e){return Error(id,e.Code,"authorization");}
        catch(Exception e) when(e is InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or ContractException){return Error(id,"INVALID_REQUEST");}
    }

    private byte[] PendingGrant(JsonElement root,string method,string id,string? caller)
    {
        if(registrations is null || !pendingGrants)return Error(id,"CAPABILITY_UNSUPPORTED");
        if(caller is null)return Error(id,"AUTH_REQUIRED","authorization");
        try
        {
            var parameters=root.GetProperty("params");JsonElement result;
            if(method=="grant.request")
            {
                if(!root.TryGetProperty("operationId",out var raw) || !Guid.TryParseExact(raw.GetString(),"D",out Guid operation))return Error(id,"INVALID_REQUEST");
                result=registrations.RequestPendingGrant(caller,operation,PendingGrantRequest.Parse(parameters));
            }
            else
            {
                Fields(parameters,"grantRequestId");
                if(root.TryGetProperty("operationId",out _) || !Guid.TryParseExact(parameters.GetProperty("grantRequestId").GetString(),"D",out Guid reference))return Error(id,"INVALID_REQUEST");
                result=registrations.PendingGrantStatus(caller,reference);
            }
            return JsonSerializer.SerializeToUtf8Bytes(new{kind="response",apiVersion=1,requestId=id,result});
        }
        catch(RegistrationException e){return Error(id,e.Code,e.Code is "AUTH_REQUIRED" or "OPERATION_UNKNOWN"?"authorization":"storage");}
        catch(Exception e) when(e is ContractException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {return Error(id,"INVALID_REQUEST");}
    }

    private byte[] Registration(JsonElement root, string method, string id, string? caller)
    {
        if (registrations is null) return Error(id, "CAPABILITY_UNSUPPORTED");
        if (caller is null) return Error(id, "AUTH_REQUIRED", "authorization");
        try
        {
            var parameters = root.GetProperty("params");
            JsonElement result;
            if (method == "app.register")
            {
                Fields(parameters, "manifest");
                if (!root.TryGetProperty("operationId", out var op) || !Guid.TryParseExact(op.GetString(), "D", out var operation))
                    return Error(id, "INVALID_REQUEST");
                var manifest = ManifestContract.Parse(Encoding.UTF8.GetBytes(parameters.GetProperty("manifest").GetRawText()));
                result = registrations.Register(caller, operation, manifest);
            }
            else
            {
                Fields(parameters, "operationId");
                if (root.TryGetProperty("operationId", out _) || !Guid.TryParseExact(parameters.GetProperty("operationId").GetString(), "D", out var operation))
                    return Error(id, "INVALID_REQUEST");
                result = registrations.Status(caller, operation);
            }
            return JsonSerializer.SerializeToUtf8Bytes(new { kind = "response", apiVersion = 1, requestId = id, result });
        }
        catch (RegistrationException e) { return Error(id, e.Code, e.Code == "OPERATION_UNKNOWN" ? "authorization" : "storage"); }
        catch (InvalidDataException) { return Error(id, "INVALID_REQUEST"); }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        { return Error(id, "STORAGE_UNAVAILABLE", "storage"); }
        catch (Exception e) when (e is ContractException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return Error(id, "INVALID_REQUEST"); }
    }

    private static byte[] Error(string id, string code, string stage = "validation") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        kind = "response", apiVersion = 1, requestId = id,
        error = new { code, stage, retryable = false, message = "Control request rejected.", details = new { } }
    });

    internal static JsonDocument ReadDocument(byte[] input) => LinkGate.Control.ControlJson.ReadDocument(input);

    private static bool Integer(JsonElement e, out int value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.GetRawText().All(c => c is >= '0' and <= '9') && e.TryGetInt32(out value);
    }

    private static void Fields(JsonElement e, params string[] allowed)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var p in e.EnumerateObject()) if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid control request.");
}
