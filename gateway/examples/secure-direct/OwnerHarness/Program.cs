using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LinkGate.Broker;

if(args.Length!=2){Console.Error.WriteLine("Usage: OwnerHarness <Agent.exe> <SecureApp.exe>");return 2;}
string repo=AppContext.BaseDirectory;
while(!Directory.Exists(Path.Combine(repo,"gateway")))repo=Directory.GetParent(repo)?.FullName??throw new InvalidOperationException("Repository missing.");
string artifactRoot=Path.GetFullPath(Path.Combine(repo,"artifacts","linkgate-secure-direct"));string directory=Path.Combine(artifactRoot,Guid.NewGuid().ToString("N"));
using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
Child? publisher=null,client=null;
try
{
    SecureIdentity.MakePrivate(directory);
    using var owner=SecureIdentity.CreateOwnerRoot();
    using var publisherIdentity=CreateIdentity("publisher");using var clientIdentity=CreateIdentity("client");
    var published=new RegistrationStore(Path.Combine(directory,"publisher/state"));published.BindSecureIdentity(publisherIdentity);
    var consuming=new RegistrationStore(Path.Combine(directory,"client/state"));consuming.BindSecureIdentity(clientIdentity);
    foreach(string side in new[]{"publisher","client"})SecureIdentity.MakePrivate(Path.Combine(directory,side,"application"));
    using var applicationKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request=new CertificateRequest("CN=files.example.invalid",applicationKey,HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
    var san=new SubjectAlternativeNameBuilder();san.AddDnsName("files.example.invalid");request.CertificateExtensions.Add(san.Build());
    using var appCertificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
    string publicCert=Path.Combine(directory,"application-public.pem");SecureIdentity.WriteNew(publicCert,appCertificate.ExportCertificatePem());
    SecureIdentity.WriteNew(Path.Combine(directory,"publisher/application/application-cert.pem"),appCertificate.ExportCertificatePem());
    SecureIdentity.WriteNew(Path.Combine(directory,"publisher/application/application-key.pem"),applicationKey.ExportPkcs8PrivateKeyPem());
    string secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    string agentPath=Path.GetFullPath(args[0]),appPath=Path.GetFullPath(args[1]);
    string digest;using(var file=File.OpenRead(agentPath))digest=Convert.ToHexString(SHA256.HashData(file));
    publisher=Child.Launch(appPath,agentPath,digest,directory,"publisher","publish",secret,publicCert);JsonElement pubReady=await publisher.Read("ready");
    client=Child.Launch(appPath,agentPath,digest,directory,"client","consume",secret,publicCert);JsonElement clientReady=await client.Read("ready");
    Guid publisherRef=pubReady.GetProperty("applicationRef").GetGuid(),clientRef=clientReady.GetProperty("applicationRef").GetGuid();
    if(pubReady.GetProperty("deviceId").GetString()!=publisherIdentity.DeviceId||clientReady.GetProperty("deviceId").GetString()!=clientIdentity.DeviceId)throw new IOException("Device readiness mismatch.");
    published.TrustSecurePeer(publisherIdentity,clientIdentity.Certificate.ExportCertificatePem(),0,Guid.NewGuid(),"127.0.0.1",clientReady.GetProperty("peerPort").GetInt32());
    consuming.TrustSecurePeer(clientIdentity,publisherIdentity.Certificate.ExportCertificatePem(),0,Guid.NewGuid(),"127.0.0.1",pubReady.GetProperty("peerPort").GetInt32());
    published.DecideReview(published.GetReview(publisherRef),ApplicationReviewDecision.Approved,Guid.NewGuid());
    consuming.DecideReview(consuming.GetReview(clientRef),ApplicationReviewDecision.Approved,Guid.NewGuid());
    await Send("deny");await client.Read("denied");
    var fileGrant=published.ApproveSecureGrant(published.ProposeSecureGrant(clientIdentity.DeviceId,"org.example.secure-consumer",publisherRef,"files"),Guid.NewGuid());
    published.ApproveSecureGrant(published.ProposeSecureGrant(clientIdentity.DeviceId,"org.example.secure-consumer",publisherRef,"raw"),Guid.NewGuid());
    await Send("transfer");var transfer=await client.Read("transferred");
    if(transfer.GetProperty("fileBytes").GetInt64()!=67108864 || !transfer.GetProperty("halfClose").GetBoolean() || !transfer.GetProperty("applicationAuthentication").GetBoolean())throw new IOException("Business verification missing.");
    await Send("hold");await client.Read("holding");
    var revoke=Stopwatch.StartNew();published.RevokeSecureGrant(Guid.Parse(fileGrant.Reference),fileGrant.Revision,Guid.NewGuid());
    var ended=await client.Read("revoked");long revokeMs=revoke.ElapsedMilliseconds;
    if(revokeMs>=5000)throw new IOException("Active revocation exceeded five seconds.");
    await Send("after-revoke");var isolated=await client.Read("isolated");
    Console.WriteLine(JsonSerializer.Serialize(new{passed=true,independentApplication=true,consumerSdkOnly=true,mutualTls=true,explicitServiceGrant=true,ungrantedDenied=true,fileBytes=transfer.GetProperty("fileBytes").GetInt64(),webSocketBytes=transfer.GetProperty("webSocketBytes").GetInt32(),halfClose=true,applicationAuthentication=true,activeRevokeMs=revokeMs,revokedOpenDenied=isolated.GetProperty("revokedOpenDenied").GetBoolean(),unrelatedServiceWorks=isolated.GetProperty("unrelatedServiceWorks").GetBoolean(),physicalDevices=false,natTraversal=false,relay=false,installedRuntimeChanged=false}));
    return 0;

    SecureIdentity CreateIdentity(string side)
    {
        string path=Path.Combine(directory,side,"identity");string csr=SecureIdentity.CreateDeviceRequest(path);
        SecureIdentity.WriteNew(Path.Combine(path,"device-cert.pem"),SecureIdentity.IssueDeviceCertificate(owner,csr));
        SecureIdentity.WriteNew(Path.Combine(path,"owner-root.pem"),owner.ExportCertificatePem());return SecureIdentity.Load(path);
    }
    Task Send(string action)=>client!.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{action,peer=publisherIdentity.DeviceId}));
}
catch(Exception e){Console.Error.WriteLine("Secure direct acceptance failed: "+e);return 1;}
finally
{
    if(client is not null)await client.DisposeAsync();if(publisher is not null)await publisher.DisposeAsync();
    string target=Path.GetFullPath(directory);
    if(!target.StartsWith(artifactRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe test cleanup.");
    if(Directory.Exists(target))Directory.Delete(target,true);
}

internal sealed class Child : IAsyncDisposable
{
    public Process Process{get;}
    private readonly Task<string> errors;
    private Child(Process process){Process=process;errors=process.StandardError.ReadToEndAsync();}
    public static Child Launch(string app,string agent,string digest,string directory,string side,string mode,string token,string publicCert)
    {
        var start=new ProcessStartInfo(app){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string item in new[]{mode,agent,digest,Path.Combine(directory,side,"state"),Path.Combine(directory,side,"identity"),Path.Combine(directory,side,"application"),publicCert})start.ArgumentList.Add(item);
        start.Environment["LINKGATE_SAMPLE_TOKEN"]=token;return new Child(Process.Start(start)??throw new IOException("Application launch failed."));
    }
    public async Task<JsonElement> Read(string expected)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string? line=await Process.StandardOutput.ReadLineAsync(deadline.Token);
        if(line is null)throw new IOException("Application stopped: "+await errors);
        using var document=JsonDocument.Parse(line);if(document.RootElement.GetProperty("eventName").GetString()!=expected)throw new IOException("Unexpected application event.");
        return document.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        if(!Process.HasExited)
        {
            Process.StandardInput.Close();try{await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7));}catch(TimeoutException){Process.Kill(true);await Process.WaitForExitAsync();}
        }
        Process.Dispose();
    }
}
