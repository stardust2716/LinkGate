using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace LinkGate.Contracts;

public sealed class ContractException : Exception
{
    public string Code => "INVALID_REQUEST";
    public ContractException() : base("Invalid public contract.") { }
}

public sealed class ConnectionPolicy
{
    public string Mode { get; }
    public int ConnectTimeoutMs { get; }
    public int DirectWaitMs { get; }
    public bool AllowRelay { get; }
    public bool Prewarm { get; }
    public int PrewarmTimeoutMs { get; }
    public int PrewarmIdleTimeoutMs { get; }
    public ulong? RelayBudgetBytes { get; }
    public ulong? RelayWarningBytes { get; }
    public string? RelayBudgetAction { get; }
    internal ConnectionPolicy(string mode,int timeout,int wait,bool relay,bool prewarm,int prewarmTimeout,int idle,ulong? budget,ulong? warning,string? action)
    { Mode=mode;ConnectTimeoutMs=timeout;DirectWaitMs=wait;AllowRelay=relay;Prewarm=prewarm;PrewarmTimeoutMs=prewarmTimeout;PrewarmIdleTimeoutMs=idle;RelayBudgetBytes=budget;RelayWarningBytes=warning;RelayBudgetAction=action; }
    internal object Wire()
    {
        var result=new Dictionary<string,object?> { ["mode"]=Mode,["connectTimeoutMs"]=ConnectTimeoutMs,["directWaitMs"]=DirectWaitMs,["allowRelay"]=AllowRelay,["prewarm"]=Prewarm,["prewarmTimeoutMs"]=PrewarmTimeoutMs,["prewarmIdleTimeoutMs"]=PrewarmIdleTimeoutMs,["relayBudgetBytes"]=RelayBudgetBytes?.ToString(CultureInfo.InvariantCulture),["relayWarningBytes"]=RelayWarningBytes?.ToString(CultureInfo.InvariantCulture) };
        if(RelayBudgetAction is not null)result["relayBudgetAction"]=RelayBudgetAction;
        return result;
    }
    public string ToJson()=>JsonSerializer.Serialize(Wire());
}

public sealed class LocalTarget
{
    public string Host=>"127.0.0.1";
    public int Port { get; }
    internal LocalTarget(int port){Port=port;}
}
public sealed class HealthCheck
{
    public string Type { get; }
    public string? Path { get; }
    public IReadOnlyList<int> ExpectedStatus { get; }
    internal HealthCheck(string type,string? path,IEnumerable<int> statuses){Type=type;Path=path;ExpectedStatus=new ReadOnlyCollection<int>(statuses.ToArray());}
    internal object Wire()=>Type=="tcp"?new Dictionary<string,object?>{["type"]=Type}:new Dictionary<string,object?>{["type"]=Type,["path"]=Path,["expectedStatus"]=ExpectedStatus};
}
public sealed class ExportService
{
    public string ServiceId {get;}
    public string Label {get;}
    public string Protocol {get;}
    public LocalTarget LocalTarget {get;}
    public string? LogicalAuthority {get;}
    public HealthCheck? HealthCheck {get;}
    internal ExportService(string id,string label,string protocol,LocalTarget target,string? authority,HealthCheck? health){ServiceId=id;Label=label;Protocol=protocol;LocalTarget=target;LogicalAuthority=authority;HealthCheck=health;}
    internal object Wire()
    {
        var r=new Dictionary<string,object?>{["serviceId"]=ServiceId,["label"]=Label,["protocol"]=Protocol,["localTarget"]=new {host=LocalTarget.Host,port=LocalTarget.Port}};
        if(LogicalAuthority is not null)r["logicalAuthority"]=LogicalAuthority;
        if(HealthCheck is not null)r["healthCheck"]=HealthCheck.Wire();return r;
    }
}
public sealed class ImportService
{
    public string Alias {get;}
    public string Protocol {get;}
    public string EndpointMode {get;}
    public string? LogicalAuthority {get;}
    public ConnectionPolicy ConnectionPolicy {get;}
    internal ImportService(string alias,string protocol,string endpointMode,string? authority,ConnectionPolicy policy){Alias=alias;Protocol=protocol;EndpointMode=endpointMode;LogicalAuthority=authority;ConnectionPolicy=policy;}
    internal object Wire()
    {var r=new Dictionary<string,object?>{["alias"]=Alias,["protocol"]=Protocol,["endpointMode"]=EndpointMode,["connectionPolicy"]=ConnectionPolicy.Wire()};if(LogicalAuthority is not null)r["logicalAuthority"]=LogicalAuthority;return r;}
}
public sealed class AppManifest
{
    public int ManifestVersion=>1;
    public string ApplicationId {get;}
    public string DisplayName {get;}
    public IReadOnlyList<ExportService> Exports {get;}
    public IReadOnlyList<ImportService> Imports {get;}
    internal AppManifest(string id,string name,IEnumerable<ExportService> exports,IEnumerable<ImportService> imports)
    {ApplicationId=id;DisplayName=name;Exports=new ReadOnlyCollection<ExportService>(exports.ToArray());Imports=new ReadOnlyCollection<ImportService>(imports.ToArray());}
    public string ToJson()=>JsonSerializer.Serialize(new {manifestVersion=ManifestVersion,applicationId=ApplicationId,displayName=DisplayName,exports=Exports.Select(x=>x.Wire()),imports=Imports.Select(x=>x.Wire())});
}
