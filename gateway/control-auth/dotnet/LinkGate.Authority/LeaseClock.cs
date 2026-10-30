namespace LinkGate.Authority;

public sealed class LeaseClock
{
    private readonly TimeProvider source;private readonly object gate=new();private DateTimeOffset effective;private long timestamp;private bool invalid;
    public LeaseClock(TimeProvider? source=null){this.source=source??TimeProvider.System;timestamp=this.source.GetTimestamp();effective=this.source.GetUtcNow();}
    internal TimeProvider Source=>source;
    internal void ObserveAuthenticatedResponseDate(DateTimeOffset utc,long requestBegan)
    {
        lock(gate)
        {
            var now=Read();TimeSpan elapsed=source.GetElapsedTime(requestBegan,now.Timestamp);
            if(elapsed<TimeSpan.Zero)throw new LeaseRejectedException();
            try{DateTimeOffset upper=utc+TimeSpan.FromSeconds(1)+elapsed+TimeSpan.FromTicks(1);if(upper>effective)effective=upper;}
            catch(ArgumentOutOfRangeException){throw new LeaseRejectedException();}
        }
    }
    internal (DateTimeOffset Utc,long Timestamp) Read()
    {
        lock(gate){if(invalid)throw new LeaseRejectedException();try{long now=source.GetTimestamp();TimeSpan elapsed=source.GetElapsedTime(timestamp,now);if(elapsed<TimeSpan.Zero)throw new LeaseRejectedException();DateTimeOffset floor=effective+elapsed,wall=source.GetUtcNow();effective=wall>floor?wall:floor;timestamp=now;return(effective,now);}catch(Exception error)when(error is ArgumentException or OverflowException or LeaseRejectedException){invalid=true;throw new LeaseRejectedException();}}
    }
}
