using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;

namespace LinkGate;

public sealed class GatewayHost : IDisposable
{
    private readonly object sync = new();
    private readonly Queue<string> events = new();
    private Process? process;
    private ProcessJob? job;
    private bool disposed;
    private readonly SemaphoreSlim startup = new(1, 1);
    public event Action? Changed;
    public string Status { get; private set; } = "已停止";
    private ConnectionDiagnostics diagnostics = new();
    public string PathEvidence { get { lock (sync) return diagnostics.Summary(); } }
    public object DiagnosticsSnapshot() { lock (sync) return diagnostics.Snapshot(); }
    public bool IsRunning { get { lock (sync) return process is { HasExited: false }; } }
    public IReadOnlyList<string> Events { get { lock (sync) return events.ToArray(); } }
    public string RuntimePath { get; }

    public GatewayHost(string? runtimePath = null) => RuntimePath = Path.GetFullPath(runtimePath ?? Path.Combine(AppContext.BaseDirectory, "runtime", "frpc.exe"));

    public async Task VerifyAsync(GatewayProfile profile, GatewayRole role, PrivateStore store, CancellationToken cancellation = default)
    {
        await VerifyVersionAsync(cancellation);
        var path = Prepare(profile, role, store);
        using var verifier = CreateProcess("verify", "-c", path);
        using var verifierJob = new ProcessJob();
        StartOwned(verifier, verifierJob);
        var stdout = DrainBoundedAsync(verifier.StandardOutput);
        var stderr = DrainBoundedAsync(verifier.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await verifier.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            verifierJob.Dispose();
            throw new GatewayException("runtime_verify_timeout");
        }
        await Task.WhenAll(stdout, stderr);
        if (verifier.ExitCode != 0) throw new GatewayException("runtime_config_rejected");
    }

