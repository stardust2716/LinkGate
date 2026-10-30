using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LinkGate.Authority;
using LinkGate.Broker;
using LinkGate.Client;
using LinkGate.Contracts;
using LinkGate.Control;

sealed class DataFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(110));
    private readonly TcpListener backend = new(IPAddress.Loopback, 0);
    private readonly ConcurrentBag<Task> handling = [];
    private readonly byte[] challenge = RandomNumberGenerator.GetBytes(32);
    private readonly LeaseSupervisor supervisor;
    private readonly SecureIdentity publisherIdentity, visitorIdentity;
    private Task serving = Task.CompletedTask;
    private LocalAgent? publisher, visitor;
    private SecurePeerServer? peer;
    private SecureConnections? connections;
    private Guid visitorApplication;
    private RegistrationStore? publishingStore;
    private Guid publisherApplication;
    private string javaDevice = "";
    private int accepted;
    private long sent;
    public int Accepted => Volatile.Read(ref accepted);
    public long Sent => Interlocked.Read(ref sent);
    private DataFixture(X509Certificate2 publisherCertificate, X509Certificate2 visitorCertificate, X509Certificate2 root, ControlClient publisherControl)
    { publisherIdentity = new(publisherCertificate, root); visitorIdentity = new(visitorCertificate, root); supervisor = new(publisherControl); }

    public static async Task<DataFixture> StartAsync(string directory, string data, X509Certificate2 publisherCertificate, X509Certificate2 visitorCertificate, X509Certificate2 root, ControlClient publisherControl, ControlClient visitorControl)
    {
        var fixture = new DataFixture(publisherCertificate, visitorCertificate, root, publisherControl);
        try { await fixture.Initialize(directory, data, visitorControl); return fixture; }
        catch { await fixture.DisposeAsync(); throw; }
    }
    private async Task Initialize(string directory, string data, ControlClient visitorControl)
    {
        backend.Start(); serving = Serve();
        var store = new RegistrationStore(Path.Combine(directory, "publisher-state")); store.BindSecureIdentity(publisherIdentity);
        store.TrustSecurePeer(publisherIdentity, visitorIdentity.Certificate.ExportCertificatePem(), 0, Guid.NewGuid());
        string javaPem = File.ReadAllText(Path.Combine(data, "java-cert.pem")); using var javaCertificate = X509Certificate2.CreateFromPem(javaPem);
        string javaID = SecureIdentity.KeyId(javaCertificate); javaDevice=javaID; publishingStore=store; store.TrustSecurePeer(publisherIdentity, javaPem, 0, Guid.NewGuid());
        publisher = await LocalAgent.StartAsync(store, null);
        var manifest = ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifestVersion = 1, applicationId = "org.example.publisher", displayName = "Authority fixture publisher",
            exports = new[] { new { serviceId = "stream", label = "Deterministic business", protocol = "tcp", localTarget = new { host = "127.0.0.1", port = ((IPEndPoint)backend.LocalEndpoint).Port } } }, imports = Array.Empty<object>()
        }));
        Guid app = (await publisher.Client.RegisterPendingAsync(manifest, Guid.NewGuid(), true, lifetime.Token)).GetProperty("applicationRef").GetGuid();
        publisherApplication=app;
        store.DecideReview(store.GetReview(app), ApplicationReviewDecision.Approved, Guid.NewGuid());
        store.ApproveSecureGrant(store.ProposeSecureGrant(visitorIdentity.DeviceId, "org.example.client", app, "stream"), Guid.NewGuid());
        peer = new SecurePeerServer(publisherIdentity, store, IPAddress.Loopback, 0, supervisor); peer.Start();
        var consumerStore = new RegistrationStore(Path.Combine(directory, "visitor-state")); consumerStore.BindSecureIdentity(visitorIdentity);
        consumerStore.TrustSecurePeer(visitorIdentity, publisherIdentity.Certificate.ExportCertificatePem(), 0, Guid.NewGuid(), "127.0.0.1", peer.Port);
        connections = new SecureConnections(consumerStore, visitorIdentity, visitorControl);
        visitor = await LocalAgent.StartAsync(consumerStore, connections);
        var consumerManifest = ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifestVersion = 1, applicationId = "org.example.client", displayName = "Authority fixture consumer", exports = Array.Empty<object>(),
            imports = new[] { new { alias = "stream", protocol = "tcp", endpointMode = "brokered", connectionPolicy = new { mode = "direct-only", allowRelay = false, connectTimeoutMs = 5000 } } }
        }));
        visitorApplication = (await visitor.Client.RegisterPendingAsync(consumerManifest, Guid.NewGuid(), true, lifetime.Token)).GetProperty("applicationRef").GetGuid();
        consumerStore.DecideReview(consumerStore.GetReview(visitorApplication), ApplicationReviewDecision.Approved, Guid.NewGuid());
        File.WriteAllBytes(Path.Combine(data, "application-challenge.bin"), challenge);
        string pending = Path.Combine(data, "peer.json.part"); File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(new { port = peer.Port })); File.Move(pending, Path.Combine(data, "peer.json"));
    }
    public void AllowJava(string data)
    { publishingStore!.ApproveSecureGrant(publishingStore.ProposeSecureGrant(javaDevice,"org.example.client",publisherApplication,"stream"),Guid.NewGuid());File.WriteAllText(Path.Combine(data,"local-approved"),"true"); }
    private async Task<JsonElement> OpenStatus()
    {
        // The development control pipe advertises a two-second frame/idle
        // budget. Explicitly rebind and pin the same held Agent before a new
        // operation; the SDK never replays an interrupted mutation itself.
        await visitor!.RebindAsync();
        Guid operation = Guid.NewGuid();
        var status = await visitor!.Client.OpenSecureDirectAsync(new PendingGrantRequest(visitorApplication, "stream", publisherIdentity.DeviceId, "org.example.publisher", "stream"), operation, true, lifetime.Token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(TimeSpan.FromSeconds(6));
        while (status.GetProperty("state").GetString() == "pending") { await Task.Delay(20, limit.Token); status = await visitor.Client.GetSecureConnectionStatusAsync(operation, limit.Token); }
        return status;
    }
    private async Task<BusinessStream> Open()
    {
        var status = await OpenStatus(); if (status.GetProperty("state").GetString() != "ready") throw new IOException("Server-authorized business open failed.");
        return await visitor!.Client.AttachSecureStreamAsync(status, lifetime.Token);
    }
    public async Task<bool> RejectedAfterRevoke()
    { var status = await OpenStatus(); return status.GetProperty("state").GetString() == "closed" && status.GetProperty("error").GetString() == "AUTH_REQUIRED"; }
    public async Task TransferAsync()
    {
        await using var stream = await Open(); await stream.WriteAsync(new byte[] { 1 }, lifetime.Token); await stream.WriteAsync(challenge, lifetime.Token); await stream.CompleteWritesAsync(lifetime.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[32768]; long count = 0; int received;
        while ((received = await stream.ReadAsync(buffer, lifetime.Token)) != 0) { hash.AppendData(buffer, 0, received); count += received; if (count > 67108864) throw new IOException("Oversized fixture response."); }
        byte[] block = Enumerable.Range(0, 65536).Select(index => (byte)(index % 251)).ToArray(); using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < 1024; i++) expected.AppendData(block);
        if (count != 67108864 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected.GetHashAndReset())) throw new IOException("Server-authorized file integrity failed.");
        await stream.DrainWriteCompletionAsync(lifetime.Token);
    }
    public async Task<BusinessStream> HoldAsync()
    {
        var stream = await Open(); try { await stream.WriteAsync(new byte[] { 2 }, lifetime.Token); await stream.WriteAsync(challenge, lifetime.Token); byte[] ready = new byte[1]; await stream.ReadExactlyAsync(ready, lifetime.Token); if (ready[0] != 1) throw new IOException("Fixture hold failed."); return stream; }
        catch { await stream.DisposeAsync(); throw; }
    }
    private async Task Serve()
    {
        try { while (!lifetime.IsCancellationRequested) { var socket = await backend.AcceptTcpClientAsync(lifetime.Token); Interlocked.Increment(ref accepted); handling.Add(Handle(socket)); } }
        catch (Exception failure) when (failure is OperationCanceledException or SocketException) { }
    }
    private async Task Handle(TcpClient socket)
    {
        using (socket) try
        {
            var stream = socket.GetStream(); byte[] request = new byte[33]; await stream.ReadExactlyAsync(request, lifetime.Token);
            if (!CryptographicOperations.FixedTimeEquals(request.AsSpan(1), challenge)) return;
            if (request[0] == 1)
            {
                if (await stream.ReadAsync(new byte[1], lifetime.Token) != 0) return;
                byte[] block = Enumerable.Range(0, 65536).Select(index => (byte)(index % 251)).ToArray();
                for (int i = 0; i < 1024; i++) { await stream.WriteAsync(block, lifetime.Token); Interlocked.Add(ref sent, block.Length); } socket.Client.Shutdown(SocketShutdown.Send);
            }
            else if (request[0] == 2) { await stream.WriteAsync(new byte[] { 1 }, lifetime.Token); _ = await stream.ReadAsync(new byte[1], lifetime.Token); }
        }
        catch (Exception failure) when (failure is IOException or SocketException or OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); backend.Stop();
        if (visitor is not null) await visitor.DisposeAsync();
        if (connections is not null) await connections.DisposeAsync();
        if (peer is not null) await peer.DisposeAsync();
        if (publisher is not null) await publisher.DisposeAsync();
        await supervisor.DisposeAsync(); await serving; await Task.WhenAll(handling.ToArray()); lifetime.Dispose();
    }

    private sealed class LocalAgent : IAsyncDisposable
    {
        public VerifiedPipeClient Client { get; private set; } = null!;
        private readonly PipeHost host;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task serving;
        private LocalAgent(RegistrationStore store, SecureConnections? connections) { host = new PipeHost(Guid.NewGuid().ToString("N"), store, false, connections); serving = host.RunAsync(lifetime.Token); }
        public static async Task<LocalAgent> StartAsync(RegistrationStore store, SecureConnections? connections)
        {
            var agent = new LocalAgent(store, connections);
            try { await agent.RebindAsync(); return agent; }
            catch { await agent.DisposeAsync(); throw; }
        }
        public async Task RebindAsync() { Client?.Dispose(); using var process=Process.GetCurrentProcess();Client=await VerifiedPipeClient.ConnectAsync(host.PipeName,process.SafeHandle,lifetime.Token);await Client.HelloAsync(lifetime.Token); }
        public async ValueTask DisposeAsync() { lifetime.Cancel(); Client?.Dispose(); try { await serving; } catch (OperationCanceledException) { } finally { host.Dispose(); lifetime.Dispose(); } }
    }
}
