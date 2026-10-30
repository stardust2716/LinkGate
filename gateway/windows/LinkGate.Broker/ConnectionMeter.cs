using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinkGate.Contracts;

namespace LinkGate.Broker;

internal sealed class ConnectionMeter
{
    internal sealed record Snapshot(long Sequence,ConnectionStatus Status);
    private readonly object gate=new();
    private readonly Guid reference,boot;private readonly string owner,device,publisher,service;private readonly ulong generation;
    private readonly DateTimeOffset accepted;private readonly long admitted;private readonly Func<long> clock;
    private long last,elapsed,sequence=1;private string state="pending",selected="none",business="none";private string? error;
    private DateTimeOffset? ready,closed;private int? setup;private bool complete,clockBad,outFin,inFin;private ulong tx,rx;
    internal ConnectionMeter(Guid reference,string owner,string device,string publisher,string service,Guid boot,ulong generation,DateTimeOffset utc,long admitted,Func<long> clock)
    {this.reference=reference;this.owner=owner;this.device=device;this.publisher=publisher;this.service=service;this.boot=boot;this.generation=generation;accepted=utc.ToUniversalTime();this.admitted=last=admitted;this.clock=clock;_=Read();}
    private void Tick(){long now=clock();if(now<last){clockBad=true;return;}last=now;elapsed=checked(now-admitted);}
    internal bool ClockValid{get{lock(gate){Tick();return !clockBad;}}}
    internal void Ready(string path,long setupMs){lock(gate){if(closed is not null)return;if(state!="pending"||path is not ("direct" or "relay")||setupMs is <0 or >60000)throw new InvalidOperationException();Tick();if(clockBad)throw new PlanningException("CLOCK_UNTRUSTED");selected=path;ready=accepted.AddMilliseconds(elapsed);setup=(int)setupMs;state="attaching";sequence++;}}
    internal void Attached(){lock(gate){if(closed is not null)return;if(state!="attaching")throw new InvalidOperationException();state="open";sequence++;}}
    internal void Fin(bool outbound){lock(gate){if(closed is not null)return;if(state is not ("open" or "draining"))throw new InvalidOperationException();if(outbound){if(outFin)return;outFin=true;}else{if(inFin)return;inFin=true;}state="draining";sequence++;}}
    internal bool Bytes(bool outbound,ulong amount)
    {
        lock(gate){if(closed is not null)return false;if(state is not ("open" or "draining"))throw new InvalidOperationException();if(amount==0)return true;
            try{if(outbound)tx=checked(tx+amount);else rx=checked(rx+amount);}catch(OverflowException){End(false,"COUNTER_OVERFLOW");return false;}business=selected;sequence++;return true;}
    }
    internal void End(bool clean,string? code)
    {
        lock(gate){if(closed is not null)return;Tick();if(clockBad){clean=false;code="CLOCK_UNTRUSTED";}if(clean&&(!outFin||!inFin)||ready is null&&code is null){clean=false;code="CONNECTION_CLOSED";}
            complete=clean&&ready is not null&&code is null;error=code;closed=accepted.AddMilliseconds(elapsed);state=code is null or "CONNECTION_CLOSED" or "GATEWAY_STOPPED"?"closed":"failed";sequence++;}
    }
    private static string Time(DateTimeOffset value)=>value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture);
    internal Snapshot Read()
    {
        lock(gate){Tick();var value=new{connectionId=reference,serviceRef=new{ownerId=owner,deviceId=device,publisherAppId=publisher,serviceId=service},bootId=boot,networkGeneration=generation.ToString(CultureInfo.InvariantCulture),flowState=state,selectedPath=selected,businessPath=business,
            directTx=(selected=="direct"?tx:0).ToString(CultureInfo.InvariantCulture),directRx=(selected=="direct"?rx:0).ToString(CultureInfo.InvariantCulture),relayTx=(selected=="relay"?tx:0).ToString(CultureInfo.InvariantCulture),relayRx=(selected=="relay"?rx:0).ToString(CultureInfo.InvariantCulture),complete,
            error=error is null?null:new{code=error,stage="connection",retryable=false,message="Connection ended.",details=new{}},acceptedUtc=Time(accepted),readyUtc=ready is null?null:Time(ready.Value),closedUtc=closed is null?null:Time(closed.Value),setupMs=setup};return new Snapshot(sequence,ConnectionStatus.Parse(JsonSerializer.SerializeToUtf8Bytes(value)));}
    }
    internal static ConnectionStatus Recovered(ConnectionStatus old,string code,DateTimeOffset utc)
    {
        var value=JsonNode.Parse(old.ToJson())!.AsObject();value["flowState"]=code is "CONNECTION_CLOSED" or "GATEWAY_STOPPED"?"closed":"failed";value["complete"]=false;
        string? floor=old.ReadyUtc??old.AcceptedUtc;if(floor is not null&&DateTimeOffset.TryParseExact(floor,"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var previous)&&utc<previous)utc=previous;
        value["closedUtc"]=Time(utc);value["error"]=JsonSerializer.SerializeToNode(new{code,stage="connection",retryable=false,message="Connection ended.",details=new{}});return ConnectionStatus.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()));
    }
}
