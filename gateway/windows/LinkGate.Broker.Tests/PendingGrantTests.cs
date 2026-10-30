using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;
using LinkGate.Client;
using LinkGate.Contracts;
using LinkGate.Review;
using Microsoft.Data.Sqlite;

internal static class PendingGrantTests
{
    private const string Declaration="""
        {"manifestVersion":1,"applicationId":"test.importer","displayName":"Importer","exports":[],"imports":[{"alias":"files","protocol":"https","endpointMode":"brokered","logicalAuthority":"files.example.invalid","connectionPolicy":{"mode":"direct-only","allowRelay":false}}]}
        """;
    private static AppManifest Manifest(string app="test.importer",bool changed=false)=>ManifestContract.Parse(Encoding.UTF8.GetBytes(Declaration.Replace("test.importer",app).Replace("\"Importer\"",changed?"\"Changed importer\"":"\"Importer\"")));
    private static Guid Register(RegistrationStore store,string caller="caller",bool approve=true,string app="test.importer")
    {Guid reference=Guid.Parse(store.Register(caller,Guid.NewGuid(),Manifest(app)).GetProperty("applicationRef").GetString()!);if(approve)store.DecideReview(store.GetReview(reference),ApplicationReviewDecision.Approved,Guid.NewGuid());return reference;}
    private static PendingGrantRequest Request(Guid app,string reason="")=>new(app,"files","device-a","test.publisher","files",reason);
    private static Guid Reference(JsonElement value)=>Guid.Parse(value.GetProperty("grantRequestId").GetString()!);
    private static string State(RegistrationStore store,Guid reference,string caller="caller")=>store.PendingGrantStatus(caller,reference).GetProperty("state").GetString()!;
    public static async Task RunAsync(Action<bool,string> check)
    {
        string repo=AppContext.BaseDirectory;while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new Exception("Repository not found");
        string root=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-pending-grant-tests"));string directory=Path.Combine(root,Guid.NewGuid().ToString("N"));
        try
        {
            Contracts(check);Binding(Path.Combine(directory,"binding"),check);Storage(Path.Combine(directory,"storage"),check);
            Quotas(Path.Combine(directory,"quotas"),check);Migration(Path.Combine(directory,"migration"),check);
            await Concurrency(Path.Combine(directory,"concurrent"),check);await NativeSdk(Path.Combine(directory,"native"),check);
            await UnknownOutcome(Path.Combine(directory,"unknown"),check);
        }
        finally
        {
            string target=Path.GetFullPath(directory);if(!target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe cleanup path");
            if(Directory.Exists(target))Directory.Delete(target,true);
        }
    }
    private static void Contracts(Action<bool,string> check)
    {
        var request=Request(Guid.NewGuid());string json=request.ToJson();using var valid=JsonDocument.Parse(json);
        check(PendingGrantRequest.Parse(valid.RootElement).ToJson()==json && request.Reason=="","pending grant contract normalizes omitted reason");
        using var omitted=JsonDocument.Parse(json.Replace(",\"reason\":\"\"",""));check(PendingGrantRequest.Parse(omitted.RootElement).ToJson()==json,"omitted and empty reason have the same canonical parameters");
        foreach(string bad in new[]{json.Replace("\"reason\":\"\"","\"reason\":null"),json.Replace("\"device-a\"","\"https://private-marker\""),json.Replace("\"files\"","\"BAD_ALIAS\""),json.Replace("\"reason\":\"\"","\"reason\":\"x\",\"reason\":\"y\""),json.Replace("\"reason\":","\"connectionPolicy\":{},\"reason\":"),json.Replace("\"test.publisher\"","\"INVALID\"")})
        {
            using var document=JsonDocument.Parse(bad);try{PendingGrantRequest.Parse(document.RootElement);throw new Exception("Invalid request accepted");}
            catch(ContractException e){check(e.Code=="INVALID_REQUEST" && e.InnerException is null && !e.Message.Contains("private-marker",StringComparison.Ordinal),"strict grant parameters rejected without reflection");}
        }
        foreach(string reason in new[]{new string('x',257),"bad\nreason","bad\u0085reason","\ud800"})
            try{Request(Guid.NewGuid(),reason);throw new Exception("Invalid reason accepted");}catch(ContractException){check(true,"reason scalar/control/encoding bounds enforced");}
        check(Request(Guid.NewGuid(),string.Concat(Enumerable.Repeat("😀",256))).Reason.EnumerateRunes().Count()==256,"reason bound counts Unicode scalars");
    }
    private static void Binding(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid app=Register(store,approve:false);var request=Request(app);Guid op=Guid.NewGuid();
        Error("AUTH_REQUIRED",()=>store.RequestPendingGrant("caller",op,request),check);
        store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
        Error("AUTH_REQUIRED",()=>store.RequestPendingGrant("other",op,request),check);
        Error("AUTH_REQUIRED",()=>store.RequestPendingGrant("caller",op,new PendingGrantRequest(app,"missing","device-a","test.publisher","files")),check);
        Error("AUTH_REQUIRED",()=>store.RequestPendingGrant("caller",op,Request(Guid.NewGuid())),check);
        var result=store.RequestPendingGrant("caller",op,request);Guid reference=Reference(result);
        check(result.GetProperty("pendingUserApproval").GetBoolean() && State(store,reference)=="pending","request records only pending user approval");
        var snapshot=store.GetPendingGrantRequest(reference);
        check(snapshot.Request.ToJson()==request.ToJson() && snapshot.ApprovedRevision==1 && snapshot.DecisionVersion==1 && snapshot.BoundImportJson.Contains("direct-only",StringComparison.Ordinal) && snapshot.BoundImportJson.Contains("\"allowRelay\":false",StringComparison.Ordinal),"request binds exact approved import and restrictive policy");
        Error("AUTH_REQUIRED",()=>store.PendingGrantStatus("other",reference),check);
        Error("AUTH_REQUIRED",()=>store.PendingGrantStatus("caller",Guid.NewGuid()),check);
        store.Register("caller",Guid.NewGuid(),Manifest(changed:true));check(State(store,reference)=="pending","pending update does not replace original approved binding");
        store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Denied,Guid.NewGuid());
        check(State(store,reference)=="superseded","new review decision conservatively supersedes prior permission request");
        check(store.RequestPendingGrant("caller",op,request).GetRawText()==result.GetRawText() && State(store,reference)=="superseded","old success replay does not reactivate superseded request");
        Error("OPERATION_CONFLICT",()=>store.RequestPendingGrant("caller",op,Request(app,"different")),check);
        Guid next=Reference(store.RequestPendingGrant("caller",Guid.NewGuid(),request));check(next!=reference && State(store,next)=="pending","new explicit request captures current approved decision version");
        store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Revoked,Guid.NewGuid());
        check(State(store,next)=="superseded","revocation supersedes captured requests");
        Error("AUTH_REQUIRED",()=>store.RequestPendingGrant("caller",Guid.NewGuid(),request),check);
        check(new RegistrationStore(directory).Status("caller",op).GetProperty("result").GetRawText()==result.GetRawText(),"original request result survives restart without authority expansion");
        var page=store.ListPendingGrantReferences(limit:1);check(page.Items.Count==1 && page.NextCursor is not null && store.ListPendingGrantReferences(page.NextCursor,1).Items.Count==1,"host request enumeration is bounded and paged");
        Guid second=Register(store,app:"test.second");Guid secondRequest=Reference(store.RequestPendingGrant("caller",Guid.NewGuid(),Request(second)));
        check(store.GetPendingGrantRequest(secondRequest).Request.ApplicationRef==second && State(store,secondRequest)=="pending" && State(store,reference)=="superseded","same caller and import alias still select only the explicit application reference");
        var terminal=new InspectTerminal("show "+app,"requests","request "+reference,"approve","quit");
        using var journal=new ReviewIntentFile(directory);long version=store.GetReview(app).DecisionVersion;
        new ReviewWorkflow(store,terminal,journal).Run();
        check(terminal.Output.Any(x=>x.Contains("没有grant",StringComparison.Ordinal)) && store.GetReview(app).DecisionVersion==version && journal.Read() is null,"request inspection cannot become a grant or reuse declaration selection");
    }
    private sealed class InspectTerminal(params string[] commands):IReviewTerminal
    {private readonly Queue<string> input=new(commands);public readonly List<string> Output=[];public string? ReadLine()=>input.Count==0?null:input.Dequeue();public void WriteLine(string value)=>Output.Add(value);}
    private static void Storage(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid app=Register(store);var request=Request(app);Guid op=Guid.NewGuid();var capacity=store.GetJournalCapacity();
        Sql(directory,"CREATE TRIGGER fail_grant BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'private-marker'); END;");
        Error("STORAGE_UNAVAILABLE",()=>store.RequestPendingGrant("caller",op,request),check);
        check(Count(directory,"grant_requests")==0 && store.GetJournalCapacity()==capacity,"failed journal insertion rolls back request and preserves revocation reserve");
        Sql(directory,"DROP TRIGGER fail_grant");Guid reference=Reference(store.RequestPendingGrant("caller",op,request));
        check(Count(directory,"grant_requests")==1 && State(store,reference)=="pending","explicit retry after proven rollback commits once");
        string originalImport=store.GetPendingGrantRequest(reference).BoundImportJson;
        Sql(directory,"UPDATE grant_requests SET request=$large",("$large",new string('x',8193)));Error("STORAGE_UNAVAILABLE",()=>store.PendingGrantStatus("caller",reference),check);
        Sql(directory,"UPDATE grant_requests SET request=$request,import_json=$large",("$request",request.ToJson()),("$large",new string('x',16385)));Error("STORAGE_UNAVAILABLE",()=>store.PendingGrantStatus("caller",reference),check);
        Sql(directory,"UPDATE grant_requests SET import_json=$import",("$import",originalImport));
        Sql(directory,"UPDATE grant_requests SET import_json='private-marker'");Error("STORAGE_UNAVAILABLE",()=>store.PendingGrantStatus("caller",reference),check);
        Sql(directory,"UPDATE operations SET result='{\"grantRequestId\":\"bad\",\"pendingUserApproval\":false}' WHERE operation=$op",("$op",op.ToString()));
        Error("STORAGE_UNAVAILABLE",()=>store.RequestPendingGrant("caller",op,request),check);
        var protocol=new ControlProtocol(store,true);var disabled=new ControlProtocol(store);
        byte[] wire=Wire("grant.request",request,Guid.NewGuid());
        check(ResponseCode(disabled.Dispatch(wire,"caller"))=="CAPABILITY_UNSUPPORTED" && ResponseCode(protocol.Dispatch(wire))=="AUTH_REQUIRED","pending grant mode is explicit and still requires platform identity");
        using var inject=JsonDocument.Parse(request.ToJson().Replace("\"reason\":","\"caller\":\"other\",\"reason\":"));
        check(ResponseCode(protocol.Dispatch(WireObject("grant.request",inject.RootElement,Guid.NewGuid()),"caller"))=="INVALID_REQUEST","caller injection rejected before any request write");
        using var empty=JsonDocument.Parse("{}");
        foreach(string method in new[]{"grant.approve","grant.revoke","grant.list","service.publish","connection.open"})
            check(ResponseCode(protocol.Dispatch(WireObject(method,empty.RootElement,Guid.NewGuid()),"caller"))=="CAPABILITY_UNSUPPORTED","pending request mode never enables grant authority or data methods");
    }
    private static void Quotas(string directory,Action<bool,string> check)
    {
        var small=new RegistrationStore(directory,5,3,10);Guid app=Register(small);var capacity=small.GetJournalCapacity();
        Error("RESOURCE_EXHAUSTED",()=>small.RequestPendingGrant("caller",Guid.NewGuid(),Request(app)),check);
        check(Count(directory,"grant_requests")==0 && small.GetJournalCapacity()==capacity,"permission requests cannot consume reserved revocation capacity");
        small.DecideReview(small.GetReview(app),ApplicationReviewDecision.Revoked,Guid.NewGuid());check(small.GetReview(app).ApprovedManifest is null,"full-budget revocation still works after rejected permission request");
        string bulk=Path.Combine(directory,"rows");var store=new RegistrationStore(bulk);Guid originalOp=Guid.NewGuid();JsonElement original=default;
        for(int group=0;group<16;group++)
        {
            string caller="scope-"+group;Guid application=Register(store,caller);var request=Request(application);
            var import=store.GetReview(application).ApprovedManifest!;using var document=JsonDocument.Parse(import.ToJson());string bound=document.RootElement.GetProperty("imports")[0].GetRawText();
            int start=1;if(group==0){original=store.RequestPendingGrant(caller,originalOp,request);start=2;}
            Sql(bulk,"""
                WITH RECURSIVE n(value) AS(SELECT $start UNION ALL SELECT value+1 FROM n WHERE value<1024)
                INSERT INTO grant_requests(reference,caller,application,request,approved_revision,decision_version,import_json)
                SELECT printf('00000000-0000-4000-8000-%012x',value+$group*1024),$caller,$application,$request,1,1,$import FROM n;
                """,("$start",start),("$group",group),("$caller",caller),("$application",application.ToString()),("$request",request.ToJson()),("$import",bound));
            if(group==0)
            {
                Error("RESOURCE_EXHAUSTED",()=>store.RequestPendingGrant(caller,Guid.NewGuid(),request),check);
                check(store.RequestPendingGrant(caller,originalOp,request).GetRawText()==original.GetRawText(),"caller request cap preserves original operation replay");
            }
        }
        Guid extra=Register(store,"extra");Error("RESOURCE_EXHAUSTED",()=>store.RequestPendingGrant("extra",Guid.NewGuid(),Request(extra)),check);
        check(Count(bulk,"grant_requests")==16384 && store.GetJournalCapacity().WithinLimits && store.GetJournalCapacity().ReservedRevocations==17,"global retained-request cap is independent of and preserves logical journal reservations");
    }
    private static void Migration(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);Guid app=Register(store);long operations=Count(directory,"operations");
        Sql(directory,"DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; DROP TABLE local_access; DROP TABLE secure_opens; DROP TABLE secure_grants; DROP TABLE secure_peers; DROP TABLE secure_identity; DROP TABLE grant_requests; ALTER TABLE application_reviews DROP COLUMN authority_epoch; PRAGMA user_version=3");var migrated=new RegistrationStore(directory);
        check(Convert.ToInt64(Scalar(directory,"PRAGMA user_version"))==8 && migrated.GetReview(app).ApprovedManifest is not null && Count(directory,"operations")==operations && Count(directory,"grant_requests")==0,"schema3 migration preserves approval/results without inventing requests");
        Guid op=Guid.NewGuid();var request=Request(app);var first=migrated.RequestPendingGrant("caller",op,request);
        check(new RegistrationStore(directory).RequestPendingGrant("caller",op,request).GetRawText()==first.GetRawText(),"schema4 request persists across restart");
        Sql(directory,"DROP TABLE grant_requests");
        try{_=new RegistrationStore(directory);throw new Exception("Missing request table recreated");}catch(InvalidDataException e){check(e.Message=="Registration storage unavailable." && Convert.ToInt64(Scalar(directory,"PRAGMA user_version"))==8,"schema8 missing request table fails closed without reset");}
        string collision=Path.Combine(directory,"index-collision");var prior=new RegistrationStore(collision);Register(prior);
        Sql(collision,"DROP TABLE connection_attachments; DROP TABLE connections; DROP INDEX secure_opens_active; DROP TABLE local_access; DROP TABLE secure_opens; DROP TABLE secure_grants; DROP TABLE secure_peers; DROP TABLE secure_identity; DROP TABLE grant_requests; ALTER TABLE application_reviews DROP COLUMN authority_epoch; CREATE INDEX grant_requests_caller ON applications(caller); PRAGMA user_version=3");
        try{_=new RegistrationStore(collision);throw new Exception("Partial migration accepted");}
        catch(InvalidDataException e){check(e.Message=="Registration storage unavailable." && e.InnerException is null && Convert.ToInt64(Scalar(collision,"PRAGMA user_version"))==3 &&
            Convert.ToInt64(Scalar(collision,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='grant_requests'"))==0 && Count(collision,"operations")==2 &&
            Convert.ToInt64(Scalar(collision,"SELECT COUNT(*) FROM application_reviews WHERE approved_manifest IS NOT NULL"))==1,"failed schema3 index creation rolls back new table and preserves approval/results");}
    }
    private static async Task Concurrency(string directory,Action<bool,string> check)
    {
        var one=new RegistrationStore(directory);Guid app=Register(one);var two=new RegistrationStore(directory);Guid op=Guid.NewGuid();var request=Request(app);
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(i=>Task.Run(()=>(i%2==0?one:two).RequestPendingGrant("caller",op,request).GetRawText())));
        check(results.Distinct().Count()==1 && Count(directory,"grant_requests")==1 && Count(directory,"operations")==3,"concurrent same-operation requests create one request and one result");
    }
    private static async Task NativeSdk(string directory,Action<bool,string> check)
    {
        var store=new RegistrationStore(directory);using var host=new PipeHost(Guid.NewGuid().ToString("N"),store,true);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Task serving=host.RunAsync(stop.Token);using var process=Process.GetCurrentProcess();Guid savedOperation=Guid.Empty;
        try
        {
            using var client=await VerifiedPipeClient.ConnectAsync(host.PipeName,process.SafeHandle,stop.Token);var hello=await client.HelloAsync(stop.Token);
            check(hello.GetProperty("capabilities").EnumerateArray().Any(c=>c.GetProperty("name").GetString()=="grant.request.pending" && c.GetProperty("state").GetString()=="experimental") &&
                hello.GetProperty("capabilities").EnumerateArray().Where(c=>c.GetProperty("name").GetString()=="identity.device-grant").All(c=>c.GetProperty("state").GetString()=="unsupported"),"native hello advertises pending only and no active grant authority");
            var registered=await client.RegisterPendingAsync(Manifest(),Guid.NewGuid(),true,stop.Token);Guid app=Guid.Parse(registered.GetProperty("applicationRef").GetString()!);var request=Request(app);
            await SdkError("AUTH_REQUIRED",()=>client.RequestGrantPendingAsync(request,Guid.NewGuid(),true,stop.Token),check);
            store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
            try{await client.RequestGrantPendingAsync(request,Guid.NewGuid(),false,stop.Token);throw new Exception("Implicit pending grant mutation");}catch(InvalidOperationException){check(Count(directory,"grant_requests")==0,"SDK opt-in rejects before writing");}
            Guid op=Guid.NewGuid();savedOperation=op;var pending=await client.RequestGrantPendingAsync(request,op,true,stop.Token);Guid reference=Reference(pending);
            var status=await client.GetGrantRequestStatusAsync(reference,stop.Token);var historical=await client.GetGrantOperationStatusAsync(op,stop.Token);
            check(status.GetProperty("state").GetString()=="pending" && historical.GetProperty("result").GetRawText()==pending.GetRawText(),"actual pinned SDK requests and explicitly queries both status types");
            Guid foreign=Register(store,"foreign");await SdkError("AUTH_REQUIRED",()=>client.RequestGrantPendingAsync(Request(foreign),Guid.NewGuid(),true,stop.Token),check);
            await SdkError("AUTH_REQUIRED",()=>client.GetGrantRequestStatusAsync(Guid.NewGuid(),stop.Token),check);
            await client.RegisterPendingAsync(Manifest(changed:true),Guid.NewGuid(),true,stop.Token);
            check((await client.GetGrantRequestStatusAsync(reference,stop.Token)).GetProperty("state").GetString()=="pending","native pending update retains approved import binding");
            store.DecideReview(store.GetReview(app),ApplicationReviewDecision.Approved,Guid.NewGuid());
            check((await client.GetGrantRequestStatusAsync(reference,stop.Token)).GetProperty("state").GetString()=="superseded" &&
                (await client.GetGrantOperationStatusAsync(op,stop.Token)).GetProperty("status").GetString()=="succeeded","SDK separates historical success from superseded request eligibility");
        }
        finally{stop.Cancel();try{await serving;}catch(OperationCanceledException){}}
        using var disabled=new PipeHost(Guid.NewGuid().ToString("N"),store);using var end=new CancellationTokenSource(TimeSpan.FromSeconds(10));Task disabledServing=disabled.RunAsync(end.Token);
        try
        {
            using var client=await VerifiedPipeClient.ConnectAsync(disabled.PipeName,process.SafeHandle,end.Token);await client.HelloAsync(end.Token);
            try{await client.RequestGrantPendingAsync(Request(Guid.NewGuid()),Guid.NewGuid(),true,end.Token);throw new Exception("Default host enabled grant requests");}
            catch(InvalidOperationException){check(Count(directory,"grant_requests")==1,"SDK rejects pending grant writes when capability is disabled");}
            check((await client.GetGrantOperationStatusAsync(savedOperation,end.Token)).GetProperty("status").GetString()=="succeeded","original grant operation can still be queried after pending feature is disabled");
        }
        finally{end.Cancel();try{await disabledServing;}catch(OperationCanceledException){}}
    }
    private static async Task UnknownOutcome(string directory,Action<bool,string> check)
    {
        foreach(bool malformed in new[]{false,true})
        {
            string state=Path.Combine(directory,malformed?"malformed":"dropped");var store=new RegistrationStore(state);Guid app=Register(store,"fault-caller");Guid operation=Guid.NewGuid();int mutations=0;
            using var process=Process.GetCurrentProcess();
            async Task Peer(Func<string,Task> action,bool fail)
            {
                string name="LinkGate."+new string('a',64)+".control.v1.test."+Guid.NewGuid().ToString("N");
                using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));var protocol=new ControlProtocol(store,true);
                async Task Serve()
                {
                    try
                    {
                        await server.WaitForConnectionAsync(stop.Token);
                        while(await FrameCodec.ReadAsync(server,stop.Token) is byte[] input)
                        {
                            using var request=JsonDocument.Parse(input);bool mutation=request.RootElement.GetProperty("method").GetString()=="grant.request";
                            byte[] output=protocol.Dispatch(input,"fault-caller");if(mutation)Interlocked.Increment(ref mutations);
                            if(mutation&&fail)
                            {
                                if(!malformed){server.Dispose();return;}
                                output=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(output).Replace("\"pendingUserApproval\":true","\"pendingUserApproval\":false"));
                            }
                            await FrameCodec.WriteAsync(server,output,stop.Token);
                        }
                    }
                    catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException){}
                }
                Task serving=Serve();try{await action(name);}finally{stop.Cancel();server.Dispose();await serving;}
            }
            await Peer(async name=>
            {
                using var client=await VerifiedPipeClient.ConnectAsync(name,process.SafeHandle);await client.HelloAsync();
                try{await client.RequestGrantPendingAsync(Request(app),operation,true);throw new Exception("Lost or bad reply accepted");}
                catch(MutationOutcomeUnknownException e){check(e.OperationId==operation && e.InnerException is null,"grant mutation uncertainty retains original operation UUID");}
            },true);
            await Peer(async name=>
            {
                using var client=await VerifiedPipeClient.ConnectAsync(name,process.SafeHandle);await client.HelloAsync();var result=await client.GetGrantOperationStatusAsync(operation);
                check(result.GetProperty("status").GetString()=="succeeded" && (await client.GetGrantRequestStatusAsync(Reference(result.GetProperty("result")))).GetProperty("state").GetString()=="pending","reconnect explicitly recovers committed permission request");
            },false);
            check(mutations==1 && Count(state,"grant_requests")==1 && Count(state,"operations")==3,"lost or malformed grant reply never triggers automatic mutation replay");
        }
    }
    private static async Task SdkError(string code,Func<Task<JsonElement>> action,Action<bool,string> check)
    {try{await action();throw new Exception("Expected "+code);}catch(BrokerRequestException e){check(e.Code==code && e.InnerException is null,code+" fixed SDK error");}}
    private static void Error(string code,Action action,Action<bool,string> check)
    {try{action();throw new Exception("Expected "+code);}catch(RegistrationException e){check(e.Code==code && e.InnerException is null && !e.ToString().Contains("private-marker",StringComparison.Ordinal),code+" fixed storage error");}}
    private static byte[] Wire(string method,PendingGrantRequest request,Guid operation)
    {using var document=JsonDocument.Parse(request.ToJson());return WireObject(method,document.RootElement,operation);}
    private static byte[] WireObject(string method,JsonElement parameters,Guid operation)=>JsonSerializer.SerializeToUtf8Bytes(new{kind="request",apiVersion=1,requestId=Guid.NewGuid().ToString(),method,operationId=operation.ToString(),@params=parameters});
    private static string ResponseCode(byte[] bytes){using var document=JsonDocument.Parse(bytes);return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;}
    private static long Count(string directory,string table)=>Convert.ToInt64(Scalar(directory,"SELECT COUNT(*) FROM "+table));
    private static void Sql(string directory,string sql,params(string,object)[] values)
    {using var c=Connection(directory);using var command=c.CreateCommand();command.CommandText=sql;foreach(var(name,value)in values)command.Parameters.AddWithValue(name,value);command.ExecuteNonQuery();}
    private static object? Scalar(string directory,string sql)
    {using var c=Connection(directory);using var command=c.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static SqliteConnection Connection(string directory)
    {var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"registration.db"),Pooling=false}.ToString());c.Open();return c;}
}
