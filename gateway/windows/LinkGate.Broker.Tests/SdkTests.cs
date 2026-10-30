using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Client;
using LinkGate.Contracts;

internal static class SdkTests
{
    private const string Manifest = """
        {"manifestVersion":1,"applicationId":"test.sdk","displayName":"SDK","exports":[{"serviceId":"files","label":"Files","protocol":"http","localTarget":{"host":"127.0.0.1","port":18481}}],"imports":[]}
        """;

    public static async Task RunAsync(Action<bool, string> check)
    {
        string repo = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(repo, "gateway"))) repo = Directory.GetParent(repo)?.FullName ?? throw new Exception("Repository not found");
        string root = Path.GetFullPath(Path.Combine(repo, "artifacts", "linkgate-sdk-tests"));
        string state = Path.Combine(root, Guid.NewGuid().ToString("N"));
        using var self = Process.GetCurrentProcess();
        var manifest = ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest));
        try
        {
            using (var host = new PipeHost(Guid.NewGuid().ToString("N"), new RegistrationStore(state)))
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                Task server = host.RunAsync(stop.Token);
                try
                {
                    using var client = await VerifiedPipeClient.ConnectAsync(host.PipeName, self.SafeHandle, stop.Token);
                    Guid op = Guid.NewGuid();
                    try { await client.RegisterPendingAsync(manifest, op, true); throw new Exception("Unnegotiated registration"); }
                    catch (InvalidOperationException) { check(true, "SDK requires negotiation"); }
                    await client.HelloAsync(stop.Token);
                    try { await client.RegisterPendingAsync(manifest, op); throw new Exception("Implicit experimental opt-in"); }
                    catch (InvalidOperationException) { check(true, "SDK requires explicit opt-in"); }
                    var first = await client.RegisterPendingAsync(manifest, op, true, stop.Token);
                    var retry = await client.RegisterPendingAsync(manifest, op, true, stop.Token);
                    check(first.GetRawText() == retry.GetRawText() && first.GetProperty("approvalRequired").GetBoolean(), "SDK stable operation result");
                    var queried = await client.GetOperationStatusAsync(op, stop.Token);
                    check(queried.GetProperty("result").GetRawText() == first.GetRawText(), "SDK explicit operation query");
                    var changed = ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest.Replace("18481", "18482")));
                    try { await client.RegisterPendingAsync(changed, op, true, stop.Token); throw new Exception("Conflict missing"); }
                    catch (BrokerRequestException e) { check(e.Code == "OPERATION_CONFLICT" && e.Message == "Broker request rejected." && e.InnerException is null, "SDK sanitized known error"); }
                    try { await client.GetOperationStatusAsync(Guid.NewGuid(), stop.Token); throw new Exception("Unknown operation hidden"); }
                    catch (BrokerRequestException e) { check(e.Code == "OPERATION_UNKNOWN", "SDK does not invent operation result"); }
                    using (var canceled = new CancellationTokenSource())
                    {
                        canceled.Cancel();
                        try { await client.RegisterPendingAsync(manifest, Guid.NewGuid(), true, canceled.Token); throw new Exception("Cancellation ignored"); }
                        catch (OperationCanceledException) { check(true, "pre-write cancellation not uncertain submission"); }
                    }
                    check((await client.GetOperationStatusAsync(op, stop.Token)).GetProperty("status").GetString() == "succeeded", "valid errors preserve session");
                    client.Dispose();
                    using var limited = await VerifiedPipeClient.ConnectAsync(host.PipeName, self.SafeHandle, stop.Token);
                    for (int i = 0; i < 32; i++) await limited.HelloAsync(stop.Token);
                    try { await limited.RegisterPendingAsync(manifest, Guid.NewGuid(), true); throw new Exception("Session budget exceeded"); }
                    catch (InvalidOperationException) { check(true, "session budget rejects before mutation write"); }
                }
                finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
            }
            foreach (string fault in new[] { "drop", "timeout", "malformed" })
                await LostReply(Path.Combine(state, fault), fault, manifest, check);
            check(typeof(VerifiedPipeClient).Assembly.GetReferencedAssemblies().All(a => a.Name is not ("LinkGate.Broker" or "Microsoft.Data.Sqlite")), "SDK assembly boundary");
        }
        finally
        {
            string target = Path.GetFullPath(state);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    private static async Task LostReply(string state, string fault, AppManifest manifest, Action<bool, string> check)
    {
        // Controlled fault peer: its fixed caller key is a fixture, not production identity verification.
        var protocol = new ControlProtocol(new RegistrationStore(state));
        string name = "LinkGate." + new string('a', 64) + ".control.v1.test." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var self = Process.GetCurrentProcess();
        Guid op = Guid.NewGuid();
        int mutationCount = 0;
        async Task Peer()
        {
            for (int session = 0; session < 2; session++)
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                var hello = await FrameCodec.ReadAsync(pipe, stop.Token) ?? throw new Exception("Hello missing");
                await FrameCodec.WriteAsync(pipe, protocol.Dispatch(hello), stop.Token);
                var request = await FrameCodec.ReadAsync(pipe, stop.Token) ?? throw new Exception("Request missing");
                using var parsed = JsonDocument.Parse(request);
                string method = parsed.RootElement.GetProperty("method").GetString()!;
                if (method == "app.register") mutationCount++;
                byte[] response = protocol.Dispatch(request, "fault-test-caller");
                if (session == 0)
                {
                    if (fault == "timeout") await Task.Delay(2500, stop.Token);
                    else if (fault == "malformed")
                    {
                        await FrameCodec.WriteAsync(pipe, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(response).Replace("\"approvalRequired\":true", "\"approvalRequired\":false")), stop.Token);
                        try { _ = await pipe.ReadAtLeastAsync(new byte[1], 1, false, stop.Token); } catch (IOException) { }
                    }
                }
                else
                {
                    await FrameCodec.WriteAsync(pipe, response, stop.Token);
                    try { _ = await pipe.ReadAtLeastAsync(new byte[1], 1, false, stop.Token); } catch (IOException) { }
                }
            }
        }
        Task server = Peer();
        try
        {
            using (var client = await VerifiedPipeClient.ConnectAsync(name, self.SafeHandle, stop.Token))
            {
                await client.HelloAsync(stop.Token);
                try { await client.RegisterPendingAsync(manifest, op, true, stop.Token); throw new Exception("Lost result accepted"); }
                catch (MutationOutcomeUnknownException e) { check(e.OperationId == op && e.InnerException is null, "unknown outcome preserves ID: " + fault); }
                try { await client.GetOperationStatusAsync(op); throw new Exception("Uncertain connection reused"); }
                catch (ObjectDisposedException) { check(true, "unknown outcome closes client: " + fault); }
            }
            using (var recovered = await VerifiedPipeClient.ConnectAsync(name, self.SafeHandle, stop.Token))
            {
                await recovered.HelloAsync(stop.Token);
                var operation = await recovered.GetOperationStatusAsync(op, stop.Token);
                check(operation.GetProperty("status").GetString() == "succeeded", "explicit recovery finds commit: " + fault);
            }
            await server;
            check(mutationCount == 1, "no automatic mutation retry: " + fault);
        }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }
}
