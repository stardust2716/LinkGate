using System.Text;
using System.Text.Json;

namespace LinkGate;

public enum GatewayRole { Publisher, Visitor }

public static class FrpConfig
{
    // JSON basic strings are also valid TOML basic strings. Validation prevents
    // control characters in labels, names and hosts before reaching this layer.
    private static string Q(string text) => JsonSerializer.Serialize(text);

    public static string Render(GatewayProfile profile, GatewayRole role, string caPath)
    {
        var output = new StringBuilder();
        output.AppendLine($"serverAddr = {Q(profile.Server.Host)}");
        output.AppendLine($"serverPort = {profile.Server.Port}");
        output.AppendLine($"natHoleStunServer = {Q(profile.Server.Host + ":" + profile.Server.StunPort)}");
        output.AppendLine("loginFailExit = false");
        output.AppendLine("auth.method = \"token\"");
        output.AppendLine($"auth.token = {Q(profile.Server.Token)}");
        output.AppendLine("transport.tls.enable = true");
        output.AppendLine($"transport.tls.trustedCaFile = {Q(caPath)}");
        output.AppendLine($"transport.tls.serverName = {Q(profile.Server.TlsServerName)}");
        output.AppendLine("log.to = \"console\"");
        output.AppendLine("log.level = \"info\"");
        output.AppendLine("log.disablePrintColor = true");
        foreach (var service in profile.Services)
        {
            var stem = profile.Namespace + "-" + service.Id;
            if (role == GatewayRole.Publisher)
            {
                foreach (var type in new[] { "xtcp", "stcp" })
                {
                    output.AppendLine("\n[[proxies]]");
                    output.AppendLine($"name = {Q(stem + "-" + type)}");
                    output.AppendLine($"type = {Q(type)}");
                    output.AppendLine($"secretKey = {Q(service.SecretKey)}");
                    output.AppendLine("localIP = \"127.0.0.1\"");
                    output.AppendLine($"localPort = {service.LocalPort}");
                    Transport(output);
                }
            }
            else
            {
                output.AppendLine("\n[[visitors]]");
                output.AppendLine($"name = {Q(stem + "-fallback")}");
                output.AppendLine("type = \"stcp\"");
                output.AppendLine($"serverName = {Q(stem + "-stcp")}");
                output.AppendLine($"secretKey = {Q(service.SecretKey)}");
                output.AppendLine("bindPort = -1");
                Transport(output);
                output.AppendLine("\n[[visitors]]");
                output.AppendLine($"name = {Q(stem + "-direct")}");
                output.AppendLine("type = \"xtcp\"");
                output.AppendLine($"serverName = {Q(stem + "-xtcp")}");
                output.AppendLine($"secretKey = {Q(service.SecretKey)}");
                output.AppendLine("bindAddr = \"127.0.0.1\"");
                output.AppendLine($"bindPort = {service.VisitorPort}");
                output.AppendLine("protocol = \"quic\"");
                output.AppendLine($"fallbackTo = {Q(stem + "-fallback")}");
                output.AppendLine("fallbackTimeoutMs = 1500");
                output.AppendLine("keepTunnelOpen = true");
                output.AppendLine("minRetryInterval = 30");
                output.AppendLine("maxRetriesAnHour = 120");
                Transport(output);
            }
        }
        return output.ToString();
    }

    private static void Transport(StringBuilder output)
    {
        output.AppendLine("transport.useEncryption = true");
        output.AppendLine("transport.useCompression = false");
    }
}
