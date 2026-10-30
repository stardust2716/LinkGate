using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

using LinkGate.Contracts;
using LinkGate.Control;

namespace LinkGate.Client;

/// <summary>The launcher must authenticate the process before supplying its handle. No name-based trust discovery.</summary>
public sealed partial class VerifiedPipeClient : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly SafeProcessHandle expectedProcess;
    private readonly SemaphoreSlim exchange = new(1, 1);
    private bool disposed;

    private VerifiedPipeClient(NamedPipeClientStream pipe, SafeProcessHandle expectedProcess)
    { this.pipe = pipe; this.expectedProcess = expectedProcess; }

    public static async Task<VerifiedPipeClient> ConnectAsync(string pipeName, SafeProcessHandle trustedProcess, CancellationToken cancellationToken = default)
    {
        if (pipeName is null || !Regex.IsMatch(pipeName, @"\ALinkGate\.[0-9a-f]{64}\.control\.v1(?:\.test\.[0-9a-f]{32})?\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid local control pipe.", nameof(pipeName));
        if (!DuplicateHandle(GetCurrentProcess(), trustedProcess, GetCurrentProcess(), out var pin, 0, false, 2))
        { pin?.Dispose(); throw IdentityError(); }
        NamedPipeClientStream? pipe = null;
        try
        {
            RequireLive(pin);
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token);
            Verify(pipe, pin);
            return new VerifiedPipeClient(pipe, pin);
        }
        catch { pipe?.Dispose(); pin.Dispose(); throw; }
    }

    private bool negotiated, pendingAvailable,pendingGrantsAvailable,secureAvailable,relayAvailable,formalAvailable;
    private int requestsSent, requestLimit = 32;
    private static readonly JsonSerializerOptions WireOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Task<JsonElement> HelloAsync(CancellationToken cancellationToken = default) =>
        ExchangeAsync("hello", new { supportedApiVersions = new[] { 1 } }, null, cancellationToken);

    public Task<JsonElement> RegisterPendingAsync(AppManifest manifest, Guid operationId, bool allowExperimentalPending = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!negotiated || !pendingAvailable || !allowExperimentalPending)
            throw new InvalidOperationException("Negotiate and explicitly enable pending registration first.");
        using var document = JsonDocument.Parse(manifest.ToJson());
        return ExchangeAsync("app.register", new { manifest = document.RootElement.Clone() }, operationId, cancellationToken);
    }

    public Task<JsonElement> GetOperationStatusAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!negotiated) throw new InvalidOperationException("Negotiate before querying operations.");
        return ExchangeAsync("operation.status", new { operationId = operationId.ToString() }, null, cancellationToken, operationId);
    }

    public Task<JsonElement> RequestGrantPendingAsync(PendingGrantRequest request,Guid operationId,bool allowExperimentalPending=false,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ArgumentNullException.ThrowIfNull(request);
        if(!negotiated || !pendingGrantsAvailable || !allowExperimentalPending)throw new InvalidOperationException("Negotiate and explicitly enable pending grant requests first.");
        using var document=JsonDocument.Parse(request.ToJson());
        return ExchangeAsync("grant.request",document.RootElement.Clone(),operationId,cancellationToken);
    }
    public Task<JsonElement> GetGrantRequestStatusAsync(Guid grantRequestId,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!negotiated || !pendingGrantsAvailable)throw new InvalidOperationException("Negotiate pending grant requests before querying request state.");
        return ExchangeAsync("grant.request.status",new{grantRequestId=grantRequestId.ToString()},null,cancellationToken,grantRequestId);
    }
    public Task<JsonElement> GetGrantOperationStatusAsync(Guid operationId,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!negotiated)throw new InvalidOperationException("Negotiate before querying operations.");
        return ExchangeAsync("operation.status",new{operationId=operationId.ToString()},null,cancellationToken,operationId,"grant.operation.status");
    }

    private async Task<JsonElement> ExchangeAsync(string method, object parameters, Guid? mutation, CancellationToken cancellationToken, Guid? lookup = null,string? resultContract=null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string id = Guid.NewGuid().ToString();
        var request = new Dictionary<string, object> { ["kind"] = "request", ["apiVersion"] = 1, ["requestId"] = id, ["method"] = method, ["params"] = parameters };
        if (mutation.HasValue) request["operationId"] = mutation.Value.ToString();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, WireOptions);
        if (payload.Length > FrameCodec.MaxBytes) throw new ArgumentException("Control request exceeds the frame limit.");
        if (requestsSent >= requestLimit) throw new InvalidOperationException("Control session request limit reached; reconnect explicitly.");
        if (!await exchange.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Control exchange already active.");
        bool attempted = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            Verify(pipe, expectedProcess);
            timeout.Token.ThrowIfCancellationRequested();
            requestsSent++;
            attempted = true;
            await FrameCodec.WriteAsync(pipe, payload, timeout.Token);
            var bytes = await FrameCodec.ReadAsync(pipe, timeout.Token) ?? throw ResponseError();
            using var document = ControlJson.ReadDocument(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("kind" or "apiVersion" or "requestId" or "result" or "error")) ||
                root.GetProperty("kind").GetString() != "response" || root.GetProperty("apiVersion").GetRawText() != "1" || root.GetProperty("requestId").GetString() != id ||
                root.TryGetProperty("result", out var result) == root.TryGetProperty("error", out var error)) throw ResponseError();
            if (root.TryGetProperty("error", out error))
            {
                Fields(error, "code", "stage", "retryable", "message", "details");
                string code = error.GetProperty("code").GetString()!, stage = error.GetProperty("stage").GetString()!;
                if (code is null || code.Length > 64 || !Regex.IsMatch(code, @"\A[A-Z][A-Z0-9_]+\z") ||
                    stage is not ("validation" or "authorization" or "discovery" or "direct" or "relay" or "attach" or "runtime" or "storage") ||
                    error.GetProperty("message").GetString() is not { Length: > 0 and <= 512 } || error.GetProperty("details").ValueKind != JsonValueKind.Object) throw ResponseError();
                throw new BrokerRequestException(code, stage, error.GetProperty("retryable").GetBoolean());
            }
            ValidateResult(resultContract??method, result, lookup);
            return result.Clone();
        }
        catch (BrokerRequestException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or ArgumentException or ContractException)
        {
            Dispose();
            if (mutation.HasValue && attempted) throw new MutationOutcomeUnknownException(mutation.Value);
            if (e is InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException or ContractException) throw ResponseError();
            throw;
        }
        finally { exchange.Release(); }
    }

    private void ValidateResult(string method, JsonElement result, Guid? lookup)
    {
        if (method == "hello")
        {
            Fields(result, "apiVersion", "agentVersion", "bootId", "capabilities", "limits");
            if (result.GetProperty("apiVersion").GetRawText() != "1" || !Guid.TryParseExact(result.GetProperty("bootId").GetString(), "D", out _) ||
                result.GetProperty("agentVersion").GetString() is not { Length: > 0 and <= 64 }) throw ResponseError();
            var capabilities = result.GetProperty("capabilities");
            if (capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() > 64) throw ResponseError();
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool pending = false,grants=false,secureStreams=false,relayStreams=false,formalStreams=false;
            foreach (var capability in capabilities.EnumerateArray())
            {
                Fields(capability, "name", "state", "reasonCode");
                string name = capability.GetProperty("name").GetString()!, state = capability.GetProperty("state").GetString()!;
                if (name is not { Length: > 0 and <= 64 } || !names.Add(name) || state is not ("supported" or "experimental" or "unsupported") ||
                    capability.GetProperty("reasonCode").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw ResponseError();
                pending |= name == "app.register.pending" && state is "supported" or "experimental";
                grants |= name == "grant.request.pending" && state is "supported" or "experimental";
                secureStreams |= name=="secure.stream.direct" && state is "supported" or "experimental";
                relayStreams |= name=="secure.stream.relay" && state is "supported" or "experimental";
                formalStreams |= name=="connection.api" && state is "supported" or "experimental";
            }
            var limits = result.GetProperty("limits");
            Fields(limits, "controlFrameBytes", "controlConnections", "requestsPerConnection", "frameTimeoutMs");
            foreach (var limit in limits.EnumerateObject())
                if (!limit.Value.TryGetInt32(out int value) || value <= 0 || value > 65536 || limit.Value.GetRawText() != value.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw ResponseError();
            negotiated = true; pendingAvailable = pending;pendingGrantsAvailable=grants;secureAvailable=secureStreams;relayAvailable=relayStreams;formalAvailable=formalStreams;
            requestLimit = Math.Min(32, limits.GetProperty("requestsPerConnection").GetInt32());
        }
        else if(method is "connection.open" or "connection.close" or "connection.attach" or "connection.operation.status")
            _=ConnectionOperation.Parse(System.Text.Encoding.UTF8.GetBytes(result.GetRawText()),lookup??throw ResponseError(),method=="connection.attach"?"streamRef":method=="connection.operation.status"?null:"connectionId");
        else if(method=="connection.status")
        {var status=ConnectionStatus.Parse(System.Text.Encoding.UTF8.GetBytes(result.GetRawText()));if(status.ConnectionId!=lookup)throw ResponseError();}
        else if (method == "app.register") ValidatePending(result);
        else if(method=="grant.request")PendingGrantRequest.ValidatePendingResult(result);
        else if(method=="grant.request.status")PendingGrantRequest.ValidateStatus(result,lookup??throw ResponseError());
        else if(method is "secure.connection.open" or "secure.relay.open" or "secure.connection.status" or "secure.connection.close")
        {ValidateConnectionResult(result,lookup);if(method=="secure.connection.open"&&result.GetProperty("path").GetString()!="direct"||method=="secure.relay.open"&&result.GetProperty("path").GetString()!="relay")throw ResponseError();}
        else
        {
            Fields(result, "operationId", "status", "result");
            if (result.GetProperty("operationId").GetString() != lookup?.ToString() || result.GetProperty("status").GetString() != "succeeded") throw ResponseError();
            if(method=="grant.operation.status")PendingGrantRequest.ValidatePendingResult(result.GetProperty("result"));
            else ValidatePending(result.GetProperty("result"));
        }
    }

    private static void ValidatePending(JsonElement result)
    {
        Fields(result, "applicationRef", "permissionDiff", "approvalRequired");
        if (!Guid.TryParseExact(result.GetProperty("applicationRef").GetString(), "D", out _) || result.GetProperty("approvalRequired").ValueKind != JsonValueKind.True) throw ResponseError();
        var diff = result.GetProperty("permissionDiff");
        Fields(diff, "addedExports", "removedExports", "changedExports", "addedImports", "removedImports", "changedImports", "declarationChanged");
        foreach (var field in diff.EnumerateObject())
        {
            if (field.Name == "declarationChanged") { if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw ResponseError(); continue; }
            if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() > 16) throw ResponseError();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in field.Value.EnumerateArray())
            {
                string value = item.GetString()!;
                if (value is null || !Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{0,31}\z") || !ids.Add(value)) throw ResponseError();
            }
        }
    }

    private static void Fields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != fields.Length || element.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal))) throw ResponseError();
    }

    private static void Verify(NamedPipeClientStream pipe, SafeProcessHandle expected)
    {
        RequireLive(expected);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0 || GetProcessId(expected) != pid) throw IdentityError();
        using var actual = OpenProcess(0x101000, false, pid); // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
        if (actual.IsInvalid || !GetProcessTimes(actual, out long actualCreated, out _, out _, out _) ||
            !GetProcessTimes(expected, out long expectedCreated, out _, out _, out _) || actualCreated != expectedCreated) throw IdentityError();
        RequireLive(actual);
        RequireLive(expected);
    }

    private static void RequireLive(SafeProcessHandle handle)
    { if (handle.IsInvalid || handle.IsClosed || WaitForSingleObject(handle, 0) != 0x102) throw IdentityError(); }
    private static UnauthorizedAccessException IdentityError() => new("Broker process identity mismatch.");
    private static InvalidDataException ResponseError() => new("Invalid control response.");
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; pipe.Dispose(); expectedProcess.Dispose();
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle source, IntPtr targetProcess, out SafeProcessHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
