using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinkGate.Processes;

public sealed class OwnedProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle process;
    private readonly FileStream executableLock;
    private bool disposed;
    public uint ProcessId{get;}
    public string StandardOutputPath{get;}
    public string StandardErrorPath{get;}
    private OwnedProcess(SafeFileHandle job,SafeProcessHandle process,FileStream executableLock,uint pid,string output,string error)
    {this.job=job;this.process=process;this.executableLock=executableLock;ProcessId=pid;StandardOutputPath=output;StandardErrorPath=error;}
    public bool HasExited=>WaitForSingleObject(process,0)==0;
    public uint ExitCode{get{if(!HasExited||!GetExitCodeProcess(process,out uint code))throw new InvalidOperationException("Process has not exited.");return code;}}
    public SafeProcessHandle DuplicateProcessHandle()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!DuplicateHandle(GetCurrentProcess(),process,GetCurrentProcess(),out var duplicate,0,false,2)){duplicate?.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}return duplicate;
    }
    public static OwnedProcess Start(string executable,IReadOnlyList<string> arguments,string expectedSha256,string privateOutputDirectory,IReadOnlyDictionary<string,string>? environment=null,string? standardInputFile=null)
    {
        executable=Path.GetFullPath(executable);RejectLinks(executable);privateOutputDirectory=Path.GetFullPath(privateOutputDirectory);MakePrivate(privateOutputDirectory);
        if(expectedSha256.Length!=64||!expectedSha256.All(Uri.IsHexDigit))throw new ArgumentException("Explicit executable digest required.");
        var locked=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read);
        SafeFileHandle? job=null;SafeProcessHandle? child=null;SafeWaitHandle? thread=null;IntPtr attributeList=IntPtr.Zero,handleList=IntPtr.Zero,environmentBlock=IntPtr.Zero;bool initialized=false;
        try
        {
            if(!Convert.ToHexString(SHA256.HashData(locked)).Equals(expectedSha256,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Runtime executable digest mismatch.");
            job=CreateJobObject(IntPtr.Zero,null);if(job.IsInvalid)throw Error();var limits=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000}};IntPtr limitData=Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimits>());
            try{Marshal.StructureToPtr(limits,limitData,false);if(!SetInformationJobObject(job,9,limitData,(uint)Marshal.SizeOf<ExtendedLimits>()))throw Error();}finally{Marshal.FreeHGlobal(limitData);}
            var security=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Inherit=1};
            string output=Path.Combine(privateOutputDirectory,"stdout.log"),error=Path.Combine(privateOutputDirectory,"stderr.log");
            if(standardInputFile is not null){standardInputFile=Path.GetFullPath(standardInputFile);RejectLinks(standardInputFile);}
            using var input=CreateFile(standardInputFile??"NUL",0x80000000,standardInputFile is null?3u:1u,ref security,3,0x80,IntPtr.Zero);
            using var stdout=CreateFile(output,0x40000000,3,ref security,1,0x80,IntPtr.Zero);
            using var stderr=CreateFile(error,0x40000000,3,ref security,1,0x80,IntPtr.Zero);
            if(input.IsInvalid||stdout.IsInvalid||stderr.IsInvalid)throw Error();
            nuint bytes=0;_=InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref bytes);if(bytes==0)throw Error();attributeList=Marshal.AllocHGlobal(checked((int)bytes));
            if(!InitializeProcThreadAttributeList(attributeList,1,0,ref bytes))throw Error();initialized=true;
            handleList=Marshal.AllocHGlobal(IntPtr.Size*3);Marshal.WriteIntPtr(handleList,0,input.DangerousGetHandle());Marshal.WriteIntPtr(handleList,IntPtr.Size,stdout.DangerousGetHandle());Marshal.WriteIntPtr(handleList,IntPtr.Size*2,stderr.DangerousGetHandle());
            if(!UpdateProcThreadAttribute(attributeList,0,new IntPtr(0x20002),handleList,(nuint)(IntPtr.Size*3),IntPtr.Zero,IntPtr.Zero))throw Error();
            string command=string.Join(" ",new[]{executable}.Concat(arguments).Select(Quote));if(command.Length>=32767)throw new ArgumentException("Runtime command exceeds limit.");
            environmentBlock=Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            var startup=new StartupInfoEx{Info=new StartupInfo{Size=Marshal.SizeOf<StartupInfoEx>(),Flags=0x101,Input=input.DangerousGetHandle(),Output=stdout.DangerousGetHandle(),Error=stderr.DangerousGetHandle()},Attributes=attributeList};
            if(!CreateProcess(executable,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,true,0x00080000|0x00000004|0x08000000|0x00000400,environmentBlock,Path.GetDirectoryName(executable),ref startup,out var info))throw Error();
            child=new SafeProcessHandle(info.Process,true);thread=new SafeWaitHandle(info.Thread,true);
            if(!AssignProcessToJobObject(job,child))throw Error();
            var result=new OwnedProcess(job,child,locked,info.Id,output,error);
            if(ResumeThread(thread)==uint.MaxValue)throw Error();job=null;child=null;return result;
        }
        catch{if(child is not null&&!child.IsInvalid)_=TerminateProcess(child,1);child?.Dispose();job?.Dispose();locked.Dispose();throw;}
        finally{thread?.Dispose();if(initialized)DeleteProcThreadAttributeList(attributeList);if(attributeList!=IntPtr.Zero)Marshal.FreeHGlobal(attributeList);if(handleList!=IntPtr.Zero)Marshal.FreeHGlobal(handleList);if(environmentBlock!=IntPtr.Zero)Marshal.FreeHGlobal(environmentBlock);}
    }
    private static string Quote(string value)
    {
        if(value.Contains('\0'))throw new ArgumentException("Invalid runtime argument.");var text=new StringBuilder("\"");int slashes=0;
        foreach(char item in value){if(item=='\\'){slashes++;continue;}if(item=='\"'){text.Append('\\',slashes*2+1).Append(item);slashes=0;continue;}text.Append('\\',slashes).Append(item);slashes=0;}
        return text.Append('\\',slashes*2).Append('"').ToString();
    }
    private static string EnvironmentBlock(IReadOnlyDictionary<string,string>? extra)
    {
        var values=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string key in new[]{"SystemRoot","WINDIR","PATH","TEMP","TMP","USERPROFILE","LOCALAPPDATA","APPDATA","SystemDrive","HOMEDRIVE","HOMEPATH","NUMBER_OF_PROCESSORS","PROCESSOR_ARCHITECTURE"})if(Environment.GetEnvironmentVariable(key) is string value)values[key]=value;
        if(extra is not null)foreach(var pair in extra){if(pair.Key.Length==0||pair.Key.Contains('=')||pair.Key.Contains('\0')||pair.Value.Contains('\0'))throw new ArgumentException("Invalid runtime environment.");values[pair.Key]=pair.Value;}
        string block=string.Join('\0',values.Select(pair=>pair.Key+"="+pair.Value))+"\0\0";if(block.Length>32767)throw new ArgumentException("Runtime environment exceeds limit.");return block;
    }
    private static void MakePrivate(string directory)
    {
        RejectLinks(directory);if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())throw new IOException("New private output directory required.");var info=Directory.CreateDirectory(directory);using var current=WindowsIdentity.GetCurrent();var user=current.User??throw new UnauthorizedAccessException();var acl=new DirectorySecurity();acl.SetOwner(user);acl.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));info.SetAccessControl(acl);
    }
    private static void RejectLinks(string path){for(string? current=path;current is not null;current=Path.GetDirectoryName(current))if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked runtime path rejected.");}
    private static Win32Exception Error()=>new(Marshal.GetLastWin32Error());
    public void Dispose(){if(disposed)return;disposed=true;job.Dispose();_=WaitForSingleObject(process,5000);process.Dispose();executableLock.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo{public int Size;public string? Reserved,Desktop,Title;public int X,Y,XSize,YSize,XCount,YCount,Fill;public int Flags;public ushort Show,ReservedBytes;public IntPtr ReservedData,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)]private struct StartupInfoEx{public StartupInfo Info;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInformation{public IntPtr Process,Thread;public uint Id,ThreadId;}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
    [DllImport("kernel32.dll",EntryPoint="CreateJobObjectW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,IntPtr data,uint length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string name,uint access,uint share,ref SecurityAttributes security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref nuint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr kind,IntPtr value,nuint length,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfoEx startup,out ProcessInformation process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool TerminateProcess(SafeProcessHandle process,uint code);
    [DllImport("kernel32.dll")]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint code);
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DuplicateHandle(IntPtr source,SafeProcessHandle handle,IntPtr target,out SafeProcessHandle duplicate,uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint options);
}
