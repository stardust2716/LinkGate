using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinkGate.Control;

namespace LinkGate.Authority;
public sealed class OwnerOutcomeUnknownException(Guid operation):IOException("Owner outcome unknown; query the original operation.")
{public Guid OperationId{get;}=operation;}
public sealed record ServerOwnerOutcome(string Kind,string Reference,string Revision,bool Active,int Affected);
public sealed partial class ControlClient
{
    public async Task<ServerOwnerOutcome> SetOwnerAuthorityAsync(string method,byte[] intent,Guid operation,CancellationToken cancellation=default)
    {
        using var document=ValidateOwnerIntent(method,intent);try
        {using var reply=await CallAsync(method,document.RootElement,true,cancellation,operation);return OwnerOutcome(reply.RootElement,method.Split('.')[1]);}
        catch(ControlRefusedException refused)when(refused.Code!="CONTROL_UNAVAILABLE"){throw;}
        catch(Exception error)when(error is ControlRefusedException or LeaseRejectedException or OperationCanceledException or IOException or InvalidDataException or JsonException or InvalidOperationException or ObjectDisposedException){throw new OwnerOutcomeUnknownException(operation);}
    }
    public async Task<ServerOwnerOutcome> GetOwnerOperationAsync(Guid operation,CancellationToken cancellation=default)
    {using var reply=await CallAsync("owner.operation.status",new{operationId=operation.ToString()},true,cancellation);return OwnerOutcome(reply.RootElement,null);}
    private static JsonDocument ValidateOwnerIntent(string method,byte[] input)
    {
        if(input.Length is <1 or >16384)throw new InvalidDataException("Invalid owner intent.");var document=ControlJson.ReadDocument(input);
        try
        {
            var value=document.RootElement;
            void Fields(JsonElement v,params string[] fields){if(v.ValueKind!=JsonValueKind.Object||!v.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields))throw new InvalidDataException();}
            string Text(JsonElement v,string field,int max,string pattern){var e=v.GetProperty(field);if(e.ValueKind!=JsonValueKind.String||e.GetString() is not string text||text.Length>max||!Regex.IsMatch(text,"\\A(?:"+pattern+")\\z",RegexOptions.CultureInvariant))throw new InvalidDataException();return text;}
            void Target(JsonElement v){Fields(v,"publisherDevice","publisherApp","service");Text(v,"publisherDevice",64,"[0-9a-f]{64}");Text(v,"publisherApp",128,"[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+");Text(v,"service",32,"[a-z][a-z0-9-]{0,31}");}
            void Revision(){string text=Text(value,"expectedRevision",20,"0|[1-9][0-9]*");if(!ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out _))throw new InvalidDataException();}
            if(method=="owner.device.set")
            {Fields(value,"device","expectedRevision","roles","active");Text(value,"device",64,"[0-9a-f]{64}");var roles=value.GetProperty("roles");Fields(roles,"owner","publish","access");foreach(var role in roles.EnumerateObject())_=role.Value.GetBoolean();}
            else if(method=="owner.service.set")
            {Fields(value,"scope","expectedRevision","protocol","authority","active");Target(value.GetProperty("scope"));Text(value,"protocol",5,"tcp|http|https|ws|wss");var authority=value.GetProperty("authority");if(authority.ValueKind!=JsonValueKind.String||authority.GetString()!.Length>256)throw new InvalidDataException();}
            else if(method=="owner.grant.set")
            {Fields(value,"scope","expectedRevision","active");var scope=value.GetProperty("scope");Fields(scope,"visitorDevice","visitorApp","target");Text(scope,"visitorDevice",64,"[0-9a-f]{64}");Text(scope,"visitorApp",128,"[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+");Target(scope.GetProperty("target"));}
            else throw new InvalidDataException();Revision();_=value.GetProperty("active").GetBoolean();return document;
        }
        catch{document.Dispose();throw new InvalidDataException("Invalid owner intent.");}
    }
    private static ServerOwnerOutcome OwnerOutcome(JsonElement value,string? expected)
    {
        LeaseJson.Fields(value,"kind","reference","revision","active","affected");string kind=LeaseJson.Text(value,"kind",8),reference=LeaseJson.Text(value,"reference",64),revision=LeaseJson.Text(value,"revision",20);
        if(kind is not ("device" or "service" or "grant")||expected is not null&&kind!=expected||!Regex.IsMatch(reference,"\\A[0-9a-f]{64}\\z")||!ulong.TryParse(revision,NumberStyles.None,CultureInfo.InvariantCulture,out var number)||number==0||number.ToString(CultureInfo.InvariantCulture)!=revision)throw new LeaseRejectedException();
        var count=value.GetProperty("affected");string raw=count.GetRawText();if(count.ValueKind!=JsonValueKind.Number||!int.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out int affected)||affected is <0 or >1000000||affected.ToString(CultureInfo.InvariantCulture)!=raw)throw new LeaseRejectedException();return new(kind,reference,revision,value.GetProperty("active").GetBoolean(),affected);
    }
}
