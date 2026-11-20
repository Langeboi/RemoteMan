using System.Runtime.CompilerServices;

namespace RemoteMan.Core.Ai;

/// <summary>Minimal server-sent events reader: yields the payload of each event's data lines.</summary>
public static class Sse
{
    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);
        var data = new System.Text.StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("data:"))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }
}
