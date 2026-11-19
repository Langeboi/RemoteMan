using System.Text.Json;

namespace RemoteMan.Protocols.Ssh;

public enum HostKeyVerdict { New, Match, Changed }

/// <summary>
/// Trust-on-first-use store (%APPDATA%\RemoteMan\known_hosts.json). RemoteMan accepts
/// unknown keys automatically, like RDM; a *changed* key is still accepted when the
/// entry allows it, but the change is reported so the UI can show a warning.
/// </summary>
public sealed class KnownHosts
{
    private readonly string _path;
    private readonly Dictionary<string, string> _keys;
    private readonly object _gate = new();

    public KnownHosts(string path)
    {
        _path = path;
        _keys = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    public HostKeyVerdict Check(string host, int port, string keyType, string sha256)
    {
        var id = $"{host}:{port}";
        var value = $"{keyType} {sha256}";
        lock (_gate)
        {
            HostKeyVerdict verdict;
            if (!_keys.TryGetValue(id, out var known)) verdict = HostKeyVerdict.New;
            else if (known == value) return HostKeyVerdict.Match;
            else verdict = HostKeyVerdict.Changed;
            _keys[id] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_keys, new JsonSerializerOptions { WriteIndented = true }));
            return verdict;
        }
    }
}
