using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkGate.Contracts;

/// <summary>References and constraints only; not a caller identity or authorization.</summary>
public sealed class ConnectionOpenRequest
{
    public Guid ApplicationRef {get;}
    public string ImportAlias {get;}
    public string DeviceId {get;}
    public string PublisherAppId {get;}
    public string RemoteServiceId {get;}
    public ConnectionPolicy ConnectionPolicy {get;}
    private ConnectionOpenRequest(Guid application,string alias,string device,string publisher,string service,ConnectionPolicy policy)
    {ApplicationRef=application;ImportAlias=alias;DeviceId=device;PublisherAppId=publisher;RemoteServiceId=service;ConnectionPolicy=policy;}
    public static ConnectionOpenRequest Parse(ReadOnlySpan<byte> input)
    {
        try
        {
            using var document=ManifestContract.Read(input,16*1024);var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["applicationRef","importAlias","deviceId","publisherAppId","remoteServiceId","connectionPolicy"]))throw new ContractException();
            if(!Guid.TryParseExact(root.GetProperty("applicationRef").GetString(),"D",out var application))throw new ContractException();
            static string Text(JsonElement element,int max,string pattern){if(element.ValueKind!=JsonValueKind.String||element.GetString() is not { } value||value.Length>max||!Regex.IsMatch(value,pattern,RegexOptions.CultureInvariant))throw new ContractException();return value;}
            string alias=Text(root.GetProperty("importAlias"),32,"^[a-z][a-z0-9-]{0,31}\\z"),device=Text(root.GetProperty("deviceId"),64,"^[0-9a-f]{64}\\z"),publisher=Text(root.GetProperty("publisherAppId"),128,"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+\\z"),service=Text(root.GetProperty("remoteServiceId"),32,"^[a-z][a-z0-9-]{0,31}\\z");
            var policy=ManifestContract.NormalizePolicy(Encoding.UTF8.GetBytes(root.GetProperty("connectionPolicy").GetRawText()));
            return new(application,alias,device,publisher,service,policy);
        }
        catch(ContractException){throw;}
        catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException){throw new ContractException();}
    }
    public bool WithinApproved(ConnectionPolicy? approved)
    {
        if(approved is null)return false;var requested=ConnectionPolicy;
        static bool Bounded(ulong? request,ulong? limit)=>limit is null||request is not null&&request.Value<=limit.Value;
        return requested.ConnectTimeoutMs<=approved.ConnectTimeoutMs&&(!requested.AllowRelay||approved.AllowRelay&&requested.DirectWaitMs>=Math.Min(approved.DirectWaitMs,requested.ConnectTimeoutMs))
            &&(!requested.Prewarm||approved.Prewarm&&requested.PrewarmTimeoutMs<=approved.PrewarmTimeoutMs&&requested.PrewarmIdleTimeoutMs<=approved.PrewarmIdleTimeoutMs)
            &&Bounded(requested.RelayBudgetBytes,approved.RelayBudgetBytes)&&Bounded(requested.RelayWarningBytes,approved.RelayWarningBytes);
    }
    public string ToJson()
    {
        using var policy=JsonDocument.Parse(ConnectionPolicy.ToJson());return JsonSerializer.Serialize(new{applicationRef=ApplicationRef,importAlias=ImportAlias,deviceId=DeviceId,publisherAppId=PublisherAppId,remoteServiceId=RemoteServiceId,connectionPolicy=policy.RootElement});
    }
}
