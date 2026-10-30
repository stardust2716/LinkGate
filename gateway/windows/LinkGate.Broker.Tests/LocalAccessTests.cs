using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

internal static class LocalAccessTests
{
    private const string Manifest="""{"manifestVersion":1,"applicationId":"org.example.visitor","displayName":"Visitor","exports":[],"imports":[{"alias":"files","protocol":"tcp","endpointMode":"brokered","connectionPolicy":{"mode":"direct-first","directWaitMs":1500,"allowRelay":true}}]}""";
    private static ConnectionOpenRequest Open(Guid app,string peer,string alias="files",string publisher="org.example.publisher",string service="files",string policy="{\"mode\":\"direct-only\",\"allowRelay\":false}")
        =>ConnectionOpenRequest.Parse(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{applicationRef=app,importAlias=alias,deviceId=peer,publisherAppId=publisher,remoteServiceId=service,connectionPolicy=JsonDocument.Parse(policy).RootElement})));
    public static void Run(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException();
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-local-access-tests")),directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            using var owner=SecureIdentity.CreateOwnerRoot();using var identity=SecureStreamTests.Identity(Path.Combine(directory,"identity"),owner);using var peer=SecureStreamTests.Identity(Path.Combine(directory,"peer"),owner);
            string state=Path.Combine(directory,"state");var store=new RegistrationStore(state);store.BindSecureIdentity(identity);store.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),0,Guid.NewGuid(),"127.0.0.1",17443);
            Guid app=store.Register("caller",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest))).GetProperty("applicationRef").GetGuid();
            store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
            var request=Open(app,peer.DeviceId);Refuse("AUTH_REQUIRED",()=>store.ResolveLocalAccess("caller",request),check);
            Guid pending=store.RequestPendingGrant("caller",Guid.NewGuid(),new PendingGrantRequest(app,"files",peer.DeviceId,"org.example.publisher","files")).GetProperty("grantRequestId").GetGuid();
            var proposal=store.ProposeLocalAccess(pending);Refuse("AUTH_REQUIRED",()=>store.ResolveLocalAccess("caller",request),check);
            check(proposal.CallerScope=="caller"&&proposal.ApplicationRef==app&&proposal.DeviceId==peer.DeviceId&&proposal.GrantId is null,"proposal binds real retained caller and exact target without authorizing traffic");
            Refuse("REVIEW_STALE",()=>store.ApproveLocalAccess(proposal with{ServiceId="other"},Guid.NewGuid()),check);
            Guid operation=Guid.NewGuid();var grant=store.ApproveLocalAccess(proposal,operation);var scope=store.ResolveLocalAccess("caller",request);
            check(scope.AccessReference.ToString()==grant.Reference&&scope.AccessRevision==1&&store.LocalAccessCurrent([scope])[0],"explicit host grant activates only its exact visitor scope");
            foreach(var invalid in new[]{Open(Guid.NewGuid(),peer.DeviceId),Open(app,new string('a',64)),Open(app,peer.DeviceId,alias:"other"),Open(app,peer.DeviceId,publisher:"org.example.other"),Open(app,peer.DeviceId,service:"other")})Refuse("AUTH_REQUIRED",()=>store.ResolveLocalAccess("caller",invalid),check);
            Refuse("AUTH_REQUIRED",()=>store.ResolveLocalAccess("other",request),check);
            Refuse("PERMISSION_DENIED",()=>store.ResolveLocalAccess("caller",Open(app,peer.DeviceId,policy:"{\"mode\":\"fast-connect\",\"directWaitMs\":0,\"allowRelay\":true}")),check);
            store=new RegistrationStore(state);check(store.GetSecureAuthorityOperation(operation)==grant&&store.ApproveLocalAccess(proposal,operation)==grant&&store.LocalAccessCurrent([scope])[0],"restart retains original approval and live exact scope");
            Refuse("OPERATION_CONFLICT",()=>store.ApproveLocalAccess(proposal with{ServiceId="other"},operation),check);
            var stale=store.ProposeLocalAccess(pending);store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Denied,Guid.NewGuid());
            check(store.LocalAccessCurrent([scope])[0],"denial-only review preserves approved visitor authority");Refuse("REVIEW_STALE",()=>store.ApproveLocalAccess(stale,Guid.NewGuid()),check);
            store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());check(!store.LocalAccessCurrent([scope])[0],"explicit reapproval invalidates old visitor grant stamps");
            Guid next=store.RequestPendingGrant("caller",Guid.NewGuid(),new PendingGrantRequest(app,"files",peer.DeviceId,"org.example.publisher","files")).GetProperty("grantRequestId").GetGuid();var nextProposal=store.ProposeLocalAccess(next);
            var capacity=store.GetJournalCapacity();Sql(state,"CREATE TRIGGER fail_access BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-marker'); END");
            Refuse("STORAGE_UNAVAILABLE",()=>store.ApproveLocalAccess(nextProposal,Guid.NewGuid()),check);check(store.GetJournalCapacity()==capacity&&!store.LocalAccessCurrent([scope])[0],"failed retained result rolls back activation and reservation together");Sql(state,"DROP TRIGGER fail_access");
            var renewed=store.ApproveLocalAccess(nextProposal,Guid.NewGuid());var live=store.ResolveLocalAccess("caller",request);
            check(renewed.Reference==grant.Reference&&renewed.Revision==2&&!store.LocalAccessCurrent([scope])[0]&&store.LocalAccessCurrent([live])[0],"explicit renewal advances grant without reviving an old active snapshot");
            capacity=store.GetJournalCapacity();store=new RegistrationStore(state,checked((int)(capacity.RetainedHostOperations+capacity.ReservedRevocations)),checked((int)(capacity.RetainedOperations+capacity.ReservedRevocations)),1024);
            check(store.GetJournalCapacity().AvailableGlobalOperations==0,"full logical journal retains visitor revocation reservation");
            var reserved=store.GetJournalCapacity();Sql(state,"CREATE TRIGGER fail_revoke BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-marker'); END");
            Refuse("STORAGE_UNAVAILABLE",()=>store.RevokeLocalAccess(Guid.Parse(renewed.Reference),renewed.Revision,Guid.NewGuid()),check);
            check(store.GetJournalCapacity()==reserved&&store.LocalAccessCurrent([live])[0],"failed full-budget revocation preserves live authority and its reserved slot");Sql(state,"DROP TRIGGER fail_revoke");Guid revokeOp=Guid.NewGuid();var revoked=store.RevokeLocalAccess(Guid.Parse(renewed.Reference),renewed.Revision,revokeOp);
            check(!revoked.Active&&revoked.AffectedGrants==1&&!store.LocalAccessCurrent([live])[0]&&store.GetJournalCapacity().AvailableGlobalOperations==0,"visitor revocation succeeds at full capacity using its own reservation");
            check(store.RevokeLocalAccess(Guid.Parse(renewed.Reference),renewed.Revision,revokeOp)==revoked&&store.ApproveLocalAccess(proposal,operation)==grant&&!store.LocalAccessCurrent([live])[0],"old approval and revoke operations replay without restoring access");
            Concurrency(Path.Combine(directory,"concurrent"),identity,peer,check);Migration(Path.Combine(directory,"migration"),identity,peer,check);PeerRevocation(Path.Combine(directory,"peer-revoke"),identity,peer,check);
            var protocol=new ControlProtocol(store,true);foreach(string method in new[]{"access.approve","access.revoke","access.list"})
            {
                byte[] wire=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{kind="request",apiVersion=1,requestId=Guid.NewGuid(),method,@params=new{}}));using var reply=JsonDocument.Parse(protocol.Dispatch(wire,"caller"));
                check(reply.RootElement.GetProperty("error").GetProperty("code").GetString()=="CAPABILITY_UNSUPPORTED","application RPC does not expose host access authority");
            }
        }
        finally{string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup.");if(Directory.Exists(target))Directory.Delete(target,true);}
    }
    private static void Concurrency(string state,SecureIdentity identity,SecureIdentity peer,Action<bool,string> check)
    {
        var one=new RegistrationStore(state);one.BindSecureIdentity(identity);one.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),0,Guid.NewGuid());
        Guid app=one.Register("caller",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest))).GetProperty("applicationRef").GetGuid();one.DecideReview(one.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
        Guid request=one.RequestPendingGrant("caller",Guid.NewGuid(),new PendingGrantRequest(app,"files",peer.DeviceId,"org.example.publisher","files")).GetProperty("grantRequestId").GetGuid();var proposal=one.ProposeLocalAccess(request);var two=new RegistrationStore(state);
        string Attempt(RegistrationStore store){try{store.ApproveLocalAccess(proposal,Guid.NewGuid());return "accepted";}catch(ApplicationReviewException e){return e.Code;}}
        var first=Task.Run(()=>Attempt(one));var second=Task.Run(()=>Attempt(two));Task.WaitAll(first,second);
        check(new[]{first.Result,second.Result}.Count(x=>x=="accepted")==1&&new[]{first.Result,second.Result}.Count(x=>x=="REVIEW_STALE")==1,"concurrent stores commit only one activation from the same proposal");
        var next=one.ProposeLocalAccess(request);Guid operation=Guid.NewGuid();var replay1=Task.Run(()=>one.ApproveLocalAccess(next,operation));var replay2=Task.Run(()=>two.ApproveLocalAccess(next,operation));Task.WaitAll(replay1,replay2);
        check(replay1.Result==replay2.Result&&replay1.Result.Revision==2,"concurrent identical operations retain one exact grant revision");
        var scope=one.ResolveLocalAccess("caller",Open(app,peer.DeviceId));Sql(state,"UPDATE local_access SET active=2");
        check(!one.LocalAccessCurrent([scope])[0],"corrupt grant rows fail closed in active monitoring");Refuse("STORAGE_UNAVAILABLE",()=>one.ResolveLocalAccess("caller",Open(app,peer.DeviceId)),check);
    }
    private static void Migration(string state,SecureIdentity identity,SecureIdentity peer,Action<bool,string> check)
    {
        var store=new RegistrationStore(state);store.BindSecureIdentity(identity);store.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),0,Guid.NewGuid());
        Guid app=store.Register("caller",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest))).GetProperty("applicationRef").GetGuid();store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
        var before=store.GetJournalCapacity();Sql(state,"DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; DROP TABLE local_access; PRAGMA user_version=6");store=new RegistrationStore(state);
        check(store.GetJournalCapacity()==before,"schema-six migration preserves operations and reservations without inventing visitor authority");Refuse("AUTH_REQUIRED",()=>store.ResolveLocalAccess("caller",Open(app,peer.DeviceId)),check);
        Sql(state,"DROP TABLE local_access");try{_=new RegistrationStore(state);throw new Exception("Missing current table recreated.");}catch(InvalidDataException){check(true,"current-schema missing visitor authority fails closed");}
    }
    private static void PeerRevocation(string state,SecureIdentity identity,SecureIdentity peer,Action<bool,string> check)
    {
        var store=new RegistrationStore(state);store.BindSecureIdentity(identity);store.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),0,Guid.NewGuid());
        Guid app=store.Register("caller",Guid.NewGuid(),ManifestContract.Parse(Encoding.UTF8.GetBytes(Manifest))).GetProperty("applicationRef").GetGuid();store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
        Guid pending=store.RequestPendingGrant("caller",Guid.NewGuid(),new PendingGrantRequest(app,"files",peer.DeviceId,"org.example.publisher","files")).GetProperty("grantRequestId").GetGuid();
        var proposal=store.ProposeLocalAccess(pending);var grant=store.ApproveLocalAccess(proposal,Guid.NewGuid());var scope=store.ResolveLocalAccess("caller",Open(app,peer.DeviceId));
        var before=store.GetJournalCapacity();store.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),1,Guid.NewGuid());check(!store.LocalAccessCurrent([scope])[0],"peer revision invalidates prior visitor authority");
        var updated=store.ApproveLocalAccess(store.ProposeLocalAccess(pending),Guid.NewGuid());var live=store.ResolveLocalAccess("caller",Open(app,peer.DeviceId));
        var revoked=store.RevokeSecurePeer(peer.DeviceId,2,Guid.NewGuid());check(revoked.AffectedGrants==1&&!store.LocalAccessCurrent([live])[0],"peer revocation atomically retires visitor grants");
        check(store.GetJournalCapacity().ReservedRevocations==1,"peer revocation releases device and visitor reservations while preserving declaration reserve");
        Refuse("AUTH_REQUIRED",()=>store.ApproveLocalAccess(proposal,Guid.NewGuid()),check);
    }
    private static void Refuse(string code,Action action,Action<bool,string> check)
    {try{action();throw new Exception("Unexpected authority acceptance: "+code);}catch(RegistrationException e){check(e.Code==code&&e.InnerException is null,"fixed visitor refusal "+code);}catch(ApplicationReviewException e){check(e.Code==code&&e.InnerException is null,"fixed owner refusal "+code);}}
    private static void Sql(string state,string sql){using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"registration.db"),Pooling=false}.ToString());c.Open();using var command=c.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
}
