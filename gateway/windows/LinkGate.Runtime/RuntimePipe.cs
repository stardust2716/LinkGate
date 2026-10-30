using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LinkGate.Runtime;

internal static class RuntimePipe
{
    internal static NamedPipeServerStream Create(string name, string sid, bool first)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{sid})(A;;GA;;;SY)", 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            uint flags = 0x40000003 | (first ? 0x00080000u : 0);
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, flags, 0x8, 64, 32768, 32768, 0, ref attributes);
            if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); } catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    internal static uint Client(NamedPipeServerStream pipe) => GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? pid : throw new UnauthorizedAccessException("Runtime identity unavailable.");
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr result, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint flags, uint mode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
