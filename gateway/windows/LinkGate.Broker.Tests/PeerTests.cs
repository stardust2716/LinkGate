using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Client;

internal static class PeerTests
{
    public static async Task<bool> RunChildAsync(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--broker-peer" or "--probe-peer" or "--bad-response-peer")) return false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var host = new PipeHost(args[1]);
        Console.WriteLine(host.PipeName);
        if (args[0] == "--broker-peer")
        {
            try { await host.RunAsync(stop.Token); } catch (OperationCanceledException) { }
        }
        else
        {
            // A different-process impostor observes whether the client sent any bytes.
            // The name is valid and the DACL is legitimate, but its process is not pinned.
            host.Dispose();
            using var probe = new NamedPipeServerStream(host.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            Task accepting = probe.WaitForConnectionAsync(stop.Token);
            Console.WriteLine("ready");
            await accepting;
            if (args[0] == "--bad-response-peer")
            {
                using var request = JsonDocument.Parse((await FrameCodec.ReadAsync(probe, stop.Token))!);
                string id = request.RootElement.GetProperty("requestId").GetString()!;
                var response = System.Text.Encoding.UTF8.GetBytes($"{{\"kind\":\"response\",\"ki\\u006ed\":\"response\",\"apiVersion\":1,\"requestId\":\"{id}\",\"result\":{{}}}}");
                await FrameCodec.WriteAsync(probe, response, stop.Token);
            }
            int count;
            try { count = await probe.ReadAsync(new byte[1], stop.Token); }
            catch (IOException) { count = 0; } // Windows may report a rejected client's close as a broken pipe.
            Console.WriteLine($"received:{count}");
        }
        return true;
    }

    private static Process Start(string mode)
    {
        string executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(Guid.NewGuid().ToString("N"));
        return Process.Start(info) ?? throw new Exception("Cannot start test peer.");
    }

    private static async Task<string> Line(Process process, CancellationToken token) =>
        await process.StandardOutput.ReadLineAsync(token) ?? throw new Exception("Test peer exited: " + await process.StandardError.ReadToEndAsync(token));

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using (var earlyHost = new PipeHost(Guid.NewGuid().ToString("N")))
        using (var earlyStop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            using (var closed = new NamedPipeClientStream(".", earlyHost.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                await closed.ConnectAsync(deadline.Token);
            Task server = earlyHost.RunAsync(earlyStop.Token);
            try
            {
                using var self = Process.GetCurrentProcess();
                using var recovered = await VerifiedPipeClient.ConnectAsync(earlyHost.PipeName, self.SafeHandle, deadline.Token);
                check((await recovered.HelloAsync(deadline.Token)).GetProperty("apiVersion").GetInt32() == 1, "recover pre-accept disconnect");
            }
            finally { earlyStop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        }
        using var trusted = Start("--broker-peer");
        try
        {
            string name = await Line(trusted, deadline.Token);
            using (var client = await VerifiedPipeClient.ConnectAsync(name, trusted.SafeHandle, deadline.Token))
            {
                var hello = await client.HelloAsync(deadline.Token);
                check(hello.GetProperty("apiVersion").GetInt32() == 1, "independent pinned Broker");
                check(hello.GetProperty("capabilities").EnumerateArray().All(c => c.GetProperty("state").GetString() == "unsupported"), "no new authorization");
            }
            using (var borrowed = Process.GetProcessById(trusted.Id))
            using (var client = await VerifiedPipeClient.ConnectAsync(name, borrowed.SafeHandle, deadline.Token))
            {
                borrowed.Dispose();
                check((await client.HelloAsync(deadline.Token)).GetProperty("apiVersion").GetInt32() == 1, "client owns duplicate pin");
            }
            using var impostor = Start("--probe-peer");
            try
            {
                string wrongName = await Line(impostor, deadline.Token);
                check(await Line(impostor, deadline.Token) == "ready", "impostor ready");
                try
                {
                    using var bad = await VerifiedPipeClient.ConnectAsync(wrongName, trusted.SafeHandle, deadline.Token);
                    throw new Exception("Impostor accepted");
                }
                catch (UnauthorizedAccessException) { check(true, "different process rejected"); }
                check(await Line(impostor, deadline.Token) == "received:0", "zero bytes delivered to impostor");
            }
            finally { Stop(impostor); }
            using var malformed = Start("--bad-response-peer");
            try
            {
                string malformedName = await Line(malformed, deadline.Token);
                check(await Line(malformed, deadline.Token) == "ready", "malformed peer ready");
                using var client = await VerifiedPipeClient.ConnectAsync(malformedName, malformed.SafeHandle, deadline.Token);
                try { await client.HelloAsync(deadline.Token); throw new Exception("Duplicate response accepted"); }
                catch (InvalidDataException e) { check(e.Message == "Invalid control response." && e.InnerException is null, "strict sanitized response rejection"); }
                try { await client.HelloAsync(deadline.Token); throw new Exception("Invalid session reused"); }
                catch (ObjectDisposedException) { check(true, "invalid response closes client"); }
            }
            finally { Stop(malformed); }
            foreach (string invalid in new[] { @"\\remote\pipe\LinkGate.control.v1", "LinkGate.fake.control.v1", name + ".extra" })
            {
                try { using var bad = await VerifiedPipeClient.ConnectAsync(invalid, trusted.SafeHandle, deadline.Token); throw new Exception("Invalid pipe accepted"); }
                catch (ArgumentException) { check(true, "noncanonical pipe rejected"); }
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { using var bad = await VerifiedPipeClient.ConnectAsync(name, trusted.SafeHandle, canceled.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { check(true, "connect canceled"); }
            }
            using (var client = await VerifiedPipeClient.ConnectAsync(name, trusted.SafeHandle, deadline.Token))
            {
                Stop(trusted);
                try { await client.HelloAsync(deadline.Token); throw new Exception("Exited peer accepted"); }
                catch (UnauthorizedAccessException) { check(true, "live session rejects terminated pin"); }
            }
            try { using var bad = await VerifiedPipeClient.ConnectAsync(name, trusted.SafeHandle, deadline.Token); throw new Exception("Dead pin accepted"); }
            catch (UnauthorizedAccessException) { check(true, "dead process fails before connecting"); }
        }
        finally { Stop(trusted); }
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited) process.Kill();
        if (!process.WaitForExit(3000)) throw new Exception("Test peer did not exit.");
    }
}
