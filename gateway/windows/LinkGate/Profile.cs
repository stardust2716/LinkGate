using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkGate;

public sealed class GatewayException(string code) : Exception(code);
public sealed record GatewayServer(string Host, int Port, string Token, string TlsServerName, string TrustedCaPem, int StunPort = 3478);
public sealed record GatewayService(string Id, string Label, int LocalPort, int VisitorPort, string SecretKey);
public sealed record GatewayProfile(int FormatVersion, string Namespace, string DeviceName, GatewayServer Server, IReadOnlyList<GatewayService> Services)
{
    public const int MaxBytes = 128 * 1024;

    public static GatewayProfile Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxBytes) throw new GatewayException("profile_size");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    public static GatewayProfile Parse(ReadOnlyMemory<byte> data)
    {
        if (data.Length is <= 0 or > MaxBytes) throw new GatewayException("profile_size");
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Fields(root, "formatVersion", "namespace", "deviceName", "server", "services");
            if (Int(root, "formatVersion") != 1) throw new GatewayException("profile_version");
            var ns = Text(root, "namespace", 12);
            if (!Regex.IsMatch(ns, "^[a-z0-9]{12}$")) throw new GatewayException("profile_namespace");
            var name = Text(root, "deviceName", 64);
            var server = root.GetProperty("server");
            Fields(server, ["host", "port", "token", "tlsServerName", "trustedCaPem"], ["stunPort"]);
            var host = Host(Text(server, "host", 253));
            var tlsName = Host(Text(server, "tlsServerName", 253));
            if (IPAddress.TryParse(tlsName, out _)) throw new GatewayException("profile_tls_name");
            var port = Port(server, "port");
            var stunPort = server.TryGetProperty("stunPort", out _) ? Port(server, "stunPort") : 3478;
            var token = Secret(server, "token");
            var ca = Text(server, "trustedCaPem", 16 * 1024, allowNewlines: true);
            if (!ca.Trim().StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) ||
                !ca.Trim().EndsWith("-----END CERTIFICATE-----", StringComparison.Ordinal) ||
                ca.Contains("PRIVATE KEY", StringComparison.Ordinal) ||
                Regex.Matches(ca, "-----BEGIN CERTIFICATE-----").Count != 1 ||
                Regex.Matches(ca, "-----END CERTIFICATE-----").Count != 1)
                throw new GatewayException("profile_ca");
            using (var certificate = X509Certificate2.CreateFromPem(ca))
            {
                var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
                if (certificate.HasPrivateKey || constraints is not { CertificateAuthority: true } ||
                    DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
                    throw new GatewayException("profile_ca");
            }
            var entries = root.GetProperty("services");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 16)
                throw new GatewayException("profile_services");
            var services = new List<GatewayService>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var ports = new HashSet<int>();
            foreach (var entry in entries.EnumerateArray())
            {
                Fields(entry, "id", "label", "localPort", "visitorPort", "secretKey");
                var id = Text(entry, "id", 32);
                if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,31}$") || !ids.Add(id)) throw new GatewayException("profile_service_id");
                var visitorPort = Port(entry, "visitorPort");
                if (!ports.Add(visitorPort)) throw new GatewayException("profile_visitor_port");
                services.Add(new(id, Text(entry, "label", 64), Port(entry, "localPort"), visitorPort, Secret(entry, "secretKey")));
            }
            return new(1, ns, name, new(host, port, token, tlsName, ca, stunPort), services.AsReadOnly());
        }
        catch (GatewayException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or CryptographicException)
        {
            throw new GatewayException("profile_invalid");
        }
    }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        formatVersion = 1, @namespace = Namespace, deviceName = DeviceName,
        server = new { host = Server.Host, port = Server.Port, token = Server.Token, tlsServerName = Server.TlsServerName, trustedCaPem = Server.TrustedCaPem, stunPort = Server.StunPort },
        services = Services.Select(s => new { id = s.Id, label = s.Label, localPort = s.LocalPort, visitorPort = s.VisitorPort, secretKey = s.SecretKey })
    }, new JsonSerializerOptions { WriteIndented = true });

    private static void Fields(JsonElement value, params string[] expected)
        => Fields(value, expected, []);
    private static void Fields(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new GatewayException("profile_object");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if ((!required.Contains(property.Name, StringComparer.Ordinal) && !optional.Contains(property.Name, StringComparer.Ordinal)) || !found.Add(property.Name))
                throw new GatewayException("profile_field");
        if (required.Any(name => !found.Contains(name))) throw new GatewayException("profile_field");
    }
    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();
    private static int Port(JsonElement element, string name)
    {
        var value = Int(element, name);
        if (value is < 1024 or > 65535) throw new GatewayException("profile_port");
        return value;
    }
    private static string Text(JsonElement element, string name, int length, bool allowNewlines = false)
    {
        var value = element.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > length ||
            value.Any(c => char.IsControl(c) && !(allowNewlines && c is '\r' or '\n')))
            throw new GatewayException("profile_text");
        return value;
    }
    private static string Secret(JsonElement element, string name)
    {
        var value = Text(element, name, 64);
        if (!Regex.IsMatch(value, "^[0-9a-fA-F]{64}$")) throw new GatewayException("profile_secret");
        return value;
    }
    private static string Host(string value)
    {
        if (IPAddress.TryParse(value, out var ip))
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || value != ip.ToString() || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224)
                throw new GatewayException("profile_host");
            return value;
        }
        if (Regex.IsMatch(value, "^[0-9.]+$")) throw new GatewayException("profile_host");
        if (value.Length > 253 || value.Split('.').Any(label => !Regex.IsMatch(label, "^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")))
            throw new GatewayException("profile_host");
        return value;
    }
}
