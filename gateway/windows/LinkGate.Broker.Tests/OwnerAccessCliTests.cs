using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;

internal static class OwnerAccessCliTests
{
    public static async Task Run(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException();
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","owner-access-cli-tests")),directory=Path.Combine(root,Guid.NewGuid().ToString("N")),state=Path.Combine(directory,"state");
        string executable=Path.Combine(repo,"gateway/windows/LinkGate.Owner.Console/bin/Release/net10.0-windows/LinkGate.Owner.Console.exe");
        async Task<(int Exit,string Output)> Execute(params string[] args)
        {
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in args)start.ArgumentList.Add(arg);using var process=Process.Start(start)??throw new IOException();var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));string text=await output;check(!(await error).Contains("private-marker",StringComparison.Ordinal),"console does not reflect rejected input");return(process.ExitCode,text);
        }
        try
        {
            using var owner=SecureIdentity.CreateOwnerRoot();using var identity=SecureStreamTests.Identity(Path.Combine(directory,"identity"),owner);using var peer=SecureStreamTests.Identity(Path.Combine(directory,"peer"),owner);var store=new RegistrationStore(state);store.BindSecureIdentity(identity);store.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),0,Guid.NewGuid());
            var manifest=ManifestContract.Parse(Encoding.UTF8.GetBytes("""{"manifestVersion":1,"applicationId":"org.example.cli","displayName":"CLI","exports":[],"imports":[{"alias":"files","protocol":"tcp","endpointMode":"brokered","connectionPolicy":{"mode":"direct-only","allowRelay":false}}]}"""));
            Guid app=store.Register("caller",Guid.NewGuid(),manifest).GetProperty("applicationRef").GetGuid();store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
            Guid request=store.RequestPendingGrant("caller",Guid.NewGuid(),new PendingGrantRequest(app,"files",peer.DeviceId,"org.example.publisher","files")).GetProperty("grantRequestId").GetGuid();
            var listing=await Execute("access-requests",state);using(var doc=JsonDocument.Parse(listing.Output))check(listing.Exit==0&&doc.RootElement.GetProperty("Items")[0].GetGuid()==request,"owner finds retained permission requests");
            string proposal=Path.Combine(directory,"proposal.json");var proposed=await Execute("access-propose",state,request.ToString(),proposal);check(proposed.Exit==0&&File.Exists(proposal),"explicit command exports current proposal");
            Guid operation=Guid.NewGuid();var approved=await Execute("access-approve",state,proposal,operation.ToString());using var result=JsonDocument.Parse(approved.Output);Guid grant=Guid.Parse(result.RootElement.GetProperty("Reference").GetString()!);check(approved.Exit==0&&result.RootElement.GetProperty("Active").GetBoolean(),"console approves exact local visitor grant");
            var replay=await Execute("access-approve",state,proposal,operation.ToString());check(replay.Exit==0&&replay.Output==approved.Output,"original operation replay does not create another grant revision");
            var shown=await Execute("access-show",state,grant.ToString());using(var doc=JsonDocument.Parse(shown.Output))check(shown.Exit==0&&doc.RootElement.GetProperty("Current").GetBoolean()&&doc.RootElement.GetProperty("Revision").GetInt64()==1,"owner can inspect exact live permission");
            store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());shown=await Execute("access-show",state,grant.ToString());using(var doc=JsonDocument.Parse(shown.Output))check(shown.Exit==0&&!doc.RootElement.GetProperty("Current").GetBoolean()&&doc.RootElement.GetProperty("Active").GetBoolean(),"stale active grant remains inspectable for cleanup");
            listing=await Execute("access-list",state);using(var doc=JsonDocument.Parse(listing.Output))check(listing.Exit==0&&doc.RootElement.GetProperty("Items")[0].GetGuid()==grant,"owner can enumerate stale grants without old operation IDs");
            var revoked=await Execute("access-revoke",state,grant.ToString(),"1",Guid.NewGuid().ToString());check(revoked.Exit==0&&!store.GetLocalAccess(grant).Active,"owner retires stale permission and its reservation");
            File.WriteAllText(proposal,File.ReadAllText(proposal).TrimEnd().TrimEnd('}')+",\"private-marker\":true}");var bad=await Execute("access-approve",state,proposal,Guid.NewGuid().ToString());check(bad.Exit!=0,"unknown proposal fields are rejected");
        }
        finally{string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException();if(Directory.Exists(target))Directory.Delete(target,true);}
    }
}
