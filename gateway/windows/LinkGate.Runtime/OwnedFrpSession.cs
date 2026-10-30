using LinkGate.Broker;
namespace LinkGate.Runtime;

internal class OwnedFrpSession : IOwnedRuntime
{
    private readonly object gate=new();private readonly string root,directory;private readonly CancellationTokenSource stop=new();internal readonly CancellationToken Token;
    private FrpRuntimeBridge? bridge;private bool closed,disposed;
    internal string Profile=>Path.Combine(directory,"profile.json");internal string Output=>Path.Combine(directory,"process");
    internal OwnedFrpSession(string root){this.root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));directory=Path.Combine(this.root,Guid.NewGuid().ToString("N"));Token=stop.Token;}
    internal void Prepare(){lock(gate){if(closed)throw new OperationCanceledException();if(Directory.Exists(directory))throw new IOException("New runtime directory required.");SecureIdentity.MakePrivate(directory);}}
    internal void OwnBridge(FrpRuntimeBridge value){lock(gate){bridge=value;if(closed)value.Stop();}}
    internal bool Alive{get{lock(gate)return !closed&&bridge is not null&&!bridge.HasExited&&!bridge.Cancellation.IsCancellationRequested;}}
    public void Stop(){FrpRuntimeBridge? owned;lock(gate){if(closed)return;closed=true;owned=bridge;}try{owned?.Stop();}finally{try{stop.Cancel();}catch(AggregateException){}}}
    public async ValueTask DisposeAsync()
    {
        Stop();lock(gate){if(disposed)return;}if(bridge is not null)await bridge.DisposeAsync();
        DeleteOwnedFiles(root,directory);
        lock(gate){disposed=true;stop.Dispose();}
    }
    internal static void DeleteOwnedFiles(string root,string directory)
    {
        string absolute=Path.GetFullPath(directory),parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));if(!absolute.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid owned cleanup.");
        for(var item=new DirectoryInfo(Path.Combine(absolute,"process"));item is not null;item=item.Parent)if(item.Exists&&(item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked runtime directory.");
        string output=Path.Combine(absolute,"process");foreach(string file in new[]{Path.Combine(output,"stdout.log"),Path.Combine(output,"stderr.log"),Path.Combine(absolute,"profile.json")})if(File.Exists(file)){if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked owned file.");File.Delete(file);}
        if(Directory.Exists(output))Directory.Delete(output,false);if(Directory.Exists(absolute))Directory.Delete(absolute,false);
    }
}
