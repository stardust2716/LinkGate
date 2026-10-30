using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkGate.Contracts;

public static class ManifestContract
{
    private static ContractException Invalid()=>new();
    public static AppManifest Parse(byte[] input)=>Parse(input.AsSpan());
    public static AppManifest Parse(ReadOnlySpan<byte> input)
    {
        try { using var doc=Read(input,48*1024);return Manifest(doc.RootElement); }
        catch(ContractException){throw;}
        catch(Exception e) when(e is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException or ArgumentException){throw Invalid();}
    }
    public static ConnectionPolicy NormalizePolicy(byte[] input)=>NormalizePolicy(input.AsSpan());
    public static ConnectionPolicy NormalizePolicy(ReadOnlySpan<byte> input)
    {
        try { using var doc=Read(input,8*1024);return Policy(doc.RootElement); }
        catch(ContractException){throw;}
        catch(Exception e) when(e is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException or ArgumentException){throw Invalid();}
    }
    internal static JsonDocument Read(ReadOnlySpan<byte> input,int max)
    {
        if(input.Length<2||input.Length>max||(input.Length>=3&&input[0]==239&&input[1]==187&&input[2]==191))throw Invalid();
        var text=new UTF8Encoding(false,true).GetString(input);ValidateUnicode(text);
        var doc=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=16});
        try {int nodes=0;ValidateTree(doc.RootElement,ref nodes);return doc;}catch{doc.Dispose();throw;}
    }
    private static void ValidateUnicode(string text)
    {
        for(int i=0;i<text.Length;i++)
        {
            if(text[i]!='"')continue;
            for(i++;i<text.Length&&text[i]!='"';i++)
            {
                char c=text[i];
                if(c=='\\')
                {
                    if(++i>=text.Length)throw Invalid();
                    if(text[i]!='u')continue;
                    int code=Hex(text,i+1);i+=4;
                    if(code is >=0xD800 and <=0xDBFF){if(i+6>=text.Length||text[i+1]!='\\'||text[i+2]!='u')throw Invalid();int low=Hex(text,i+3);if(low<0xDC00||low>0xDFFF)throw Invalid();i+=6;}
                    else if(code is >=0xDC00 and <=0xDFFF)throw Invalid();
                }
                else if(char.IsHighSurrogate(c)){if(++i>=text.Length||!char.IsLowSurrogate(text[i]))throw Invalid();}
                else if(char.IsLowSurrogate(c))throw Invalid();
            }
        }
    }
    private static int Hex(string text,int start)
    {if(start+4>text.Length)throw Invalid();int code=0;for(int i=start;i<start+4;i++){char c=text[i];int n=c is >= '0' and <= '9'?c-'0':c is >= 'a' and <= 'f'?c-'a'+10:c is >= 'A' and <= 'F'?c-'A'+10:-1;if(n<0)throw Invalid();code=code*16+n;}return code;}
    private static void ValidateTree(JsonElement e,ref int nodes)
    {
        if(++nodes>10000)throw Invalid();
        if(e.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in e.EnumerateObject()){if(!names.Add(p.Name))throw Invalid();ValidateTree(p.Value,ref nodes);}}
        else if(e.ValueKind==JsonValueKind.Array)foreach(var item in e.EnumerateArray())ValidateTree(item,ref nodes);
    }
    private static Dictionary<string,JsonElement> Fields(JsonElement e,string[] required,params string[] optional)
    {if(e.ValueKind!=JsonValueKind.Object)throw Invalid();var r=new Dictionary<string,JsonElement>(StringComparer.Ordinal);foreach(var p in e.EnumerateObject()){if(!required.Contains(p.Name,StringComparer.Ordinal)&&!optional.Contains(p.Name,StringComparer.Ordinal))throw Invalid();if(!r.TryAdd(p.Name,p.Value))throw Invalid();}if(required.Any(x=>!r.ContainsKey(x)))throw Invalid();return r;}
    private static string Text(JsonElement e,int min,int max)
    {if(e.ValueKind!=JsonValueKind.String)throw Invalid();var s=e.GetString()!;int count=s.EnumerateRunes().Count();if(count<min||count>max||s.Any(c=>c<32||c==127))throw Invalid();return s;}
    private static string Label(JsonElement e)
    {var s=Text(e,1,64);if(s.EnumerateRunes().All(Rune.IsWhiteSpace))throw Invalid();return s;}
    private static string Match(JsonElement e,int min,int max,string pattern)
    {var s=Text(e,min,max);if(!Regex.IsMatch(s,pattern,RegexOptions.CultureInvariant))throw Invalid();return s;}
    private static string Choice(JsonElement e,params string[] values)
    {var s=Text(e,1,32);if(!values.Contains(s,StringComparer.Ordinal))throw Invalid();return s;}
    private static int Int(JsonElement e,int min,int max)
    {if(e.ValueKind!=JsonValueKind.Number||!Regex.IsMatch(e.GetRawText(),"^-?(0|[1-9][0-9]*)\\z",RegexOptions.CultureInvariant)||!e.TryGetInt32(out int n)||n<min||n>max)throw Invalid();return n;}
    private static bool Bool(JsonElement e)=>e.ValueKind==JsonValueKind.True?true:e.ValueKind==JsonValueKind.False?false:throw Invalid();
    private static ulong? Bytes(JsonElement e)
    {if(e.ValueKind==JsonValueKind.Null)return null;var s=Text(e,1,20);if(!Regex.IsMatch(s,"^(0|[1-9][0-9]*)\\z",RegexOptions.CultureInvariant)||!ulong.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out var n))throw Invalid();return n;}
    private static string Authority(JsonElement e)
    {
        var s=Text(e,1,259);if(s.Any(c=>c>127))throw Invalid();var parts=s.Split(':');if(parts.Length>2)throw Invalid();string host=parts[0];
        if(host.Length>253||host.Length==0||host.EndsWith('.'))throw Invalid();
        if(host.Split('.').Any(x=>!Regex.IsMatch(x,"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\z",RegexOptions.CultureInvariant)))throw Invalid();
        if(Regex.IsMatch(host,"^(?:(?:0[xX][0-9a-fA-F]+)|[0-9]+)(?:\\.(?:(?:0[xX][0-9a-fA-F]+)|[0-9]+))*\\z",RegexOptions.CultureInvariant))throw Invalid();
        string suffix="";if(parts.Length==2){if(!Regex.IsMatch(parts[1],"^[1-9][0-9]{0,4}\\z",RegexOptions.CultureInvariant)||!int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out int p)||p>65535)throw Invalid();suffix=":"+parts[1];}
        return host.ToLowerInvariant()+suffix;
    }
    private static JsonElement[] Array(JsonElement e,int max)
    {if(e.ValueKind!=JsonValueKind.Array||e.GetArrayLength()>max)throw Invalid();return e.EnumerateArray().ToArray();}
    private static AppManifest Manifest(JsonElement e)
    {
        var d=Fields(e,["manifestVersion","applicationId","displayName","exports","imports"]);Int(d["manifestVersion"],1,1);
        string id=Match(d["applicationId"],3,128,"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+\\z"),name=Label(d["displayName"]);
        var ex=Array(d["exports"],16).Select(Export).ToArray();var im=Array(d["imports"],16).Select(Import).ToArray();
        if(ex.Length+im.Length==0||ex.Select(x=>x.ServiceId).Distinct(StringComparer.Ordinal).Count()!=ex.Length||im.Select(x=>x.Alias).Distinct(StringComparer.Ordinal).Count()!=im.Length)throw Invalid();
        return new(id,name,ex,im);
    }
    private static ExportService Export(JsonElement e)
    {
        var d=Fields(e,["serviceId","label","protocol","localTarget"],"logicalAuthority","healthCheck");
        string id=Match(d["serviceId"],1,32,"^[a-z][a-z0-9-]{0,31}\\z"),label=Label(d["label"]),protocol=Choice(d["protocol"],"tcp","http","https","ws","wss");
        var target=Fields(d["localTarget"],["host","port"]);if(Text(target["host"],1,32)!="127.0.0.1")throw Invalid();var local=new LocalTarget(Int(target["port"],1024,65535));
        string? authority=d.TryGetValue("logicalAuthority",out var a)?Authority(a):null;if(protocol is "https" or "wss"&&authority is null)throw Invalid();
        var health=d.TryGetValue("healthCheck",out var h)?Health(h):null;return new(id,label,protocol,local,authority,health);
    }
    private static ImportService Import(JsonElement e)
    {
        var d=Fields(e,["alias","protocol","endpointMode","connectionPolicy"],"logicalAuthority");
        string alias=Match(d["alias"],1,32,"^[a-z][a-z0-9-]{0,31}\\z"),protocol=Choice(d["protocol"],"tcp","http","https","ws","wss"),mode=Choice(d["endpointMode"],"brokered","loopback");
        string? authority=d.TryGetValue("logicalAuthority",out var a)?Authority(a):null;if(protocol is "https" or "wss"&&authority is null)throw Invalid();
        return new(alias,protocol,mode,authority,Policy(d["connectionPolicy"]));
    }
    private static HealthCheck Health(JsonElement e)
    {
        var d=Fields(e,["type"],"path","expectedStatus");var type=Choice(d["type"],"tcp","http");
        if(type=="tcp"){if(d.Count!=1)throw Invalid();return new(type,null,[]);}
        if(d.Count!=3||!d.ContainsKey("path")||!d.ContainsKey("expectedStatus"))throw Invalid();var path=Text(d["path"],1,1024);
        if(!path.StartsWith('/')||path.StartsWith("//",StringComparison.Ordinal)||path.Contains('#')||path.EnumerateRunes().Any(Rune.IsWhiteSpace))throw Invalid();
        var statuses=Array(d["expectedStatus"],10).Select(x=>Int(x,100,599)).ToArray();if(statuses.Length==0||statuses.Distinct().Count()!=statuses.Length)throw Invalid();return new(type,path,statuses);
    }
    private static ConnectionPolicy Policy(JsonElement e)
    {
        var d=Fields(e,["mode"],"connectTimeoutMs","directWaitMs","allowRelay","prewarm","prewarmTimeoutMs","prewarmIdleTimeoutMs","relayBudgetBytes","relayWarningBytes","relayBudgetAction");
        string mode=Choice(d["mode"],"fast-connect","direct-first","direct-only");
        int timeout=d.TryGetValue("connectTimeoutMs",out var t)?Int(t,1000,60000):15000;
        int wait=d.TryGetValue("directWaitMs",out var w)?Int(w,0,60000):mode=="direct-only"?timeout:Math.Min(mode=="fast-connect"?1500:8000,timeout);
        bool relay=d.TryGetValue("allowRelay",out var r)?Bool(r):true;
        if(wait>timeout||(mode=="direct-only"&&(!d.ContainsKey("allowRelay")||relay||wait!=timeout)))throw Invalid();
        bool prewarm=d.TryGetValue("prewarm",out var p)&&Bool(p);
        int prewarmTimeout=d.TryGetValue("prewarmTimeoutMs",out var pt)?Int(pt,1000,60000):8000;
        int idle=d.TryGetValue("prewarmIdleTimeoutMs",out var pi)?Int(pi,1000,600000):60000;
        ulong? budget=d.TryGetValue("relayBudgetBytes",out var b)?Bytes(b):null,warning=d.TryGetValue("relayWarningBytes",out var q)?Bytes(q):null;
        string? action=d.TryGetValue("relayBudgetAction",out var actionElement)?Choice(actionElement,"stop-relay"):null;
        if((budget.HasValue&&action is null)||(!budget.HasValue&&action is not null)||(budget.HasValue&&warning.HasValue&&warning.Value>budget.Value))throw Invalid();
        return new(mode,timeout,wait,relay,prewarm,prewarmTimeout,idle,budget,warning,action);
    }
}
