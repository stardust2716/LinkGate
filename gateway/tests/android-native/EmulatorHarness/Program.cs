using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LinkGate.Processes;

if(args.Length!=2){Console.Error.WriteLine("Usage: EmulatorHarness <android-sdk> <owned-artifacts-directory>");return 2;}
string sdk=Path.GetFullPath(args[0]),directory=Path.GetFullPath(args[1]);
string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository missing.");
string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-emulator"));
if(!directory.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Fixture directory outside owned artifacts.");
foreach(int port in new[]{5038,5556,5557}){var probe=new TcpListener(IPAddress.Loopback,port);try{probe.Start();}finally{probe.Stop();}}
string adb=Path.Combine(sdk,"platform-tools","adb.exe"),emulator=Path.Combine(sdk,"emulator","emulator.exe");
string Digest(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
var environment=new Dictionary<string,string>{{"ANDROID_HOME",sdk},{"ANDROID_AVD_HOME",Path.Combine(root,"avd")},{"ANDROID_USER_HOME",Path.Combine(root,"user")},{"ANDROID_ADB_SERVER_PORT","5038"},{"ADB_SERVER_SOCKET","tcp:5038"},{"ADB_MDNS_AUTO_CONNECT","0"}};
Directory.CreateDirectory(directory);
using var adbServer=OwnedProcess.Start(adb,["-P","5038","--one-device","LINKGATE-EMULATOR-ONLY","nodaemon","server"],Digest(adb),Path.Combine(directory,"adb"),environment);
using(var startup=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    while(true)
    {
        if(adbServer.HasExited)throw new IOException("Isolated ADB server failed; inspect owned stderr.");
        try{using var check=new TcpClient();await check.ConnectAsync(IPAddress.Loopback,5038,startup.Token);break;}
        catch(SocketException){await Task.Delay(100,startup.Token);}
    }
}
using var guest=OwnedProcess.Start(emulator,["-avd","linkgate_native_36","-ports","5556,5557","-no-window","-no-audio","-no-boot-anim","-no-snapshot","-no-direct-adb","-adb-path",adb,"-gpu","swiftshader","-camera-back","none","-camera-front","none","-memory","2048","-cores","2"],Digest(emulator),Path.Combine(directory,"emulator"),environment);
string ready=JsonSerializer.Serialize(new{owned=true,ownerPid=Environment.ProcessId,adbPort=5038,serial="emulator-5556",headless=true,guestPid=guest.ProcessId,adbPid=adbServer.ProcessId,directory});File.WriteAllText(Path.Combine(directory,"ready.json"),ready);Console.WriteLine(ready);
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(30));
try{while(!File.Exists(Path.Combine(directory,"stop"))&&!deadline.IsCancellationRequested){if(guest.HasExited||adbServer.HasExited)throw new IOException("Owned emulator or ADB exited unexpectedly.");await Task.Delay(500,deadline.Token);}}
catch(OperationCanceledException)when(deadline.IsCancellationRequested){ }
return 0;
