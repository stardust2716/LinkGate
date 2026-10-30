using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using LinkGate.Authority;
using LinkGate.Control;
using LinkGate.Processes;

if (args.Length != 3) return 2;
string repo = AppContext.BaseDirectory; while (!Directory.Exists(Path.Combine(repo, "gateway"))) repo = Directory.GetParent(repo)?.FullName ?? throw new IOException("Repository missing.");
string testRoot = Path.GetFullPath(Path.Combine(repo, "artifacts", "control-authority-interop")), directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N")), data = Path.Combine(directory, "data");
int checks = 0, javaChecks = 0; long revokeMs = 0, businessRevokeMs = 0, epochCloseMs = 0, outageCloseMs = 0; int backendConnections = 0; ulong renewals = 0;
void Check(bool value) { if (!value) throw new IOException("Actual control authority verification failed."); checks++; }
string Digest(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
string Read(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
async Task Wait(string path, params OwnedProcess[] children)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (!File.Exists(path) || new FileInfo(path).Length == 0)
    {
        foreach (var child in children) if (child.HasExited)
        {
            string error = Read(child.StandardErrorPath).Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(error, "^control-stage=[a-z-]+;failure=[A-Za-z]+\\z")) error = "fixed-child-failure";
            throw new IOException("Owned control child exited: " + error);
        }
        await Task.Delay(20, deadline.Token);
    }
}
async Task Refused(Func<Task> call, string code)
{
    try { await call(); throw new IOException("Refused control operation succeeded."); }
    catch (ControlRefusedException error) { Check(error.Code == code && error.InnerException is null); }
}
X509Certificate2 Certificate(string name) => X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(data, name + ".pem")));
try
{
    for (string? parent = data; parent is not null; parent = Path.GetDirectoryName(parent)) if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked fixture rejected.");
    var folder = Directory.CreateDirectory(data); using var current = WindowsIdentity.GetCurrent(); var user = current.User ?? throw new UnauthorizedAccessException();
    var acl = new DirectorySecurity(); acl.SetOwner(user); acl.SetAccessRuleProtection(true, false);
    foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    folder.SetAccessControl(acl);
    using var owner = new TestKey(); using var pub = new TestKey(); using var vis = new TestKey();
    var ownerKey = owner.Key; var publisherKey = pub.Key; var visitorKey = vis.Key;
    foreach (var entry in new[] { ("owner", ownerKey), ("publisher", publisherKey), ("visitor", visitorKey) })
        File.WriteAllText(Path.Combine(data, entry.Item1 + ".csr"), new CertificateRequest("CN=LinkGate control integration", entry.Item2, HashAlgorithmName.SHA256).CreateSigningRequestPem());
    string javaExecutable = Path.GetFullPath(args[1]), serverExecutable = Path.GetFullPath(args[0]);
    var environment = new Dictionary<string,string> { ["HTTPS_PROXY"] = "http://127.0.0.1:1", ["HTTP_PROXY"] = "http://127.0.0.1:1" };
    using var java = OwnedProcess.Start(javaExecutable, ["-cp", args[2], "dev.linkgate.interop.ControlAuthorityClient", data], Digest(javaExecutable), Path.Combine(directory, "java"), environment);
    await Wait(Path.Combine(data, "java.csr"), java);
    using var server = OwnedProcess.Start(serverExecutable, [data], Digest(serverExecutable), Path.Combine(directory, "server"));
    await Wait(Path.Combine(data, "ready.json"), server, java);
    using var configuration = ControlJson.ReadDocument(File.ReadAllBytes(Path.Combine(data, "ready.json"))); var config = configuration.RootElement; int port = config.GetProperty("port").GetInt32();
    using var root = Certificate("owner-root"); using var serverCert = Certificate("server-cert"); using var publisherPublic = Certificate("publisher-cert"); using var visitorPublic = Certificate("visitor-cert");
    using var publisherCert = publisherPublic.CopyWithPrivateKey(publisherKey); using var visitorCert = visitorPublic.CopyWithPrivateKey(visitorKey);
    using var publisher = await ControlClient.ConnectAsync(IPAddress.Loopback, port, publisherCert, serverCert, root);
    using var visitor = await ControlClient.ConnectAsync(IPAddress.Loopback, port, visitorCert, serverCert, root);
    var target = new ServiceTarget(config.GetProperty("publisher").GetString()!, "org.example.publisher", "files");
    using(var lagging=await ControlClient.ConnectAsync(IPAddress.Loopback,port,visitorCert,serverCert,root,new LeaseClock(new LaggingClock())))
    {var lease=await lagging.AcquireAsync("org.example.client",target);Check(lease.Receipt.Active&&lease.Receipt.Remaining<=TimeSpan.FromSeconds(120));}
    var acquired = await visitor.AcquireAsync("org.example.client", target); Check(acquired.Receipt.Active); Check(acquired.Receipt.Authority.Scope.VisitorDevice == config.GetProperty("visitor").GetString()); Check(!acquired.ToString().Contains(acquired.Token));
    var verified = await publisher.ValidateAsync(acquired.Token, acquired.Receipt.Authority.Scope); Check(verified.Receipt.Active && verified.Receipt.Authority == acquired.Receipt.Authority);
    Check((await publisher.CheckAsync([verified.Receipt]))[0]); Check((await visitor.CheckAsync([acquired.Receipt]))[0]);
    await using var supervisor = new LeaseSupervisor(publisher);
    var guards = Enumerable.Range(0,128).Select(_ => supervisor.Register(verified)).ToArray();
    await Refused(() => { supervisor.Register(verified); return Task.CompletedTask; }, "RESOURCE_EXHAUSTED");
    await Refused(async () => { _ = await visitor.AcquireAsync("org.example.other", target); }, "AUTH_REQUIRED");
    await Refused(async () => { using var invalid = await ControlClient.ConnectAsync(IPAddress.Loopback, port, visitorCert, publisherPublic, root); }, "CONTROL_UNAVAILABLE");
    await using var business = await DataFixture.StartAsync(directory, data, publisherCert, visitorCert, root, publisher, visitor);
    await Wait(Path.Combine(data,"java-local-denied"),java,server);Check(business.Accepted==0);business.AllowJava(data);
    await business.TransferAsync();
    await using var held = await business.HoldAsync();
    async Task<bool> Closed() {try { return await held.ReadAsync(new byte[1])==0; }catch(IOException){return true;} }
    Task<bool> blockedBusiness = Closed();
    await Wait(Path.Combine(data, "java-initial"), java, server);
    string javaToken = File.ReadAllText(Path.Combine(data, "java-token.jwt")); var javaScope = new AccessScope(config.GetProperty("java").GetString()!, "org.example.client", target);
    Check((await publisher.ValidateAsync(javaToken, javaScope)).Receipt.Active);
    // Abort actual stalled TCP/TLS establishment within the caller's tighter deadline.
    using (var stall = new TcpListener(IPAddress.Loopback, 0))
    {
        stall.Start(); var acceptedTask = stall.AcceptTcpClientAsync(); using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var began = System.Diagnostics.Stopwatch.StartNew();
        try { using var ignored = await ControlClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)stall.LocalEndpoint).Port, visitorCert, serverCert, root, cancellation: cancel.Token); throw new IOException("Canceled control handshake succeeded."); }
        catch (OperationCanceledException) { Check(began.Elapsed < TimeSpan.FromSeconds(2)); }
        using var accepted = await acceptedTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    File.WriteAllText(Path.Combine(data,"observe-before"),"true");await Wait(Path.Combine(data,"observation-before.json"),server);
    using var before=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data,"observation-before.json")));
    // Real elapsed time, not an accelerated/mock clock. Both visitor Agents renew
    // their 120-second signed leases at the 60-second threshold without redialing.
    await Task.Delay(TimeSpan.FromSeconds(65));Check(!blockedBusiness.IsCompleted);
    File.WriteAllText(Path.Combine(data,"observe-after"),"true");await Wait(Path.Combine(data,"observation-after.json"),server);
    using var after=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data,"observation-after.json")));
    renewals=after.RootElement.GetProperty("publisherValidations").GetUInt64()-before.RootElement.GetProperty("publisherValidations").GetUInt64();
    Check(renewals>=2);Check(after.RootElement.GetProperty("leasesIssued").GetUInt64()>=before.RootElement.GetProperty("leasesIssued").GetUInt64()+2);
    Check(business.Accepted==4);
    var revokeWatch = System.Diagnostics.Stopwatch.StartNew(); File.WriteAllText(Path.Combine(data, "revoke"), "true"); await Wait(Path.Combine(data, "revoked"), java, server);
    while(guards.Any(guard=>!guard.Cancellation.IsCancellationRequested)) { if(revokeWatch.Elapsed>=TimeSpan.FromSeconds(5))throw new IOException("Active authorization revoke deadline exceeded."); await Task.Delay(10); }
    revokeMs = revokeWatch.ElapsedMilliseconds; Check(guards.All(guard=>!guard.Active));
    Check(await blockedBusiness.WaitAsync(TimeSpan.FromSeconds(5)));businessRevokeMs=revokeWatch.ElapsedMilliseconds;Check(businessRevokeMs<5000);
    Check(await business.RejectedAfterRevoke());
    try { guards[0].Renew(verified); throw new IOException("Revoked guard revived."); } catch(LeaseRejectedException) { Check(true); }
    Check(!(await publisher.CheckAsync([verified.Receipt]))[0]);
    await Refused(async () => { _ = await visitor.AcquireAsync("org.example.client", target); }, "AUTH_REQUIRED");
    await Refused(async () => { _ = await publisher.ValidateAsync(acquired.Token, acquired.Receipt.Authority.Scope); }, "AUTH_REQUIRED");
    await Refused(async () => { _ = await publisher.ValidateAsync(javaToken, javaScope); }, "AUTH_REQUIRED");
    using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!java.HasExited) await Task.Delay(20, finish.Token);
    Check(business.Accepted==4&&business.Sent==134217728);backendConnections=business.Accepted;
    Check(java.ExitCode == 0); using var result = JsonDocument.Parse(Read(java.StandardOutputPath)); javaChecks = result.RootElement.GetProperty("checks").GetInt32(); Check(result.RootElement.GetProperty("passed").GetBoolean() && javaChecks >= 10);
    File.WriteAllText(Path.Combine(data,"approve"),"true");await Wait(Path.Combine(data,"approved"),server);
    var newlyApproved=await visitor.AcquireAsync("org.example.client",target);Check(newlyApproved.Receipt.Authority.GrantRevision==3);
    try{guards[0].Renew(newlyApproved);throw new IOException("Reapproval revived a closed guard.");}catch(LeaseRejectedException){Check(true);}
    await using var epochHeld=await business.HoldAsync();
    async Task<bool> Ended(BusinessStream stream){try{return await stream.ReadAsync(new byte[1])==0;}catch(IOException){return true;}}
    var epochRead=Ended(epochHeld);var epochWatch=System.Diagnostics.Stopwatch.StartNew();File.WriteAllText(Path.Combine(data,"restart"),"true");await Wait(Path.Combine(data,"restarted"),server);
    Check(await epochRead.WaitAsync(TimeSpan.FromSeconds(5)));epochCloseMs=epochWatch.ElapsedMilliseconds;Check(epochCloseMs<5000);
    try{_=await visitor.AcquireAsync("org.example.client",target);throw new IOException("Old epoch accepted a new lease.");}catch(LeaseRejectedException){Check(true);}
    try{_=await visitor.AcquireAsync("org.example.client",target);throw new IOException("Closed epoch reopened.");}catch(LeaseRejectedException){Check(true);}
    using var recoveredPublisher=await ControlClient.ConnectAsync(IPAddress.Loopback,port,publisherCert,serverCert,root);
    using var recoveredVisitor=await ControlClient.ConnectAsync(IPAddress.Loopback,port,visitorCert,serverCert,root);
    await Refused(async()=>{_=await recoveredPublisher.ValidateAsync(acquired.Token,acquired.Receipt.Authority.Scope);},"AUTH_REQUIRED");
    string recovery=Path.Combine(data,"recovery");Directory.CreateDirectory(recovery);File.Copy(Path.Combine(data,"java-cert.pem"),Path.Combine(recovery,"java-cert.pem"));
    await using var restoredBusiness=await DataFixture.StartAsync(Path.Combine(directory,"recovery-state"),recovery,publisherCert,visitorCert,root,recoveredPublisher,recoveredVisitor);
    await using var outageHeld=await restoredBusiness.HoldAsync();var outageRead=Ended(outageHeld);
    var outageWatch=System.Diagnostics.Stopwatch.StartNew();File.WriteAllText(Path.Combine(data,"stop"),"true");await Wait(Path.Combine(data,"stopped"),server);
    Check(await outageRead.WaitAsync(TimeSpan.FromSeconds(5)));outageCloseMs=outageWatch.ElapsedMilliseconds;Check(outageCloseMs<5000);

}
finally
{
    string target = Path.GetFullPath(directory); if (!target.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (Directory.Exists(target)) { try { Directory.Delete(target, true); } catch (IOException) { await Task.Delay(25, deadline.Token); } }
}
string evidence = JsonSerializer.Serialize(new { passed = true, dotnetChecks = checks, javaChecks, actualMutualTlsHttp = true, actualGoAuthority = true, crossLanguageGrant = true, independentSignedLease = true, revokeRejectsNewAuthority = true, fixedPin = true, canceledHandshake = true, activeAuthorityGuards = 128, activeAuthorityRevokeMs = revokeMs, actualSdkAndJavaBusiness = true, realRenewalObservationSeconds = 65, actualRenewedPublisherValidations = renewals, fileBytesEach = 67108864, bothLocalAndServerGrant = true, requestFin = true, activeBusinessRevokeMs = businessRevokeMs, backendConnections, serverEpochChangeClosedMs = epochCloseMs, controlOutageClosedMs = outageCloseMs, explicitNewControlGeneration = true, clientPrivateKeysExported = false, physicalDevice = false, cloudAccessed = false, frpIntegrated = false });
File.WriteAllText(Path.Combine(repo, "artifacts", "control-authority-interop-summary.json"), evidence); Console.WriteLine(evidence); return 0;

sealed class TestKey : IDisposable
{
    private readonly CngKey handle = CngKey.Create(CngAlgorithm.ECDsaP256, "LinkGate.Control.Test." + Guid.NewGuid().ToString("N"), new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing, Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider });
    public ECDsaCng Key { get; }
    public TestKey() { Key = new ECDsaCng(handle); }
    public void Dispose() { try { handle.Delete(); } finally { Key.Dispose(); handle.Dispose(); } }
}

sealed class LaggingClock:TimeProvider {public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UtcNow-TimeSpan.FromSeconds(30);}
