using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LinkGate.Processes;

string executable=Environment.ProcessPath??throw new IOException("Fixture executable missing.");
string Digest(){using var input=File.OpenRead(executable);return Convert.ToHexString(SHA256.HashData(input));}
if(args.Length>0&&args[0]=="--echo"){Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));return 0;}
if(args.Length>0&&args[0]=="--stdin"){Console.Write(Console.In.ReadToEnd());return 0;}
if(args.Length>0&&args[0]=="--wait"){await Task.Delay(Timeout.Infinite);return 0;}
if(args.Length>0&&args[0]=="--spawn")
{
    var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--wait");using var child=Process.Start(start)??throw new IOException();Console.WriteLine(child.Id);await Task.Delay(Timeout.Infinite);return 0;
}
if(args.Length==2&&args[0]=="--owner")
{
    using var child=OwnedProcess.Start(executable,["--wait"],Digest(),args[1]);Console.WriteLine(child.ProcessId);await Task.Delay(Timeout.Infinite);return 0;
}
string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository missing.");
string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-owned-process-tests")),directory=Path.Combine(root,Guid.NewGuid().ToString("N"));int checks=0;
void Check(bool value,string label){if(!value)throw new IOException(label);checks++;}
async Task Wait(Func<bool> ready){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!ready())await Task.Delay(20,deadline.Token);}
string Read(string file){using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(input);return reader.ReadToEnd();}
try
{
    string[] arguments=["","a b",@"c:\folder\","a\"b","$()`&|>","中文"];
    using(var child=OwnedProcess.Start(executable,new[]{"--echo"}.Concat(arguments).ToArray(),Digest(),Path.Combine(directory,"arguments")))
    {
        await Wait(()=>child.HasExited);Check(child.ExitCode==0,"Child failed.");var observed=JsonSerializer.Deserialize<string[]>(Read(child.StandardOutputPath));Check(observed is not null&&observed.SequenceEqual(arguments),"Native argument quoting changed content.");
    }
    try{using var denied=OwnedProcess.Start(executable,["--wait"],new string('0',64),Path.Combine(directory,"wrong-digest"));throw new IOException("Wrong digest accepted.");}catch(UnauthorizedAccessException){Check(true,"Wrong digest denied.");}
    string inputPath=Path.Combine(directory,"input.txt"),inputValue="fixture input\n\"quoted\" 中文 $()`&|>";File.WriteAllText(inputPath,inputValue,new System.Text.UTF8Encoding(false));
    using(var child=OwnedProcess.Start(executable,["--stdin"],Digest(),Path.Combine(directory,"input"),standardInputFile:inputPath)){await Wait(()=>child.HasExited);Check(child.ExitCode==0&&Read(child.StandardOutputPath)==inputValue,"Explicit standard input changed content.");}
    Process grandchild;
    using(var child=OwnedProcess.Start(executable,["--spawn"],Digest(),Path.Combine(directory,"tree")))
    {
        int pid=0;await Wait(()=>int.TryParse(Read(child.StandardOutputPath).Trim(),out pid));grandchild=Process.GetProcessById(pid);Check(Path.GetFullPath(grandchild.MainModule!.FileName)==Path.GetFullPath(executable),"Unexpected descendant executable.");Check(!grandchild.HasExited,"Descendant exited before cleanup.");
    }
    using(grandchild){await Wait(()=>grandchild.HasExited);Check(true,"Explicit Job disposal closed descendant.");}
    var ownerStart=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};ownerStart.ArgumentList.Add("--owner");ownerStart.ArgumentList.Add(Path.Combine(directory,"owner-crash"));
    using(var owner=Process.Start(ownerStart)??throw new IOException("Owner fixture launch failed."))
    {
        try
        {
            string? line=await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));if(!int.TryParse(line,out int pid))throw new IOException("Owner did not report held child.");using var held=Process.GetProcessById(pid);Check(Path.GetFullPath(held.MainModule!.FileName)==Path.GetFullPath(executable),"Unexpected owned child.");
            owner.Kill();await owner.WaitForExitAsync();await Wait(()=>held.HasExited);Check(true,"Owner crash closed kernel Job child.");
        }
        finally{if(!owner.HasExited){owner.Kill(true);await owner.WaitForExitAsync();}}
    }
}
finally
{
    string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup path.");
    // Job termination is asynchronous for descendants; process exit can precede final kernel file-handle release.
    using var cleanupDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while(Directory.Exists(target))
    {
        try{Directory.Delete(target,true);}
        catch(IOException){await Task.Delay(25,cleanupDeadline.Token);}
    }
}
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks,actualChildren=true,argumentContentPreserved=true,explicitJobCleanup=true,ownerCrashCleanup=true,unrelatedProcessesTouched=false}));return 0;
