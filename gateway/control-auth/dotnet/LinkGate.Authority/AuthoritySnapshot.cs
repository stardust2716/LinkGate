using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinkGate.Control;

namespace LinkGate.Authority;

public sealed class LeaseRejectedException:Exception
{
    public string Code=>"AUTH_REQUIRED";
    public LeaseRejectedException():base("Server authorization lease rejected.") { }
}

public sealed record ServiceTarget
{
    public string PublisherDevice{get;}
    public string PublisherApp{get;}
    public string Service{get;}
    public ServiceTarget(string publisherDevice,string publisherApp,string service){LeaseJson.Key(publisherDevice);LeaseJson.App(publisherApp);LeaseJson.Service(service);PublisherDevice=publisherDevice;PublisherApp=publisherApp;Service=service;}
}
public sealed record AccessScope
{
    public string VisitorDevice{get;}
    public string VisitorApp{get;}
    public ServiceTarget Target{get;}
    public AccessScope(string visitorDevice,string visitorApp,ServiceTarget target){LeaseJson.Key(visitorDevice);LeaseJson.App(visitorApp);ArgumentNullException.ThrowIfNull(target);VisitorDevice=visitorDevice;VisitorApp=visitorApp;Target=target;}
}
public sealed record AuthoritySnapshot
{
    public AccessScope Scope{get;}
    public ulong VisitorRevision{get;}
    public ulong PublisherRevision{get;}
    public ulong ServiceRevision{get;}
    public ulong GrantRevision{get;}
    public string Protocol{get;}
    public string Authority{get;}
    private AuthoritySnapshot(AccessScope scope,ulong visitor,ulong publisher,ulong service,ulong grant,string protocol,string authority){Scope=scope;VisitorRevision=visitor;PublisherRevision=publisher;ServiceRevision=service;GrantRevision=grant;Protocol=protocol;Authority=authority;}
    public static AuthoritySnapshot Parse(byte[] json){try{using var document=ControlJson.ReadDocument(json);return Parse(document.RootElement);}catch(Exception e)when(e is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or OverflowException){throw new LeaseRejectedException();}}
    public static AuthoritySnapshot Parse(JsonElement root)
    {
        try {
            LeaseJson.Fields(root,"grant","service");var grant=root.GetProperty("grant");var service=root.GetProperty("service");
            LeaseJson.Fields(grant,"scope","revision","visitorRevision","publisherRevision","serviceRevision","active");LeaseJson.Fields(service,"scope","revision","publisherRevision","active","protocol","authority");
            if(grant.GetProperty("active").ValueKind!=JsonValueKind.True||service.GetProperty("active").ValueKind!=JsonValueKind.True)throw new LeaseRejectedException();
            var scope=LeaseJson.Scope(grant.GetProperty("scope"));var target=LeaseJson.Target(service.GetProperty("scope"));
            ulong visitor=LeaseJson.Counter(grant,"visitorRevision"),publisher=LeaseJson.Counter(grant,"publisherRevision"),revision=LeaseJson.Counter(service,"revision"),grantRevision=LeaseJson.Counter(grant,"revision");
            if(scope.Target!=target||revision!=LeaseJson.Counter(grant,"serviceRevision")||publisher!=LeaseJson.Counter(service,"publisherRevision"))throw new LeaseRejectedException();
            string protocol=LeaseJson.Text(service,"protocol",5),authority=LeaseJson.Text(service,"authority",259,true);
            if(protocol is not("tcp" or "http" or "https" or "ws" or "wss")||(protocol is "https" or "wss")&&authority.Length==0||authority.Any(char.IsControl))throw new LeaseRejectedException();
            return new AuthoritySnapshot(scope,visitor,publisher,revision,grantRevision,protocol,authority);
        }catch(Exception e)when(e is JsonException or InvalidOperationException or ArgumentException or OverflowException){throw new LeaseRejectedException();}
    }
}

internal static class LeaseJson
{
    internal static void Fields(JsonElement value,params string[] fields){if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=fields.Length||fields.Any(name=>!value.TryGetProperty(name,out _))||value.EnumerateObject().Any(property=>!fields.Contains(property.Name,StringComparer.Ordinal)))throw new LeaseRejectedException();}
    internal static string Text(JsonElement value,string name,int maximum,bool empty=false){string text=value.GetProperty(name).GetString()??throw new LeaseRejectedException();if(text.Length>maximum||(!empty&&text.Length==0)||text.Any(char.IsControl))throw new LeaseRejectedException();return text;}
    internal static void Key(string value){if(value is null||value.Length!=64||value.Any(item=>!(item is >= '0' and <= '9' or >= 'a' and <= 'f')))throw new LeaseRejectedException();}
    internal static void App(string value){if(string.IsNullOrEmpty(value)||value.Length>128||!Regex.IsMatch(value,"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+\\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking))throw new LeaseRejectedException();}
    internal static void Service(string value){if(string.IsNullOrEmpty(value)||value.Length>32||!Regex.IsMatch(value,"^[a-z][a-z0-9-]{0,31}\\z",RegexOptions.CultureInvariant|RegexOptions.NonBacktracking))throw new LeaseRejectedException();}
    internal static ulong Counter(JsonElement value,string name,bool active=true){string text=Text(value,name,20);if(!ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out ulong number)||number==0||(active&&number==ulong.MaxValue)||number.ToString(CultureInfo.InvariantCulture)!=text)throw new LeaseRejectedException();return number;}
    internal static long Integer(JsonElement value,string name){var raw=value.GetProperty(name);string text=raw.GetRawText();if(raw.ValueKind!=JsonValueKind.Number||text.Any(item=>item is <'0' or >'9')||!raw.TryGetInt64(out long number)||number<1)throw new LeaseRejectedException();return number;}
    internal static Guid UUID(JsonElement value,string name){string text=Text(value,name,36);if(!Guid.TryParseExact(text,"D",out Guid result)||result.ToString()!=text)throw new LeaseRejectedException();return result;}
    internal static ServiceTarget Target(JsonElement value){Fields(value,"publisherDevice","publisherApp","service");return new ServiceTarget(Text(value,"publisherDevice",64),Text(value,"publisherApp",128),Text(value,"service",32));}
    internal static AccessScope Scope(JsonElement value){Fields(value,"visitorDevice","visitorApp","target");return new AccessScope(Text(value,"visitorDevice",64),Text(value,"visitorApp",128),Target(value.GetProperty("target")));}
}
