using System.Globalization;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

internal static class ConnectionJournalTests
{
    private static readonly DateTimeOffset Utc=new(2026,10,30,1,0,0,TimeSpan.Zero);
    public static void Run(Action<bool,string> check)
    {
        Meter(check);string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new IOException();
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-connection-journal-tests")),directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            using var owner=SecureIdentity.CreateOwnerRoot();using var identity=SecureStreamTests.Identity(Path.Combine(directory,"identity"),owner);using var peer=SecureStreamTests.Identity(Path.Combine(directory,"peer"),owner);
            string state=Path.Combine(directory,"state");var store=new RegistrationStore(state);store.BindSecureIdentity(identity);store.TrustSecurePeer(identity,peer.Certificate.ExportCertificatePem(),0,Guid.NewGuid());
            var manifest=ManifestContract.Parse(Encoding.UTF8.GetBytes("""{"manifestVersion":1,"applicationId":"org.example.visitor","displayName":"Visitor","exports":[],"imports":[{"alias":"files","protocol":"tcp","endpointMode":"brokered","connectionPolicy":{"mode":"direct-only","allowRelay":false}}]}"""));
            Guid app=store.Register("caller",Guid.NewGuid(),manifest).GetProperty("applicationRef").GetGuid();store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
            var request=ConnectionOpenRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(new{applicationRef=app,importAlias="files",deviceId=peer.DeviceId,publisherAppId="org.example.publisher",remoteServiceId="files",connectionPolicy=new{mode="direct-only",allowRelay=false}}));
            Guid boot=Guid.NewGuid(),operation=Guid.NewGuid();long now=0;DateTimeOffset utc=DateTimeOffset.UtcNow;
            Refuse("AUTH_REQUIRED",()=>store.BeginConnection("caller",operation,request,boot,1,utc,0,()=>now),check);
            Guid pending=store.RequestPendingGrant("caller",Guid.NewGuid(),new PendingGrantRequest(app,"files",peer.DeviceId,"org.example.publisher","files")).GetProperty("grantRequestId").GetGuid();store.ApproveLocalAccess(store.ProposeLocalAccess(pending),Guid.NewGuid());
            var accepted=store.BeginConnection("caller",operation,request,boot,1,utc,0,()=>now);Guid reference=accepted.Status.ConnectionId;
            check(accepted.Created&&accepted.Scope is not null&&accepted.Status.FlowState=="pending"&&Op(store,"caller",operation).Status=="pending","open atomically records current access and pending outcome");
            check(!store.BeginConnection("caller",operation,request,boot,1,utc,0,()=>now).Created,"matching retained open never creates another flow");Refuse("OPERATION_UNKNOWN",()=>store.ConnectionStatus("other",reference),check);
            var changed=ConnectionOpenRequest.Parse(Encoding.UTF8.GetBytes(request.ToJson().Replace("15000","10000",StringComparison.Ordinal)));Refuse("OPERATION_CONFLICT",()=>store.BeginConnection("caller",operation,changed,boot,1,utc,0,()=>now),check);
            Refuse("CONNECTION_NOT_READY",()=>store.AttachConnection("caller",Guid.NewGuid(),reference,boot),check);
            var meter=new ConnectionMeter(reference,accepted.Status.OwnerId,peer.DeviceId,"org.example.publisher","files",boot,1,utc,0,()=>now);var stale=meter.Read();now=10;meter.Ready("direct",10);check(store.PublishConnection("caller",boot,meter.Read()),"ready snapshot persists");
            check(Op(store,"caller",operation).ConnectionId==reference&&Op(store,"caller",operation).Status=="succeeded","authenticated readiness completes the original open operation");
            Guid attach=Guid.NewGuid();var claim=ConnectionOperation.Parse(Encoding.UTF8.GetBytes(store.AttachConnection("caller",attach,reference,boot).GetRawText()),attach,"streamRef");Guid stream=claim.StreamRef!.Value;
            check(store.AttachConnection("caller",attach,reference,boot).GetRawText()==store.AttachConnection("caller",attach,reference,boot).GetRawText(),"same attach operation retains one stream reference");
            Refuse("STREAM_ALREADY_ATTACHED",()=>store.AttachConnection("caller",Guid.NewGuid(),reference,boot),check);Refuse("AUTH_REQUIRED",()=>store.ConsumeConnectionAttachment("other",stream,boot),check);Refuse("AUTH_REQUIRED",()=>store.ConsumeConnectionAttachment("caller",stream,Guid.NewGuid()),check);
            check(store.ConsumeConnectionAttachment("caller",stream,boot)==reference,"native attachment consumes the caller and boot-bound reference");Refuse("AUTH_REQUIRED",()=>store.ConsumeConnectionAttachment("caller",stream,boot),check);
            meter.Attached();meter.Bytes(true,5);meter.Fin(true);check(store.PublishConnection("caller",boot,meter.Read())&&store.ConnectionStatus("caller",reference).FlowState=="draining","coalesced half-closed snapshot retains payload without requiring an intermediate open write");
            check(!store.PublishConnection("caller",boot,stale),"old snapshot cannot overwrite newer counters");
            var regressed=System.Text.Json.Nodes.JsonNode.Parse(meter.Read().Status.ToJson())!.AsObject();regressed["flowState"]="open";var badState=LinkGate.Contracts.ConnectionStatus.Parse(Encoding.UTF8.GetBytes(regressed.ToJsonString()));Refuse("STORAGE_UNAVAILABLE",()=>store.PublishConnection("caller",boot,new ConnectionMeter.Snapshot(meter.Read().Sequence+1,badState)),check);
            store=new RegistrationStore(state);Guid nextBoot=Guid.NewGuid();store.RecoverConnections(nextBoot,utc.AddMilliseconds(100));var recovered=store.ConnectionStatus("caller",reference);
            check(recovered.ErrorCode=="GATEWAY_RESTARTED"&&recovered.DirectTx==5&&!recovered.Complete&&recovered.BootId==boot,"crash recovery preserves known bytes and original boot with incomplete tail");
            check(Op(store,"caller",operation).Status=="succeeded"&&!store.BeginConnection("caller",operation,request,nextBoot,2,utc,0,()=>now).Created&&!store.PublishConnection("caller",boot,meter.Read()),"recovery never redials or rewrites a previously successful open");
            Guid early=Guid.NewGuid();var pendingFlow=store.BeginConnection("caller",early,request,nextBoot,2,utc,0,()=>now);store.RecoverConnections(Guid.NewGuid(),utc.AddSeconds(1));
            check(Op(store,"caller",early).Status=="failed"&&Op(store,"caller",early).ErrorCode=="GATEWAY_RESTARTED","pre-ready crash retains failed open outcome");
            Guid close=Guid.NewGuid();check(store.PrepareConnectionClose("caller",close,reference).GetRawText()==store.PrepareConnectionClose("caller",close,reference).GetRawText(),"close mutation is retained without restoring access");Refuse("OPERATION_CONFLICT",()=>store.PrepareConnectionClose("caller",operation,reference),check);
            var capacity=store.GetJournalCapacity();Sql(state,"CREATE TRIGGER fail_connection BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-marker'); END");
            Refuse("STORAGE_UNAVAILABLE",()=>store.BeginConnection("caller",Guid.NewGuid(),request,nextBoot,2,utc,0,()=>now),check);check(store.GetJournalCapacity()==capacity,"failed open record rolls back connection insertion");Sql(state,"DROP TRIGGER fail_connection");
            var live=store.BeginConnection("caller",Guid.NewGuid(),request,nextBoot,2,utc,0,()=>now);var liveMeter=new ConnectionMeter(live.Status.ConnectionId,live.Status.OwnerId,peer.DeviceId,"org.example.publisher","files",nextBoot,2,utc,0,()=>now);liveMeter.Ready("direct",10);store.PublishConnection("caller",nextBoot,liveMeter.Read());
            for(int i=0;i<127;i++)store.BeginSecureOpen("caller",Guid.NewGuid(),"fixture-"+i,Guid.NewGuid(),nextBoot);
            Refuse("RESOURCE_EXHAUSTED",()=>store.BeginConnection("caller",Guid.NewGuid(),request,nextBoot,2,utc,0,()=>now),check);
            Refuse("RESOURCE_EXHAUSTED",()=>store.BeginSecureOpen("caller",Guid.NewGuid(),"full",Guid.NewGuid(),nextBoot),check);
            check(!store.BeginConnection("caller",operation,request,nextBoot,2,utc,0,()=>now).Created,"retained original open precedes combined formal and legacy active-flow cap");
            var grant=store.ProposeLocalAccess(pending);store.RevokeLocalAccess(grant.GrantId!.Value,grant.GrantRevision,Guid.NewGuid());Refuse("AUTH_REQUIRED",()=>store.AttachConnection("caller",Guid.NewGuid(),live.Status.ConnectionId,nextBoot),check);
            Migration(Path.Combine(directory,"migration"),check);
        }
        finally{string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe cleanup.");if(Directory.Exists(target))Directory.Delete(target,true);}
    }
    private static void Meter(Action<bool,string> check)
    {
        long clock=0;var meter=new ConnectionMeter(Guid.NewGuid(),new string('a',64),new string('b',64),"org.example.publisher","files",Guid.NewGuid(),1,Utc,0,()=>clock);
        clock=10;meter.Ready("direct",10);check(meter.Read().Status.BusinessPath=="none","transport selection does not invent business bytes");meter.Attached();meter.Bytes(true,33);meter.Fin(true);meter.Bytes(false,67108864);meter.Fin(false);meter.End(true,null);var complete=meter.Read();
        check(complete.Status.Complete&&complete.Status.DirectTx==33&&complete.Status.DirectRx==67108864&&complete.Status.ClosedUtc is not null,"exact bidirectional bytes and both FINs permit complete after drain");meter.Bytes(true,1);meter.End(false,"CONNECTION_CLOSED");check(meter.Read().Sequence==complete.Sequence,"terminal meter ignores late payload and events");
        var partial=new ConnectionMeter(Guid.NewGuid(),new string('a',64),new string('b',64),"org.example.publisher","files",Guid.NewGuid(),1,Utc,0,()=>clock);partial.Ready("relay",10);partial.Attached();partial.Bytes(false,10);partial.Fin(true);partial.End(true,null);
        check(!partial.Read().Status.Complete&&partial.Read().Status.ErrorCode=="CONNECTION_CLOSED"&&partial.Read().Status.RelayRx==10,"one FIN never becomes complete and known relay bytes remain");
        var overflow=new ConnectionMeter(Guid.NewGuid(),new string('a',64),new string('b',64),"org.example.publisher","files",Guid.NewGuid(),1,Utc,0,()=>clock);overflow.Ready("direct",10);overflow.Attached();overflow.Bytes(true,ulong.MaxValue);check(!overflow.Bytes(true,1)&&overflow.Read().Status.ErrorCode=="COUNTER_OVERFLOW"&&overflow.Read().Status.DirectTx==ulong.MaxValue,"counter overflow fails without wrapping");
        var rollback=new ConnectionMeter(Guid.NewGuid(),new string('a',64),new string('b',64),"org.example.publisher","files",Guid.NewGuid(),1,Utc,0,()=>clock);rollback.Ready("direct",10);clock=1;check(!rollback.ClockValid,"monotonic clock rollback invalidates meter");rollback.End(false,"CONNECTION_CLOSED");check(rollback.Read().Status.ErrorCode=="CLOCK_UNTRUSTED","rollback keeps a fixed failure and non-regressing UTC floor");
    }
    private static void Migration(string state,Action<bool,string> check)
    {
        var store=new RegistrationStore(state);Sql(state,"DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; PRAGMA user_version=7");store=new RegistrationStore(state);check(store.GetJournalCapacity().RetainedOperations==0,"schema-seven migration creates no flows or operation outcomes");
        Sql(state,"DROP TABLE connection_attachments");try{_=new RegistrationStore(state);throw new Exception("Missing attachment table recreated.");}catch(InvalidDataException){check(true,"missing current-schema attachment journal fails closed");}
    }
    private static ConnectionOperation Op(RegistrationStore store,string caller,Guid operation)=>ConnectionOperation.Parse(Encoding.UTF8.GetBytes(store.Status(caller,operation).GetRawText()),operation,"connectionId");
    private static void Refuse(string code,Action action,Action<bool,string> check){try{action();throw new Exception("Unexpected connection acceptance: "+code);}catch(RegistrationException e){check(e.Code==code&&e.InnerException is null,"fixed connection refusal "+code);}}
    private static void Sql(string state,string sql){using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(state,"registration.db"),Pooling=false}.ToString());c.Open();using var command=c.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
}
