using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkGate.Contracts;

public sealed class PendingGrantRequest
{
    public Guid ApplicationRef { get; }
    public string ImportAlias { get; }
    public string DeviceId { get; }
    public string PublisherAppId { get; }
    public string RemoteServiceId { get; }
    public string Reason { get; }
    public PendingGrantRequest(Guid applicationRef,string importAlias,string deviceId,string publisherAppId,string remoteServiceId,string? reason=null)
    {
        ApplicationRef=applicationRef;
        ImportAlias=Match(importAlias,1,32,@"\A[a-z][a-z0-9-]{0,31}\z");
        DeviceId=Match(deviceId,1,128,@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z");
        PublisherAppId=Match(publisherAppId,3,128,@"\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+\z");
        RemoteServiceId=Match(remoteServiceId,1,32,@"\A[a-z][a-z0-9-]{0,31}\z");
        Reason=reason??"";
        try
        {
            _=new UTF8Encoding(false,true).GetByteCount(Reason);
            if(Reason.EnumerateRunes().Count()>256 || Reason.EnumerateRunes().Any(Rune.IsControl))throw new ContractException();
        }
        catch(EncoderFallbackException){throw new ContractException();}
    }
    public string ToJson()=>JsonSerializer.Serialize(new{applicationRef=ApplicationRef.ToString(),importAlias=ImportAlias,deviceId=DeviceId,
        publisherAppId=PublisherAppId,remoteServiceId=RemoteServiceId,reason=Reason});
    public static PendingGrantRequest Parse(JsonElement value)
    {
        try
        {
            string[] fields=["applicationRef","importAlias","deviceId","publisherAppId","remoteServiceId","reason"];
            if(value.ValueKind!=JsonValueKind.Object)throw new ContractException();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in value.EnumerateObject())if(!fields.Contains(property.Name,StringComparer.Ordinal)||!seen.Add(property.Name))throw new ContractException();
            if(fields.Take(5).Any(name=>!seen.Contains(name)) || !Guid.TryParseExact(value.GetProperty("applicationRef").GetString(),"D",out Guid reference))throw new ContractException();
            string reason=value.TryGetProperty("reason",out var raw)?raw.GetString()??throw new ContractException():"";
            return new PendingGrantRequest(reference,value.GetProperty("importAlias").GetString()!,value.GetProperty("deviceId").GetString()!,
                value.GetProperty("publisherAppId").GetString()!,value.GetProperty("remoteServiceId").GetString()!,reason);
        }
        catch(Exception e) when(e is InvalidOperationException or KeyNotFoundException or FormatException){throw new ContractException();}
    }
    public static void ValidatePendingResult(JsonElement value)
    {
        try{Exact(value,"grantRequestId","pendingUserApproval");if(!Guid.TryParseExact(value.GetProperty("grantRequestId").GetString(),"D",out _) || value.GetProperty("pendingUserApproval").ValueKind!=JsonValueKind.True)throw new ContractException();}
        catch(InvalidOperationException){throw new ContractException();}
    }
    public static void ValidateStatus(JsonElement value,Guid expected)
    {
        try{Exact(value,"grantRequestId","state");if(value.GetProperty("grantRequestId").GetString()!=expected.ToString() || value.GetProperty("state").GetString() is not ("pending" or "superseded"))throw new ContractException();}
        catch(InvalidOperationException){throw new ContractException();}
    }
    private static string Match(string? value,int min,int max,string pattern)
    {if(value is null || value.Length<min || value.Length>max || !Regex.IsMatch(value,pattern,RegexOptions.CultureInvariant))throw new ContractException();return value;}
    private static void Exact(JsonElement value,params string[] fields)
    {if(value.ValueKind!=JsonValueKind.Object || !value.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(fields.Order()))throw new ContractException();}
}
