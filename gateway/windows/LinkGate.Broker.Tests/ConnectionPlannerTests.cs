using System.Text;
using LinkGate.Broker;
using LinkGate.Contracts;

internal static class ConnectionPlannerTests
{
    private sealed class Resource : IAsyncDisposable
    {
        internal int Disposals;internal readonly TaskCompletionSource Disposed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DisposeAsync(){Interlocked.Increment(ref Disposals);Disposed.TrySetResult();return ValueTask.CompletedTask;}
    }
    private static ConnectionPolicy Policy(bool relay=true,int wait=100,string extra="")=>ManifestContract.NormalizePolicy(Encoding.UTF8.GetBytes("{\"mode\":\"fast-connect\",\"connectTimeoutMs\":1000,\"directWaitMs\":"+wait+",\"allowRelay\":"+relay.ToString().ToLowerInvariant()+extra+"}"));
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Fail(Task task,string code,Action<bool,string> check)
    {
        try{await task.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("Unexpected planning success.");}
        catch(PlanningException error){check(error.Code==code&&error.InnerException is null,"bounded planning failure "+code);}
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        long now=0;int reentrantAborts=0,cancelSignals=0;
        var context=new ConnectionPlanner<Resource>.Attempt(new ConnectionPlanner<Resource>.Watch(()=>now,0),1000,default);
        using var observed=context.Token.Register(()=>cancelSignals++);context.Track(()=>{reentrantAborts++;context.Release();});context.Track(()=>reentrantAborts++);
        context.Dispose();context.Release();context.Dispose();check(reentrantAborts==2&&cancelSignals==1&&context.Token.IsCancellationRequested,"reentrant release preserves all raw aborts and cancellation without accessing a disposed source");
        var planner=new ConnectionPlanner<Resource>(()=>Interlocked.Read(ref now),1);int relayCalls=0;
        Resource first=new();
        await using(var plan=await planner.OpenAsync(Policy(),0,default,_=>{now=40;return Task.FromResult(first);},_=>{relayCalls++;return Task.FromResult(new Resource());}))
            check(plan.Path=="direct"&&plan.Fallback=="NONE"&&plan.SetupMs==40&&ReferenceEquals(plan.Value,first)&&relayCalls==0,"ready direct wins without relay");
        check(first.Disposals==1,"selected transport is disposed once");
        now=200;
        await using(var plan=await planner.OpenAsync(Policy(),0,default,_=>throw new Exception("Budget reset"),attempt=>{check(attempt.RemainingMs()==800,"prior authorization consumes total budget");return Task.FromResult(new Resource());}))
            check(plan.Path=="relay"&&plan.SetupMs==200&&plan.Fallback=="DIRECT_WINDOW_EXPIRED","consumed direct window is not restarted");
        now=1000;await Fail(planner.OpenAsync(Policy(),0,default,_=>throw new Exception("Unexpected IO"),_=>throw new Exception("Unexpected IO")),"CONNECT_TIMEOUT",check);
        now=0;
        await using(var plan=await planner.OpenAsync(Policy(false,1),0,default,attempt=>{check(attempt.RemainingMs()==1000,"relay forbidden uses full direct budget");now=500;return Task.FromResult(new Resource());},_=>throw new Exception("Forbidden relay")))
            check(plan.Path=="direct"&&plan.SetupMs==500,"direct-only does not use direct wait cutoff");
        now=0;
        await using(var plan=await planner.OpenAsync(Policy(),0,default,_=>throw new IOException("private marker"),_=>Task.FromResult(new Resource())))
            check(plan.Path=="relay"&&plan.Fallback=="DIRECT_FAILED","definite direct failure permits one explicit relay");
        await Fail(planner.OpenAsync(Policy(false),0,default,_=>throw new IOException(),_=>throw new Exception("Forbidden relay")),"DIRECT_FAILED",check);
        foreach(string code in new[]{"AUTH_REQUIRED","RESOURCE_EXHAUSTED","CAPABILITY_UNSUPPORTED","CLOCK_UNTRUSTED","CANCELLED"})
            await Fail(planner.OpenAsync(Policy(),0,default,_=>throw new PlanningException(code),_=>throw new Exception("Terminal failure fell back")),code,check);
        await Fail(planner.OpenAsync(Policy(),0,default,_=>throw new System.Security.SecurityException("private marker"),_=>throw new Exception("Terminal identity failure fell back")),"AUTH_REQUIRED",check);
        foreach(string extra in new[]{",\"prewarm\":true",",\"relayBudgetBytes\":\"100\",\"relayBudgetAction\":\"stop-relay\"",",\"relayWarningBytes\":\"100\""})
            await Fail(planner.OpenAsync(Policy(extra:extra),0,default,_=>throw new Exception("Unsupported IO"),_=>throw new Exception("Unsupported IO")),"CAPABILITY_UNSUPPORTED",check);

        // A cancelled but still executing direct attempt retains its setup slot.
        now=0;var entered=Signal();var release=Signal();var late=new Resource();int aborted=0;
        Task<ConnectionPlanner<Resource>.Result> opening=planner.OpenAsync(Policy(wait:10),0,default,async context=>{context.Track(()=>Interlocked.Increment(ref aborted));entered.TrySetResult();await release.Task;return late;},_=>Task.FromResult(new Resource()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Interlocked.Exchange(ref now,10);
            await using(var plan=await opening.WaitAsync(TimeSpan.FromSeconds(3)))check(plan.Path=="relay"&&aborted==1,"direct timeout aborts owned raw handles before selecting relay");
            await Fail(planner.OpenAsync(Policy(),10,default,_=>throw new Exception("Over-capacity direct"),_=>throw new Exception("Capacity failure fell back")),"RESOURCE_EXHAUSTED",check);
        }
        finally{release.TrySetResult();}
        await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));check(late.Disposals==1,"late direct result is disposed and never replaces selected relay");

        now=0;entered=Signal();release=Signal();late=new Resource();aborted=0;using var cancel=new CancellationTokenSource();
        opening=planner.OpenAsync(Policy(),0,cancel.Token,async context=>{context.Track(()=>Interlocked.Increment(ref aborted));entered.TrySetResult();await release.Task;return late;},_=>throw new Exception("Cancelled attempt fell back"));
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));cancel.Cancel();await Fail(opening,"CANCELLED",check);check(aborted==1,"parent cancellation aborts only its candidate");}finally{release.TrySetResult();}
        await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        now=100;entered=Signal();release=Signal();late=new Resource();
        opening=planner.OpenAsync(Policy(),100,default,async _=>{entered.TrySetResult();await release.Task;return late;},_=>throw new Exception("Clock failure fell back"));
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Interlocked.Exchange(ref now,99);await Fail(opening,"CLOCK_UNTRUSTED",check);Interlocked.Exchange(ref now,200);}finally{release.TrySetResult();}
        await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));check(late.Disposals==1,"clock recovery cannot revive an abandoned attempt");
    }
}
