using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;

internal static class PeerCapacityTests
{
    public static async Task Run(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException();
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-peer-capacity-tests")),directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(90));var target=new TcpListener(IPAddress.Loopback,0);var accepted=new ConcurrentBag<TcpClient>();var peers=new ConcurrentBag<PeerConnection>();var eof=new ConcurrentBag<Task<int>>();Task accepting=Task.CompletedTask;
        try
        {
            using var owner=SecureIdentity.CreateOwnerRoot();using var publisher=SecureStreamTests.Identity(Path.Combine(directory,"publisher"),owner);using var visitor=SecureStreamTests.Identity(Path.Combine(directory,"visitor"),owner);
            var store=new RegistrationStore(Path.Combine(directory,"state"));store.BindSecureIdentity(publisher);store.TrustSecurePeer(publisher,visitor.Certificate.ExportCertificatePem(),0,Guid.NewGuid());target.Start();
            var manifest=ManifestContract.Parse(JsonSerializer.SerializeToUtf8Bytes(new{manifestVersion=1,applicationId="org.example.capacity-publisher",displayName="Capacity",exports=new[]{new{serviceId="held",label="Held",protocol="tcp",localTarget=new{host="127.0.0.1",port=((IPEndPoint)target.LocalEndpoint).Port}}},imports=Array.Empty<object>()}));
            Guid app=store.Register("publisher",Guid.NewGuid(),manifest).GetProperty("applicationRef").GetGuid();store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());var grant=store.ApproveSecureGrant(store.ProposeSecureGrant(visitor.DeviceId,"org.example.capacity-client",app,"held"),Guid.NewGuid());
            await using var server=new SecurePeerServer(publisher,store,IPAddress.Loopback,0);server.Start();accepting=Accept();
            var endpoint=new SecurePeer(publisher.DeviceId,publisher.Certificate.ExportCertificatePem(),1,true,"127.0.0.1",server.Port);var scope=new SecureOpenScope("org.example.capacity-client","org.example.capacity-publisher","held","tcp",null);
            using var admission=new SemaphoreSlim(8,8);
            await Task.WhenAll(Enumerable.Range(0,128).Select(async _=>{await admission.WaitAsync(lifetime.Token);try{peers.Add(await PeerConnection.ConnectAsync(visitor,endpoint,scope,lifetime.Token));}finally{admission.Release();}}));
            check(peers.Count==128&&accepted.Count==128,"128 simultaneous authenticated streams release their handshake slots and reach only the approved backend");
            try{await using var excess=await PeerConnection.ConnectAsync(visitor,endpoint,scope,lifetime.Token);throw new IOException("Session limit bypassed.");}catch(PeerRefusalException refused){check(refused.Code=="RESOURCE_EXHAUSTED"&&accepted.Count==128,"the next authenticated session is refused atomically before another backend connection");}
            Task[] blocked=peers.Select(async peer=>{try{if(await peer.Stream.ReadAsync(new byte[1],lifetime.Token)!=0)throw new IOException("Unexpected payload.");}catch(IOException){}}).ToArray();
            var watch=System.Diagnostics.Stopwatch.StartNew();store.RevokeSecureGrant(Guid.Parse(grant.Reference),grant.Revision,Guid.NewGuid());await Task.WhenAll(blocked).WaitAsync(TimeSpan.FromSeconds(5));await Task.WhenAll(eof).WaitAsync(TimeSpan.FromSeconds(5));
            check(watch.ElapsedMilliseconds<5000&&eof.All(item=>item.Result==0),"revocation closes 128 blocked business streams and their backend sockets within five seconds");
            async Task Accept(){try{while(!lifetime.IsCancellationRequested){var socket=await target.AcceptTcpClientAsync(lifetime.Token);accepted.Add(socket);eof.Add(socket.GetStream().ReadAsync(new byte[1],lifetime.Token).AsTask());}}catch(Exception failure)when(failure is OperationCanceledException or SocketException){}}
        }
        finally
        {
            lifetime.Cancel();target.Stop();await accepting;foreach(var peer in peers)await peer.DisposeAsync();foreach(var socket in accepted)socket.Dispose();try{await Task.WhenAll(eof);}catch(Exception failure)when(failure is IOException or OperationCanceledException){}
            string path=Path.GetFullPath(directory);if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup.");if(Directory.Exists(path))Directory.Delete(path,true);
        }
    }
}
