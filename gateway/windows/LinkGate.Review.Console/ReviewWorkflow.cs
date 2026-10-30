using System.Security.Cryptography;
using LinkGate.Broker;

namespace LinkGate.Review;

public sealed class ReviewWorkflow(RegistrationStore store,IReviewTerminal terminal,IReviewIntentJournal journal)
{
    private ApplicationReviewSnapshot? selected;
    private Guid? next;
    private Guid? grantNext;
    public int Run()
    {
        terminal.WriteLine(ReviewText.Boundary);
        var last=journal.Read();if(last is not null)terminal.WriteLine("上次操作："+last.OperationId+(last.Acknowledged?" 已确认":" 待恢复；新决定已阻止"));
        terminal.WriteLine("命令：list / next / show <UUID> / capacity / approve / deny / revoke / operation <UUID> / recover / abandon / requests / requests-next / request <UUID> / quit");
        try
        {
            while(true)
            {
                terminal.WriteLine(">");string? command=Read();if(command is null)return 0;
                string[] words=command.Split(' ',StringSplitOptions.RemoveEmptyEntries);if(words.Length==0)continue;
                if(words[0] is "show" or "list" or "next" or "request" or "requests" or "requests-next")selected=null;
                try
                {
                    if(words.Length==1)
                    {
                        switch(words[0])
                        {
                            case "quit": return 0;
                            case "list": List(null);continue;
                            case "next": if(next is Guid cursor)List(cursor);else terminal.WriteLine("没有下一页；list 刷新。");continue;
                            case "capacity": terminal.WriteLine(System.Text.Json.JsonSerializer.Serialize(store.GetJournalCapacity()));continue;
                            case "approve": Decide(ApplicationReviewDecision.Approved);continue;
                            case "deny": Decide(ApplicationReviewDecision.Denied);continue;
                            case "revoke": Decide(ApplicationReviewDecision.Revoked);continue;
                            case "recover": Recover();continue;
                            case "abandon": Abandon();continue;
                            case "requests": ListRequests(null);continue;
                            case "requests-next": if(grantNext is Guid grantCursor)ListRequests(grantCursor);else terminal.WriteLine("没有下一页申请；requests 刷新。");continue;
                        }
                    }
                    if(words.Length==2 && Guid.TryParseExact(words[1],"D",out Guid reference))
                    {
                        if(words[0]=="show")
                        {
                            selected=null;var snapshot=store.GetReview(reference);
                            foreach(string line in ReviewText.Snapshot(snapshot))terminal.WriteLine(line);
                            selected=snapshot;continue;
                        }
                        if(words[0]=="operation"){terminal.WriteLine(ReviewText.Historical(store.GetReviewOperation(reference)));continue;}
                        if(words[0]=="request")
                        {
                            terminal.WriteLine("权限申请待确认：没有grant；设备/发布者/服务文字尚未认证。仅只读检查，不能用 approve 批准申请。");
                            terminal.WriteLine(System.Text.Json.JsonSerializer.Serialize(store.GetPendingGrantRequest(reference),new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));continue;
                        }
                    }
                    selected=null;terminal.WriteLine("无效命令。");
                }
                catch(ApplicationReviewException e){selected=null;terminal.WriteLine("审核未确认："+Code(e.Code)+"；若有待恢复回执，请 recover。");}
                catch(ReviewJournalException e){selected=null;terminal.WriteLine("本地回执错误："+Code(e.Code)+"；不重发操作。");}
            }
        }
        catch(ReviewInputException){terminal.WriteLine("输入超过边界或包含控制字符；已停止，未自动确认。");return 2;}
    }
    private void ListRequests(Guid? after)
    {
        selected=null;var page=store.ListPendingGrantReferences(after,20);
        foreach(Guid reference in page.Items)
        {
            var value=store.GetPendingGrantRequest(reference);
            terminal.WriteLine(reference+" app="+value.Request.ApplicationRef+" alias="+ReviewText.Quote(value.Request.ImportAlias)+" state="+value.State);
        }
        grantNext=page.NextCursor;terminal.WriteLine(grantNext is null?"本页申请结束；requests 可刷新。":"使用 requests-next 查看下一页申请。");
    }
    private string? Read()
    {
        string? value=terminal.ReadLine();
        if(value is not null && (value.Length>128 || value.Any(c=>c<32||c>126)))throw new ReviewInputException();
        return value;
    }
    private void List(Guid? after)
    {
        selected=null;var page=store.ListReviewReferences(after,20);
        foreach(Guid reference in page.Items)
        {
            var value=store.GetReview(reference);
            terminal.WriteLine(reference+" app="+ReviewText.Quote(value.PendingManifest.ApplicationId)+" label="+ReviewText.Quote(value.PendingManifest.DisplayName)+
                " pending="+ReviewText.Number(value.PendingRevision)+" version="+ReviewText.Number(value.DecisionVersion)+" approved="+(value.ApprovedManifest is null?"no":"yes"));
        }
        next=page.NextCursor;terminal.WriteLine(next is null?"本页结束；list 可刷新并发新增项。":"使用 next 查看下一页。");
    }
    private void Decide(ApplicationReviewDecision decision)
    {
        var snapshot=selected;selected=null;
        if(snapshot is null){terminal.WriteLine("先 show 完整声明，再选择决定。");return;}
        if(journal.Read() is {Acknowledged:false}){terminal.WriteLine("待恢复操作阻止新决定；请 recover。");return;}
        var intent=new ReviewIntent(Guid.NewGuid(),snapshot.ApplicationRef,snapshot.PendingRevision,snapshot.DecisionVersion,decision);
        terminal.WriteLine("拟提交操作："+intent.OperationId+"；应用："+intent.ApplicationRef+"；决定："+decision);
        if(!Confirm(decision.ToString().ToUpperInvariant()))return;
        journal.Prepare(intent);
        var result=store.DecideReview(snapshot,decision,intent.OperationId);
        Match(intent,result);journal.Acknowledge(intent.OperationId);
        terminal.WriteLine("操作号："+intent.OperationId);terminal.WriteLine(ReviewText.Historical(result));
    }
    private void Recover()
    {
        selected=null;var intent=journal.Read();if(intent is null || intent.Acknowledged){terminal.WriteLine("没有待恢复操作。");return;}
        try
        {
            var result=store.GetReviewOperation(intent.OperationId);Match(intent,result);journal.Acknowledge(intent.OperationId);
            terminal.WriteLine("原操作已查询并确认："+intent.OperationId);terminal.WriteLine(ReviewText.Historical(result));
        }
        catch(ApplicationReviewException e) when(e.Code=="OPERATION_UNKNOWN")
        {terminal.WriteLine("未查到原操作；不自动重发。请 show 核对当前状态；abandon 仅解除本地等待，不代表服务端回滚。");}
    }
    private void Abandon()
    {
        selected=null;var intent=journal.Read();if(intent is null || intent.Acknowledged){terminal.WriteLine("没有待恢复操作。");return;}
        try{store.GetReviewOperation(intent.OperationId);terminal.WriteLine("已有历史结果，请 recover；未放弃。");return;}
        catch(ApplicationReviewException e) when(e.Code=="OPERATION_UNKNOWN"){}
        terminal.WriteLine("警告：仅结束本地等待，不更改 Broker 状态，也不保证原操作未执行。操作："+intent.OperationId);
        if(!Confirm("ABANDON"))return;
        journal.Acknowledge(intent.OperationId);terminal.WriteLine("本地等待已结束；新的决定须重新 show 和确认。");
    }
    private bool Confirm(string action)
    {
        string phrase=action+" "+Convert.ToHexString(RandomNumberGenerator.GetBytes(4));terminal.WriteLine("确认短语："+phrase);
        if(string.Equals(Read(),phrase,StringComparison.Ordinal))return true;
        terminal.WriteLine("已取消，未提交。");return false;
    }
    private static void Match(ReviewIntent intent,ApplicationReviewResult result)
    {
        if(intent.DecisionVersion==long.MaxValue || result.ApplicationRef!=intent.ApplicationRef || result.PendingRevision!=intent.PendingRevision ||
            result.DecisionVersion!=intent.DecisionVersion+1 || result.Decision!=intent.Decision)
            throw new ReviewJournalException("REVIEW_RESULT_MISMATCH");
    }
    private static string Code(string code)=>code is "REVIEW_STALE" or "APPLICATION_UNKNOWN" or "OPERATION_CONFLICT" or "OPERATION_UNKNOWN" or
        "RESOURCE_EXHAUSTED" or "STORAGE_UNAVAILABLE" or "REVIEW_RECOVERY_REQUIRED" or "REVIEW_JOURNAL_UNAVAILABLE" or
        "REVIEW_BUSY_OR_UNAVAILABLE" or "REVIEW_RESULT_MISMATCH"?code:"REVIEW_FAILED";
}
