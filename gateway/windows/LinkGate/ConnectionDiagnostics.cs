using System.Diagnostics;
using System.Text.Json;

namespace LinkGate;

/// <summary>Cumulative per-stream counters, scoped to one owned runtime generation.</summary>
public sealed class ConnectionDiagnostics
{
    private sealed record Entry(string Path, string Role, string Phase, long Tx, long Rx, long Elapsed);
    private readonly Dictionary<long, Entry> streams = new();
    private readonly Queue<(double Time, long Bytes)> samples = new();
    private long directTx, directRx, relayTx, relayRx, setupMs;
    private string reason = "none";
    private bool natHole, incomplete;
    private int physicalInterfaceIndex;
    private readonly HashSet<string> boundSocketKinds = new();
    private string natPreparationFailure = "none";
    private string stunStatus = "not-started";
    private int stunObservations, stunAttemptedEndpoints;
    private bool stunFallbackUsed;
    public bool ObserveStun(string line)
    {
        const string prefix="LINKGATE_STUN ";
        try
        {
            if(!line.StartsWith(prefix,StringComparison.Ordinal)||line.Length>512)return false;
            using var document=JsonDocument.Parse(line[prefix.Length..]);var r=document.RootElement;
            var fields=r.EnumerateObject().Select(x=>x.Name).ToArray();
            if(fields.Length!=5||fields.Distinct().Count()!=5||fields.Except(new[]{"v","status","observations","attemptedEndpoints","fallbackUsed"}).Any())return false;
            var status=r.GetProperty("status").GetString();var observations=r.GetProperty("observations").GetInt32();var attempts=r.GetProperty("attemptedEndpoints").GetInt32();
            var fallback=r.GetProperty("fallbackUsed").GetBoolean();
            if(r.GetProperty("v").GetInt32()!=1||observations is <0 or >2||attempts<observations||attempts>8||
                (status=="ready"?observations!=2:status=="insufficient"?observations!=1:status=="unreachable"?observations!=0:true))return false;
            stunStatus=status!;stunObservations=observations;stunAttemptedEndpoints=attempts;stunFallbackUsed=fallback;
            natPreparationFailure=status=="ready"?"none":status=="insufficient"?"stun_insufficient_observations":"stun_unreachable";
            return true;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return false;}
    }
    public void ConfigureEgress(int index) => physicalInterfaceIndex = index;
    public void NatPreparationFailed() => natPreparationFailure = "stun_insufficient_observations";
    public bool ObserveEgress(string line)
    {
        try
        {
            if (physicalInterfaceIndex == 0 || line.Length > 512 || !line.StartsWith("LINKGATE_EGRESS ", StringComparison.Ordinal)) return false;
            using var doc = JsonDocument.Parse(line[16..]); var r = doc.RootElement;
            var fields=r.EnumerateObject().Select(x=>x.Name).ToArray();
            if(fields.Length!=4||fields.Distinct().Count()!=4||fields.Except(new[]{"v","mode","kind","interfaceIndex"}).Any())return false;
            var kind=r.GetProperty("kind").GetString();
            if(r.GetProperty("v").GetInt32()!=1||r.GetProperty("mode").GetString()!="physical-ipv4"||r.GetProperty("interfaceIndex").GetInt32()!=physicalInterfaceIndex||kind is not ("tcp" or "stun" or "hole" or "random-hole" or "kcp"))return false;
            boundSocketKinds.Add(kind);return true;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return false;}
    }
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public void NatHoleObserved() { natHole = true; natPreparationFailure = "none"; }
    public void EndActive()
    {
        if (streams.Values.Any(x => x.Phase == "open")) incomplete = true;
        foreach (var id in streams.Keys.ToArray()) streams[id] = streams[id] with { Phase = "closed" };
    }
    public bool Observe(string line)
    {
        const string prefix = "LINKGATE_METRIC ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length > 2048) return false;
        try
        {
            using var doc = JsonDocument.Parse(line[prefix.Length..]);
            var j = doc.RootElement;
            var fields = j.EnumerateObject().Select(x => x.Name).ToArray();
            var expected = new[] { "v", "id", "role", "path", "phase", "tx", "rx", "setupMs", "elapsedMs", "reason" };
            if (fields.Length != expected.Length || fields.Distinct().Count() != expected.Length || fields.Except(expected).Any() || j.GetProperty("v").GetInt32() != 1) return false;
            var id = j.GetProperty("id").GetInt64();
            var tx = j.GetProperty("tx").GetInt64(); var rx = j.GetProperty("rx").GetInt64();
            var setup = j.GetProperty("setupMs").GetInt64(); var elapsed = j.GetProperty("elapsedMs").GetInt64();
            var path = j.GetProperty("path").GetString()!; var role = j.GetProperty("role").GetString()!;
            var phase = j.GetProperty("phase").GetString()!; var why = j.GetProperty("reason").GetString()!;
            if (id < 1 || tx < 0 || rx < 0 || tx > 1L << 50 || rx > 1L << 50 || setup < 0 || elapsed < setup || elapsed > 31536000000L || role is not ("visitor" or "publisher") ||
                phase is not ("open" or "closed" or "failed") || path is not ("direct" or "relay" or "unknown") ||
                (phase == "failed") != (path == "unknown") || (phase == "failed" && (tx != 0 || rx != 0)) ||
                why is not ("" or "direct_tunnel_unavailable" or "connection_setup_failed" or "direct_unavailable_relay_failed")) return false;
            streams.TryGetValue(id, out var previous);
            if (previous is not null && (previous.Phase != "open" || previous.Path != path || previous.Role != role || previous.Tx > tx || previous.Rx > rx || previous.Elapsed > elapsed)) return false;
            if (previous is null && streams.Count >= 8192) { incomplete = true; return false; }
            var dt = tx - (previous?.Tx ?? 0); var dr = rx - (previous?.Rx ?? 0);
            if (path == "direct") { directTx += dt; directRx += dr; }
            if (path == "relay") { relayTx += dt; relayRx += dr; }
            setupMs = setup;
            if (why.Length > 0) reason = why;
            streams[id] = new(path, role, phase, tx, rx, elapsed);
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException) { return false; }
    }
    public object Snapshot()
    {
        var now = clock.Elapsed.TotalSeconds;
        var bytes = directTx + directRx + relayTx + relayRx;
        if (samples.Count == 0 || now - samples.Last().Time >= .2) samples.Enqueue((now, bytes));
        while (samples.Count > 1 && now - samples.Peek().Time > 3) samples.Dequeue();
        var first = samples.Peek();
        var rate = now - first.Time < .2 ? 0 : (bytes - first.Bytes) / (now - first.Time);
        var active = streams.Values.Where(x => x.Phase == "open").ToArray();
        var paths = active.Select(x => x.Path).Distinct().Order().ToArray();
        return new { schemaVersion = 1, currentPath = paths.Length == 0 ? "idle" : incomplete ? "incomplete" : string.Join("+", paths),
            natHoleObserved = natHole, businessDirectObserved = directTx + directRx > 0,
            directTx, directRx, relayTx, relayRx, activeConnections = active.Length, setupMs,
            bytesPerSecond = (long)rate, fallbackReason = reason, complete = !incomplete,
            egressMode = physicalInterfaceIndex > 0 ? "physical-ipv4" : "system",physicalInterfaceIndex,verifiedBoundSocketKinds=boundSocketKinds.Order().ToArray(),natPreparationFailure,
            stunStatus,stunObservations,stunAttemptedEndpoints,stunFallbackUsed };
    }
    public string Summary()
    {
        var json = JsonSerializer.SerializeToElement(Snapshot());
        return $"当前路径：{json.GetProperty("currentPath").GetString()} · 活跃连接 {json.GetProperty("activeConnections")}\n" +
            $"直连 ↑{directTx:N0} ↓{directRx:N0} B；中继 ↑{relayTx:N0} ↓{relayRx:N0} B\n" +
            $"最近吞吐 {json.GetProperty("bytesPerSecond")} B/s · 最近建立 {setupMs} ms\n" +
            $"NAT 打洞：{(natHole ? "观察到" : "未观察到")}；业务直连：{(directTx + directRx > 0 ? "有字节证据" : "无字节证据")}\n" +
            $"回退/失败：{reason} · 统计{(incomplete ? "不完整" : "完整")}\n"+
            $"出站：{(physicalInterfaceIndex > 0 ? "物理网卡 IPv4 #"+physicalInterfaceIndex : "系统路由")}；套接字确认 {string.Join(",",boundSocketKinds)}\n"+
            $"STUN：{stunStatus}，观测 {stunObservations}/2，端点 {stunAttemptedEndpoints}，备用{(stunFallbackUsed?"已尝试":"未使用")}\n"+
            $"NAT 准备：{(natPreparationFailure == "none" ? "未记录失败" : natPreparationFailure=="stun_unreachable"?"未获取有效 STUN 响应":"STUN 映射观测不足，打洞尚未开始")}";
    }
}
