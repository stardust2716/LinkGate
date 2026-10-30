using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LinkGate.Broker;

if (await PeerTests.RunChildAsync(args)) return;
if (await RegistrationTests.RunChildAsync(args)) return;
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
if(args.SequenceEqual(new[]{"--scoped"}))
{
    LocalAccessTests.Run(Check);ConnectionJournalTests.Run(Check);await ConnectionPlannerTests.RunAsync(Check);await FormalApiTests.Run(Check);await OwnerAccessCliTests.Run(Check);
    Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks,suite="scoped-routing",cloudAccessed=false,installedRuntimeChanged=false}));return;
}

if(args.SequenceEqual(new[]{"--secure-stream"}))
{
    await SecureStreamTests.RunAsync(Check);
    Console.WriteLine(JsonSerializer.Serialize(new{passed=true,checks,suite="secure-stream",cloudAccessed=false}));return;
}

byte[] Request(string method = "hello", string parameters = "{\"supportedApiVersions\":[1]}") =>
    Encoding.UTF8.GetBytes($"{{\"kind\":\"request\",\"apiVersion\":1,\"requestId\":\"00000000-0000-4000-8000-000000000001\",\"method\":\"{method}\",\"params\":{parameters}}}");
string Error(ControlProtocol p, byte[] bytes) => JsonDocument.Parse(p.Dispatch(bytes)).RootElement.GetProperty("error").GetProperty("code").GetString()!;
void Reject(ControlProtocol p, byte[] bytes)
{
    try { p.Dispatch(bytes); throw new Exception("Malformed request accepted"); }
    catch (InvalidDataException e) { Check(e.Message == "Invalid control request." && e.InnerException is null, "sanitized error"); }
}
var protocol = new ControlProtocol();
var valid = Request();
using var hello = JsonDocument.Parse(protocol.Dispatch(valid));
Check(hello.RootElement.GetProperty("result").GetProperty("capabilities").EnumerateArray().All(x => x.GetProperty("state").GetString() == "unsupported"), "honest capabilities");
Check(hello.RootElement.GetProperty("requestId").GetString() == "00000000-0000-4000-8000-000000000001", "correlation");
Check(Error(protocol, Request(parameters: "{\"supportedApiVersions\":[2]}")) == "VERSION_UNSUPPORTED", "version negotiation");
foreach (string parameters in new[] { "{}", "{\"supportedApiVersions\":[]}", "{\"supportedApiVersions\":[1.0]}", "{\"supportedApiVersions\":[true]}", "{\"supportedApiVersions\":[0]}", "{\"supportedApiVersions\":[1],\"pid\":1}", "{\"supportedApiVersions\":[1,2,3,4,5,6,7,8,9]}" })
    Check(Error(protocol, Request(parameters: parameters)) == "INVALID_REQUEST", "invalid hello params");
Check(Error(protocol, Request("gateway.stop", "{}")) == "CAPABILITY_UNSUPPORTED", "no management dispatch");
Check(Error(protocol, Request("gateway.status", "{}")) == "CAPABILITY_UNSUPPORTED", "no global status disclosure");
string text = Encoding.UTF8.GetString(valid);
foreach (string malformed in new[] { "{}", text.Replace("\"apiVersion\":1", "\"apiVersion\":1.0"), text.Replace("\"kind\":", "\"kind\":\"request\",\"kind\":"), text.Replace("\"kind\":", "\"ki\\u006ed\":\"request\",\"kind\":"), text.Replace("\"params\":", "\"fakeSid\":\"secret-marker\",\"params\":"), text.Replace("hello", "\\ud800"), text + "{}", "\ufeff" + text, text.Replace("00000000-0000-4000-8000-000000000001", "not-uuid") })
    Reject(protocol, Encoding.UTF8.GetBytes(malformed));
