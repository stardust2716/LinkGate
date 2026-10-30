using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Processes;
using Microsoft.Win32.SafeHandles;

namespace LinkGate.Runtime;

public enum FrpRuntimeRole { Publish, Access }

public sealed class FrpRuntimeBridge : IAsyncDisposable
{
    private readonly RegistrationStore registrations;
    private readonly SecureIdentity identity;
    private readonly Guid application;
    private readonly long approvedRevision, authorityEpoch;
    private readonly string applicationID, role, serverKey, sid;
    private readonly IPEndPoint server;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly ConcurrentDictionary<long, Task> handling = new();
    private readonly HashSet<NamedPipeServerStream> pipes = [];
    private readonly HashSet<TcpClient> sockets = [];
    private readonly SemaphoreSlim handshakes = new(16);
    private NamedPipeServerStream? accepting;
    private OwnedProcess? child;
    private SafeProcessHandle? process;
    private Task? listener, monitor;
    private long handler;
    private bool stopped;
    private readonly object clockGate = new();
    private long lastTicks = Stopwatch.GetTimestamp();
    private DateTime effectiveUtc = DateTime.UtcNow;
    private string stopReason = "";
    public string StopReason => Volatile.Read(ref stopReason);
    private void Fail(string reason) { Interlocked.CompareExchange(ref stopReason, reason, ""); Stop(); }
    public string PipeName { get; } = "LinkGate.runtime.v1." + Guid.NewGuid().ToString("N");
    public string Namespace { get; }
    public CancellationToken Cancellation => lifetime.Token;
    public bool HasExited => process is not { IsClosed: false, IsInvalid: false } held || WaitForSingleObject(held, 0) == 0;

