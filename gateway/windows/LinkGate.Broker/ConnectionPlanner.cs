using System.Diagnostics;
using System.Security;
using System.Security.Authentication;
using LinkGate.Contracts;
using LinkGate.Authority;

namespace LinkGate.Broker;

internal sealed class PlanningException(string code) : IOException("Connection planning failed: "+code)
{
    public string Code { get; }=code;
}

// Agent-only: delegates establish authenticated transports without application payload.
internal sealed class ConnectionPlanner<T> where T:IAsyncDisposable
{
    internal static long Now(){long now=Stopwatch.GetTimestamp(),frequency=Stopwatch.Frequency;return checked(now/frequency*1000+now%frequency*1000/frequency);}
    private readonly Func<long> clock;
    private readonly SemaphoreSlim directSlots,relaySlots;
    internal ConnectionPlanner(Func<long>? clock=null,int setupPerPath=4)
    {
        if(setupPerPath is <1 or >16)throw new ArgumentOutOfRangeException(nameof(setupPerPath));
        this.clock=clock??Now;directSlots=new(setupPerPath);relaySlots=new(setupPerPath);
    }
    internal sealed class Watch(Func<long> clock,long admitted)
    {
        private readonly object gate=new();private long last=admitted;private bool invalid;
        internal long Read(){lock(gate){long now=clock();if(invalid||now<last){invalid=true;throw new PlanningException("CLOCK_UNTRUSTED");}last=now;return now;}}
    }
    internal sealed class Attempt : IDisposable
    {
        private readonly Watch watch;private readonly long deadline;
        private readonly object gate=new();private readonly List<Action> aborts=[];
        private readonly CancellationTokenSource stop=new();private readonly CancellationTokenRegistration parent;private readonly CancellationToken token;
        private bool closed,disposing,released;internal CancellationToken Token=>token;internal long Deadline=>deadline;
        internal Attempt(Watch watch,long deadline,CancellationToken parent)
        {this.watch=watch;this.deadline=deadline;token=stop.Token;this.parent=parent.Register(Dispose);}
        internal int RemainingMs(){lock(gate){if(closed)throw new PlanningException("CANCELLED");}long left=deadline-watch.Read();if(left<=0)throw new PlanningException("CONNECT_TIMEOUT");return (int)Math.Min(60000,left);}
        internal void Track(Action abort)
        {
            bool full,closing;lock(gate){closing=closed;full=aborts.Count>=16;if(!closing&&!full)aborts.Add(abort);}
            if(closing||full){Quiet(abort);if(full)Dispose();throw new PlanningException(full?"RESOURCE_EXHAUSTED":"CANCELLED");}
        }
        public void Dispose()
        {
            Action[] closing;lock(gate){if(closed)return;closed=true;disposing=true;closing=aborts.ToArray();aborts.Clear();}
            try{foreach(var abort in closing)Quiet(abort);try{stop.Cancel();}catch(AggregateException){}}
            finally{bool dispose;lock(gate){disposing=false;dispose=released;}if(dispose)stop.Dispose();}
        }
        // Called only after setup finishes and its selected/late value has been disposed.
        internal void Release(){Dispose();parent.Dispose();bool dispose;lock(gate){released=true;dispose=!disposing;}if(dispose)stop.Dispose();}
    }
    internal sealed class Result : IAsyncDisposable
    {
        private readonly Attempt attempt;private int disposed;
        internal T Value {get;}internal string Path {get;}internal string Fallback {get;}internal long SetupMs {get;}
        internal Result(Attempt attempt,T value,string path,string fallback,long setup)
        {this.attempt=attempt;Value=value;Path=path;Fallback=fallback;SetupMs=setup;}
        public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)!=0)return;attempt.Dispose();try{await Value.DisposeAsync();}finally{attempt.Release();}}
    }
    private sealed record Ready(T Value,long At);
    private sealed class Candidate
    {
        internal readonly Attempt Context;internal readonly Task<Ready> Work;
        private int abandoned;
        internal Candidate(Watch watch,long deadline,CancellationToken parent,SemaphoreSlim slots,Func<Attempt,Task<T>> open,string failure)
        {
            Context=new(watch,deadline,parent);
            if(!slots.Wait(0)){Work=Task.FromException<Ready>(new PlanningException("RESOURCE_EXHAUSTED"));return;}
            Work=Task.Run(async()=>
            {
                T? value=default;bool owned=false;
                try{Context.RemainingMs();value=await open(Context);owned=true;long now=watch.Read();if(Context.Token.IsCancellationRequested)throw new PlanningException("CANCELLED");if(now>=deadline)throw new PlanningException("CONNECT_TIMEOUT");owned=false;return new Ready(value,now);}
                catch(Exception e){Context.Dispose();if(owned)await QuietDispose(value!);throw Classify(e,failure);}
                finally{slots.Release();}
            });
        }
        internal void Abandon()
        {
            if(Interlocked.Exchange(ref abandoned,1)!=0)return;Context.Dispose();
            _=Cleanup();
            async Task Cleanup(){try{var late=await Work;await QuietDispose(late.Value);}catch(Exception){}finally{Context.Release();}}
        }
    }
    internal async Task<Result> OpenAsync(ConnectionPolicy policy,long admitted,CancellationToken parent,Func<Attempt,Task<T>> direct,Func<Attempt,Task<T>> relay)
    {
        if(policy.Prewarm||policy.RelayBudgetBytes is not null||policy.RelayWarningBytes is not null)throw new PlanningException("CAPABILITY_UNSUPPORTED");
        var watch=new Watch(clock,admitted);long end,directEnd;
        try{end=checked(admitted+policy.ConnectTimeoutMs);directEnd=policy.AllowRelay?Math.Min(end,checked(admitted+policy.DirectWaitMs)):end;}catch(OverflowException){throw new PlanningException("CLOCK_UNTRUSTED");}
        Candidate? first=null,second=null,winner=null;string fallback="NONE";
        try
        {
            parent.ThrowIfCancellationRequested();if(watch.Read()>=end)throw new PlanningException("CONNECT_TIMEOUT");
            if(watch.Read()<directEnd)
            {
                first=new(watch,directEnd,parent,directSlots,direct,"DIRECT_FAILED");
                try{var ready=await Wait(first,watch,directEnd,end,parent,true);winner=first;return new(first.Context,ready.Value,"direct","NONE",ready.At-admitted);}
                catch(PlanningException failure){if(Terminal(failure.Code))throw;fallback=failure.Code is "CONNECT_TIMEOUT" or "DIRECT_WINDOW_EXPIRED"?"DIRECT_WINDOW_EXPIRED":"DIRECT_FAILED";first.Abandon();}
            }
            else fallback="DIRECT_WINDOW_EXPIRED";
            if(!policy.AllowRelay)throw new PlanningException(watch.Read()>=end?"CONNECT_TIMEOUT":"DIRECT_FAILED");
            parent.ThrowIfCancellationRequested();if(watch.Read()>=end)throw new PlanningException("CONNECT_TIMEOUT");
            second=new(watch,end,parent,relaySlots,relay,"RELAY_FAILED");var result=await Wait(second,watch,end,end,parent,false);winner=second;return new(second.Context,result.Value,"relay",fallback,result.At-admitted);
        }
        catch(OperationCanceledException){throw new PlanningException("CANCELLED");}
        finally{if(first is not null&&first!=winner)first.Abandon();if(second is not null&&second!=winner)second.Abandon();}
    }
    private static async Task<Ready> Wait(Candidate candidate,Watch watch,long phaseEnd,long end,CancellationToken parent,bool direct)
    {
        while(true)
        {
            parent.ThrowIfCancellationRequested();long now=watch.Read();if(now>=end)throw new PlanningException("CONNECT_TIMEOUT");
            if(candidate.Work.IsCompleted)
            {
                var ready=await candidate.Work;parent.ThrowIfCancellationRequested();if(watch.Read()>=end)throw new PlanningException("CONNECT_TIMEOUT");if(ready.At>=phaseEnd)throw new PlanningException(direct?"DIRECT_WINDOW_EXPIRED":"CONNECT_TIMEOUT");return ready;
            }
            if(now>=phaseEnd)throw new PlanningException(direct?"DIRECT_WINDOW_EXPIRED":"CONNECT_TIMEOUT");
            await Task.WhenAny(candidate.Work,Task.Delay((int)Math.Min(25,phaseEnd-now),parent));
        }
    }
    private static bool Terminal(string code)=>code is "AUTH_REQUIRED" or "RESOURCE_EXHAUSTED" or "CAPABILITY_UNSUPPORTED" or "CLOCK_UNTRUSTED" or "CANCELLED";
    private static PlanningException Classify(Exception e,string failure)=>e switch
    {
        PlanningException p=>p,
        ConnectionRouteException r=>new(r.Code),
        LeaseRejectedException or UnauthorizedAccessException or SecurityException or AuthenticationException=>new("AUTH_REQUIRED"),
        ControlRefusedException c=>new(c.Code=="RESOURCE_EXHAUSTED"?c.Code:"AUTH_REQUIRED"),
        PeerRefusalException p=>new(p.Code is "AUTH_REQUIRED" or "RESOURCE_EXHAUSTED" or "CAPABILITY_UNSUPPORTED"?p.Code:failure),
        OperationCanceledException=>new("CANCELLED"),
        _=>new(failure)
    };
    private static void Quiet(Action action){try{action();}catch(Exception){}}
    private static async ValueTask QuietDispose(T value){try{await value.DisposeAsync();}catch(Exception){}}
}
