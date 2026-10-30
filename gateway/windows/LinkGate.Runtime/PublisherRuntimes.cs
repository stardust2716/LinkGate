using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;

namespace LinkGate.Runtime;
public sealed class PublisherRuntimes : IAsyncDisposable
{
    private sealed record Published(long Revision,long Epoch,OwnedFrpSession Session,long Started);
    private readonly RegistrationStore store;private readonly SecureIdentity identity;private readonly IPEndPoint server;private readonly string key,executable,digest,root;private readonly IPEndPoint[] stun;private readonly int peerPort;
    private readonly Dictionary<Guid,Published> live=[];private readonly Dictionary<Guid,(int Failures,long Next)> retry=[];private readonly CancellationTokenSource stop=new();private readonly Task work;private int count;private Task? disposal;
    public int RuntimeCount=>Volatile.Read(ref count);
    public PublisherRuntimes(RegistrationStore store,SecureIdentity identity,IPEndPoint server,string serverKey,string executable,string sha256,string privateRoot,int localPeerPort,IReadOnlyList<IPEndPoint>? stunEndpoints=null)
    {if(localPeerPort is <1 or >65535)throw new ArgumentException();this.store=store;this.identity=identity;this.server=server;key=serverKey;this.executable=executable;digest=sha256;root=Path.GetFullPath(privateRoot);peerPort=localPeerPort;stun=(stunEndpoints??[]).ToArray();SecureIdentity.MakePrivate(root);work=Run();}
    private async Task Run()
    {
        try{using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));do{try{await Refresh();}catch(Exception){foreach(var item in live.Values)item.Session.Stop();}}while(await timer.WaitForNextTickAsync(stop.Token));}catch(OperationCanceledException){}
    }
    private async Task Refresh()
    {
        var approved=new Dictionary<Guid,ApplicationReviewSnapshot>();Guid? after=null;
        do{var page=store.ListReviewReferences(after,200);foreach(var id in page.Items){var review=store.GetReview(id);if(review.ApprovedRevision is not null&&review.ApprovedManifest?.Exports.Count>0)approved[id]=review;}after=page.NextCursor;}while(after is not null&&!stop.IsCancellationRequested);
        foreach(var id in live.Keys.ToArray())
        {var prior=live[id];if(!approved.TryGetValue(id,out var review)||review.ApprovedRevision!=prior.Revision||review.AuthorityEpoch!=prior.Epoch||!prior.Session.Alive){try{await prior.Session.DisposeAsync();live.Remove(id);Volatile.Write(ref count,live.Count);}catch(Exception){}}}
        foreach(var id in retry.Keys.Where(id=>!approved.ContainsKey(id)).ToArray())retry.Remove(id);
        foreach(var pair in approved.OrderBy(x=>x.Key))
        {
            if(stop.IsCancellationRequested||live.Count>=16)break;if(live.TryGetValue(pair.Key,out var prior)){if(OwnedRuntimePool<string,OwnedFrpSession>.Now()-prior.Started>30000)retry.Remove(pair.Key);continue;}long now=OwnedRuntimePool<string,OwnedFrpSession>.Now();if(retry.TryGetValue(pair.Key,out var delay)&&now<delay.Next)continue;int failures=Math.Min(6,delay.Failures+1);retry[pair.Key]=(failures,now+Math.Min(30000,1000L<<(failures-1)));var review=pair.Value;var app=review.ApprovedManifest!;var session=new OwnedFrpSession(root);
            try
            {
                session.Prepare();string ns=Namespace(identity.DeviceId,app.ApplicationId,"publish");var proxies=new List<object>();foreach(var export in app.Exports)
                {string name="lg1_"+Hash("service\n"+identity.DeviceId+"\n"+app.ApplicationId+"\n"+export.ServiceId);foreach(string kind in stun.Length>=2?new[]{"stcp","xtcp"}:new[]{"stcp"})proxies.Add(new{name=name+"_"+kind,type=kind,secretKey=ns+"."+name+"_"+kind,allowUsers=new[]{"*"},localIP="127.0.0.1",localPort=peerPort,metadatas=new Dictionary<string,string>{{"linkgate.service",export.ServiceId}}});}
                SecureIdentity.WriteNew(session.Profile,JsonSerializer.Serialize(new{user=ns,clientID=ns+"."+Guid.NewGuid().ToString("N"),serverAddr="127.0.0.1",serverPort=1,natHoleStunServer=stun.Length>=2?stun[0].ToString():"127.0.0.1:1",loginFailExit=true,transport=new{protocol="tcp",wireProtocol="v2",tcpMux=true,heartbeatInterval=1,heartbeatTimeout=15},log=new{to="console",level="error"},proxies}));
                if(stop.IsCancellationRequested){await session.DisposeAsync();break;}
                FrpRuntimeBridge.Start(store,identity,pair.Key,FrpRuntimeRole.Publish,server,key,executable,digest,["-c",session.Profile],session.Output,stun,session.OwnBridge);live[pair.Key]=new(review.ApprovedRevision!.Value,review.AuthorityEpoch,session,now);Volatile.Write(ref count,live.Count);
            }catch(Exception){try{await session.DisposeAsync();}catch(Exception){live[pair.Key]=new(review.ApprovedRevision!.Value,review.AuthorityEpoch,session,now);Volatile.Write(ref count,live.Count);}}
        }
    }
    public ValueTask DisposeAsync(){lock(live)return new ValueTask(disposal??=DisposeOwned());}
    private async Task DisposeOwned(){stop.Cancel();try{await work;foreach(var published in live.Values)await published.Session.DisposeAsync();live.Clear();Volatile.Write(ref count,0);}finally{stop.Dispose();}}
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Namespace(string device,string app,string role)=>"lg1_"+Hash("namespace\n"+device+"\n"+app+"\n"+role);
}
