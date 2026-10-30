using System.Text.Json;

namespace LinkGate;

internal static class RuntimeIntegrity
{
    // The lock is embedded in this build, never loaded from a mutable runtime directory.
    public static readonly string WindowsHash = Load();
    private static string Load()
    {
        using var source = typeof(RuntimeIntegrity).Assembly.GetManifestResourceStream("LinkGate.RuntimeLock")!;
        using var doc = JsonDocument.Parse(source);
        var hash = doc.RootElement.GetProperty("windowsSha256").GetString()!;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new GatewayException("runtime_lock");
        return hash;
    }
}
