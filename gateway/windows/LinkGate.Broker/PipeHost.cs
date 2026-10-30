using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinkGate.Broker;

/// <summary>Development per-user control listener with opt-in pending registration; never starts the transport runtime.</summary>
public sealed class PipeHost : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly string ownerSid;
    private readonly ControlProtocol protocol;
    private readonly bool registrationEnabled;
    public string PipeName { get; }

    public PipeHost(string? testSuffix = null, RegistrationStore? registrations = null):this(testSuffix,registrations,false) { }
    public PipeHost(string? testSuffix,RegistrationStore? registrations,bool enablePendingGrants):this(testSuffix,registrations,enablePendingGrants,null){ }
    public PipeHost(string? testSuffix,RegistrationStore? registrations,bool enablePendingGrants,ISecureConnections? secureConnections)
    {
        protocol = new ControlProtocol(registrations,enablePendingGrants,secureConnections);
        registrationEnabled = registrations is not null;
        using var ownerIdentity = WindowsIdentity.GetCurrent();
        ownerSid = ownerIdentity.User?.Value ?? throw new UnauthorizedAccessException();
        if (testSuffix is not null && !Guid.TryParseExact(testSuffix, "N", out _)) throw new ArgumentException("Invalid test suffix.");
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerSid))).ToLowerInvariant();
        PipeName = $"LinkGate.{scope}.control.v1" + (testSuffix is null ? "" : $".test.{testSuffix}");
        pipe=CreateProtectedPipe(PipeName,ownerSid);
    }
    internal static NamedPipeServerStream CreateProtectedPipe(string name,string ownerSid)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{ownerSid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // DUPLEX | OVERLAPPED | FIRST_PIPE_INSTANCE; byte mode | REJECT_REMOTE_CLIENTS.
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080003, 0x8, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await pipe.WaitForConnectionAsync(cancellationToken); }
            catch (IOException e) when ((e.HResult & 0xffff) is 109 or 232 or 233)
            {
                // A client may connect and close before the overlapped accept begins.
                if (!DisconnectNamedPipe(pipe.SafePipeHandle) && Marshal.GetLastWin32Error() != 233)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                continue;
            }
            try
            {
                for (int i = 0; i < 32; i++)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    var input = await FrameCodec.ReadAsync(pipe, deadline.Token);
                    if (input is null) break;
                    string? clientSid = null;
                    pipe.RunAsClient(() =>
                    {
                        using var identity = WindowsIdentity.GetCurrent();
                        clientSid = identity.User?.Value;
                    });
                    if (!StringComparer.Ordinal.Equals(ownerSid, clientSid)) throw new UnauthorizedAccessException();
                    string? caller = registrationEnabled ? CallerIdentity.Capture(pipe, ownerSid) : null;
                    var output = protocol.Dispatch(input, caller);
                    using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                    await FrameCodec.WriteAsync(pipe, output, writeDeadline.Token);
                    if (i == 31)
                    {
                        // DisconnectNamedPipe discards unread output. Allow the peer to
                        // consume the last response before recycling this native instance.
                        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        drain.CancelAfter(TimeSpan.FromSeconds(2));
                        _ = await pipe.ReadAtLeastAsync(new byte[1], 1, throwOnEndOfStream: false, drain.Token);
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException) { }
            // A peer can close after a read completes: IsConnected may already be false
            // while the native instance still needs DisconnectNamedPipe before reuse.
            finally { pipe.Disconnect(); }
        }
    }

    public void Dispose() => pipe.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DisconnectNamedPipe(SafePipeHandle pipe);
}