    private FrpRuntimeBridge(RegistrationStore registrations, SecureIdentity identity, Guid application, FrpRuntimeRole role, IPEndPoint server, string serverKey)
    {
        if (server.AddressFamily != AddressFamily.InterNetwork || server.Port is < 1 or > 65535 || serverKey.Length != 64 || serverKey.Any(value => !(value is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Explicit authenticated IPv4 server required.");
        this.registrations = registrations; this.identity = identity; this.application = application; this.server = new IPEndPoint(server.Address, server.Port); this.serverKey = serverKey;
        this.role = role switch { FrpRuntimeRole.Publish => "publish", FrpRuntimeRole.Access => "access", _ => throw new ArgumentException("Explicit runtime role required.") };
        var review = registrations.GetReview(application);
        if (review.ApprovedRevision is not { } approved || review.ApprovedManifest is not { } manifest || (role == FrpRuntimeRole.Publish ? manifest.Exports.Count == 0 : manifest.Imports.Count == 0)) throw new UnauthorizedAccessException("Approved application required.");
        approvedRevision = approved; authorityEpoch = review.AuthorityEpoch; applicationID = manifest.ApplicationId;
        registrations.BindSecureIdentity(identity);
        SecureIdentity.ValidateDeviceCertificate(identity.Certificate, identity.Root);
        Namespace = "lg1_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("namespace\n" + identity.DeviceId + "\n" + applicationID + "\n" + this.role))).ToLowerInvariant();
        using var current = WindowsIdentity.GetCurrent(); sid = current.User?.Value ?? throw new UnauthorizedAccessException();
        accepting = RuntimePipe.Create(PipeName, sid, true);
    }

    public static FrpRuntimeBridge Start(RegistrationStore registrations, SecureIdentity identity, Guid application, FrpRuntimeRole role, IPEndPoint server, string serverKey, string executable, string sha256, IReadOnlyList<string> arguments, string privateOutputDirectory, IReadOnlyList<IPEndPoint>? stunEndpoints = null, Action<FrpRuntimeBridge>? takeOwnership = null)
    {
        var stun=(stunEndpoints??Array.Empty<IPEndPoint>()).Take(5).Select(endpoint=>endpoint is null?throw new ArgumentException("Explicit IPv4 STUN endpoint required."):new IPEndPoint(new IPAddress(endpoint.Address.GetAddressBytes()),endpoint.Port)).ToArray();
        if(stun.Length>4||stun.Any(endpoint=>!ValidStun(endpoint))||stun.Select(endpoint=>endpoint.ToString()).Distinct(StringComparer.Ordinal).Count()!=stun.Length)throw new ArgumentException("Explicit distinct IPv4 STUN endpoints required.");
        string configuredStun=JsonSerializer.Serialize(stun.Select(endpoint=>new IPEndPoint(endpoint.Address,endpoint.Port).ToString()).ToArray());
        var bridge = new FrpRuntimeBridge(registrations, identity, application, role, server, serverKey);
        try
        {
            // The pipe is reserved first. The process is held in its Job before
            // any accepted pipe can be checked or forwarded to the server.
            takeOwnership?.Invoke(bridge);
            lock(bridge.gate)
            {
            if(bridge.stopped)throw new OperationCanceledException();
            bridge.child = OwnedProcess.Start(executable, arguments, sha256, privateOutputDirectory, new Dictionary<string, string>
            {
                ["LINKGATE_AGENT_PIPE"] = @"\\.\pipe\" + bridge.PipeName,
                ["LINKGATE_AGENT_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["LINKGATE_STUN_ENDPOINTS"] = configuredStun,
                ["HTTP_PROXY"] = "", ["HTTPS_PROXY"] = "", ["ALL_PROXY"] = ""
            });
            bridge.process = bridge.child.DuplicateProcessHandle();
            }
            bridge.listener = bridge.ListenAsync(); bridge.monitor = bridge.MonitorAsync(); return bridge;
        }
        catch { bridge.Stop(); bridge.process?.Dispose(); bridge.lifetime.Dispose(); bridge.handshakes.Dispose(); throw; }
    }

    private static bool ValidStun(IPEndPoint endpoint)
    {
        if(endpoint is null||endpoint.AddressFamily!=AddressFamily.InterNetwork||endpoint.Port is <1 or >65535||endpoint.Address.Equals(IPAddress.Any)||endpoint.Address.Equals(IPAddress.Broadcast))return false;
        byte[] ip=endpoint.Address.GetAddressBytes();return !(ip[0] is >=224 and <=239)&&!(ip[0]==198&&ip[1] is 18 or 19)&&!(ip[0]==169&&ip[1]==254);
    }
    private bool Current()
    {
        DateTime now;
        lock(clockGate){long ticks=Stopwatch.GetTimestamp();DateTime floor=effectiveUtc+Stopwatch.GetElapsedTime(lastTicks,ticks),wall=DateTime.UtcNow;effectiveUtc=wall>floor?wall:floor;lastTicks=ticks;now=effectiveUtc;}
        if (now >= identity.Certificate.NotAfter.ToUniversalTime() || now >= identity.Root.NotAfter.ToUniversalTime()) return false;
        var review = registrations.GetReview(application);
        return review.ApprovedRevision == approvedRevision && review.AuthorityEpoch == authorityEpoch && review.ApprovedManifest?.ApplicationId == applicationID;
    }
    private bool ActualChild(NamedPipeServerStream pipe)
    {
        var held = process;
        if (held is null || held.IsClosed || held.IsInvalid || WaitForSingleObject(held, 0) != 0x102) return false;
        uint expected = GetProcessId(held);
        return expected != 0 && RuntimePipe.Client(pipe) == expected && WaitForSingleObject(held, 0) == 0x102 && RuntimePipe.Client(pipe) == expected;
    }
    private async Task ListenAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                NamedPipeServerStream pending; lock (gate) pending = accepting ?? throw new OperationCanceledException();
                try { await pending.WaitForConnectionAsync(lifetime.Token); }
                catch(IOException error) when((error.HResult&0xffff) is 109 or 232 or 233) {
                    var replacement=RuntimePipe.Create(PipeName,sid,false);
                    lock(gate){if(stopped){replacement.Dispose();pending.Dispose();return;}accepting=replacement;}
                    pending.Dispose();continue;
                }
                // Allocate while the connected instance still reserves the name.
                // No gap allows an unrelated process to replace the namespace.
                var next = RuntimePipe.Create(PipeName, sid, false);
                bool accepted;
                lock (gate)
                {
                    if (stopped) { next.Dispose(); pending.Dispose(); return; }
                    accepting = next; accepted = pipes.Count < 62 && handshakes.Wait(0);
                    if (accepted) pipes.Add(pending);
                }
                if (!accepted) { pending.Dispose(); continue; }
                long id = Interlocked.Increment(ref handler);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); handling[id] = completion.Task;
                _ = HandleTracked(pending, id, completion);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception) { Fail("BRIDGE_FAILED"); }
        finally { Stop(); }
    }
    private async Task HandleTracked(NamedPipeServerStream pipe, long id, TaskCompletionSource completion)
    {
        TcpClient? socket = null; bool opening = true;
        try
        {
            if (!ActualChild(pipe) || !Current()) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
            socket = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            lock (gate) { if (stopped) return; sockets.Add(socket); }
            await socket.ConnectAsync(server.Address, server.Port, deadline.Token);
            using var secure = new SslStream(socket.GetStream(), false);
            var options = identity.ClientOptions(serverKey); options.AllowTlsResume = false; options.ApplicationProtocols = [new SslApplicationProtocol("linkgate-frp/1")];
            await secure.AuthenticateAsClientAsync(options, deadline.Token);
            if (secure.NegotiatedApplicationProtocol != new SslApplicationProtocol("linkgate-frp/1") || !ActualChild(pipe) || !Current()) return;
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { kind = "linkgate.bridge", apiVersion = 1, applicationId = applicationID, role });
            if (body.Length > 4096) throw new IOException("Runtime scope exceeded bound.");
            byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
            await secure.WriteAsync(header, deadline.Token); await secure.WriteAsync(body, deadline.Token); await secure.FlushAsync(deadline.Token);
            handshakes.Release(); opening = false;
            using var copying = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            Task upstream = pipe.CopyToAsync(secure, 32768, copying.Token), downstream = secure.CopyToAsync(pipe, 32768, copying.Token);
            await Task.WhenAny(upstream, downstream);
            copying.Cancel(); socket.Dispose(); pipe.Dispose();
            try { await Task.WhenAll(upstream, downstream); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        catch (Exception error) when (error is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException or ApplicationReviewException) { }
        catch (Exception) { Fail("BRIDGE_FAILED"); }
        finally
        {
            socket?.Dispose(); pipe.Dispose(); if (opening) handshakes.Release();
            lock (gate) { pipes.Remove(pipe); if (socket is not null) sockets.Remove(socket); }
            completion.TrySetResult(); handling.TryRemove(id, out _);
        }
    }
    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(lifetime.Token)) { if (HasExited) { Fail("RUNTIME_EXITED"); break; } if (!Current()) { Fail("AUTHORITY_CHANGED"); break; } }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception) { Fail("BRIDGE_FAILED"); }
        finally { Stop(); }
    }
    public void Stop()
    {
        TcpClient[] remote; NamedPipeServerStream[] local; OwnedProcess? owned;
        lock (gate)
        {
            if (stopped) return; stopped = true; Interlocked.CompareExchange(ref stopReason,"STOPPED",""); accepting?.Dispose(); accepting = null; remote = sockets.ToArray(); local = pipes.ToArray(); owned = child;
        }
        lifetime.Cancel(); foreach (var socket in remote) socket.Dispose(); foreach (var pipe in local) pipe.Dispose(); owned?.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        Stop(); await Task.WhenAll(listener ?? Task.CompletedTask, monitor ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(handling.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)); process?.Dispose(); handshakes.Dispose(); lifetime.Dispose();
    }
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(SafeProcessHandle process);
}
