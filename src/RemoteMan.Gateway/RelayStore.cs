using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteMan.Gateway;

public sealed class SiteRecord
{
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    /// <summary>Public keys (SPKI, base64) of the connectors that served this site. RemoteMan accepts only these.</summary>
    public List<string> Keys { get; set; } = [];
    public DateTimeOffset Created { get; set; }
}

public sealed class DeviceRecord
{
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    /// <summary>Sites this device may use; "*" = all.</summary>
    public List<string> Sites { get; set; } = ["*"];
    public DateTimeOffset Created { get; set; }

    public bool MayUse(string site) => Sites.Contains("*") || Sites.Contains(site, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The relay's sites and devices, in one JSON file in its volume. Tokens are stored only as SHA-256 hashes
/// (they are 256 random bits, so a fast hash is enough). The admin commands edit the file while the relay runs;
/// the relay notices the change on the next request.
/// </summary>
public sealed class RelayStore
{
    private sealed class Data
    {
        public List<SiteRecord> Sites { get; set; } = [];
        public List<DeviceRecord> Devices { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private Data _data = new();
    private DateTime _loadedStamp = DateTime.MinValue;

    public RelayStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Reload();
    }

    public IReadOnlyList<SiteRecord> Sites { get { Reload(); lock (_lock) return _data.Sites.ToList(); } }
    public IReadOnlyList<DeviceRecord> Devices { get { Reload(); lock (_lock) return _data.Devices.ToList(); } }

    public static string NewToken() => GatewayInvite.Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public SiteRecord? SiteByToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        Reload();
        var h = Hash(token);
        lock (_lock) return _data.Sites.FirstOrDefault(s => FixedEquals(s.TokenHash, h));
    }

    public DeviceRecord? DeviceByToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        Reload();
        var h = Hash(token);
        lock (_lock) return _data.Devices.FirstOrDefault(d => FixedEquals(d.TokenHash, h));
    }

    public SiteRecord? Site(string name) { Reload(); lock (_lock) return _data.Sites.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }

    /// <summary>New site; returns its token (shown once).</summary>
    public string AddSite(string name)
    {
        if (!GatewayProtocol.IsValidName(name)) throw new ArgumentException("A site name is 1–40 letters, digits, '-', '_' or '.'.");
        var token = NewToken();
        Update(d =>
        {
            if (d.Sites.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already a site called {name}.");
            d.Sites.Add(new SiteRecord { Name = name, TokenHash = Hash(token), Created = DateTimeOffset.UtcNow });
        });
        return token;
    }

    /// <summary>A new token for a site (the old one stops working; connectors need the new one).</summary>
    public string NewSiteToken(string name)
    {
        var token = NewToken();
        Update(d => (Find(d.Sites, name) ?? throw new ArgumentException($"No site called {name}.")).TokenHash = Hash(token));
        return token;
    }

    public bool RemoveSite(string name)
    {
        var removed = false;
        Update(d => removed = d.Sites.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0);
        return removed;
    }

    public void ResetKeys(string name) => Update(d => (Find(d.Sites, name) ?? throw new ArgumentException($"No site called {name}.")).Keys.Clear());

    /// <summary>Records a connector's key for its site. True when it was new.</summary>
    public bool AddKey(string site, string key)
    {
        lock (_lock) { if (Find(_data.Sites, site) is { } s && s.Keys.Contains(key)) return false; }
        var added = false;
        Update(d =>
        {
            if (Find(d.Sites, site) is { } s && !s.Keys.Contains(key)) { s.Keys.Add(key); added = true; }
        });
        return added;
    }

    /// <summary>New device; returns its token (shown once, inside the invite).</summary>
    public string AddDevice(string name, IEnumerable<string>? sites)
    {
        if (!GatewayProtocol.IsValidName(name)) throw new ArgumentException("A device name is 1–40 letters, digits, '-', '_' or '.'.");
        var list = sites?.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } l ? l : ["*"];
        var token = NewToken();
        Update(d =>
        {
            if (d.Devices.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already a device called {name}. Remove it first, or pick another name.");
            foreach (var s in list.Where(s => s != "*"))
                if (Find(d.Sites, s) is null) throw new ArgumentException($"No site called {s}. Add it first with: gateway site add {s}");
            d.Devices.Add(new DeviceRecord { Name = name, TokenHash = Hash(token), Sites = list, Created = DateTimeOffset.UtcNow });
        });
        return token;
    }

    public bool RemoveDevice(string name)
    {
        var removed = false;
        Update(d => removed = d.Devices.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0);
        return removed;
    }

    private static SiteRecord? Find(List<SiteRecord> sites, string name) => sites.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private void Reload()
    {
        var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        lock (_lock)
        {
            if (stamp == _loadedStamp) return;
            _data = stamp == DateTime.MinValue ? new Data() : JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), Json) ?? new Data();
            _loadedStamp = stamp;
        }
    }

    private void Update(Action<Data> change)
    {
        lock (_lock)
        {
            // Always start from the file, so an admin command and the running relay don't overwrite each other.
            var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            var data = stamp == DateTime.MinValue ? new Data() : JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), Json) ?? new Data();
            change(data);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _path, overwrite: true);
            _data = data;
            _loadedStamp = File.GetLastWriteTimeUtc(_path);
        }
    }
}
