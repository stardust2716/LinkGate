using System.Text;
using LinkGate.Broker;
using LinkGate.Contracts;
using Microsoft.Data.Sqlite;

internal static class CapacityTests
{
    private const string Declaration="""
        {"manifestVersion":1,"applicationId":"test.capacity","displayName":"Capacity","exports":[{"serviceId":"files","label":"Files","protocol":"http","localTarget":{"host":"127.0.0.1","port":18481}}],"imports":[]}
        """;
    private static AppManifest Manifest(int port=18481)=>ManifestContract.Parse(Encoding.UTF8.GetBytes(Declaration.Replace("18481",port.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    private static Guid Register(RegistrationStore store,string caller="caller",Guid? operation=null,int port=18481)=>
        Guid.Parse(store.Register(caller,operation??Guid.NewGuid(),Manifest(port)).GetProperty("applicationRef").GetString()!);

    public static async Task RunAsync(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;
        while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new Exception("Repository not found");
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-capacity-tests"));
        string directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            GlobalBudget(Path.Combine(directory,"global"),check);
            HostBudget(Path.Combine(directory,"host"),check);
            MultipleApprovals(Path.Combine(directory,"multiple"),check);
            Admission(Path.Combine(directory,"admission"),check);
            Rollback(Path.Combine(directory,"rollback"),check);
            Migration(Path.Combine(directory,"migration"),check);
            await Concurrent(Path.Combine(directory,"concurrent"),check);
        }
        finally
        {
            string target=Path.GetFullPath(directory);
            if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe cleanup path");
            if(Directory.Exists(target))Directory.Delete(target,true);
        }
    }
    private static void GlobalBudget(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory,20,4,10);Guid registration=Guid.NewGuid(),reference=Register(store,operation:registration);
        var initial=store.GetReview(reference);Guid approve=Guid.NewGuid();var approved=store.DecideReview(initial,ApplicationReviewDecision.Approved,approve);
        var capacity=store.GetJournalCapacity();
        check(capacity.RetainedOperations==2 && capacity.RetainedHostOperations==1 && capacity.ReservedRevocations==1 && capacity.AvailableGlobalOperations==1,"approval reserves global and host slot");
        Register(store);capacity=store.GetJournalCapacity();
        check(capacity.RetainedOperations==3 && capacity.AvailableGlobalOperations==0 && capacity.WithinLimits,"ordinary registration fills only unreserved budget");
        RegistrationError("RESOURCE_EXHAUSTED",()=>Register(store,"another-caller"),check);
        ReviewError("RESOURCE_EXHAUSTED",()=>store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Denied,Guid.NewGuid()),check);
        check(Register(store,operation:registration)==reference && store.GetReviewOperation(approve)==approved &&
            store.DecideReview(initial,ApplicationReviewDecision.Approved,approve)==approved && store.GetJournalCapacity()==capacity,"full-budget replay or query does not spend reservation");
        var restart=new RegistrationStore(directory,20,4,10);
        check(restart.GetJournalCapacity()==capacity,"restart derives same reservation from approved row");
        var before=restart.GetReview(reference);Guid revoke=Guid.NewGuid();var revoked=restart.DecideReview(before,ApplicationReviewDecision.Revoked,revoke);
        var full=restart.GetJournalCapacity();
        check(revoked.DecisionVersion==2 && restart.GetReview(reference).ApprovedManifest is null && full.RetainedOperations==4 && full.ReservedRevocations==0 && full.WithinLimits,"full global budget permits reserved revocation without overflow");
        check(restart.DecideReview(before,ApplicationReviewDecision.Revoked,revoke)==revoked && restart.GetReviewOperation(revoke)==revoked &&
            restart.DecideReview(initial,ApplicationReviewDecision.Approved,approve)==approved && restart.GetJournalCapacity()==full &&
            restart.GetReview(reference).ApprovedManifest is null,"old operations neither release twice nor restore reservation");
        ReviewError("RESOURCE_EXHAUSTED",()=>restart.DecideReview(restart.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
    }
    private static void HostBudget(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory,2,20,10);Guid reference=Register(store);
        store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid());
        var capacity=store.GetJournalCapacity();
        check(capacity.AvailableHostOperations==0 && capacity.AvailableGlobalOperations>0,"host budget includes its reserved revocation");
        ReviewError("RESOURCE_EXHAUSTED",()=>store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Denied,Guid.NewGuid()),check);
        Guid other=Register(store,"other");check(store.GetReview(other).ApprovedManifest is null,"full host budget still allows ordinary caller within its own budget");
        ReviewError("RESOURCE_EXHAUSTED",()=>store.DecideReview(store.GetReview(other),ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Revoked,Guid.NewGuid());capacity=store.GetJournalCapacity();
        check(capacity.RetainedHostOperations==2 && capacity.ReservedRevocations==0 && capacity.WithinLimits,"revocation consumes host reservation at full host budget");
        ReviewError("RESOURCE_EXHAUSTED",()=>store.DecideReview(store.GetReview(other),ApplicationReviewDecision.Revoked,Guid.NewGuid()),check);
    }
    private static void MultipleApprovals(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory,10,8,10);Guid a=Register(store,"a"),b=Register(store,"b");
        store.DecideReview(store.GetReview(a),ApplicationReviewDecision.Approved,Guid.NewGuid());
        store.DecideReview(store.GetReview(b),ApplicationReviewDecision.Approved,Guid.NewGuid());
        check(store.GetJournalCapacity().ReservedRevocations==2,"each approved declaration owns one reservation");
        Register(store,"a",port:18482);
        check(store.GetReview(a).ApprovedManifest?.Exports[0].LocalTarget.Port==18481 && store.GetJournalCapacity().ReservedRevocations==2,"pending update does not release prior approval reservation");
        store.DecideReview(store.GetReview(a),ApplicationReviewDecision.Denied,Guid.NewGuid());
        check(store.GetJournalCapacity().AvailableGlobalOperations==0 && store.GetJournalCapacity().ReservedRevocations==2,"denial keeps both reservations at full budget");
        store.DecideReview(store.GetReview(a),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        check(store.GetJournalCapacity().ReservedRevocations==1 && store.GetReview(b).ApprovedManifest is not null,"revocation releases only its declaration reservation");
        store.DecideReview(store.GetReview(b),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        var final=store.GetJournalCapacity();check(final.RetainedOperations==8 && final.ReservedRevocations==0 && final.WithinLimits,"all approvals can be revoked while every logical slot is occupied");
    }
    private static void Admission(string directory,Action<bool,string> check)
    {
        var global=new RegistrationStore(directory,4,2,10);Guid reference=Register(global);
        ReviewError("RESOURCE_EXHAUSTED",()=>global.DecideReview(global.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        check(global.GetReview(reference).DecisionVersion==0 && global.GetJournalCapacity().RetainedOperations==1,"approval is not committed when future revocation cannot fit");
        global.DecideReview(global.GetReview(reference),ApplicationReviewDecision.Denied,Guid.NewGuid());
        check(global.GetJournalCapacity().RetainedOperations==2 && global.GetJournalCapacity().ReservedRevocations==0,"unapproved denial uses one ordinary slot");
        var host=new RegistrationStore(Path.Combine(directory,"small-host"),1,10,10);reference=Register(host);
        ReviewError("RESOURCE_EXHAUSTED",()=>host.DecideReview(host.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid()),check);
        host.DecideReview(host.GetReview(reference),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        check(host.GetJournalCapacity().RetainedHostOperations==1 && host.GetJournalCapacity().ReservedRevocations==0,"revoking unapproved declaration cannot invent a reservation");
        ReviewError("RESOURCE_EXHAUSTED",()=>host.DecideReview(host.GetReview(reference),ApplicationReviewDecision.Revoked,Guid.NewGuid()),check);
        var replacement=new RegistrationStore(Path.Combine(directory,"replacement"),10,4,10);reference=Register(replacement);
        replacement.DecideReview(replacement.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid());
        replacement.DecideReview(replacement.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid());
        check(replacement.GetJournalCapacity().ReservedRevocations==1 && replacement.GetJournalCapacity().AvailableGlobalOperations==0,"replacement approval retains a single reservation");
        replacement.DecideReview(replacement.GetReview(reference),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        check(replacement.GetJournalCapacity().WithinLimits && replacement.GetReview(reference).ApprovedManifest is null,"replacement approval remains revocable at full budget");
    }
    private static void Rollback(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory,3,3,10);Guid reference=Register(store);store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid());
        var before=store.GetReview(reference);var capacity=store.GetJournalCapacity();Guid operation=Guid.NewGuid();
        Sql(directory,"CREATE TRIGGER fail_revoke BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-test-marker'); END;");
        ReviewError("STORAGE_UNAVAILABLE",()=>store.DecideReview(before,ApplicationReviewDecision.Revoked,operation),check);
        check(store.GetJournalCapacity()==capacity && store.GetReview(reference).DecisionVersion==1 && store.GetReview(reference).ApprovedManifest is not null,"failed revocation rolls back both approval and reservation");
        ReviewError("OPERATION_UNKNOWN",()=>store.GetReviewOperation(operation),check);
        Sql(directory,"DROP TRIGGER fail_revoke");var restart=new RegistrationStore(directory,3,3,10);
        var result=restart.DecideReview(restart.GetReview(reference),ApplicationReviewDecision.Revoked,operation);
        check(result.DecisionVersion==2 && restart.GetJournalCapacity().RetainedOperations==3 && restart.GetJournalCapacity().ReservedRevocations==0,"explicit retry after rollback consumes restored reservation once");
        var after=new RegistrationStore(directory,3,3,10);
        check(after.GetReviewOperation(operation)==result && after.GetReview(reference).ApprovedManifest is null,"committed revoke and original result survive full-budget reopen");
    }
    private static (Guid Reference,Guid Operation,ApplicationReviewResult Result) Legacy(string directory)
    {
        var store=new RegistrationStore(directory);Guid reference=Register(store),operation=Guid.NewGuid();
        var result=store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Approved,operation);
        // Schema two has exactly these tables and rows but does not enforce reservations.
        Sql(directory,"DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; DROP TABLE local_access; DROP TABLE secure_opens; DROP TABLE secure_grants; DROP TABLE secure_peers; DROP TABLE secure_identity; DROP TABLE grant_requests; ALTER TABLE application_reviews DROP COLUMN authority_epoch; PRAGMA user_version=2");return(reference,operation,result);
    }
    private static void Migration(string directory,Action<bool,string> check)
    {
        var legacy=Legacy(directory);var migrated=new RegistrationStore(directory,2,3,10);
        check(Convert.ToInt64(Scalar(directory,"PRAGMA user_version"))==8 && migrated.GetReviewOperation(legacy.Operation)==legacy.Result && migrated.GetReview(legacy.Reference).DecisionVersion==1,"schema2 migration preserves approval version and operation result");
        check(migrated.GetJournalCapacity().ReservedRevocations==1 && migrated.GetJournalCapacity().AvailableGlobalOperations==0 && migrated.GetJournalCapacity().AvailableHostOperations==0,"schema2 approval gains reservation at exact limits");
        migrated.DecideReview(migrated.GetReview(legacy.Reference),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        check(migrated.GetReview(legacy.Reference).ApprovedManifest is null && migrated.GetJournalCapacity().WithinLimits,"migrated full-budget approval remains revocable");
        foreach(bool hostFull in new[]{false,true})
        {
            string failed=Path.Combine(directory,hostFull?"host-overflow":"global-overflow");var old=Legacy(failed);
            try{_=new RegistrationStore(failed,hostFull?1:10,hostFull?10:2,10);throw new Exception("Overcommitted migration accepted");}
            catch(InvalidDataException e){check(e.Message=="Registration storage unavailable." && e.InnerException is null &&
                Convert.ToInt64(Scalar(failed,"PRAGMA user_version"))==2 && Convert.ToInt64(Scalar(failed,"SELECT COUNT(*) FROM operations"))==2 &&
                Convert.ToInt64(Scalar(failed,"SELECT COUNT(*) FROM application_reviews WHERE approved_manifest IS NOT NULL AND decision_version=1"))==1,"overcommitted migration preserves schema2 and all approved/result rows");}
            var recovered=new RegistrationStore(failed,2,3,10);
            check(recovered.GetReviewOperation(old.Operation)==old.Result && recovered.GetJournalCapacity().WithinLimits,"explicit compatible migration does not lose historical operation");
            try{_=new RegistrationStore(failed,1,3,10);throw new Exception("Reduced quota discarded reservation");}
            catch(InvalidDataException){check(Convert.ToInt64(Scalar(failed,"PRAGMA user_version"))==8 && recovered.GetJournalCapacity().ReservedRevocations==1,"schema7 reopen with insufficient limit fails without dropping reservation");}
        }
        string unapproved=Path.Combine(directory,"full-unapproved");var ordinary=new RegistrationStore(unapproved,3,3,10);Register(ordinary);Register(ordinary);Register(ordinary);
        Sql(unapproved,"DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; DROP TABLE local_access; DROP TABLE secure_opens; DROP TABLE secure_grants; DROP TABLE secure_peers; DROP TABLE secure_identity; DROP TABLE grant_requests; ALTER TABLE application_reviews DROP COLUMN authority_epoch; PRAGMA user_version=2");var full=new RegistrationStore(unapproved,3,3,10);
        check(full.GetJournalCapacity().RetainedOperations==3 && full.GetJournalCapacity().ReservedRevocations==0 && full.GetJournalCapacity().WithinLimits,"full schema2 without approved declarations needs no reservation");
    }
    private static async Task Concurrent(string directory,Action<bool,string> check)
    {
        var one=new RegistrationStore(directory,100,4,10);Guid a=Register(one,"a"),b=Register(one,"b");var two=new RegistrationStore(directory,100,4,10);
        var snapshots=new[]{one.GetReview(a),two.GetReview(b)};
        var outcomes=await Task.WhenAll(snapshots.Select((snapshot,i)=>Task.Run(()=>
        {
            try{(i==0?one:two).DecideReview(snapshot,ApplicationReviewDecision.Approved,Guid.NewGuid());return "approved";}
            catch(ApplicationReviewException e){return e.Code;}
        })));
        check(outcomes.Count(x=>x=="approved")==1 && outcomes.Count(x=>x=="RESOURCE_EXHAUSTED")==1 && one.GetJournalCapacity().WithinLimits,"concurrent approvals cannot oversubscribe future revocation budget");
        Guid winner=one.GetReview(a).ApprovedManifest is not null?a:b;var snapshot=one.GetReview(winner);Guid revoke=Guid.NewGuid();
        var work=Enumerable.Range(0,4).Select(i=>Task.Run(()=>
        {
            try{Register(i%2==0?one:two,"noise-"+i);return false;}catch(RegistrationException e){return e.Code=="RESOURCE_EXHAUSTED";}
        })).ToArray();
        var revocations=Enumerable.Range(0,4).Select(i=>Task.Run(()=>(i%2==0?one:two).DecideReview(snapshot,ApplicationReviewDecision.Revoked,revoke))).ToArray();
        bool[] rejected=await Task.WhenAll(work);var results=await Task.WhenAll(revocations);
        check(rejected.All(x=>x) && results.Distinct().Count()==1,"ordinary writers cannot race away a reserved slot or duplicate revoke result");
        var final=one.GetJournalCapacity();check(final.RetainedOperations==4 && final.ReservedRevocations==0 && final.WithinLimits && one.GetReview(winner).ApprovedManifest is null,"concurrent full-budget revoke never exceeds hard journal limits");
    }
    private static void ReviewError(string code,Action action,Action<bool,string> check)
    {
        try{action();throw new Exception("Expected "+code);}
        catch(ApplicationReviewException e){check(e.Code==code && e.Message=="Application review rejected." && e.InnerException is null && !e.ToString().Contains("private-test-marker"),code+" review error sanitized");}
    }
    private static void RegistrationError(string code,Action action,Action<bool,string> check)
    {try{action();throw new Exception("Expected "+code);}catch(RegistrationException e){check(e.Code==code,code+" registration error");}}
    private static void Sql(string directory,string sql)
    {using var c=Connection(directory);using var command=c.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static object? Scalar(string directory,string sql)
    {using var c=Connection(directory);using var command=c.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static SqliteConnection Connection(string directory)
    {var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"registration.db"),Pooling=false}.ToString());c.Open();return c;}
}
