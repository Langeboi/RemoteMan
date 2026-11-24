using System.Text.Json;

namespace RemoteMan.Gateway.Server.Admin;

/// <summary>
/// The console's activity list: the newest events in memory, and every event appended to activity.jsonl in the data
/// volume so the list survives a restart. The file is cut back to the newest events now and then.
/// </summary>
public sealed class ActivityLog
{
    private const int Keep = 2000;
    private readonly string? _path;
    private readonly LinkedList<GatewayEvent> _events = new();
    private readonly object _lock = new();
    private int _appended;

    public ActivityLog(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path).TakeLast(Keep))
                if (TryParse(line) is { } e) _events.AddLast(e);
        }
        catch (IOException) { }
    }

    public void Add(GatewayEvent e)
    {
        lock (_lock)
        {
            _events.AddLast(e);
            while (_events.Count > Keep) _events.RemoveFirst();
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(e, GatewayProtocol.Json) + "\n");
                if (++_appended % 500 == 0)
                {
                    var tmp = _path + ".tmp";
                    File.WriteAllLines(tmp, _events.Select(x => JsonSerializer.Serialize(x, GatewayProtocol.Json)));
                    File.Move(tmp, _path, overwrite: true);
                }
            }
            catch (IOException) { }
        }
    }

    public List<GatewayEvent> Latest(int n, string? site = null, string? subject = null)
    {
        lock (_lock)
            return _events.Reverse()
                .Where(e => site is null || string.Equals(e.Site, site, StringComparison.OrdinalIgnoreCase))
                .Where(e => subject is null || string.Equals(e.Subject, subject, StringComparison.OrdinalIgnoreCase))
                .Take(n).ToList();
    }

    private static GatewayEvent? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<GatewayEvent>(line, GatewayProtocol.Json); }
        catch (JsonException) { return null; }
    }
}
