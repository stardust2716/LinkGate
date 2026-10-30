using LinkGate.Runtime;
using LinkGate.Broker;
using System.Text.Json;

int checks=0;void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
async Task Count(OwnedRuntimePool<string,Resource> pool,int count){using var deadline=new CancellationTokenSource(2000);while(pool.Count!=count){deadline.Token.ThrowIfCancellationRequested();await Task.Yield();}}
async Task Exhausted(Task task){try{await task;throw new Exception("Capacity widened.");}catch(ConnectionRouteException e){Check(e.Code=="RESOURCE_EXHAUSTED","fixed capacity refusal");}}
async Task Cancelled(Task task){try{await task;throw new Exception("Cancelled lease delivered.");}catch(OperationCanceledException){Check(true,"independent waiter cancellation");}}
long now=0;
await using(var pool=new OwnedRuntimePool<string,Resource>(idleMs:60000,clock:()=>now))
{
    var started=Signal();var release=Signal();var value=new Resource();int creations=0;using var cancel=new CancellationTokenSource();
    async Task<Resource> Factory(OwnedRuntimePool<string,Resource>.Creation c){Interlocked.Increment(ref creations);c.Own(value);started.TrySetResult();await release.Task;return value;}
    var first=pool.Acquire("scope",Factory,5000,cancel.Token).AsTask();await started.Task.WaitAsync(TimeSpan.FromSeconds(2));var second=pool.Acquire("scope",Factory,5000,default).AsTask();cancel.Cancel();await Cancelled(first);Check(!value.Stopped&&pool.LeaseCount==1,"waiter does not cancel shared startup");release.TrySetResult();using(var lease=await second){Check(ReferenceEquals(lease.Value,value)&&creations==1,"one exact-scope runtime");}
    now=59999;pool.Maintain(_=>true,_=>true);Check(pool.Count==1&&!value.Stopped,"idle runtime retained before deadline");now=60000;pool.Maintain(_=>true,_=>true);await Count(pool,0);Check(value.Stopped&&value.Disposals==1,"idle expiry disposes owned resource");
}
now=0;
await using(var pool=new OwnedRuntimePool<string,Resource>(1,clock:()=>now))
{
    var value=new Resource{HoldDispose=Signal()};using var lease=await pool.Acquire("old",c=>{c.Own(value);return Task.FromResult(value);},5000,default);
    pool.Maintain(_=>false,_=>true);await value.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));Check(value.Stopped&&pool.Count==1,"revoked live resource retains disposal capacity");await Exhausted(pool.Acquire("new",_=>throw new Exception(),5000,default).AsTask());value.HoldDispose.TrySetResult();await Count(pool,0);
    using var replacement=await pool.Acquire("new",c=>{var v=new Resource();c.Own(v);return Task.FromResult(v);},5000,default);Check(!ReferenceEquals(value,replacement.Value),"retired scope replaced only after cleanup");
}
await using(var pool=new OwnedRuntimePool<string,Resource>(1,clock:()=>now))
{
    var entered=Signal();var release=Signal();var value=new Resource();using var cancel=new CancellationTokenSource();
    var pending=pool.Acquire("late",async c=>{c.Own(value);entered.TrySetResult();await release.Task;return value;},5000,cancel.Token).AsTask();await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));cancel.Cancel();await Cancelled(pending);Check(value.Stopped&&pool.Count==1,"registered early resource stops before startup returns");await Exhausted(pool.Acquire("other",_=>throw new Exception(),5000,default).AsTask());release.TrySetResult();await Count(pool,0);Check(value.Disposals==1,"late startup result disposed once");
}
await using(var pool=new OwnedRuntimePool<string,Resource>(1,clock:()=>now))
{
    var value=new Resource{FailDispose=true};using var lease=await pool.Acquire("retry",c=>{c.Own(value);return Task.FromResult(value);},5000,default);pool.Invalidate();await value.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));Check(pool.Count==1,"failed cleanup retains capacity");value.FailDispose=false;
    using var budget=new CancellationTokenSource(2000);while(pool.Count!=0){budget.Token.ThrowIfCancellationRequested();pool.Maintain(_=>true,_=>true);await Task.Yield();}Check(value.Disposals>=2,"maintenance retries failed cleanup");
}
await using(var pool=new OwnedRuntimePool<string,Resource>(clock:()=>now))
{
    var value=new Resource();using var lease=await pool.Acquire("clock",c=>{c.Own(value);return Task.FromResult(value);},5000,default);now=-1;pool.Maintain(_=>true,_=>true);await Count(pool,0);Check(value.Stopped,"clock rollback retires active runtime");now=0;try{await pool.Acquire("new",_=>throw new Exception(),5000,default);throw new Exception("Closed pool admitted.");}catch(IOException){Check(true,"clock recovery cannot reopen closed pool");}
}
string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException();
string files=Path.GetFullPath(Path.Combine(repo,"artifacts","runtime-owned-files",Guid.NewGuid().ToString("N"))),session=Path.Combine(files,"session");Directory.CreateDirectory(Path.Combine(session,"process"));File.WriteAllBytes(Path.Combine(session,"process","stdout.log"),[]);File.WriteAllBytes(Path.Combine(session,"process","stderr.log"),[]);File.WriteAllText(Path.Combine(session,"profile.json"),"{}");
try{FrpRoutes.DeleteOwnedFiles(files,Path.GetDirectoryName(files)!);throw new Exception("Cleanup escaped root.");}catch(IOException){Check(Directory.Exists(session),"cleanup rejects paths outside explicit root");}
FrpRoutes.DeleteOwnedFiles(files,session);Check(!Directory.Exists(session),"empty owned logs are valid cleanup inputs");Directory.Delete(files,false);
string configRoot=Path.GetFullPath(Path.Combine(repo,"artifacts","runtime-server-settings",Guid.NewGuid().ToString("N")));
using(var owner=SecureIdentity.CreateOwnerRoot())
{
    string csr=SecureIdentity.CreateDeviceRequest(configRoot);string pem=SecureIdentity.IssueDeviceCertificate(owner,csr);using var leaf=System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);string key=SecureIdentity.KeyId(leaf);
    byte[] Settings(string host,string[] endpoints)=>JsonSerializer.SerializeToUtf8Bytes(new{schema=1,host,controlPort=17444,backendPort=17445,serverCertificate=pem,stunEndpoints=endpoints});
    using(var settings=AuthenticatedServerSettings.Parse(Settings("127.0.0.1",["127.0.0.1:3478","127.0.0.1:3479"]),owner,key))Check(settings.ServerKey==key&&settings.StunEndpoints.Count==2,"confirmed server public settings accepted");
    foreach(var input in new[]{Settings("127.1",[]),Settings("127.0.0.1",["127.0.0.1:3478","127.0.0.1:3478"]),Settings("127.0.0.1",["198.18.0.1:3478"])})try{using var invalid=AuthenticatedServerSettings.Parse(input,owner,key);throw new Exception("Invalid settings accepted.");}catch(InvalidDataException){Check(true,"noncanonical or unsafe settings rejected");}
    try{using var invalid=AuthenticatedServerSettings.Parse(Settings("127.0.0.1",[]),owner,new string('0',64));throw new Exception("Unconfirmed server accepted.");}catch(InvalidDataException){Check(true,"server SPKI confirmation is required");}
}
File.Delete(Path.Combine(configRoot,"device-key.pem"));Directory.Delete(configRoot,false);
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks,suite="runtime-ownership",networkTested=false,installedRuntimeChanged=false}));

sealed class Resource:IOwnedRuntime
{
    private int stopped;internal bool Stopped=>stopped!=0;internal int Disposals;internal volatile bool FailDispose;internal TaskCompletionSource? HoldDispose;internal readonly TaskCompletionSource DisposeEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Stop()=>Interlocked.Exchange(ref stopped,1);
    public async ValueTask DisposeAsync(){Interlocked.Increment(ref Disposals);DisposeEntered.TrySetResult();if(HoldDispose is not null)await HoldDispose.Task;if(FailDispose)throw new IOException("Owned fixture cleanup failure.");}
}
