using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinkGate.Contracts;

var arguments=new Dictionary<string,string>(StringComparer.Ordinal);
for(int i=0;i<args.Length;i+=2){if(i+1>=args.Length||args[i] is not ("--vectors" or "--output")||!arguments.TryAdd(args[i],args[i+1]))throw new ArgumentException("Expected --vectors PATH --output PATH.");}
if(arguments.Count!=2)throw new ArgumentException("Expected --vectors PATH --output PATH.");
using var corpus=JsonDocument.Parse(File.ReadAllBytes(arguments["--vectors"]));
if(corpus.RootElement.GetProperty("corpusVersion").GetInt32()!=1)throw new Exception("Unsupported corpus version.");
var results=new List<object>();int passed=0;
foreach(var vector in corpus.RootElement.GetProperty("cases").EnumerateArray())
{
    var id=vector.GetProperty("id").GetString()!;bool accepted;JsonNode? normalized=null;string? errorCode=null;
    try
    {
        var bytes=Convert.FromBase64String(vector.GetProperty("inputUtf8Base64").GetString()!);
        var kind=vector.GetProperty("kind").GetString();
        string json;
        if(kind=="connection-status")json=ConnectionStatus.Parse(bytes).ToJson();
        else if(kind=="connection-operation")json=ConnectionOperation.Parse(bytes,Guid.Parse(vector.GetProperty("expectedOperation").GetString()!),vector.GetProperty("expectedKind").GetString()).ToJson();
        else if(kind=="connection-open")
        {
            var request=ConnectionOpenRequest.Parse(bytes);var approved=ManifestContract.NormalizePolicy(Convert.FromBase64String(vector.GetProperty("approvedPolicyUtf8Base64").GetString()!));
            using var value=JsonDocument.Parse(request.ToJson());json=JsonSerializer.Serialize(new{request=value.RootElement,withinApproved=request.WithinApproved(approved)});
        }
        else json=kind=="manifest"?ManifestContract.Parse(bytes).ToJson():kind=="policy"?ManifestContract.NormalizePolicy(bytes).ToJson():throw new Exception("Unsupported corpus kind.");
        accepted=true;normalized=JsonNode.Parse(json);
    }
    catch(ContractException error){accepted=false;errorCode=error.Code;if(error.Message!="Invalid public contract."||error.InnerException is not null)throw new Exception("Unsafe error surface.");}
    var expected=vector.GetProperty("accepted").GetBoolean();
    bool good=accepted==expected&&(accepted?JsonNode.DeepEquals(normalized,JsonNode.Parse(vector.GetProperty("normalized").GetRawText())):errorCode==vector.GetProperty("errorCode").GetString());
    if(good)passed++;else Console.Error.WriteLine("Conformance mismatch: "+id);
    var result=new Dictionary<string,object?>{["id"]=id,["accepted"]=accepted};if(accepted)result["normalized"]=normalized;else result["errorCode"]=errorCode;results.Add(result);
}
var manifest=ManifestContract.Parse(Encoding.UTF8.GetBytes("{\"manifestVersion\":1,\"applicationId\":\"org.example.app\",\"displayName\":\"Test\",\"exports\":[{\"serviceId\":\"data\",\"label\":\"Data\",\"protocol\":\"http\",\"localTarget\":{\"host\":\"127.0.0.1\",\"port\":18480},\"healthCheck\":{\"type\":\"http\",\"path\":\"/health\",\"expectedStatus\":[200]}}],\"imports\":[]}"));
foreach(var collection in new object[]{manifest.Exports,manifest.Imports,manifest.Exports[0].HealthCheck!.ExpectedStatus})
{
    if(collection is not IList list||!list.IsReadOnly)throw new Exception("Model collection is writable.");
    try{list.Clear();throw new Exception("Model mutation was accepted.");}catch(NotSupportedException){}
}
foreach(var model in new[]{typeof(AppManifest),typeof(ConnectionPolicy),typeof(ExportService),typeof(ImportService),typeof(LocalTarget),typeof(HealthCheck),typeof(ConnectionOpenRequest),typeof(ConnectionStatus),typeof(ConnectionOperation)})
    if(model.GetProperties().Any(p=>p.SetMethod is {IsPublic:true}))throw new Exception("Model has a public setter.");
try{ManifestContract.Parse(Encoding.UTF8.GetBytes("{\"token\":\"SIMULATED_PRIVATE_VALUE\"}"));throw new Exception("Invalid fixture accepted.");}catch(ContractException error){if(error.ToString().Contains("SIMULATED_PRIVATE_VALUE",StringComparison.Ordinal))throw new Exception("Input reflected.");}
var summary=new {contractVersion=1,total=results.Count,passed,cases=results};
var output=Path.GetFullPath(arguments["--output"]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllText(output,JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new {contractVersion=1,total=results.Count,passed,immutableModelsChecked=true,errorBoundaryChecked=true}));
return passed==results.Count?0:1;
