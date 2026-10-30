using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using LinkGate.Broker;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

internal static class ReviewTests
{
    private const string Declaration="""
        {"manifestVersion":1,"applicationId":"test.review","displayName":"Review","exports":[{"serviceId":"files","label":"Files","protocol":"http","localTarget":{"host":"127.0.0.1","port":18481}}],"imports":[]}
        """;
    private static AppManifest Manifest(int port=18481)=>ManifestContract.Parse(Encoding.UTF8.GetBytes(Declaration.Replace("18481",port.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    private static Guid Register(RegistrationStore store,string caller="caller",Guid? op=null,int port=18481)=>
        Guid.Parse(store.Register(caller,op??Guid.NewGuid(),Manifest(port)).GetProperty("applicationRef").GetString()!);
    public static async Task RunAsync(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;
        while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new Exception("Repository not found");
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-review-tests"));
        string directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            Lifecycle(Path.Combine(directory,"lifecycle"),check);
            Migration(Path.Combine(directory,"migration"),check);
            Faults(Path.Combine(directory,"faults"),check);
            Quotas(Path.Combine(directory,"quotas"),check);
            await Concurrent(Path.Combine(directory,"concurrent"),check);
            await PipeBoundary(Path.Combine(directory,"pipe"),check);
        }
        finally
        {
            string target=Path.GetFullPath(directory);
            if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe cleanup path");
            if(Directory.Exists(target))Directory.Delete(target,true);
        }
    }
    private static void Lifecycle(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid registerOp=Guid.NewGuid();Guid reference=Register(store,op:registerOp);
        var initial=store.GetReview(reference);
        check(initial.PendingRevision==1 && initial.DecisionVersion==0 && initial.LastDecision==ApplicationReviewDecision.None && initial.ApprovedManifest is null,"new declaration has no approval");
        Guid approveOp=Guid.NewGuid();var approval=store.DecideReview(initial,ApplicationReviewDecision.Approved,approveOp);
        check(approval.DecisionVersion==1 && approval.ApprovedRevision==1,"exact declaration approved");
        var approved=store.GetReview(reference);
        check(approved.ApprovedManifest?.Exports[0].LocalTarget.Port==18481 && approved.ReviewedRevision==1,"approved snapshot contains exact target");
        Register(store);check(store.GetReview(reference).PendingRevision==1,"identical new registration preserves revision");
        Register(store,port:18482);var changed=store.GetReview(reference);
        check(changed.PendingRevision==2 && changed.ApprovedRevision==1 && changed.ApprovedManifest?.Exports[0].LocalTarget.Port==18481 && changed.DecisionVersion==1,"pending update preserves old approved target");
        Error("REVIEW_STALE",()=>store.DecideReview(initial,ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        Error("OPERATION_CONFLICT",()=>store.DecideReview(initial,ApplicationReviewDecision.Denied,approveOp),check);
        var denial=store.DecideReview(changed,ApplicationReviewDecision.Denied,Guid.NewGuid());
        var denied=store.GetReview(reference);
        check(denial.DecisionVersion==2 && denied.LastDecision==ApplicationReviewDecision.Denied && denied.ReviewedRevision==2 && denied.ApprovedRevision==1,"deny pending keeps prior approval");
        Guid revokeOp=Guid.NewGuid();store.DecideReview(denied,ApplicationReviewDecision.Revoked,revokeOp);
        var revoked=store.GetReview(reference);
        check(revoked.DecisionVersion==3 && revoked.ApprovedManifest is null && revoked.ApprovedRevision is null,"revoke clears approved declaration");
        check(store.DecideReview(initial,ApplicationReviewDecision.Approved,approveOp)==approval,"old approve operation returns original result");
        check(store.GetReviewOperation(approveOp)==approval && store.GetReview(reference).LastDecision==ApplicationReviewDecision.Revoked,"query or replay does not resurrect approval");
        Error("REVIEW_STALE",()=>store.DecideReview(denied,ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        store.DecideReview(revoked,ApplicationReviewDecision.Approved,Guid.NewGuid());
        var reopened=new RegistrationStore(directory).GetReview(reference);
        check(reopened.DecisionVersion==4 && reopened.ApprovedRevision==2 && reopened.ApprovedManifest?.Exports[0].LocalTarget.Port==18482,"reapproval persists across restart");
        Register(store,op:registerOp);check(store.GetReview(reference).PendingRevision==2,"old registration retry does not revert pending");
        var beforeAba=store.GetReview(reference);Register(store,port:18483);Register(store,port:18482);
        check(store.GetReview(reference).PendingRevision==4,"ABA advances pending revisions");
        Error("REVIEW_STALE",()=>store.DecideReview(beforeAba,ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        var otherStore=new RegistrationStore(Path.Combine(directory,"other"));
        Error("REVIEW_STALE",()=>otherStore.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        Guid other=Register(store,"other-caller");check(store.GetReview(other).ApprovedManifest is null,"same app text in another scope stays unapproved");
        Error("APPLICATION_UNKNOWN",()=>store.GetReview(Guid.NewGuid()),check);
        Error("OPERATION_UNKNOWN",()=>store.GetReviewOperation(Guid.NewGuid()),check);
        check(initial.PendingRevision==1 && initial.DecisionVersion==0 && initial.ApprovedManifest is null,"returned snapshot remains immutable");
    }
    private static void Migration(string directory,Action<bool,string> check)
    {
        Directory.CreateDirectory(directory);Guid reference=Guid.NewGuid(),op=Guid.NewGuid();
        string result=JsonSerializer.Serialize(new{applicationRef=reference.ToString(),permissionDiff=new{addedExports=new[]{"files"},removedExports=Array.Empty<string>(),changedExports=Array.Empty<string>(),addedImports=Array.Empty<string>(),removedImports=Array.Empty<string>(),changedImports=Array.Empty<string>(),declarationChanged=true},approvalRequired=true});
        string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("app.register\n"+Manifest().ToJson())));
        Sql(directory,"""
            CREATE TABLE applications(caller TEXT NOT NULL,app TEXT NOT NULL,reference TEXT NOT NULL,manifest TEXT NOT NULL,PRIMARY KEY(caller,app));
            CREATE TABLE operations(caller TEXT NOT NULL,operation TEXT NOT NULL,digest TEXT NOT NULL,result TEXT NOT NULL,PRIMARY KEY(caller,operation));
            INSERT INTO applications VALUES('legacy','test.review',$reference,$manifest);
            INSERT INTO operations VALUES('legacy',$op,$digest,$result);
            PRAGMA user_version=1;
            """,("$reference",reference.ToString()),("$manifest",Manifest().ToJson()),("$op",op.ToString()),("$result",result),("$digest",digest));
        var store=new RegistrationStore(directory);var migrated=store.GetReview(reference);
        check(Convert.ToInt64(Scalar(directory,"PRAGMA user_version"))==8 && migrated.PendingRevision==1 && migrated.DecisionVersion==0 && migrated.ApprovedManifest is null,"schema1 migration creates only unapproved state");
        check(store.Status("legacy",op).GetProperty("result").GetRawText()==result,"migration preserves old operation bytes");
        check(store.Register("legacy",op,Manifest()).GetRawText()==result && store.GetReview(reference).DecisionVersion==0,"migrated operation retries do not create approval");
        string failure=Path.Combine(directory,"failed");Directory.CreateDirectory(failure);
        Sql(failure,"""
            CREATE TABLE applications(caller TEXT,app TEXT,reference TEXT,manifest TEXT);
            CREATE TABLE operations(caller TEXT,operation TEXT,digest TEXT,result TEXT);
            CREATE TABLE application_reviews(collision TEXT); PRAGMA user_version=1;
            """);
        try{_=new RegistrationStore(failure);throw new Exception("Invalid migration accepted");}
        catch(InvalidDataException e){check(e.Message=="Registration storage unavailable." && e.InnerException is null && Convert.ToInt64(Scalar(failure,"PRAGMA user_version"))==1,"migration failure preserves old schema version with fixed error");}
        string interrupted=Path.Combine(directory,"insert-failed");Directory.CreateDirectory(interrupted);
        Sql(interrupted,"""
            CREATE TABLE applications(caller TEXT,app TEXT,reference TEXT,manifest TEXT);
            CREATE TABLE operations(caller TEXT,operation TEXT,digest TEXT,result TEXT);
            INSERT INTO applications VALUES(NULL,'test.review','bad','{}'); PRAGMA user_version=1;
            """);
        try{_=new RegistrationStore(interrupted);throw new Exception("Corrupt legacy row migrated");}
        catch(InvalidDataException e){check(e.Message=="Registration storage unavailable." && e.InnerException is null && Convert.ToInt64(Scalar(interrupted,"PRAGMA user_version"))==1 &&
            Convert.ToInt64(Scalar(interrupted,"SELECT COUNT(*) FROM sqlite_master WHERE name='application_reviews'"))==0 &&
            Convert.ToInt64(Scalar(interrupted,"SELECT COUNT(*) FROM applications"))==1,"migration insertion failure rolls back DDL and preserves legacy rows");}
        Sql(directory,"DROP TABLE application_reviews");
        Error("STORAGE_UNAVAILABLE",()=>store.GetReview(reference),check);
        try{_=new RegistrationStore(directory);throw new Exception("Missing review table recreated");}
        catch(InvalidDataException e){check(e.Message=="Registration storage unavailable." && Convert.ToInt64(Scalar(directory,"SELECT COUNT(*) FROM sqlite_master WHERE name='application_reviews'"))==0,"schema8 missing review table never recreated");}
    }
    private static void Faults(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid reference=Register(store);var snapshot=store.GetReview(reference);Guid op=Guid.NewGuid();
        Sql(directory,"CREATE TRIGGER fail_admin BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-test-marker'); END;");
        Error("STORAGE_UNAVAILABLE",()=>store.DecideReview(snapshot,ApplicationReviewDecision.Approved,op),check);
        check(new RegistrationStore(directory).GetReview(reference).DecisionVersion==0 && Convert.ToInt64(Scalar(directory,"SELECT COUNT(*) FROM operations"))==1,"failed journal insertion rolls back approval");
        Sql(directory,"DROP TRIGGER fail_admin");var result=store.DecideReview(snapshot,ApplicationReviewDecision.Approved,op);
        check(result.DecisionVersion==1,"original operation may be explicitly retried after proven rollback");
        Sql(directory,"CREATE TRIGGER fail_update BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-test-marker'); END;");
        try{Register(store,port:18482);throw new Exception("Registration fault ignored");}
        catch(SqliteException){check(store.GetReview(reference).PendingRevision==1 && store.GetReview(reference).ApprovedRevision==1,"failed pending update rolls back its revision and keeps approval");}
        Sql(directory,"DROP TRIGGER fail_update");
        Sql(directory,"UPDATE operations SET result='{\"Decision\":1,\"Decision\":3}' WHERE operation=$op",("$op",op.ToString()));
        Error("STORAGE_UNAVAILABLE",()=>store.GetReviewOperation(op),check);
        Error("STORAGE_UNAVAILABLE",()=>store.DecideReview(snapshot,ApplicationReviewDecision.Approved,op),check);
        foreach(string corruption in new[]{"decision_version=-1","decision='bad'","approved_revision=99","approved_manifest='private-test-marker'","pending_revision=1.5"})
        {
            Sql(directory,"UPDATE application_reviews SET "+corruption);
            Error("STORAGE_UNAVAILABLE",()=>store.GetReview(reference),check);
            Sql(directory,"UPDATE application_reviews SET pending_revision=1,decision_version=1,reviewed_revision=1,decision='Approved',approved_revision=1,approved_manifest=$manifest",("$manifest",Manifest().ToJson()));
        }
        Sql(directory,"DELETE FROM application_reviews");
        Error("STORAGE_UNAVAILABLE",()=>store.GetReview(reference),check);
        try{Register(store,port:18482);throw new Exception("Missing review silently repaired");}
        catch(RegistrationException e){check(e.Code=="STORAGE_UNAVAILABLE","registration does not repair missing review row");}
    }
    private static void Quotas(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory,2,3,10);Guid reference=Register(store);var snapshot=store.GetReview(reference);Guid op=Guid.NewGuid();
        var first=store.DecideReview(snapshot,ApplicationReviewDecision.Approved,op);
        Error("RESOURCE_EXHAUSTED",()=>store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Denied,Guid.NewGuid()),check);
        check(store.DecideReview(snapshot,ApplicationReviewDecision.Approved,op)==first && store.GetReviewOperation(op)==first,"admin replay and query survive full quota");
        check(store.GetReview(reference).DecisionVersion==1,"quota failure preserves decision");
        store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        check(store.GetReview(reference).ApprovedManifest is null,"reservation permits revocation at full budget");
        var overflow=new RegistrationStore(Path.Combine(directory,"overflow"));Guid other=Register(overflow);
        Sql(Path.Combine(directory,"overflow"),"UPDATE application_reviews SET decision_version=9223372036854775807,reviewed_revision=1,decision='Denied'");
        Error("RESOURCE_EXHAUSTED",()=>overflow.DecideReview(overflow.GetReview(other),ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        Sql(Path.Combine(directory,"overflow"),"UPDATE application_reviews SET pending_revision=9223372036854775807");
        try{Register(overflow,port:18482);throw new Exception("Revision overflow accepted");}catch(RegistrationException e){check(e.Code=="RESOURCE_EXHAUSTED","pending revision overflow refused");}
    }
    private static async Task Concurrent(string directory,Action<bool,string> check)
    {
        var one=new RegistrationStore(directory);Guid reference=Register(one);var two=new RegistrationStore(directory);var snapshot=one.GetReview(reference);
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(i=>Task.Run(()=>
        {
            try{(i%2==0?one:two).DecideReview(snapshot,ApplicationReviewDecision.Approved,Guid.NewGuid());return "accepted";}
            catch(ApplicationReviewException e){return e.Code;}
        })));
        check(results.Count(x=>x=="accepted")==1 && results.Count(x=>x=="REVIEW_STALE")==11,"concurrent distinct decisions use compare-and-swap versions");
        check(one.GetReview(reference).DecisionVersion==1 && Convert.ToInt64(Scalar(directory,"SELECT COUNT(*) FROM operations"))==2,"only winning decision committed");
        snapshot=one.GetReview(reference);Guid revoke=Guid.NewGuid();
        var replayed=await Task.WhenAll(Enumerable.Range(0,12).Select(i=>Task.Run(()=>(i%2==0?one:two).DecideReview(snapshot,ApplicationReviewDecision.Revoked,revoke))));
        check(replayed.Distinct().Count()==1 && one.GetReview(reference).DecisionVersion==2 && one.GetReview(reference).ApprovedManifest is null,"same admin operation commits once across store instances");
    }
    private static async Task PipeBoundary(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid reference=Register(store);Guid adminOp=Guid.NewGuid();
        store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Approved,adminOp);
        using var host=new PipeHost(Guid.NewGuid().ToString("N"),store);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task run=host.RunAsync(stop.Token);
        try
        {
            using var client=new NamedPipeClientStream(".",host.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous,System.Security.Principal.TokenImpersonationLevel.Impersonation);
            await client.ConnectAsync(stop.Token);
            foreach(string method in new[]{"app.review","app.approve","app.deny","app.revoke","host.app.review","host.operation.status","host.journal.capacity","host.review.list","grant.revoke","operation.status"})
            {
                byte[] request=JsonSerializer.SerializeToUtf8Bytes(new{kind="request",apiVersion=1,requestId=Guid.NewGuid().ToString(),method,@params=new{operationId=adminOp.ToString()}});
                await FrameCodec.WriteAsync(client,request,stop.Token);
                using var response=JsonDocument.Parse((await FrameCodec.ReadAsync(client,stop.Token))!);
                check(response.RootElement.GetProperty("error").GetProperty("code").GetString()==(method=="operation.status"?"OPERATION_UNKNOWN":"CAPABILITY_UNSUPPORTED"),"application pipe cannot manage review or read host operations");
            }
            check(store.GetReview(reference).DecisionVersion==1,"forbidden RPC left approved state unchanged");
            async Task<JsonElement> RegisterThroughPipe(int port)
            {
                using var manifest=JsonDocument.Parse(Manifest(port).ToJson());
                byte[] request=JsonSerializer.SerializeToUtf8Bytes(new{kind="request",apiVersion=1,requestId=Guid.NewGuid().ToString(),method="app.register",operationId=Guid.NewGuid().ToString(),@params=new{manifest=manifest.RootElement}});
                await FrameCodec.WriteAsync(client,request,stop.Token);
                using var response=JsonDocument.Parse((await FrameCodec.ReadAsync(client,stop.Token))!);return response.RootElement.GetProperty("result").Clone();
            }
            var registered=await RegisterThroughPipe(18481);Guid nativeReference=Guid.Parse(registered.GetProperty("applicationRef").GetString()!);
            check(store.GetReview(nativeReference).ApprovedManifest is null,"actual OS caller registration begins unapproved");
            store.DecideReview(store.GetReview(nativeReference),ApplicationReviewDecision.Approved,Guid.NewGuid());
            var updated=await RegisterThroughPipe(18482);var nativeReview=store.GetReview(nativeReference);
            check(updated.GetProperty("approvalRequired").GetBoolean() && nativeReview.PendingRevision==2 && nativeReview.ApprovedManifest?.Exports[0].LocalTarget.Port==18481,"native caller update cannot replace host-approved target");
        }
        finally{stop.Cancel();try{await run;}catch(OperationCanceledException){}}
    }
    private static void Error(string code,Action action,Action<bool,string> check)
    {
        try{action();throw new Exception("Expected "+code);}
        catch(ApplicationReviewException e){check(e.Code==code && e.Message=="Application review rejected." && e.InnerException is null && !e.ToString().Contains("private-test-marker"),code+" sanitized");}
    }
    private static void Sql(string directory,string sql,params(string,object)[] values)
    {
        using var connection=Connection(directory);using var command=connection.CreateCommand();command.CommandText=sql;
        foreach(var (name,value) in values)command.Parameters.AddWithValue(name,value);command.ExecuteNonQuery();
    }
    private static object? Scalar(string directory,string sql)
    {using var connection=Connection(directory);using var command=connection.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static SqliteConnection Connection(string directory)
    {var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"registration.db"),Pooling=false}.ToString());connection.Open();return connection;}
}