Reject(protocol, [0xff, 0xfe]);
Reject(protocol, new byte[65537]);
Reject(protocol, Request(parameters: "{\"supportedApiVersions\":[1],\"secret\":" + new string('[', 17) + "0" + new string(']', 17) + "}"));
Reject(protocol, Request(parameters: "{\"supportedApiVersions\":[1],\"secret\":[" + string.Join(',', Enumerable.Repeat("0", 10001)) + "]}"));
// The bound counts the final encoded envelope, including otherwise permissible whitespace.
Check(protocol.Dispatch(valid.Concat(Enumerable.Repeat((byte)' ', 65536 - valid.Length)).ToArray()).Length > 0, "maximum JSON frame accepted");
Reject(protocol, valid.Concat(Enumerable.Repeat((byte)' ', 65537 - valid.Length)).ToArray());
await using (var stream = new MemoryStream())
{
    await FrameCodec.WriteAsync(stream, valid, default); stream.Position = 0;
    Check((await FrameCodec.ReadAsync(stream, default))!.SequenceEqual(valid), "framing roundtrip");
    Check(await FrameCodec.ReadAsync(stream, default) is null, "clean EOF");
}
foreach (uint length in new uint[] { 0, 65537, uint.MaxValue })
{
    var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, length);
    try { await FrameCodec.ReadAsync(new MemoryStream(header), default); throw new Exception("Bad length accepted"); }
    catch (InvalidDataException) { checks++; }
}
foreach (byte[] truncated in new byte[][] { [1, 0], [2, 0, 0, 0, 42] })
{
    try { await FrameCodec.ReadAsync(new MemoryStream(truncated), default); throw new Exception("Truncation accepted"); }
    catch (EndOfStreamException) { checks++; }
}

using var host = new PipeHost(Guid.NewGuid().ToString("N"));
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Task run = host.RunAsync(stop.Token);
async Task<NamedPipeClientStream> Connect()
{
    var client = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
        System.Security.Principal.TokenImpersonationLevel.Impersonation);
    try { await client.ConnectAsync(3000, stop.Token); return client; }
    catch { client.Dispose(); throw; }
}
try
{
    try { using var duplicate = new PipeHost(host.PipeName.Split('.').Last()); throw new Exception("Occupied pipe accepted"); }
    catch (Win32Exception) { checks++; }
    string? boot = null;
    for (int session = 0; session < 3; session++)
    {
        await using var client = await Connect();
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)valid.Length);
        foreach (byte b in header) await client.WriteAsync(new byte[] { b }, stop.Token);
        foreach (byte b in valid) await client.WriteAsync(new byte[] { b }, stop.Token);
        using var response = JsonDocument.Parse((await FrameCodec.ReadAsync(client, stop.Token))!);
        string current = response.RootElement.GetProperty("result").GetProperty("bootId").GetString()!;
        Check(boot is null || boot == current, "boot stable across clients"); boot = current;
    }
    await using (var bad = await Connect())
    {
        await bad.WriteAsync(new byte[] { 0, 0, 1, 0 }, stop.Token); // 65536 with no body, triggers 2s deadline.
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(5000);
        try { Check(await bad.ReadAsync(one, timeout.Token) == 0, "slow frame disconnected"); }
        catch (IOException) { checks++; }
    }
    await using (var client = await Connect())
    {
        for (int i = 0; i < 32; i++)
        {
            await FrameCodec.WriteAsync(client, valid, stop.Token);
            Check(await FrameCodec.ReadAsync(client, stop.Token) is not null, "bounded request processed");
        }
        try { Check(await client.ReadAsync(new byte[1], stop.Token) == 0, "request cap disconnected"); }
        catch (IOException) { checks++; }
    }
    await using (var client = await Connect())
    {
        await FrameCodec.WriteAsync(client, valid, stop.Token);
        Check(await FrameCodec.ReadAsync(client, stop.Token) is not null, "recovery after timeout/cap");
    }
}
finally
{
    stop.Cancel();
    try { await run; } catch (OperationCanceledException) { }
}
await PeerTests.RunAsync(Check);
await RegistrationTests.RunAsync(Check);
await ReviewTests.RunAsync(Check);
await CapacityTests.RunAsync(Check);
await ReviewConsoleTests.RunAsync(Check);
await PendingGrantTests.RunAsync(Check);
await SecureStreamTests.RunAsync(Check);
await ConnectionPlannerTests.RunAsync(Check);
AuthorityEpochTests.Run(Check);
LocalAccessTests.Run(Check);
ConnectionJournalTests.Run(Check);
await PeerCapacityTests.Run(Check);
await SdkTests.RunAsync(Check);
await FormalApiTests.Run(Check);
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, actualNativePipe = true, independentProcessPeerTested = true,
    sameUserSidVerified = true, otherUserTested = false, remoteMachineTested = false,
    cloudAccessed = false, installedRuntimeChanged = false }));
