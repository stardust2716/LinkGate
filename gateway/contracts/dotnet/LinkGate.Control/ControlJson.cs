using System.Text;
using System.Text.Json;
namespace LinkGate.Control;
public static class ControlJson
{
    public static JsonDocument ReadDocument(byte[] input)
    {
        if (input.Length == 0 || input.Length > FrameCodec.MaxBytes) throw Invalid();
        try
        {
            string text = new UTF8Encoding(false, true).GetString(input);
            ValidateEscapes(text);
            var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            try { int values = 0; ValidateTree(document.RootElement, ref values); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { throw Invalid(); }
    }

    private static void ValidateTree(JsonElement e, ref int values)
    {
        if (++values > 10000) throw Invalid();
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw Invalid();
                ValidateTree(p.Value, ref values);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray()) ValidateTree(item, ref values);
    }

    private static void ValidateEscapes(string text)
    {
        // UTF-8 decoding already rejects literal surrogates. Check escaped pairs before JsonDocument can replace them.
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') continue;
            if (++i >= text.Length) throw Invalid();
            if (text[i] != 'u') continue;
            int code = Hex(text, i + 1); i += 4;
            if (code is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 6 >= text.Length || text[i + 1] != '\\' || text[i + 2] != 'u') throw Invalid();
                int low = Hex(text, i + 3);
                if (low is < 0xDC00 or > 0xDFFF) throw Invalid();
                i += 6;
            }
            else if (code is >= 0xDC00 and <= 0xDFFF) throw Invalid();
        }
    }

    private static int Hex(string text, int start)
    {
        if (start + 4 > text.Length || !int.TryParse(text.AsSpan(start, 4), System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out int value)) throw Invalid();
        return value;
    }
    private static InvalidDataException Invalid() => new("Invalid control request.");
}
