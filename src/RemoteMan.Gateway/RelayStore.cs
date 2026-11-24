using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteMan.Gateway;

public sealed class SiteRecord
{
    public string Name { get; set; } = "";
    /// <summary>The site's first token (from <c>gateway site add</c>). Connectors added later get their own, in <see cref="ConnectorTokens"/>.</summary>
    public string TokenHash { get; set; } = "";
    /// <summary>Public keys (SPKI, base64) of the connectors that served this site. RemoteMan accepts only these.</summary>
    public List<string> Keys { get; set; } = [];
    public DateTimeOffset Created { get; set; }
    public string? Description { get; set; }
    /// <summary>
    /// What the site's connectors may open, in <see cref="AllowList"/> syntax, set from the admin console. Null: only the
    /// connector's own ALLOW counts. When set, a connector opens only what both this and its own ALLOW allow.
    /// </summary>
    public string? Resources { get; set; }
    /// <summary>One token per connector added from the console, so each can be revoked on its own.</summary>
    public List<ConnectorToken> ConnectorTokens { get; set; } = [];
    /// <summary>Every connector that has served this site, for the console (online state lives in <see cref="Relay"/>).</summary>
    public List<KnownConnector> Connectors { get; set; } = [];
}

public sealed class ConnectorToken
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
}

