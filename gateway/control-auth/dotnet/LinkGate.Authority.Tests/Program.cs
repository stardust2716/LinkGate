using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinkGate.Authority;
using LinkGate.Control;
using LinkGate.Processes;

if(args.Length!=3){Console.Error.WriteLine("Usage: Authority.Tests <source-built-issuer.exe> <java.exe> <trusted-interop-classpath>");return 2;}
string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException("Repository missing.");
string root=Path.GetFullPath(Path.Combine(repo,"artifacts","server-authority-interop")),directory=Path.Combine(root,Guid.NewGuid().ToString("N")),data=Path.Combine(directory,"data");int checks=0,javaChecks=0;
string Digest(string executable){using var file=File.OpenRead(executable);return Convert.ToHexString(SHA256.HashData(file));}
string Read(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(file);return reader.ReadToEnd();}
void Check(bool value,string label){if(!value)throw new IOException(label);checks++;}
async Task Complete(OwnedProcess child){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(30));while(!child.HasExited)await Task.Delay(20,limit.Token);if(child.ExitCode!=0){string diagnostic=Read(child.StandardErrorPath).Trim();if(!System.Text.RegularExpressions.Regex.IsMatch(diagnostic,"^authority-stage=[a-z-]+;failure=[A-Za-z]+\\z"))diagnostic="fixed-child-failure";throw new IOException("Owned interoperability child failed: "+diagnostic);}}
async Task Denied(Func<Task> work){try{await work();throw new IOException("Invalid lease accepted.");}catch(LeaseRejectedException failure){Check(failure.InnerException is null&&failure.Code=="AUTH_REQUIRED","Lease error leaked raw details.");}}
try
{
    string issuerExecutable=Path.GetFullPath(args[0]),javaExecutable=Path.GetFullPath(args[1]);using(var producer=OwnedProcess.Start(issuerExecutable,[data],Digest(issuerExecutable),data))await Complete(producer);
    using var certificate=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data,"server-cert.pem")));using var owner=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data,"owner-root.pem")));
    using var metadata=ControlJson.ReadDocument(File.ReadAllBytes(Path.Combine(data,"metadata.json")));string issuer=metadata.RootElement.GetProperty("issuer").GetString()!;Guid epoch=metadata.RootElement.GetProperty("epoch").GetGuid();DateTimeOffset issued=DateTimeOffset.FromUnixTimeSeconds(metadata.RootElement.GetProperty("issuedUnix").GetInt64());
    byte[] snapshotBytes=File.ReadAllBytes(Path.Combine(data,"snapshot.json"));var current=AuthoritySnapshot.Parse(snapshotBytes);string first=File.ReadAllText(Path.Combine(data,"first.jwt")),next=File.ReadAllText(Path.Combine(data,"next.jwt"));
    using var missingSan=X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data,"missing-san-cert.pem")));
    var liveClock=new LeaseClock();var verified=await ServerLease.VerifyAsync(first,certificate,owner,issuer,epoch,current,liveClock);Check(verified.Active&&verified.Authority==current,"Go-issued ES256 lease did not verify.");
    await Denied(async()=>{_=await ServerLease.VerifyAsync(first,missingSan,owner,issuer,epoch,current,liveClock);});
    await Denied(async()=>{_=await ServerLease.VerifyAsync(first,certificate,owner,new string('0',64),epoch,current,liveClock);});await Denied(async()=>{_=await ServerLease.VerifyAsync(first,certificate,owner,issuer,Guid.NewGuid(),current,liveClock);});
    JsonNode changed=JsonNode.Parse(snapshotBytes)!;changed["grant"]!["scope"]!["visitorApp"]="org.example.other";var other=AuthoritySnapshot.Parse(JsonSerializer.SerializeToUtf8Bytes(changed));await Denied(async()=>{_=await ServerLease.VerifyAsync(first,certificate,owner,issuer,epoch,other,liveClock);});
    using(var invalid=ControlJson.ReadDocument(File.ReadAllBytes(Path.Combine(data,"invalid.json"))))foreach(var test in invalid.RootElement.EnumerateObject())await Denied(async()=>{_=await ServerLease.VerifyAsync(test.Value.GetString()!,certificate,owner,issuer,epoch,current,liveClock);});
    var fake=new FixtureClock(issued);var clock=new LeaseClock(fake);var window=new LeaseWindow();var original=await ServerLease.VerifyAsync(first,certificate,owner,issuer,epoch,current,clock);Check(ReferenceEquals(window.Accept(original),original),"Initial window refused.");fake.Advance(TimeSpan.FromSeconds(60));var replay=await ServerLease.VerifyAsync(first,certificate,owner,issuer,epoch,current,clock);Check(ReferenceEquals(window.Accept(replay),original),"Replay replaced original receipt.");
    var higher=await ServerLease.VerifyAsync(next,certificate,owner,issuer,epoch,current,clock);window.Accept(higher);fake.Advance(TimeSpan.FromSeconds(61));await Denied(()=>{_=window.Current;return Task.CompletedTask;});await Denied(async()=>{_=await ServerLease.VerifyAsync(next,certificate,owner,issuer,epoch,current,clock);});
    fake=new FixtureClock(issued);clock=new LeaseClock(fake);window=new LeaseWindow();original=await ServerLease.VerifyAsync(first,certificate,owner,issuer,epoch,current,clock);window.Accept(original);fake.Advance(TimeSpan.FromSeconds(10));Check(window.Current.Active,"Valid elapsed receipt stopped.");fake.Advance(TimeSpan.FromSeconds(-1));await Denied(()=>{_=window.Current;return Task.CompletedTask;});fake.Advance(TimeSpan.FromSeconds(2));await Denied(()=>{_=window.Current;return Task.CompletedTask;});
    fake=new FixtureClock(issued);clock=new LeaseClock(fake);window=new LeaseWindow();original=await ServerLease.VerifyAsync(first,certificate,owner,issuer,epoch,current,clock);window.Accept(original);window.Close();await Denied(()=>{window.Accept(original);return Task.CompletedTask;});
    using(var java=OwnedProcess.Start(javaExecutable,["-cp",args[2],"dev.linkgate.interop.AuthorityClient",data],Digest(javaExecutable),Path.Combine(directory,"java"))) {await Complete(java);using var observed=JsonDocument.Parse(Read(java.StandardOutputPath));javaChecks=observed.RootElement.GetProperty("checks").GetInt32();Check(observed.RootElement.GetProperty("passed").GetBoolean()&&javaChecks>=15,"Java lease conformance did not complete.");}
}
finally
{
    string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup.");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(Directory.Exists(target)){try{Directory.Delete(target,true);}catch(IOException){await Task.Delay(25,timeout.Token);}}
}
string evidence=JsonSerializer.Serialize(new{passed=true,dotnetChecks=checks,javaChecks,goIssuedActualEs256=true,dotnetAndJavaVerified=true,signedMalformedRejected=true,stalledWallClockBounded=true,monotonicRollbackClosed=true,lateRepliesClosed=true,privateKeyExported=false,physicalDevice=false,frpIntegrated=false,cloudAccessed=false});File.WriteAllText(Path.Combine(repo,"artifacts","server-authority-interop-summary.json"),evidence);Console.WriteLine(evidence);return 0;

sealed class FixtureClock(DateTimeOffset utc):TimeProvider
{
    private long timestamp;
    public override DateTimeOffset GetUtcNow()=>utc;
    public override long GetTimestamp()=>timestamp;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed)=>timestamp+=elapsed.Ticks;
}
