using System.Runtime.InteropServices;
using System.Text.Json;

namespace LinkGate;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainWindow());
            return 0;
        }
        if (args.Contains("--ui", StringComparer.Ordinal))
        {
            try
            {
                var options = Parse(args);
                var profile = GatewayProfile.Load(options["--profile"]);
                var role = Role(options["--run"]);
                ApplicationConfiguration.Initialize();
                Application.Run(new MainWindow(profile, role, new PrivateStore(options.GetValueOrDefault("--state-dir"))));
                return 0;
            }
            catch (Exception) { return 2; }
        }
        AttachConsole(uint.MaxValue);
        try { return CommandAsync(args).GetAwaiter().GetResult(); }
        catch (GatewayException error) { Emit(new { ok = false, code = error.Message }); return 2; }
        catch (Exception) { Emit(new { ok = false, code = "operation_failed" }); return 2; }
    }

    private static async Task<int> CommandAsync(string[] args)
    {
        var options = Parse(args);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        if (options.TryGetValue("--duration-seconds", out var duration))
        {
            if (!int.TryParse(duration, out var seconds) || seconds is < 1 or > 86400) throw new GatewayException("arguments");
            cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
        }
        if (options.ContainsKey("--demo"))
        {
            var port = options.TryGetValue("--port", out var raw) && int.TryParse(raw, out var selected) ? selected : 18480;
            if (port is < 1024 or > 65535) throw new GatewayException("arguments");
            await DemoServer.RunAsync(port, cancellation.Token);
            return 0;
        }
        if (!options.TryGetValue("--profile", out var path)) throw new GatewayException("arguments");
        var profile = GatewayProfile.Load(path);
        var role = options.TryGetValue("--run", out var roleValue) ? Role(roleValue) : GatewayRole.Publisher;
        var store = new PrivateStore(options.GetValueOrDefault("--state-dir"));
        using var host = new GatewayHost(options.GetValueOrDefault("--runtime"));
        if (options.ContainsKey("--verify"))
        {
            await host.VerifyAsync(profile, GatewayRole.Publisher, store, cancellation.Token);
            await host.VerifyAsync(profile, GatewayRole.Visitor, store, cancellation.Token);
            Emit(new { ok = true, profile = "valid", runtimeConfig = "valid", services = profile.Services.Count });
            return 0;
        }
        if (!options.ContainsKey("--run")) throw new GatewayException("arguments");
        await host.StartAsync(profile, role, store);
        Emit(new { ok = true, state = "running", role = role.ToString().ToLowerInvariant(), path = "unknown" });
        try
        {
            while (!cancellation.IsCancellationRequested && host.IsRunning)
            {
                Emit(new { timeUtc = DateTimeOffset.UtcNow, diagnostics = host.DiagnosticsSnapshot() });
                await Task.Delay(1000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        var canceled = cancellation.IsCancellationRequested;
        host.Stop();
        Emit(new { ok = canceled, state = "stopped", code = canceled ? "requested" : "runtime_exited" });
        return canceled ? 0 : 3;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new[] { "--verify", "--demo", "--ui" };
        var values = new[] { "--profile", "--state-dir", "--runtime", "--run", "--port", "--duration-seconds" };
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (result.ContainsKey(option)) throw new GatewayException("arguments");
            if (flags.Contains(option)) result.Add(option, "true");
            else if (values.Contains(option) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) result.Add(option, args[++i]);
            else throw new GatewayException("arguments");
        }
        var actions = new[] { "--verify", "--run", "--demo" }.Count(result.ContainsKey);
        if (actions != 1 || result.ContainsKey("--port") && !result.ContainsKey("--demo")) throw new GatewayException("arguments");
        if (result.ContainsKey("--ui") && (!result.ContainsKey("--run") || !result.ContainsKey("--profile") || result.ContainsKey("--duration-seconds"))) throw new GatewayException("arguments");
        return result;
    }
    private static GatewayRole Role(string value) => value switch
    {
        "publisher" => GatewayRole.Publisher,
        "visitor" => GatewayRole.Visitor,
        _ => throw new GatewayException("arguments")
    };
    public static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
