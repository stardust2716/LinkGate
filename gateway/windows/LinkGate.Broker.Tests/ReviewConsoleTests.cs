using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Contracts;
using LinkGate.Review;
using Microsoft.Data.Sqlite;

internal static class ReviewConsoleTests
{
    private const string Declaration="""
        {"manifestVersion":1,"applicationId":"test.console","displayName":"Console","exports":[{"serviceId":"files","label":"Files","protocol":"http","localTarget":{"host":"127.0.0.1","port":18481}}],"imports":[]}
        """;
    private static Guid Register(RegistrationStore store,string caller="caller",int port=18481)=>Guid.Parse(store.Register(caller,Guid.NewGuid(),
        ManifestContract.Parse(Encoding.UTF8.GetBytes(Declaration.Replace("18481",port.ToString(System.Globalization.CultureInfo.InvariantCulture))))).GetProperty("applicationRef").GetString()!);
    private sealed class Terminal(params string[] commands) : IReviewTerminal
    {
        private readonly Queue<string> input=new(commands);
        public readonly List<string> Output=[];
        public Action? Confirming;
        private string? phrase;
        public string? ReadLine()
        {
            if(input.Count==0)return null;string next=input.Dequeue();
            if(next=="!confirm"){Confirming?.Invoke();return phrase??throw new Exception("No challenge rendered");}
            return next=="!EOF"?null:next;
        }
        public void WriteLine(string text){Output.Add(text);if(text.StartsWith("确认短语：",StringComparison.Ordinal))phrase=text[5..];}
        public bool Contains(string value)=>Output.Any(line=>line.Contains(value,StringComparison.Ordinal));
    }
    private sealed class FaultJournal(IReviewIntentJournal inner) : IReviewIntentJournal
    {
        public bool FailPrepare,FailAck;
        public ReviewIntent? Read()=>inner.Read();
        public void Prepare(ReviewIntent intent){if(FailPrepare)throw new ReviewJournalException("REVIEW_JOURNAL_UNAVAILABLE");inner.Prepare(intent);}
        public void Acknowledge(Guid operation){if(FailAck)throw new ReviewJournalException("REVIEW_JOURNAL_UNAVAILABLE");inner.Acknowledge(operation);}
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;
        while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new Exception("Repository not found");
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-review-console-tests"));string directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            Paging(Path.Combine(directory,"paging"),check);
            Confirmation(Path.Combine(directory,"confirmation"),check);
            Workflow(Path.Combine(directory,"workflow"),check);
            Recovery(Path.Combine(directory,"recovery"),check);
            StaleAndAbandon(Path.Combine(directory,"stale"),check);
            Receipt(Path.Combine(directory,"receipt"),check);
            Text(check);
            await Redirected(repo,Path.Combine(directory,"redirected"),check);
        }
        finally
        {
            string target=Path.GetFullPath(directory);
            if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe cleanup path");
            if(Directory.Exists(target))Directory.Delete(target,true);
        }
    }
    private static void Paging(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);var expected=Enumerable.Range(0,5).Select(i=>Register(store,"caller-"+i)).OrderBy(x=>x.ToString(),StringComparer.Ordinal).ToArray();
        var seen=new List<Guid>();Guid? cursor=null;int pages=0;
        do{var page=store.ListReviewReferences(cursor,2);seen.AddRange(page.Items);cursor=page.NextCursor;pages++;}while(cursor is not null);
        check(seen.SequenceEqual(expected) && pages==3,"keyset paging returns each application reference once");
        var first=store.ListReviewReferences(limit:1);
        try{((IList<Guid>)first.Items).Clear();throw new Exception("Mutable reference page");}catch(NotSupportedException){check(true,"reference page collection is immutable");}
        foreach(int limit in new[]{0,201})try{store.ListReviewReferences(limit:limit);throw new Exception("Unbounded page accepted");}catch(ArgumentOutOfRangeException){check(true,"page limit enforced");}
        check(store.GetReview(expected[0]).CallerScope.StartsWith("caller-",StringComparison.Ordinal),"host snapshot exposes scope provenance without binary verification claim");
        Sql(directory,"UPDATE applications SET reference='invalid' WHERE caller='caller-0'");
        try{store.ListReviewReferences();throw new Exception("Corrupt reference accepted");}catch(ApplicationReviewException e){check(e.Code=="STORAGE_UNAVAILABLE" && e.InnerException is null,"corrupt reference fails closed");}
    }
    private static void Confirmation(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid reference=Register(store);using var journal=new ReviewIntentFile(directory);
        var terminal=new Terminal("approve","show "+reference,"approve","wrong","approve","show "+reference,"deny","!EOF");
        check(new ReviewWorkflow(store,terminal,journal).Run()==0 && store.GetReview(reference).DecisionVersion==0 && journal.Read() is null,"missing snapshot, wrong confirmation and EOF do not prepare or submit");
        check(terminal.Contains("先 show") && terminal.Contains("已取消"),"cancellation and missing selection are explicit");
        terminal=new Terminal("show "+reference,"show invalid","approve","quit");new ReviewWorkflow(store,terminal,journal).Run();
        check(store.GetReview(reference).DecisionVersion==0 && journal.Read() is null,"invalid navigation clears prior decision target");
        var failure=new FaultJournal(journal){FailPrepare=true};terminal=new Terminal("show "+reference,"approve","!confirm","quit");
        new ReviewWorkflow(store,terminal,failure).Run();
        check(store.GetReview(reference).DecisionVersion==0 && journal.Read() is null && terminal.Contains("REVIEW_JOURNAL_UNAVAILABLE"),"failed receipt preparation sends no Broker decision");
    }
    private static void Workflow(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid reference=Register(store);using var journal=new ReviewIntentFile(directory);
        var terminal=new Terminal("list","capacity","show "+reference,"approve","!confirm","quit");
        check(new ReviewWorkflow(store,terminal,journal).Run()==0 && store.GetReview(reference).ApprovedManifest is not null && store.GetReview(reference).DecisionVersion==1,"confirmed workflow approves exact rendered declaration");
        var approved=journal.Read()!;
        check(approved.Acknowledged && approved.Decision==ApplicationReviewDecision.Approved && store.GetReviewOperation(approved.OperationId).DecisionVersion==1,"operation receipt persists before decision and acknowledges known result");
        check(terminal.Contains("待审核完整声明") && terminal.Contains("先前批准完整声明") && terminal.Contains("18481") && terminal.Contains("不是二进制认证"),"review renders full declarations and identity boundary");
        terminal=new Terminal("show "+reference,"deny","!confirm","show "+reference,"revoke","!confirm","operation "+approved.OperationId,"quit");
        new ReviewWorkflow(store,terminal,journal).Run();
        check(store.GetReview(reference).LastDecision==ApplicationReviewDecision.Revoked && store.GetReview(reference).DecisionVersion==3 && store.GetReview(reference).ApprovedManifest is null,"console supports denial and explicit revocation");
        check(terminal.Contains("历史结果（不代表当前批准）") && journal.Read() is {Acknowledged:true,Decision:ApplicationReviewDecision.Revoked},"historical approval does not restore current approval");
        string full=Path.Combine(directory,"full");var fullStore=new RegistrationStore(full,2,3,10);Guid candidate=Register(fullStore);
        fullStore.DecideReview(fullStore.GetReview(candidate),ApplicationReviewDecision.Approved,Guid.NewGuid());
        using var fullJournal=new ReviewIntentFile(full);terminal=new Terminal("show "+candidate,"revoke","!confirm","quit");
        new ReviewWorkflow(fullStore,terminal,fullJournal).Run();
        check(fullJournal.Read() is {Acknowledged:true,Decision:ApplicationReviewDecision.Revoked} && fullStore.GetReview(candidate).ApprovedManifest is null &&
            fullStore.GetJournalCapacity().RetainedOperations==3 && fullStore.GetJournalCapacity().WithinLimits,"interactive workflow preserves reserved revocation at full logical capacity");
    }
    private static void Recovery(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid reference=Register(store);
        Guid operation;
        using(var journal=new ReviewIntentFile(directory))
        {
            var failure=new FaultJournal(journal){FailAck=true};var terminal=new Terminal("show "+reference,"approve","!confirm","show "+reference,"revoke","quit");
            new ReviewWorkflow(store,terminal,failure).Run();operation=journal.Read()!.OperationId;
            check(journal.Read() is {Acknowledged:false} && store.GetReview(reference).DecisionVersion==1 && store.GetReview(reference).ApprovedManifest is not null,"failed acknowledgement retains uncertain receipt and blocks further decisions");
            check(terminal.Contains("待恢复操作阻止新决定") && Count(directory)==2,"uncertain workflow never automatically retries or submits another decision");
        }
        using(var journal=new ReviewIntentFile(directory))
        {
            var terminal=new Terminal("recover","quit");new ReviewWorkflow(new RegistrationStore(directory),terminal,journal).Run();
            check(journal.Read() is {Acknowledged:true} receipt && receipt.OperationId==operation && Count(directory)==2 && store.GetReview(reference).DecisionVersion==1,"restart recovery queries original operation without replay");
            check(terminal.Contains("原操作已查询并确认"),"recovery reports original acknowledgement");
        }
        string mismatch=Path.Combine(directory,"mismatch");var otherStore=new RegistrationStore(mismatch);Guid other=Register(otherStore);var snapshot=otherStore.GetReview(other);
        using(var journal=new ReviewIntentFile(mismatch))
        {
            Guid op=Guid.NewGuid();journal.Prepare(new ReviewIntent(op,other,1,0,ApplicationReviewDecision.Approved));
            otherStore.DecideReview(snapshot,ApplicationReviewDecision.Denied,op);
            var terminal=new Terminal("recover","quit");new ReviewWorkflow(otherStore,terminal,journal).Run();
            check(journal.Read() is {Acknowledged:false} && terminal.Contains("REVIEW_RESULT_MISMATCH") && otherStore.GetReview(other).DecisionVersion==1,"recovery rejects an operation result that does not match prepared intent");
        }
    }
    private static void StaleAndAbandon(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid reference=Register(store);using var journal=new ReviewIntentFile(directory);
        var terminal=new Terminal("show "+reference,"approve","!confirm","recover","quit");
        terminal.Confirming=()=>Register(store,port:18482);
        new ReviewWorkflow(store,terminal,journal).Run();
        check(store.GetReview(reference).DecisionVersion==0 && store.GetReview(reference).PendingRevision==2 && journal.Read() is {Acknowledged:false},"concurrent pending update rejects rendered stale confirmation");
        check(terminal.Contains("REVIEW_STALE") && terminal.Contains("未查到原操作") && Count(directory)==2,"unknown result is reported without replay");
        terminal=new Terminal("abandon","wrong","show "+reference,"approve","quit");new ReviewWorkflow(store,terminal,journal).Run();
        check(journal.Read() is {Acknowledged:false} && Count(directory)==2,"cancelled abandonment leaves recovery block intact");
        terminal=new Terminal("abandon","!confirm","show "+reference,"approve","!confirm","quit");new ReviewWorkflow(store,terminal,journal).Run();
        check(journal.Read() is {Acknowledged:true} && store.GetReview(reference).ApprovedManifest?.Exports[0].LocalTarget.Port==18482 && store.GetReview(reference).DecisionVersion==1 && Count(directory)==3,"explicit abandon permits only a newly rendered and confirmed decision");
        Guid prepared=Guid.NewGuid();var current=store.GetReview(reference);journal.Prepare(new ReviewIntent(prepared,reference,current.PendingRevision,current.DecisionVersion,ApplicationReviewDecision.Revoked));
        store.DecideReview(current,ApplicationReviewDecision.Revoked,prepared);terminal=new Terminal("abandon","quit");new ReviewWorkflow(store,terminal,journal).Run();
        check(journal.Read() is {Acknowledged:false} && terminal.Contains("已有历史结果，请 recover"),"abandonment refuses to hide an existing operation result");
    }
    private static void Receipt(string directory,Action<bool,string> check)
    {
        _=new RegistrationStore(directory);var intent=new ReviewIntent(Guid.NewGuid(),Guid.NewGuid(),1,0,ApplicationReviewDecision.Approved);
        using(var journal=new ReviewIntentFile(directory))
        {
            try{using var second=new ReviewIntentFile(directory);throw new Exception("Concurrent console lease accepted");}
            catch(ReviewJournalException e){check(e.Code=="REVIEW_BUSY_OR_UNAVAILABLE" && e.InnerException is null,"exclusive receipt lease rejects another console");}
            journal.Prepare(intent);check(journal.Read()==intent,"prepared receipt round trips strict data");
            try{journal.Prepare(intent with{OperationId=Guid.NewGuid()});throw new Exception("Unresolved receipt overwritten");}
            catch(ReviewJournalException e){check(e.Code=="REVIEW_RECOVERY_REQUIRED" && journal.Read()==intent,"unresolved receipt cannot be overwritten");}
            try{journal.Acknowledge(Guid.NewGuid());throw new Exception("Wrong acknowledgement accepted");}
            catch(ReviewJournalException e){check(e.Code=="REVIEW_JOURNAL_UNAVAILABLE" && journal.Read()==intent,"acknowledgement must match original operation");}
            journal.Acknowledge(intent.OperationId);check(journal.Read()==intent with{Acknowledged=true},"acknowledged receipt retains original references");
        }
        using(var reopened=new ReviewIntentFile(directory)){check(reopened.Read()==intent with{Acknowledged=true},"receipt survives lease release and reopen");}
        string path=Path.Combine(directory,".last-review-intent.json");
        foreach(byte[] corrupt in new[]{Encoding.UTF8.GetBytes("{\"operationId\":1,\"operationId\":2}"),new byte[2049],Encoding.UTF8.GetBytes("private-test-marker")})
        {
            File.WriteAllBytes(path,corrupt);using var journal=new ReviewIntentFile(directory);
            try{journal.Read();throw new Exception("Corrupt receipt reset");}
            catch(ReviewJournalException e){check(e.Code=="REVIEW_JOURNAL_UNAVAILABLE" && e.InnerException is null && !e.ToString().Contains("private-test-marker") && File.ReadAllBytes(path).SequenceEqual(corrupt),"corrupt or oversized receipt fails closed without reset");}
        }
        File.Delete(path);Directory.CreateDirectory(path);
        try{using var journal=new ReviewIntentFile(directory);throw new Exception("Directory accepted as receipt file");}
        catch(ReviewJournalException){check(true,"receipt path guard rejects directory collision");}
        string unprotected=Path.Combine(directory,"unprotected");Directory.CreateDirectory(unprotected);
        try{using var journal=new ReviewIntentFile(unprotected);throw new Exception("Unprotected state directory accepted");}
        catch(ReviewJournalException e){check(e.Code=="REVIEW_JOURNAL_UNAVAILABLE" && !File.Exists(Path.Combine(unprotected,".review-console.lock")),"receipt lease requires protected Broker state ACL");}
        string broad=Path.Combine(directory,"broad-receipt");_=new RegistrationStore(broad);
        using(var journal=new ReviewIntentFile(broad)){journal.Prepare(intent);}
        string broadPath=Path.Combine(broad,".last-review-intent.json");var file=new FileInfo(broadPath);var acl=file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.Read,AccessControlType.Allow));file.SetAccessControl(acl);
        try{using var journal=new ReviewIntentFile(broad);throw new Exception("Broad receipt ACL accepted");}
        catch(ReviewJournalException e){check(e.Code=="REVIEW_JOURNAL_UNAVAILABLE","receipt guard rejects other-user read access");}
    }
    private static void Text(Action<bool,string> check)
    {
        string quoted=ReviewText.Quote("label\n\u001b[2J\u202e伪造提示");
        check(quoted.All(c=>c<128) && !quoted.Contains('\u001b') && !quoted.Contains('\u202e') && !quoted.Contains('\n') && quoted.Contains("\\u001B",StringComparison.OrdinalIgnoreCase),"untrusted terminal and Unicode controls are ASCII escaped");
        foreach(string input in new[]{new string('x',129)+"\n","approve\u001b[2J\n","approve\tall\n","approve\rX\n","unterminated"})
        {
            var terminal=new TextReviewTerminal(new StringReader(input),new StringWriter());
            try{terminal.ReadLine();throw new Exception("Invalid terminal input accepted");}catch(ReviewInputException){check(true,"bounded terminal rejects oversized/control/partial input");}
        }
        var valid=new TextReviewTerminal(new StringReader(new string('x',128)+"\r\n"),new StringWriter());check(valid.ReadLine()?.Length==128 && valid.ReadLine() is null,"terminal accepts exact bound and empty EOF");
    }
    private static async Task Redirected(string repo,string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Register(store);Sql(directory,"DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; DROP TABLE local_access; DROP TABLE secure_opens; DROP TABLE secure_grants; DROP TABLE secure_peers; DROP TABLE secure_identity; DROP TABLE grant_requests; ALTER TABLE application_reviews DROP COLUMN authority_epoch; PRAGMA user_version=2");
        byte[] before=SHA256.HashData(File.ReadAllBytes(Path.Combine(directory,"registration.db")));
        string executable=Path.Combine(repo,"gateway/windows/LinkGate.Review.Console/bin/Release/net10.0-windows/LinkGate.Review.Console.exe");
        foreach(string target in new[]{directory,Path.Combine(directory,"not-created")})
        {
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(target);
            using var process=Process.Start(start)??throw new Exception("Review child failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try{await process.WaitForExitAsync(timeout.Token);string error=await process.StandardError.ReadToEndAsync(timeout.Token);check(process.ExitCode==2 && error.Contains("state was not opened",StringComparison.Ordinal),"actual redirected executable rejects before state initialization");}
            finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
        }
        check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory,"registration.db")))) && !Directory.Exists(Path.Combine(directory,"not-created")) && !File.Exists(Path.Combine(directory,".review-console.lock")),"redirected refusal neither migrates existing database nor creates state or lease");
    }
    private static long Count(string directory)
    {using var c=Connection(directory);using var command=c.CreateCommand();command.CommandText="SELECT COUNT(*) FROM operations";return Convert.ToInt64(command.ExecuteScalar());}
    private static void Sql(string directory,string sql)
    {using var c=Connection(directory);using var command=c.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static SqliteConnection Connection(string directory)
    {var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"registration.db"),Pooling=false}.ToString());c.Open();return c;}
}