    private async Task VerifyVersionAsync(CancellationToken cancellation)
    {
        using var version = CreateProcess("--version");
        using var versionJob = new ProcessJob();
        StartOwned(version, versionJob);
        var output = ReadVersionAsync(version.StandardOutput);
        var errors = DrainBoundedAsync(version.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await version.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { versionJob.Dispose(); throw new GatewayException("runtime_version_timeout"); }
        await errors;
        if (version.ExitCode != 0 || (await output).Trim() != "0.71.1") throw new GatewayException("runtime_version");
    }

    private static async Task<string> ReadVersionAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[256];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 1024) result.Append(buffer, 0, Math.Min(count, 1024 - result.Length));
        return result.ToString();
    }

    public async Task StartAsync(GatewayProfile profile, GatewayRole role, PrivateStore store)
    {
        await startup.WaitAsync();
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (process is { HasExited: true })
                {
                    job?.Dispose(); job = null;
                    process.Dispose(); process = null;
                }
                if (process is not null) throw new GatewayException("already_running");
            }
            await StartCoreAsync(profile, role, store);
        }
        finally { startup.Release(); }
    }

    private async Task StartCoreAsync(GatewayProfile profile, GatewayRole role, PrivateStore store)
    {
        await VerifyAsync(profile, role, store);
        var egress = await PhysicalEgress.DetectAsync();
        var next = CreateProcess("-c", Path.Combine(store.DirectoryPath, role.ToString().ToLowerInvariant() + ".toml"));
        egress.Apply(next.StartInfo);
        var nextJob = new ProcessJob();
        try
        {
            next.EnableRaisingEvents = true;
            next.Exited += (_, _) =>
            {
                lock (sync) { if (ReferenceEquals(process, next)) Status = "运行时已退出"; }
                Announce("运行时退出；不会自动重放业务操作。");
            };
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (process is not null) throw new GatewayException("already_running");
                StartOwned(next, nextJob);
                process = next;
                job = nextJob;
                Status = "运行中，等待服务器注册";
                diagnostics = new();
                diagnostics.ConfigureEgress(egress.Adapter?.Index ?? 0);
            }
            _ = DrainOwnedAsync(next);
            Announce("运行时已启动；进程运行不代表直连。");
            Announce(egress.Description);
        }
        catch { nextJob.Dispose(); next.Dispose(); throw; }
    }

    private string Prepare(GatewayProfile profile, GatewayRole role, PrivateStore store)
    {
        var caPath = store.Write("ca.pem", Encoding.UTF8.GetBytes(profile.Server.TrustedCaPem));
        return store.Write(role.ToString().ToLowerInvariant() + ".toml", Encoding.UTF8.GetBytes(FrpConfig.Render(profile, role, caPath)));
    }
    private Process CreateProcess(params string[] arguments)
    {
        if (!File.Exists(RuntimePath) || File.GetAttributes(RuntimePath).HasFlag(FileAttributes.ReparsePoint)) throw new GatewayException("runtime_missing");
        using (var source = File.OpenRead(RuntimePath))
            if (!Convert.ToHexString(SHA256.HashData(source)).Equals(RuntimeIntegrity.WindowsHash, StringComparison.OrdinalIgnoreCase))
                throw new GatewayException("runtime_integrity");
        var info = new ProcessStartInfo(RuntimePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(RuntimePath)!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in new[] { "LINKGATE_BIND_INDEX", "LINKGATE_BIND_IPV4", "LINKGATE_BIND_MAC" }) info.Environment.Remove(key);
        info.Environment["LINKGATE_MULTI_STUN"] = "1";
        return new Process { StartInfo = info };
    }
    private static void StartOwned(Process process, ProcessJob job)
    {
        if (!process.Start()) throw new GatewayException("runtime_start");
        try { job.Assign(process); }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw new GatewayException("runtime_job"); }
    }
    private static async Task DrainBoundedAsync(StreamReader reader)
    {
        // Avoid buffering arbitrary runtime output, including credentials in errors.
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) > 0) { }
    }
    private async Task PumpOwnedAsync(StreamReader reader, Process owner)
    {
        try
        {
            await RuntimeLogReader.PumpAsync(reader, line =>
            {
                lock (sync)
                {
                    // A late pipe read from the stopped generation cannot update
                    // the status or path evidence for a replacement process.
                    if (ReferenceEquals(process, owner)) Observe(line);
                }
            });
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
    private async Task DrainOwnedAsync(Process owner)
    {
        await Task.WhenAll(PumpOwnedAsync(owner.StandardOutput, owner), PumpOwnedAsync(owner.StandardError, owner));
        lock (sync) { if (ReferenceEquals(process, owner)) diagnostics.EndActive(); }
    }
    private void Observe(string line)
    {
        if (line.StartsWith("LINKGATE_STUN ", StringComparison.Ordinal)) { if (diagnostics.ObserveStun(line)) Changed?.Invoke(); return; }
        if (line.StartsWith("LINKGATE_EGRESS ", StringComparison.Ordinal)) { if (diagnostics.ObserveEgress(line)) Changed?.Invoke(); return; }
        if (line.Contains("nathole prepare error", StringComparison.OrdinalIgnoreCase) && line.Contains("not enough addresses", StringComparison.OrdinalIgnoreCase))
        { diagnostics.NatPreparationFailed(); Changed?.Invoke(); return; }
        if (line.StartsWith("LINKGATE_METRIC ", StringComparison.Ordinal))
        {
            if (diagnostics.Observe(line)) Changed?.Invoke();
            return;
        }
        // Retain only classified events, never raw output or arbitrary error text.
        if (line.Contains("login to server success", StringComparison.OrdinalIgnoreCase))
        {
            lock (sync) Status = "服务器注册成功，业务连通待验证";
            Announce("服务器注册成功。");
        }
        else if (line.Contains("start proxy success", StringComparison.OrdinalIgnoreCase)) Announce("服务注册成功。");
        // FRP 0.71.1 emits this before QUICSession.Init. It is only NAT
        // candidate evidence, not a completed application or QUIC connection.
        // The word "fallback" can merely be part of a configured visitor name;
        // even a DEBUG transfer attempt does not prove STCP carried data.
        else if (line.Contains("establishing nat hole connection successful", StringComparison.OrdinalIgnoreCase))
        {
            lock (sync) diagnostics.NatHoleObserved();
            Announce("观察到 NAT 打洞候选成功，业务路径待验证。");
        }
        else if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase)) Announce("运行时报告连接错误；具体路径尚未确认。");
    }
    private void Announce(string message)
    {
        lock (sync)
        {
            events.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + message);
            while (events.Count > 100) events.Dequeue();
        }
        Changed?.Invoke();
    }
    public void Stop()
    {
        Process? old;
        ProcessJob? oldJob;
        lock (sync)
        {
            old = process; oldJob = job; process = null; job = null;
            Status = "已停止"; diagnostics.EndActive();
        }
        oldJob?.Dispose();
        if (old is not null)
        {
            try { if (!old.HasExited && !old.WaitForExit(3000)) old.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            old.Dispose();
        }
        Announce("网关已停止，仅回收本实例持有的运行时。");
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        Stop();
    }
}
