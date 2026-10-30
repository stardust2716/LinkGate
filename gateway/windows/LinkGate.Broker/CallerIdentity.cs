using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinkGate.Broker;

internal static class CallerIdentity
{
    internal static string Capture(NamedPipeServerStream pipe, string sid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw Denied();
        using var process = OpenProcess(0x101000, false, pid);
        if (process.IsInvalid || WaitForSingleObject(process, 0) != 0x102 ||
            !OpenProcessToken(process, 8, out var token)) throw Denied();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User?.Value != sid) throw Denied();
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length) || length == 0 ||
            !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint confirmed) || confirmed != pid ||
            WaitForSingleObject(process, 0) != 0x102) throw Denied();
        string image = Path.GetFullPath(path.ToString()).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "\n" + image))).ToLowerInvariant();
    }

    private static UnauthorizedAccessException Denied() => new("Caller identity unavailable.");
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
}
