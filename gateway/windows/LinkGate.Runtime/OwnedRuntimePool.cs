using System.Diagnostics;
using System.Security;
using LinkGate.Broker;

namespace LinkGate.Runtime;

internal interface IOwnedRuntime : IAsyncDisposable { void Stop(); }
internal sealed class OwnedRuntimePool<K,V> : IAsyncDisposable where K:notnull where V:class,IOwnedRuntime
{
    private readonly object gate=new();
    private readonly Dictionary<K,Record> active=[];private readonly HashSet<Record> owned=[];
    private readonly int capacity;private readonly long idleMs;private readonly Func<long> clock;private readonly SemaphoreSlim creating;
    private long last;private bool closed;
    internal static long Now(){long n=Stopwatch.GetTimestamp(),f=Stopwatch.Frequency;return checked(n/f*1000+n%f*1000/f);}
    internal sealed class Creation
    {
        private readonly OwnedRuntimePool<K,V> pool;private readonly Record record;
        internal Creation(OwnedRuntimePool<K,V> pool,Record record){this.pool=pool;this.record=record;}
        internal CancellationToken Cancellation=>record.Token;
        internal long Started=>record.Started;
        internal void Own(V resource)
        {
            ArgumentNullException.ThrowIfNull(resource);bool retired;
            lock(pool.gate){if(record.Value is not null||!record.Creating)throw new InvalidOperationException("Runtime already owned.");record.Value=resource;retired=record.Retired;}
            if(retired){resource.Stop();throw new OperationCanceledException(record.Token);}
        }
    }
    internal sealed class Record
    {
        internal readonly K Key;internal readonly long Started;internal readonly CancellationTokenSource Stop=new();internal readonly CancellationToken Token;
        internal readonly TaskCompletionSource<V> Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);internal Task Work=Task.CompletedTask,Disposal=Task.CompletedTask;
        internal int References=1;internal bool Creating=true,Retired,Closing,Disposed;internal long IdleSince;internal V? Value,Unexpected;
        internal Record(K key,long started){Key=key;Started=started;Token=Stop.Token;}
    }
    internal sealed class Lease : IDisposable
    {
        private readonly OwnedRuntimePool<K,V> pool;private readonly Record record;private int released;
        internal Lease(OwnedRuntimePool<K,V> pool,Record record){this.pool=pool;this.record=record;}
        internal V Value{get{lock(pool.gate){if(released!=0||record.Retired||record.Creating||record.Value is null)throw Unavailable();return record.Value;}}}
        public void Dispose()
        {
            if(Interlocked.Exchange(ref released,1)!=0)return;long now;
            try{now=pool.ReadClock();}catch(IOException){pool.Close();now=0;}
            bool retire=false;lock(pool.gate){record.References--;if(record.References==0&&!record.Retired){if(record.Creating)retire=true;else record.IdleSince=now;}}
            if(retire)pool.Retire(record);
        }
    }
    internal OwnedRuntimePool(int capacity=16,int maximumCreating=4,long idleMs=60000,Func<long>? clock=null)
    {if(capacity is <1 or >16||maximumCreating is <1 or >4||idleMs is <1 or >60000)throw new ArgumentException();this.capacity=capacity;this.idleMs=idleMs;this.clock=clock??Now;last=this.clock();creating=new(maximumCreating);}
    private long ReadClock(){lock(gate){long now=clock();if(now<last)throw new ConnectionRouteException("CLOCK_UNTRUSTED");last=now;return now;}}
    internal async ValueTask<Lease> Acquire(K key,Func<Creation,Task<V>> factory,long deadline,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();long now;try{now=ReadClock();}catch(IOException){Close();throw;}if(deadline<=now)throw new TimeoutException("Route budget elapsed.");Record record;
        lock(gate)
        {
            if(closed)throw Unavailable();
            if(active.TryGetValue(key,out var found)){record=found;record.References++;}
            else{if(owned.Count>=capacity||!creating.Wait(0))throw new ConnectionRouteException("RESOURCE_EXHAUSTED");record=new Record(key,now);active.Add(key,record);owned.Add(record);record.Work=Task.Run(()=>Create(record,factory));}
        }
        var lease=new Lease(this,record);bool delivered=false;
        try
        {
            long remaining=deadline-ReadClock();if(remaining<=0)throw new TimeoutException("Route budget elapsed.");
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation);budget.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(60000,remaining)));
            try{await record.Ready.Task.WaitAsync(budget.Token);}catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){throw new TimeoutException("Route budget elapsed.");}
            cancellation.ThrowIfCancellationRequested();if(ReadClock()>=deadline)throw new TimeoutException("Route budget elapsed.");_=lease.Value;delivered=true;return lease;
        }
        catch(ConnectionRouteException error)when(error.Code=="CLOCK_UNTRUSTED"){Close();throw;}
        finally{if(!delivered)lease.Dispose();}
    }
    private async Task Create(Record record,Func<Creation,Task<V>> factory)
    {
        V? value=null;Exception? error=null;
        try{value=await factory(new Creation(this,record));if(value is null)throw Unavailable();}
        catch(Exception failure){error=failure switch{ConnectionRouteException route=>new ConnectionRouteException(route.Code),UnauthorizedAccessException or ApplicationReviewException=>new UnauthorizedAccessException("Current route authority required."),SecurityException or System.Security.Cryptography.CryptographicException or InvalidDataException=>new SecurityException("Runtime integrity required."),_=>Unavailable()};}
        bool valid;
        lock(gate)
        {
            record.Creating=false;if(record.Value is null)record.Value=value;else if(value is not null&&!ReferenceEquals(record.Value,value)){record.Unexpected=value;error=Unavailable();}
            valid=error is null&&value is not null&&!closed&&!record.Retired&&record.References>0;
            if(valid)record.Ready.TrySetResult(value!);else record.Ready.TrySetException(error??Unavailable());
        }
        creating.Release();if(!valid)Retire(record);
    }
    private void Retire(Record record)
    {
        V? value,unexpected;lock(gate){record.Retired=true;if(active.TryGetValue(record.Key,out var current)&&ReferenceEquals(current,record))active.Remove(record.Key);value=record.Value;unexpected=record.Unexpected;record.Ready.TrySetException(Unavailable());}
        try{record.Stop.Cancel();}catch(AggregateException){}catch(ObjectDisposedException){}
        try{value?.Stop();}catch(Exception){}try{unexpected?.Stop();}catch(Exception){}ScheduleDispose(record);
    }
    private void ScheduleDispose(Record record)
    {lock(gate){if(record.Creating||record.Closing||record.Disposed)return;record.Closing=true;record.Disposal=Task.Run(()=>DisposeRecord(record));}}
    private async Task DisposeRecord(Record record)
    {
        bool success=true;try{if(record.Value is not null)await record.Value.DisposeAsync();}catch(Exception){success=false;}
        try{if(record.Unexpected is not null)await record.Unexpected.DisposeAsync();}catch(Exception){success=false;}
        lock(gate){record.Closing=false;if(success){record.Disposed=true;owned.Remove(record);record.Stop.Dispose();}}
    }
    internal void Maintain(Func<K,bool> current,Func<V,bool> alive)
    {
        long now;try{now=ReadClock();}catch(IOException){Close();return;}Record[] all;lock(gate)all=owned.ToArray();
        foreach(var record in all)
        {
            bool retired,creating,expired;V? value;lock(gate){retired=record.Retired;creating=record.Creating;value=record.Value;expired=!creating&&record.References==0&&now-record.IdleSince>=idleMs;}
            if(retired){ScheduleDispose(record);continue;}bool valid;try{valid=current(record.Key)&&(creating||value is not null&&alive(value));}catch(Exception){valid=false;}if(!valid||expired)Retire(record);
        }
    }
    internal bool Contains(K key){lock(gate)return active.ContainsKey(key);}
    internal int Count{get{lock(gate)return owned.Count;}}
    internal int LeaseCount{get{lock(gate)return owned.Sum(x=>x.References);}}
    internal void Invalidate(){Record[] all;lock(gate)all=owned.ToArray();foreach(var record in all)Retire(record);}
    internal void Close(){lock(gate)closed=true;Invalidate();}
    public async ValueTask DisposeAsync()
    {
        Close();Record[] all;lock(gate)all=owned.ToArray();await Task.WhenAll(all.Select(x=>x.Work)).WaitAsync(TimeSpan.FromSeconds(5));
        foreach(var record in all)ScheduleDispose(record);await Task.WhenAll(all.Select(x=>x.Disposal)).WaitAsync(TimeSpan.FromSeconds(5));
        if(Count!=0)throw new IOException("Owned runtime cleanup incomplete.");
    }
    private static IOException Unavailable()=>new("Private runtime unavailable.");
}