public sealed class KnownConnector
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string? From { get; set; }
    public string? Version { get; set; }
    /// <summary>The connector's own ALLOW, as it reported it.</summary>
    public string? Allow { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class DeviceRecord
{
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    /// <summary>Sites this device may use; "*" = all.</summary>
    public List<string> Sites { get; set; } = ["*"];
    public DateTimeOffset Created { get; set; }
    /// <summary>The person the device belongs to (free text, groups devices into users in the console).</summary>
    public string? Owner { get; set; }
    /// <summary>Paused from the console: refused like a removed device, but can be turned back on.</summary>
    public bool Disabled { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string? LastIp { get; set; }

    public bool MayUse(string site) => Sites.Contains("*") || Sites.Contains(site, StringComparer.OrdinalIgnoreCase);
}

public sealed class AdminRecord
{
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>Base32 TOTP secret when two-step sign-in is on.</summary>
    public string? TotpSecret { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastLogin { get; set; }
    /// <summary>Bumped when the password or 2FA changes, which signs out this admin's other sessions.</summary>
    public int Stamp { get; set; }
}

/// <summary>A link a person opens (or pastes into RemoteMan) to get a device token without anyone copying it around.</summary>
public sealed class InviteRecord
{
    public string Id { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public string? Owner { get; set; }
    /// <summary>The device name to create; null lets the PC name itself.</summary>
    public string? Device { get; set; }
    public List<string> Sites { get; set; } = ["*"];
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Expires { get; set; }
    public string? CreatedBy { get; set; }
    public int UsesLeft { get; set; } = 1;
    public List<string> UsedBy { get; set; } = [];
}

public sealed class SetupCode
{
    public string Hash { get; set; } = "";
    public DateTimeOffset Expires { get; set; }
}

/// <summary>
/// The relay's sites, devices, console admins and invites, in one JSON file in its volume. Tokens are stored only as
/// SHA-256 hashes (they are 256 random bits, so a fast hash is enough); passwords as PBKDF2. The admin commands edit
/// the file while the relay runs; the relay notices the change on the next request.
/// </summary>
public sealed class RelayStore
{
    private sealed class Data
    {
        public List<SiteRecord> Sites { get; set; } = [];
        public List<DeviceRecord> Devices { get; set; } = [];
        public List<AdminRecord> Admins { get; set; } = [];
        public List<InviteRecord> Invites { get; set; } = [];
        public SetupCode? Setup { get; set; }
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

    public string FilePath => _path;
    public IReadOnlyList<SiteRecord> Sites { get { Reload(); lock (_lock) return _data.Sites.ToList(); } }
    public IReadOnlyList<DeviceRecord> Devices { get { Reload(); lock (_lock) return _data.Devices.ToList(); } }
    public IReadOnlyList<AdminRecord> Admins { get { Reload(); lock (_lock) return _data.Admins.ToList(); } }
    public IReadOnlyList<InviteRecord> Invites { get { Reload(); lock (_lock) return _data.Invites.Where(i => i.Expires > DateTimeOffset.UtcNow && i.UsesLeft > 0).ToList(); } }

    public static string NewToken() => GatewayInvite.Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // ---- sites and connectors ----

    public SiteRecord? SiteByToken(string? token) => SiteAndTokenByToken(token)?.Site;

    /// <summary>The site a connector token belongs to, and which of its tokens it is (null: the site's first token).</summary>
    public (SiteRecord Site, ConnectorToken? Token)? SiteAndTokenByToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        Reload();
        var h = Hash(token);
        lock (_lock)
        {
            foreach (var s in _data.Sites)
            {
                if (s.TokenHash.Length > 0 && FixedEquals(s.TokenHash, h)) return (s, null);
                if (s.ConnectorTokens.FirstOrDefault(t => FixedEquals(t.Hash, h)) is { } t) return (s, t);
            }
        }
        return null;
    }

    public SiteRecord? Site(string name) { Reload(); lock (_lock) return Find(_data.Sites, name); }

    /// <summary>New site; returns its token (shown once).</summary>
    public string AddSite(string name, string? description = null)
    {
        if (!GatewayProtocol.IsValidName(name)) throw new ArgumentException("A site name is 1–40 letters, digits, '-', '_' or '.'.");
        var token = NewToken();
        Update(d =>
        {
            if (Find(d.Sites, name) is not null) throw new ArgumentException($"There is already a site called {name}.");
            d.Sites.Add(new SiteRecord { Name = name, TokenHash = Hash(token), Created = DateTimeOffset.UtcNow, Description = Clean(description, 200) });
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

    /// <summary>A token for one more connector on a site, next to the ones it has. Returns the token (shown once) and its id.</summary>
    public (string Token, string Id) AddConnectorToken(string site, string connectorName)
    {
        if (!GatewayProtocol.IsValidName(connectorName)) throw new ArgumentException("A connector name is 1–40 letters, digits, '-', '_' or '.'.");
        var token = NewToken();
        var id = NewId();
        Update(d =>
        {
            var s = Find(d.Sites, site) ?? throw new ArgumentException($"No site called {site}.");
            s.ConnectorTokens.Add(new ConnectorToken { Id = id, Name = connectorName, Hash = Hash(token), Created = DateTimeOffset.UtcNow });
        });
        return (token, id);
    }

    /// <summary>Revokes one connector token; "site" revokes the site's first token.</summary>
    public bool RevokeConnectorToken(string site, string id)
    {
        var removed = false;
        Update(d =>
        {
            var s = Find(d.Sites, site) ?? throw new ArgumentException($"No site called {site}.");
            if (id == "site") { removed = s.TokenHash.Length > 0; s.TokenHash = ""; }
            else removed = s.ConnectorTokens.RemoveAll(t => t.Id == id) > 0;
        });
        return removed;
    }

    public void UpdateSite(string name, string? description, string? resources)
    {
        if (resources is not null && resources.Trim().Length > 0) _ = new AllowList(resources);   // throws FormatException on a bad rule
        Update(d =>
        {
            var s = Find(d.Sites, name) ?? throw new ArgumentException($"No site called {name}.");
            s.Description = Clean(description, 200);
            s.Resources = string.IsNullOrWhiteSpace(resources) ? null : AllowList.Normalize(resources);
        });
    }

    public bool RemoveSite(string name)
    {
        var removed = false;
        Update(d =>
        {
            removed = d.Sites.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            foreach (var dev in d.Devices) dev.Sites.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        });
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

    /// <summary>Notes a connector that just connected (or just left: <paramref name="leaving"/>), for the console.</summary>
    public void RecordConnector(string site, string name, string key, string? from, string? version, string? allow, string? tokenId, bool leaving = false)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            Update(d =>
            {
                if (Find(d.Sites, site) is not { } s) return;
                var c = s.Connectors.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Key == key);
                if (c is null)
                {
                    c = new KnownConnector { Name = name, Key = key, FirstSeen = now };
                    s.Connectors.Add(c);
                    // Keep the list short: the newest 20 per site.
                    if (s.Connectors.Count > 20) s.Connectors.Remove(s.Connectors.OrderBy(x => x.LastSeen).First());
                }
                c.LastSeen = now;
                if (!leaving) { c.From = from; c.Version = version; c.Allow = allow; }
                if (tokenId is not null && s.ConnectorTokens.FirstOrDefault(t => t.Id == tokenId) is { } t) t.LastUsed = now;
            });
        }
        catch (IOException) { }   // bookkeeping only; never fail a connector over it
    }

    public bool ForgetConnector(string site, string name, string key)
    {
        var removed = false;
        Update(d =>
        {
            if (Find(d.Sites, site) is { } s)
                removed = s.Connectors.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && c.Key == key) > 0;
        });
        return removed;
    }

    // ---- devices ----

    public DeviceRecord? DeviceByToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        Reload();
        var h = Hash(token);
        lock (_lock) return _data.Devices.FirstOrDefault(d => !d.Disabled && FixedEquals(d.TokenHash, h));
    }

    public DeviceRecord? Device(string name) { Reload(); lock (_lock) return _data.Devices.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }

    /// <summary>New device; returns its token (shown once, inside the invite).</summary>
    public string AddDevice(string name, IEnumerable<string>? sites, string? owner = null)
    {
        if (!GatewayProtocol.IsValidName(name)) throw new ArgumentException("A device name is 1–40 letters, digits, '-', '_' or '.'.");
        var token = NewToken();
        Update(d =>
        {
            if (d.Devices.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already a device called {name}. Remove it first, or pick another name.");
            d.Devices.Add(new DeviceRecord { Name = name, TokenHash = Hash(token), Sites = CheckSites(d, sites), Created = DateTimeOffset.UtcNow, Owner = Clean(owner, 80) });
        });
        return token;
    }

    public void UpdateDevice(string name, IEnumerable<string>? sites, string? owner, bool disabled)
    {
        Update(d =>
        {
            var dev = d.Devices.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"No device called {name}.");
            dev.Sites = CheckSites(d, sites);
            dev.Owner = Clean(owner, 80);
            dev.Disabled = disabled;
        });
    }

    /// <summary>Notes when and from where a device last talked to the relay; written at most every few minutes.</summary>
    public void TouchDevice(string name, string? ip)
    {
        Reload();
        lock (_lock)
        {
            if (_data.Devices.FirstOrDefault(x => x.Name == name) is { } cur && cur.LastIp == ip && cur.LastSeen is { } seen && DateTimeOffset.UtcNow - seen < TimeSpan.FromMinutes(5)) return;
        }
        try
        {
            Update(d =>
            {
                if (d.Devices.FirstOrDefault(x => x.Name == name) is { } dev) { dev.LastSeen = DateTimeOffset.UtcNow; dev.LastIp = ip; }
            });
        }
        catch (IOException) { }
    }

    public bool RemoveDevice(string name)
    {
        var removed = false;
        Update(d => removed = d.Devices.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0);
        return removed;
    }

    private static List<string> CheckSites(Data d, IEnumerable<string>? sites)
    {
        var list = sites?.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } l ? l : ["*"];
        if (list.Contains("*")) return ["*"];
        foreach (var s in list)
            if (Find(d.Sites, s) is null) throw new ArgumentException($"No site called {s}. Add it first with: gateway site add {s}");
        return list.Select(s => Find(d.Sites, s)!.Name).ToList();
    }

    // ---- invites (enrollment links) ----

    /// <summary>A new invite link code (shown once). Redeeming it creates a device.</summary>
    public (string Code, InviteRecord Invite) AddInvite(string? owner, string? device, IEnumerable<string>? sites, TimeSpan lifetime, int uses, string? createdBy)
    {
        if (device is not null && !GatewayProtocol.IsValidName(device)) throw new ArgumentException("A device name is 1–40 letters, digits, '-', '_' or '.'.");
        var code = NewToken();
        InviteRecord? rec = null;
        Update(d =>
        {
            if (device is not null && d.Devices.Any(x => x.Name.Equals(device, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already a device called {device}.");
            d.Invites.RemoveAll(i => i.Expires < DateTimeOffset.UtcNow || i.UsesLeft <= 0);
            rec = new InviteRecord
            {
                Id = NewId(), CodeHash = Hash(code), Owner = Clean(owner, 80), Device = device, Sites = CheckSites(d, sites),
                Created = DateTimeOffset.UtcNow, Expires = DateTimeOffset.UtcNow + lifetime, CreatedBy = createdBy,
                UsesLeft = device is null ? Math.Clamp(uses, 1, 50) : 1,
            };
            d.Invites.Add(rec);
        });
        return (code, rec!);
    }

    public InviteRecord? InviteByCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        Reload();
        var h = Hash(code);
        lock (_lock) return _data.Invites.FirstOrDefault(i => FixedEquals(i.CodeHash, h) && i.Expires > DateTimeOffset.UtcNow && i.UsesLeft > 0);
    }

    /// <summary>
    /// Uses an invite: creates the device (named by the invite, or <paramref name="requestedName"/>, made unique) and
    /// returns its name and token. Null when the code isn't valid any more.
    /// </summary>
    public (string Device, string Token, InviteRecord Invite)? RedeemInvite(string code, string? requestedName)
    {
        var h = Hash(code);
        var token = NewToken();
        (string, string, InviteRecord)? result = null;
        Update(d =>
        {
            var inv = d.Invites.FirstOrDefault(i => FixedEquals(i.CodeHash, h) && i.Expires > DateTimeOffset.UtcNow && i.UsesLeft > 0);
            if (inv is null) return;
            var baseName = inv.Device ?? SafeName(requestedName) ?? "device";
            var name = baseName;
            for (var n = 2; d.Devices.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{baseName[..Math.Min(baseName.Length, 36)]}-{n}";
            var sites = inv.Sites.Where(s => s == "*" || Find(d.Sites, s) is not null).ToList();
            d.Devices.Add(new DeviceRecord { Name = name, TokenHash = Hash(token), Sites = sites.Count > 0 ? sites : ["*"], Created = DateTimeOffset.UtcNow, Owner = inv.Owner });
            inv.UsesLeft--;
            inv.UsedBy.Add(name);
            result = (name, token, inv);
        });
        return result;
    }

    public bool RevokeInvite(string id)
    {
        var removed = false;
        Update(d => removed = d.Invites.RemoveAll(i => i.Id == id) > 0);
        return removed;
    }

    /// <summary>"Lange's PC" → "Lange-s-PC"; null when nothing usable is left.</summary>
    public static string? SafeName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder();
        foreach (var ch in text.Trim())
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        if (s.Length > 40) s = s[..40];
        return GatewayProtocol.IsValidName(s) ? s : null;
    }

    // ---- console admins ----

    public AdminRecord? Admin(string name) { Reload(); lock (_lock) return _data.Admins.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }

    public void AddAdmin(string name, string password)
    {
        if (!GatewayProtocol.IsValidName(name)) throw new ArgumentException("A user name is 1–40 letters, digits, '-', '_' or '.'.");
        CheckPassword(password);
        var hash = Passwords.Hash(password);
        Update(d =>
        {
            if (d.Admins.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already an admin called {name}.");
            d.Admins.Add(new AdminRecord { Name = name, PasswordHash = hash, Created = DateTimeOffset.UtcNow });
            d.Setup = null;
        });
    }

    public void SetPassword(string name, string password, bool clearTotp = false)
    {
        CheckPassword(password);
        var hash = Passwords.Hash(password);
        Update(d =>
        {
            var a = d.Admins.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"No admin called {name}.");
            a.PasswordHash = hash;
            if (clearTotp) a.TotpSecret = null;
            a.Stamp++;
        });
    }

    public void SetTotp(string name, string? secret) => Update(d =>
    {
        var a = d.Admins.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"No admin called {name}.");
        a.TotpSecret = secret;
        a.Stamp++;
    });

    public void NoteLogin(string name) { try { Update(d => { if (d.Admins.FirstOrDefault(x => x.Name == name) is { } a) a.LastLogin = DateTimeOffset.UtcNow; }); } catch (IOException) { } }

    public bool RemoveAdmin(string name)
    {
        var removed = false;
        Update(d =>
        {
            if (d.Admins.Count == 1 && d.Admins[0].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("That's the last admin. Add another one first.");
            removed = d.Admins.RemoveAll(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        });
        return removed;
    }

    public static void CheckPassword(string password)
    {
        if (password.Length < 10) throw new ArgumentException("Use at least 10 characters for the password.");
        if (password.Length > 200) throw new ArgumentException("That password is too long.");
    }

    /// <summary>A one-time code that lets whoever has it create the first admin (or a new one, from the CLI). Valid for an hour.</summary>
    public string NewSetupCode(TimeSpan? lifetime = null)
    {
        var code = NewToken()[..24];
        Update(d => d.Setup = new SetupCode { Hash = Hash(code), Expires = DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(1)) });
        return code;
    }

    public bool SetupCodeValid(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        Reload();
        lock (_lock) return _data.Setup is { } s && s.Expires > DateTimeOffset.UtcNow && FixedEquals(s.Hash, Hash(code));
    }

    /// <summary>Creates an admin with a setup code, which is then used up.</summary>
    public void AddAdminWithSetupCode(string code, string name, string password)
    {
        if (!SetupCodeValid(code)) throw new UnauthorizedAccessException("That setup link has expired or was used. Make a new one on the relay: gateway admin setup");
        AddAdmin(name, password);   // clears the code
    }

    // ---- plumbing ----

    private static SiteRecord? Find(List<SiteRecord> sites, string name) => sites.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Clean(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : new string(text.Trim().Where(c => !char.IsControl(c)).Take(max).ToArray());

    private static bool FixedEquals(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

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

/// <summary>PBKDF2-SHA256 password hashes: "pbkdf2$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;".</summary>
public static class Passwords
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2" || !int.TryParse(p[1], out var it)) return false;
        try
        {
            var salt = Convert.FromBase64String(p[2]);
            var want = Convert.FromBase64String(p[3]);
            var got = Rfc2898DeriveBytes.Pbkdf2(password, salt, it, HashAlgorithmName.SHA256, want.Length);
            return CryptographicOperations.FixedTimeEquals(got, want);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>A hash to check against when the user doesn't exist, so a wrong name takes as long as a wrong password.</summary>
    public static readonly string Dummy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)));
}
