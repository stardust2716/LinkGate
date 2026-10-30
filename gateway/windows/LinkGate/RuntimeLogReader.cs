using System.Text;

namespace LinkGate;

public static class RuntimeLogReader
{
    public const int MaximumLineLength = 4096;

    public static async Task PumpAsync(TextReader reader, Action<string> observe)
    {
        var buffer = new char[2048];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var value = buffer[i];
                if (value == '\n')
                {
                    observe(line.ToString());
                    line.Clear();
                }
                else if (value != '\r' && line.Length < MaximumLineLength) line.Append(value);
            }
        }
        if (line.Length > 0) observe(line.ToString());
    }
}
