using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using LinkGate;

var passed = 0;
var physical = new PhysicalAdapter(11, "192.168.1.46", "02:11:22:33:44:55", "Ethernet");
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (GatewayException) { passed++; return; }
    throw new Exception("FAIL accepted: " + name);
}
Check(PhysicalEgress.Select(new(false, [])).Adapter is null, "no TUN leaves system route untouched");
Check(PhysicalEgress.Select(new(true, [physical])).Adapter == physical, "unique physical adapter selected");
Reject(() => PhysicalEgress.Select(new(true, [])), "missing physical adapter fails closed");
Reject(() => PhysicalEgress.Select(new(true, [physical, physical with { Index = 12 }])), "ambiguous adapters fail closed");
foreach (var bad in new[] { physical with { Address = "198.18.0.1" }, physical with { Address = "127.0.0.1" }, physical with { Mac = "invalid" }, physical with { Index = 0 } })
    Reject(() => PhysicalEgress.Select(new(true, [bad])), "invalid egress candidate rejected");
{
    var d = new ConnectionDiagnostics(); d.ConfigureEgress(11);
    Check(d.ObserveEgress("LINKGATE_EGRESS {\"v\":1,\"mode\":\"physical-ipv4\",\"kind\":\"tcp\",\"interfaceIndex\":11}"), "matching native socket option confirmation");
    Check(!d.ObserveEgress("LINKGATE_EGRESS {\"v\":1,\"mode\":\"physical-ipv4\",\"kind\":\"tcp\",\"interfaceIndex\":50}"), "wrong adapter cannot confirm bypass");
    d.NatPreparationFailed();
    var state=System.Text.Json.JsonSerializer.SerializeToElement(d.Snapshot());
    Check(state.GetProperty("natPreparationFailure").GetString()=="stun_insufficient_observations" && !state.GetProperty("businessDirectObserved").GetBoolean(), "STUN failure distinct from bypass and P2P");
    d.NatHoleObserved();
    Check(System.Text.Json.JsonSerializer.SerializeToElement(d.Snapshot()).GetProperty("natPreparationFailure").GetString()=="none", "later NAT success clears obsolete preparation error");
}
using var key = RSA.Create(2048);
{
    var d=new ConnectionDiagnostics();
    const string valid="LINKGATE_STUN {\"v\":1,\"status\":\"ready\",\"observations\":2,\"attemptedEndpoints\":2,\"fallbackUsed\":true}";
    Check(d.ObserveStun(valid),"two real STUN observations accepted");
    var state=System.Text.Json.JsonSerializer.SerializeToElement(d.Snapshot());
    Check(state.GetProperty("stunObservations").GetInt32()==2&&!state.GetProperty("natHoleObserved").GetBoolean()&&!state.GetProperty("businessDirectObserved").GetBoolean(),"STUN readiness is not NAT or business proof");
    foreach(var bad in new[]{valid.Replace("\"observations\":2","\"observations\":1"),valid.Replace("\"v\":1","\"v\":1,\"v\":1"),valid.Replace("\"attemptedEndpoints\":2","\"attemptedEndpoints\":-1"),valid.Replace("ready","unknown"),valid.Replace("true","\"true\"")})
        Check(!d.ObserveStun(bad),"malformed STUN report rejected");
    Check(d.ObserveStun(valid.Replace("ready","unreachable").Replace("\"observations\":2","\"observations\":0"))&&System.Text.Json.JsonSerializer.SerializeToElement(d.Snapshot()).GetProperty("natPreparationFailure").GetString()=="stun_unreachable","unreachable STUN distinguished");
    Check(d.ObserveStun(valid)&&System.Text.Json.JsonSerializer.SerializeToElement(d.Snapshot()).GetProperty("natPreparationFailure").GetString()=="none","later ready report clears preparation failure");
}
var request = new CertificateRequest("CN=LinkGate tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
var source = new GatewayProfile(1, "abc123def456", "Test computer", new GatewayServer("relay.example.invalid", 17400,
    new string('a', 64), "gateway.internal", certificate.ExportCertificatePem(), 21116),
    [new("demo", "Read-only demo", 18480, 18481, new string('b', 64))]);
var bytes = source.ToJson();
var profile = GatewayProfile.Parse(bytes);
Check(profile.Namespace == source.Namespace && profile.Services.Count == 1 && profile.Server.StunPort == 21116, "valid profile roundtrip");
JsonObject Node() => JsonNode.Parse(bytes)!.AsObject();
GatewayProfile Parse(JsonObject value) => GatewayProfile.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()));
foreach (var host in new[] { "http://example.com", "0.0.0.0", "224.0.0.1", "example.com/path", "example.com\nhello", "-invalid.example", "example..com", "::1", "127.1", "0177.0.0.1", "0x7f000001", "999.0.0.1" })
{
    var bad = Node(); bad["server"]!["host"] = host;
    Reject(() => Parse(bad), "host boundary");
}
foreach (var location in new[] { "root", "server", "service" })
{
    var bad = Node();
    var target = location == "root" ? bad : location == "server" ? bad["server"]!.AsObject() : bad["services"]![0]!.AsObject();
    target["unexpected"] = "value";
    Reject(() => Parse(bad), "unknown field " + location);
}
var duplicate = Encoding.UTF8.GetString(bytes).Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1", StringComparison.Ordinal);
Reject(() => GatewayProfile.Parse(Encoding.UTF8.GetBytes(duplicate)), "duplicate JSON property");
foreach (var field in new[] { "token", "tlsServerName", "trustedCaPem" })
{
    var bad = Node(); bad["server"]!.AsObject().Remove(field);
    Reject(() => Parse(bad), "missing server field");
}
foreach (var port in new[] { 0, 22, 65536 })
{
    var bad = Node(); bad["services"]![0]!["visitorPort"] = port;
    Reject(() => Parse(bad), "visitor port boundary");
}
{
    var bad = Node(); bad["services"]!.AsArray().Add(bad["services"]![0]!.DeepClone());
    Reject(() => Parse(bad), "duplicate service id");
    bad["services"]![1]!["id"] = "other";
    Reject(() => Parse(bad), "duplicate visitor port");
}
{
    var bad = Node(); bad["server"]!["trustedCaPem"] = "-----BEGIN " + "PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----";
    Reject(() => Parse(bad), "private key in CA");
    bad["server"]!["trustedCaPem"] = certificate.ExportCertificatePem() + certificate.ExportCertificatePem();
    Reject(() => Parse(bad), "multiple CA certificates");
    bad["server"]!["trustedCaPem"] = certificate.ExportCertificatePem() + "unexpected-tail";
    Reject(() => Parse(bad), "CA trailing text");
}
{
    var optional = Node(); optional["server"]!.AsObject().Remove("stunPort");
    Check(Parse(optional).Server.StunPort == 3478, "optional STUN default");
}
Reject(() => GatewayProfile.Parse(new byte[GatewayProfile.MaxBytes + 1]), "size limit");
var publisher = FrpConfig.Render(profile, GatewayRole.Publisher, "C:\\Users\\Test User\\ca.pem");
var visitor = FrpConfig.Render(profile, GatewayRole.Visitor, "C:\\Users\\Test User\\ca.pem");
Check(publisher.Contains("type = \"xtcp\"") && publisher.Contains("type = \"stcp\"") && !publisher.Contains("remotePort"), "only private publisher types");
Check(publisher.Contains("localIP = \"127.0.0.1\"") && publisher.Contains("natHoleStunServer = \"relay.example.invalid:21116\""), "loopback and self-hosted STUN");
Check(visitor.Contains("bindAddr = \"127.0.0.1\"") && visitor.Contains("bindPort = -1") && visitor.Contains("fallbackTo = \"abc123def456-demo-fallback\""), "visitor fallback remains private");
Check(visitor.Contains("fallbackTimeoutMs = 1500") && visitor.Contains("transport.tls.enable = true") && visitor.Contains("transport.tls.serverName = \"gateway.internal\""), "timeout and TLS identity");
var temporary = Path.Combine(Path.GetTempPath(), "LinkGate-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var state = new PrivateStore(Path.Combine(temporary, "private"));
    var input = Path.Combine(temporary, "input.json");
    File.WriteAllBytes(input, bytes);
    state.Import(input);
    var previous = File.ReadAllBytes(state.ProfilePath);
    File.WriteAllText(input, "{}");
    Reject(() => state.Import(input), "invalid import rejected");
    Check(File.ReadAllBytes(state.ProfilePath).SequenceEqual(previous), "invalid import preserves existing profile");
    Check(new DirectoryInfo(state.DirectoryPath).GetAccessControl().AreAccessRulesProtected, "state ACL does not inherit");
    Check(Directory.GetFiles(state.DirectoryPath, "*.new").Length == 0, "no temporary files left");
}
finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
using (var host = new GatewayHost())
{
    var observe = typeof(GatewayHost).GetMethod("Observe", BindingFlags.Instance | BindingFlags.NonPublic)!;
    for (var i = 0; i < 200; i++) observe.Invoke(host, ["error token " + profile.Server.Token + " service " + profile.Services[0].SecretKey]);
    Check(host.Events.Count == 100 && host.Events.All(line => !line.Contains(profile.Server.Token) && !line.Contains(profile.Services[0].SecretKey)), "bounded classified log excludes credentials");
    Check(!host.IsRunning && !JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(host.DiagnosticsSnapshot()))!["businessDirectObserved"]!.GetValue<bool>(), "process state is not direct evidence");
    foreach (var line in new[]
    {
        "visitor [abc123def456-demo-fallback] added",
        "start visitor success [abc123def456-demo-fallback]",
        "try to transfer connection to visitor: abc123def456-demo-fallback",
        "open tunnel error: timeout",
        "establish quic connection success"
    }) observe.Invoke(host, [line]);
    Check(!JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(host.DiagnosticsSnapshot()))!["businessDirectObserved"]!.GetValue<bool>(), "visitor names and transfer attempts are not path proof");
    observe.Invoke(host, ["[I] establishing nat hole connection successful"]);
    var snapshot=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(host.DiagnosticsSnapshot()))!;
    Check(snapshot["natHoleObserved"]!.GetValue<bool>() && !snapshot["businessDirectObserved"]!.GetValue<bool>(), "exact FRP NAT event is limited evidence");
}
{
    var d=new ConnectionDiagnostics();
    string Metric(long id,string path,string phase,long tx,long rx,long elapsed=10,string reason="") =>
        "LINKGATE_METRIC "+System.Text.Json.JsonSerializer.Serialize(new{v=1,id,role="visitor",path,phase,tx,rx,setupMs=5,elapsedMs=elapsed,reason});
    JsonNode Snapshot()=>JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(d.Snapshot()))!;
    Check(d.Observe(Metric(1,"direct","open",0,0)),"selected path without bytes");
    Check(!Snapshot()["businessDirectObserved"]!.GetValue<bool>(),"zero byte direct is not business proof");
    Check(d.Observe(Metric(1,"direct","open",100,200,20)),"direct data");
    Check(d.Observe(Metric(1,"direct","open",100,200,20)) && Snapshot()["directRx"]!.GetValue<long>()==200,"duplicate cumulative sample not double counted");
    Check(!d.Observe(Metric(1,"direct","open",99,200,30)),"decreasing counters rejected");
    Check(d.Observe(Metric(2,"relay","open",50,70,30,"direct_tunnel_unavailable")) && Snapshot()["currentPath"]!.GetValue<string>()=="direct+relay","mixed active paths");
    Check(d.Observe(Metric(1,"direct","closed",100,210,40)) && !d.Observe(Metric(1,"direct","open",101,211,50)),"closed streams cannot be reopened by stale sample");
    Check(!d.Observe(Metric(3,"unknown","open",0,0)) && !d.Observe(Metric(3,"relay","open",-1,0)),"invalid path and negative bytes rejected");
    Check(d.Observe(Metric(4,"unknown","failed",0,0,100,"direct_unavailable_relay_failed")),"failed setup distinct from relay data");
    d.EndActive(); Check(Snapshot()["currentPath"]!.GetValue<string>()=="idle" && Snapshot()["directRx"]!.GetValue<long>()==210,"stop clears active paths but preserves evidence");
}
{
    var received = new List<string>();
    using var input = new StringReader(new string('x', 2 * 1024 * 1024) + "\r\nsecond\nlast");
    await RuntimeLogReader.PumpAsync(input, received.Add);
    Check(received.Count == 3 && received[0].Length == RuntimeLogReader.MaximumLineLength && received[1] == "second" && received[2] == "last", "log pump bounds lines without losing subsequent events");
}
using (var child = new Process
{
    StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
    {
        UseShellExecute = false, CreateNoWindow = true, Arguments = "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60"
    }
})
{
    using (var job = new ProcessJob()) { child.Start(); job.Assign(child); Check(!child.HasExited, "owned child running"); }
    Check(child.WaitForExit(5000), "closing job kills owned child");
}
{
    var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    using var done = new CancellationTokenSource();
    var serving = DemoServer.RunAsync(port, done.Token);
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var health = await http.GetStringAsync($"http://127.0.0.1:{port}/health");
        Check(health.Contains("linkgate-demo", StringComparison.Ordinal), "demo HTTP health");
        var body = await http.GetByteArrayAsync($"http://127.0.0.1:{port}/bytes/1025");
        Check(body.Length == 1025 && body.Select((value, i) => value == (byte)(i % 251)).All(value => value), "demo file integrity");
        using var response = await http.PostAsync($"http://127.0.0.1:{port}/health", new StringContent("test"));
        Check(response.StatusCode == HttpStatusCode.MethodNotAllowed, "demo rejects writes");
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
        await socket.SendAsync("linkgate-test"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[1024];
        var message = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        Check(message.MessageType == WebSocketMessageType.Text && message.EndOfMessage && Encoding.UTF8.GetString(buffer, 0, message.Count) == "linkgate-test", "demo WebSocket echo");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
    finally { done.Cancel(); await serving; }
}
Console.WriteLine($"PASS {passed} checks; configuration, secret boundary, atomic import, log limits and process ownership.");
