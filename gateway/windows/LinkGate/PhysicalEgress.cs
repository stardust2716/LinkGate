using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkGate;

public sealed record PhysicalAdapter(int Index, string Address, string Mac, string Label);
public sealed record EgressSnapshot(bool TunActive, List<PhysicalAdapter> Candidates);
public sealed record PhysicalEgress(PhysicalAdapter? Adapter)
{
    public string Description => Adapter is null ? "系统路由（未检测到活动 TUN）" : "IPv4 绑定物理网卡：" + Adapter.Label;
    public static PhysicalEgress Select(EgressSnapshot snapshot)
    {
        if (!snapshot.TunActive) return new((PhysicalAdapter?)null);
        if (snapshot.Candidates.Count != 1) throw new GatewayException("tun_bypass_network_ambiguous");
        var a = snapshot.Candidates[0];
        if (a.Index is <= 0 or > 0xffffff || !IPAddress.TryParse(a.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.ToString() != a.Address || IPAddress.IsLoopback(ip) || ip.GetAddressBytes()[0] is 0 or >= 224 || a.Address.StartsWith("169.254.", StringComparison.Ordinal) ||
            a.Address.StartsWith("198.18.", StringComparison.Ordinal) || a.Address.StartsWith("198.19.", StringComparison.Ordinal) ||
            !Regex.IsMatch(a.Mac, "^[0-9a-f]{2}(:[0-9a-f]{2}){5}$") || a.Label.Length is < 1 or > 80 || a.Label.Any(char.IsControl))
            throw new GatewayException("tun_bypass_adapter_invalid");
        return new(a);
    }
    public void Apply(ProcessStartInfo start)
    {
        if (Adapter is null) return;
        start.Environment["LINKGATE_BIND_INDEX"] = Adapter.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["LINKGATE_BIND_IPV4"] = Adapter.Address;
        start.Environment["LINKGATE_BIND_MAC"] = Adapter.Mac;
    }
    public static async Task<PhysicalEgress> DetectAsync()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) }) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var job = new ProcessJob();
        if (!process.Start()) throw new GatewayException("network_probe_start");
        try { job.Assign(process); }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw new GatewayException("network_probe_job"); }
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new GatewayException("network_probe_timeout"); }
        await error;
        if (process.ExitCode != 0) throw new GatewayException("network_probe_failed");
        try { return Select(JsonSerializer.Deserialize<EgressSnapshot>(await output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException()); }
        catch (JsonException) { throw new GatewayException("network_probe_invalid"); }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
        { if (text.Length + n > 16384) throw new GatewayException("network_probe_output_limit"); text.Append(buffer, 0, n); }
        return text.ToString();
    }
    private const string Script = """
        $ErrorActionPreference='Stop'
        $ProgressPreference='SilentlyContinue'
        [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
        $adapters=@(Get-NetAdapter -ErrorAction Stop)
        $tun=@($adapters|Where-Object { $_.Status -eq 'Up' -and !$_.HardwareInterface -and ($_.Name -match '(?i)^Meta$|clash|mihomo|wintun|\btun\b' -or $_.InterfaceDescription -match '(?i)clash|mihomo|wintun|\btun\b') })
        $physical=@()
        if($tun.Count){
          foreach($adapter in @($adapters|Where-Object {$_.HardwareInterface -and $_.Status -eq 'Up'})){
            $gateways=@(Get-NetRoute -AddressFamily IPv4 -InterfaceIndex $adapter.ifIndex -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore -ErrorAction SilentlyContinue|Where-Object NextHop -ne '0.0.0.0'|Select-Object -ExpandProperty NextHop -Unique)
            $ips=@(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $adapter.ifIndex -ErrorAction SilentlyContinue|Where-Object {$_.AddressState -eq 'Preferred' -and !$_.SkipAsSource -and $_.IPAddress -notmatch '^(169\.254\.|127\.|0\.)'})
            if($gateways.Count -eq 1 -and $ips.Count -eq 1){$physical+=@{index=[int]$adapter.ifIndex;address=[string]$ips[0].IPAddress;mac=([string]$adapter.MacAddress).Replace('-',':').ToLowerInvariant();label=[string]$adapter.Name}}
          }
        }
        if($physical.Count -gt 32){throw 'Too many adapters'}
        @{tunActive=($tun.Count -gt 0);candidates=@($physical)}|ConvertTo-Json -Depth 4 -Compress
        """;
}
