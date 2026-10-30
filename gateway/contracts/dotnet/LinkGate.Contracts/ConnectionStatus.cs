using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkGate.Contracts;

/// <summary>Transport observation, not permission or an application delivery receipt.</summary>
public sealed class ConnectionStatus
{
    public Guid ConnectionId {get;}
    public string OwnerId {get;}
    public string DeviceId {get;}
    public string PublisherAppId {get;}
    public string ServiceId {get;}
    public Guid BootId {get;}
    public ulong NetworkGeneration {get;}
    public string FlowState {get;}
    public string SelectedPath {get;}
    public string BusinessPath {get;}
    public ulong DirectTx {get;}
    public ulong DirectRx {get;}
    public ulong RelayTx {get;}
    public ulong RelayRx {get;}
    public bool Complete {get;}
    public string? ErrorCode {get;}
    public string? AcceptedUtc {get;}
    public string? ReadyUtc {get;}
    public string? ClosedUtc {get;}
    public int? SetupMs {get;}
    private readonly string encoded;
    public static ConnectionStatus Parse(ReadOnlySpan<byte> input)
    {
        try{using var document=ManifestContract.Read(input,16384);return new ConnectionStatus(document.RootElement);}
        catch(ContractException){throw;}catch(Exception e)when(e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException){throw new ContractException();}
    }
    private ConnectionStatus(JsonElement value)
    {
        Fields(value,"connectionId","serviceRef","bootId","networkGeneration","flowState","selectedPath","businessPath","directTx","directRx","relayTx","relayRx","complete","error","acceptedUtc","readyUtc","closedUtc","setupMs");
        ConnectionId=Uuid(value.GetProperty("connectionId"));BootId=Uuid(value.GetProperty("bootId"));var service=value.GetProperty("serviceRef");Fields(service,"ownerId","deviceId","publisherAppId","serviceId");
        OwnerId=Text(service.GetProperty("ownerId"),64,"[0-9a-f]{64}");DeviceId=Text(service.GetProperty("deviceId"),64,"[0-9a-f]{64}");PublisherAppId=Text(service.GetProperty("publisherAppId"),128,"[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+");ServiceId=Text(service.GetProperty("serviceId"),32,"[a-z][a-z0-9-]{0,31}");
        NetworkGeneration=Decimal(value.GetProperty("networkGeneration"));if(NetworkGeneration==0)throw new ContractException();
        FlowState=Choice(value.GetProperty("flowState"),"pending","attaching","open","draining","closed","failed");SelectedPath=Choice(value.GetProperty("selectedPath"),"none","direct","relay");BusinessPath=Choice(value.GetProperty("businessPath"),"none","direct","relay");
        DirectTx=Decimal(value.GetProperty("directTx"));DirectRx=Decimal(value.GetProperty("directRx"));RelayTx=Decimal(value.GetProperty("relayTx"));RelayRx=Decimal(value.GetProperty("relayRx"));
        Complete=value.GetProperty("complete").GetBoolean();AcceptedUtc=Time(value.GetProperty("acceptedUtc"));ReadyUtc=Time(value.GetProperty("readyUtc"));ClosedUtc=Time(value.GetProperty("closedUtc"));var setup=value.GetProperty("setupMs");
        if(setup.ValueKind==JsonValueKind.Null)SetupMs=null;else{if(setup.ValueKind!=JsonValueKind.Number||!Regex.IsMatch(setup.GetRawText(),"\\A(?:0|[1-9][0-9]*)\\z")||!setup.TryGetInt32(out var n)||n is <0 or >60000)throw new ContractException();SetupMs=n;}
        ErrorCode=value.GetProperty("error").ValueKind==JsonValueKind.Null?null:Error(value.GetProperty("error"));
        bool payload=DirectTx!=0||DirectRx!=0||RelayTx!=0||RelayRx!=0,terminal=FlowState is "closed" or "failed";
        if((ReadyUtc is null)!=(SetupMs is null)||(SelectedPath=="none")!=(ReadyUtc is null)||terminal!=(ClosedUtc is not null)||!terminal&&ErrorCode is not null||FlowState=="failed"&&ErrorCode is null||Complete&&(FlowState!="closed"||ErrorCode is not null||ReadyUtc is null))throw new ContractException();
        if(SelectedPath=="direct"&&(RelayTx!=0||RelayRx!=0)||SelectedPath=="relay"&&(DirectTx!=0||DirectRx!=0)||payload&&BusinessPath!=SelectedPath||!payload&&BusinessPath!="none"||SelectedPath=="none"&&payload)throw new ContractException();
        if(FlowState=="pending"&&(SelectedPath!="none"||payload)||FlowState=="attaching"&&(ReadyUtc is null||payload)||(FlowState is "open" or "draining")&&ReadyUtc is null)throw new ContractException();
        if(AcceptedUtc is not null&&ReadyUtc is not null&&string.CompareOrdinal(ReadyUtc,AcceptedUtc)<0||AcceptedUtc is not null&&ClosedUtc is not null&&string.CompareOrdinal(ClosedUtc,AcceptedUtc)<0||ReadyUtc is not null&&ClosedUtc is not null&&string.CompareOrdinal(ClosedUtc,ReadyUtc)<0)throw new ContractException();
        encoded=value.GetRawText();
    }
    internal static void Fields(JsonElement value,params string[] fields){if(value.ValueKind!=JsonValueKind.Object||!value.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields))throw new ContractException();}
    internal static Guid Uuid(JsonElement element){string value=Text(element,36,"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");if(!Guid.TryParseExact(value,"D",out var id)||id.ToString()!=value)throw new ContractException();return id;}
    internal static string Text(JsonElement element,int max,string pattern){if(element.ValueKind!=JsonValueKind.String||element.GetString() is not string value||value.Length>max||!Regex.IsMatch(value,"\\A(?:"+pattern+")\\z",RegexOptions.CultureInvariant))throw new ContractException();return value;}
    private static string Choice(JsonElement element,params string[] allowed){string text=Text(element,32,"[a-z]+");if(!allowed.Contains(text,StringComparer.Ordinal))throw new ContractException();return text;}
    private static ulong Decimal(JsonElement element){string text=Text(element,20,"0|[1-9][0-9]*");if(!ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value))throw new ContractException();return value;}
    private static string? Time(JsonElement element){if(element.ValueKind==JsonValueKind.Null)return null;string value=Text(element,24,"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z");if(!DateTimeOffset.TryParseExact(value,"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out _))throw new ContractException();return value;}
    internal static string Error(JsonElement value){Fields(value,"code","stage","retryable","message","details");string code=Text(value.GetProperty("code"),64,"[A-Z][A-Z0-9_]{0,63}");if(value.GetProperty("stage").GetString()!="connection"||value.GetProperty("retryable").ValueKind!=JsonValueKind.False||value.GetProperty("message").GetString()!="Connection ended.")throw new ContractException();Fields(value.GetProperty("details"));return code;}
    public string ToJson()=>encoded;
    public JsonElement Wire(){using var document=JsonDocument.Parse(encoded);return document.RootElement.Clone();}
}
