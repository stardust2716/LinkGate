using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LinkGate.Control;

namespace LinkGate.Authority;

public sealed class ControlRefusedException : Exception
{
    public string Code { get; }
    internal ControlRefusedException(string code) : base("Server control request refused.") { Code = code; }
}

// Agent-only. Intentionally not a record: diagnostic formatting must never reveal Token.
public sealed class AuthorizedLease
{
    public string Token { get; }
    public LeaseReceipt Receipt { get; }
    internal AuthorizedLease(string token, LeaseReceipt receipt) { Token = token; Receipt = receipt; }
    public override string ToString() => "Verified server authority";
}

public sealed partial class ControlClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly X509Certificate2 server, root;
    private readonly string issuer, device;
    private readonly LeaseClock clock;
    private readonly SemaphoreSlim calls = new(8);
    private readonly CancellationTokenSource stopped = new();
    private readonly object gate = new();
    private Guid? epoch;
    private bool closed;
    public string DeviceID => device;
    internal LeaseClock Clock => clock;
    internal void CheckReceipt(LeaseReceipt receipt)
    {
        lock (gate)
        {
            CheckOpen();
            if (!ReferenceEquals(receipt.Clock, clock) || receipt.Issuer != issuer || receipt.ServerEpoch != epoch || (receipt.Authority.Scope.VisitorDevice != device && receipt.Authority.Scope.Target.PublisherDevice != device) || !receipt.Active) throw new LeaseRejectedException();
        }
    }

    private ControlClient(IPAddress address, int port, X509Certificate2 deviceCertificate, X509Certificate2 serverCertificate, X509Certificate2 ownerRoot, LeaseClock clock)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || port is < 1 or > 65535 || !deviceCertificate.HasPrivateKey) throw new LeaseRejectedException();
        this.clock = clock; server = serverCertificate; root = ownerRoot;
        ServerLease.ValidateCertificate(deviceCertificate, root, clock.Read().Utc);
        ServerLease.ValidateCertificate(server, root, clock.Read().Utc);
        issuer = Key(server); device = Key(deviceCertificate);
        endpoint = new Uri($"https://linkgate.device:{port}/control/v1");
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(2), MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, port), cancellation); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "linkgate.device", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowTlsResume = false,
                ClientCertificates = new X509CertificateCollection { deviceCertificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => deviceCertificate,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    try
                    {
                        using var peer = new X509Certificate2(certificate);
                        ServerLease.ValidateCertificate(peer, root, clock.Read().Utc);
                        return Key(peer) == issuer;
                    }
                    catch (Exception e) when (e is CryptographicException or LeaseRejectedException or ArgumentException) { return false; }
                }
            }
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async Task<ControlClient> ConnectAsync(IPAddress address, int port, X509Certificate2 deviceCertificate, X509Certificate2 serverCertificate, X509Certificate2 ownerRoot, LeaseClock? clock = null, CancellationToken cancellation = default)
    {
        var client = new ControlClient(address, port, deviceCertificate, serverCertificate, ownerRoot, clock ?? new LeaseClock());
        try
        {
            using var reply = await client.CallAsync("hello", new { }, false, cancellation);
            var result = reply.RootElement;
            LeaseJson.Fields(result, "server", "epoch", "maxLeaseSeconds");
            if (LeaseJson.Text(result, "server", 64) != client.issuer || LeaseJson.Integer(result, "maxLeaseSeconds") != 120) throw new LeaseRejectedException();
            Guid epoch = LeaseJson.UUID(result, "epoch");
            lock (client.gate) { client.CheckOpen(); client.epoch = epoch; }
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public async Task<AuthorizedLease> AcquireAsync(string application, ServiceTarget target, CancellationToken cancellation = default)
    {
        var expected = new AccessScope(device, application, target);
        using var reply = await CallAsync("lease.acquire", new { visitorApp = application, target = Target(target) }, true, cancellation);
        var value = reply.RootElement; LeaseJson.Fields(value, "current", "token", "serverEpoch", "issuer");
        return await VerifyAsync(LeaseJson.Text(value, "token", 16384), value, expected);
    }

    public async Task<AuthorizedLease> ValidateAsync(string token, AccessScope expected, CancellationToken cancellation = default)
    {
        if (expected.Target.PublisherDevice != device || string.IsNullOrEmpty(token) || token.Length > 16384) throw new LeaseRejectedException();
        using var reply = await CallAsync("lease.validate", new { token }, true, cancellation);
        var value = reply.RootElement; LeaseJson.Fields(value, "current", "serverEpoch", "issuer");
        return await VerifyAsync(token, value, expected);
    }

    public async Task<bool[]> CheckAsync(IReadOnlyList<LeaseReceipt> receipts, CancellationToken cancellation = default)
    {
        if (receipts.Count is < 1 or > 64) throw new LeaseRejectedException();
        var values = receipts.ToArray(); foreach (var receipt in values) CheckReceipt(receipt);
        using var reply = await CallAsync("lease.check", new { leases = values.Select(value => new
        {
            scope = new { visitorDevice = value.Authority.Scope.VisitorDevice, visitorApp = value.Authority.Scope.VisitorApp, target = Target(value.Authority.Scope.Target) },
            visitorRevision = value.Authority.VisitorRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            publisherRevision = value.Authority.PublisherRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            serviceRevision = value.Authority.ServiceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            grantRevision = value.Authority.GrantRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            protocol = value.Authority.Protocol, authority = value.Authority.Authority
        }).ToArray() }, true, cancellation);
        var result = reply.RootElement; LeaseJson.Fields(result, "valid", "serverEpoch", "issuer");
        Guid returned = LeaseJson.UUID(result, "serverEpoch");
        bool changed; lock (gate) { CheckOpen(); changed = returned != epoch || LeaseJson.Text(result, "issuer", 64) != issuer; }
        if (changed) { Dispose(); throw new LeaseRejectedException(); }
        var validity = result.GetProperty("valid");
        if (validity.ValueKind != JsonValueKind.Array || validity.GetArrayLength() != values.Length || validity.EnumerateArray().Any(item => item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) throw new LeaseRejectedException();
        return validity.EnumerateArray().Select((item, index) => item.GetBoolean() && values[index].Active).ToArray();
    }

    private async Task<AuthorizedLease> VerifyAsync(string token, JsonElement value, AccessScope expected)
    {
        Guid returnedEpoch = LeaseJson.UUID(value, "serverEpoch"), known;
        lock (gate) { CheckOpen(); known = epoch ?? throw new LeaseRejectedException(); }
        if (returnedEpoch != known || LeaseJson.Text(value, "issuer", 64) != issuer) { Dispose(); throw new LeaseRejectedException(); }
        var snapshot = AuthoritySnapshot.Parse(value.GetProperty("current"));
        if (snapshot.Scope != expected) throw new LeaseRejectedException();
        var receipt = await ServerLease.VerifyAsync(token, server, root, issuer, known, snapshot, clock);
        lock (gate) { CheckOpen(); return new AuthorizedLease(token, receipt); }
    }

    private async Task<JsonDocument> CallAsync(string method, object parameters, bool connected, CancellationToken cancellation, Guid? operation = null)
    {
        lock (gate) { CheckOpen(); if (connected && !epoch.HasValue) throw new LeaseRejectedException(); }
        if (!calls.Wait(0)) throw new ControlRefusedException("RESOURCE_EXHAUSTED");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopped.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            string id = Guid.NewGuid().ToString();
            var envelope=new Dictionary<string,object>{["kind"]="request",["apiVersion"]=1,["requestId"]=id,["method"]=method,["params"]=parameters};if(operation is not null)envelope["operationId"]=operation.Value.ToString();
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (body.Length > 65536) throw new LeaseRejectedException();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body), Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            long requestBegan=clock.Read().Timestamp;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > 65536) throw new LeaseRejectedException();
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var output = new MemoryStream(); var buffer = new byte[8192];
            while (true) { int count = await stream.ReadAsync(buffer, limit.Token); if (count == 0) break; if (output.Length + count > 65536) throw new LeaseRejectedException(); output.Write(buffer, 0, count); }
            using var document = ControlJson.ReadDocument(output.ToArray()); var frame = document.RootElement;
            bool error = frame.TryGetProperty("error", out var failure);
            LeaseJson.Fields(frame, "kind", "apiVersion", "requestId", error ? "error" : "result");
            if (LeaseJson.Text(frame, "kind", 8) != "response" || LeaseJson.Integer(frame, "apiVersion") != 1 || LeaseJson.Text(frame, "requestId", 36) != id) throw new LeaseRejectedException();
            lock (gate) { CheckOpen(); }
            if (error)
            {
                LeaseJson.Fields(failure, "code", "stage", "retryable"); string code = LeaseJson.Text(failure, "code", 32);
                if (!Codes.Contains(code) || LeaseJson.Text(failure, "stage", 8) != "control" || failure.GetProperty("retryable").ValueKind != JsonValueKind.False) throw new LeaseRejectedException();
                throw new ControlRefusedException(code);
            }
            var result=JsonDocument.Parse(frame.GetProperty("result").GetRawText());
            try{lock(gate){CheckOpen();if(response.Headers.Date is { } observed)clock.ObserveAuthenticatedResponseDate(observed,requestBegan);}return result;}
            catch{result.Dispose();throw;}
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException or InvalidDataException or FormatException)
        { throw new ControlRefusedException("CONTROL_UNAVAILABLE"); }
        finally { calls.Release(); }
    }

    private static readonly HashSet<string> Codes = ["INVALID_REQUEST", "VERSION_UNSUPPORTED", "METHOD_UNSUPPORTED", "AUTH_REQUIRED", "RESOURCE_EXHAUSTED", "STORAGE_UNAVAILABLE", "IDENTITY_CHANGED", "REVIEW_STALE", "OPERATION_CONFLICT", "OPERATION_UNKNOWN", "CLOCK_UNTRUSTED"];
    private static object Target(ServiceTarget target) => new { publisherDevice = target.PublisherDevice, publisherApp = target.PublisherApp, service = target.Service };
    private static string Key(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    private void CheckOpen() { if (closed) throw new LeaseRejectedException(); }
    public void Dispose()
    {
        lock (gate) { if (closed) return; closed = true; }
        stopped.Cancel(); http.Dispose();
    }
}
