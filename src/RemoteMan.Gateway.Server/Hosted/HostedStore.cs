using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteMan.Gateway.Server.Hosted;

/// <summary>Someone who can sign in to RemoteMan (the app) and the website. Console admins get one too.</summary>
public sealed class HostedAccount
{
    public string Name { get; set; } = "";
    public string? FullName { get; set; }
    /// <summary>PBKDF2 hash; empty for console admins, whose password lives with the relay's admins.</summary>
    public string PasswordHash { get; set; } = "";
    public string? TotpSecret { get; set; }
    /// <summary>Made by an admin with a first password: they pick their own at the first sign-in.</summary>
    public bool MustChangePassword { get; set; }
    public bool Disabled { get; set; }
    /// <summary>A console admin (the relay's admin list): signs in with that password, manages everything.</summary>
    public bool ConsoleAdmin { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastLogin { get; set; }
    /// <summary>Bumped when the password or 2FA changes: signs out website sessions made before.</summary>
    public int Stamp { get; set; }
    /// <summary>Their login on the hosted PostgreSQL server, and its password. The app gets both after signing in.</summary>
    public string DbLogin { get; set; } = "";
    public string DbPassword { get; set; } = "";
}

public enum WorkspaceAccess { Read, Edit, Admin }

public sealed class WorkspaceMember
{
    public string Account { get; set; } = "";
    public WorkspaceAccess Access { get; set; } = WorkspaceAccess.Edit;
}

public sealed class HostedWorkspace
{
    /// <summary>8 hex digits; the database is rmw_&lt;id&gt;.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset Created { get; set; }
    public string? CreatedBy { get; set; }
    public List<WorkspaceMember> Members { get; set; } = [];
    public string Database => "rmw_" + Id;
}

/// <summary>An app signed in to an account. Only the token's hash is kept.</summary>
public sealed class AppToken
{
    public string Id { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Account { get; set; } = "";
    public string? Device { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string? LastIp { get; set; }
}

public sealed class BackupRecord
{
    public string Workspace { get; set; } = "";
    public string File { get; set; } = "";
    public DateTimeOffset Taken { get; set; }
    public long Size { get; set; }
    /// <summary>nightly, manual, before-delete, before-restore.</summary>
    public string Kind { get; set; } = "nightly";
    public Dictionary<string, long> Rows { get; set; } = [];
}

public sealed class RestoreTest
{
    public DateTimeOffset At { get; set; }
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>
/// Accounts, workspaces, signed-in apps and the backup list, in hosted.json next to relay.json. Same rules as the relay's
/// store: every change starts from the file and replaces it atomically, readable by the relay's user only.
/// </summary>
public sealed partial class HostedStore
{
    private sealed class Data
    {
        public List<HostedAccount> Accounts { get; set; } = [];
        public List<HostedWorkspace> Workspaces { get; set; } = [];
        public List<AppToken> Tokens { get; set; } = [];
        public List<BackupRecord> Backups { get; set; } = [];
        public List<RestoreTest> RestoreTests { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>An app that hasn't been seen for this long has to sign in again.</summary>
    public static readonly TimeSpan TokenIdle = TimeSpan.FromDays(90);

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data = new();
    private DateTime _loadedStamp = DateTime.MinValue;

    public HostedStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Reload();
    }

    public IReadOnlyList<HostedAccount> Accounts { get { Reload(); lock (_lock) return _data.Accounts.ToList(); } }
    public IReadOnlyList<HostedWorkspace> Workspaces { get { Reload(); lock (_lock) return _data.Workspaces.ToList(); } }
    public IReadOnlyList<AppToken> Tokens { get { Reload(); lock (_lock) return _data.Tokens.Where(t => DateTimeOffset.UtcNow - t.LastSeen < TokenIdle).ToList(); } }
    public IReadOnlyList<BackupRecord> Backups { get { Reload(); lock (_lock) return _data.Backups.ToList(); } }
    public IReadOnlyList<RestoreTest> RestoreTests { get { Reload(); lock (_lock) return _data.RestoreTests.ToList(); } }

    // ---- accounts ----

    /// <summary>Names PostgreSQL or this server use themselves.</summary>
    private static readonly string[] Reserved = ["postgres", "public", "remoteman", "admin", "root", "replication", "template0", "template1", "none", "current_user", "session_user"];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex AccountName();

    /// <summary>Lowercase letters, digits and _, starting with a letter: usable as a database login as is.</summary>
    public static void CheckAccountName(string name)
    {
        if (!AccountName().IsMatch(name)) throw new ArgumentException("A username is 1–40 lowercase letters, digits and _, starting with a letter.");
        if (Reserved.Contains(name) || name.StartsWith("pg_") || name.StartsWith("rmw_")) throw new ArgumentException($"{name} is reserved. Pick another username.");
    }

    /// <summary>A console admin's name turned into a database login ("Lange" → "lange", "a.b" → "a_b").</summary>
    public static string DbLoginFor(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        var s = sb.ToString();
        if (s.Length == 0 || !char.IsAsciiLetter(s[0])) s = "u_" + s;
        if (Reserved.Contains(s) || s.StartsWith("pg_") || s.StartsWith("rmw_")) s = "u_" + s;
        return s.Length > 40 ? s[..40] : s;
    }

    public HostedAccount? Account(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        Reload();
        lock (_lock) return _data.Accounts.FirstOrDefault(a => a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public HostedAccount AddAccount(string name, string? fullName, string password, bool mustChange)
    {
        name = name.Trim().ToLowerInvariant();
        CheckAccountName(name);
        RelayStore.CheckPassword(password);
        var hash = Passwords.Hash(password);
        HostedAccount? made = null;
        Update(d =>
        {
            if (d.Accounts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || a.DbLogin == name))
                throw new ArgumentException($"There is already an account called {name}.");
            d.Accounts.Add(made = new HostedAccount
            {
                Name = name, FullName = Clean(fullName, 80), PasswordHash = hash, MustChangePassword = mustChange,
                Created = DateTimeOffset.UtcNow, DbLogin = name, DbPassword = NewDbPassword(),
            });
        });
        return made!;
    }

    /// <summary>The account that goes with a console admin, made the first time it's needed.</summary>
    public HostedAccount EnsureAdminAccount(string adminName)
    {
        if (Account(adminName) is { ConsoleAdmin: true } a) return a;
        HostedAccount? result = null;
        Update(d =>
        {
            var existing = d.Accounts.FirstOrDefault(x => x.Name.Equals(adminName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // A member account with an admin's name: the admin list wins (they sign in with that password).
                existing.ConsoleAdmin = true;
                existing.PasswordHash = "";
                existing.MustChangePassword = false;
                result = existing;
                return;
            }
            var login = DbLoginFor(adminName);
            for (var i = 2; d.Accounts.Any(x => x.DbLogin == login); i++) login = DbLoginFor(adminName) + i;
            d.Accounts.Add(result = new HostedAccount
            {
                Name = adminName, ConsoleAdmin = true, Created = DateTimeOffset.UtcNow, DbLogin = login, DbPassword = NewDbPassword(),
            });
        });
        return result!;
    }

    public void UpdateAccount(string name, Action<HostedAccount> change) => Update(d =>
    {
        var a = d.Accounts.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"No account called {name}.");
        change(a);
    });

    public void SetPassword(string name, string password, bool mustChange)
    {
        RelayStore.CheckPassword(password);
        var hash = Passwords.Hash(password);
        UpdateAccount(name, a =>
        {
            if (a.ConsoleAdmin) throw new ArgumentException($"{a.Name} is a console admin: change that password under Settings.");
            a.PasswordHash = hash;
            a.MustChangePassword = mustChange;
            a.Stamp++;
        });
    }

    public void NoteLogin(string name) { try { UpdateAccount(name, a => a.LastLogin = DateTimeOffset.UtcNow); } catch (Exception ex) when (ex is IOException or ArgumentException) { } }

    /// <summary>Removes the account, its apps' sign-ins and its place in every workspace.</summary>
    public HostedAccount? RemoveAccount(string name)
    {
        HostedAccount? removed = null;
        Update(d =>
        {
            removed = d.Accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed is null) return;
            d.Accounts.Remove(removed);
            d.Tokens.RemoveAll(t => t.Account.Equals(removed.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var w in d.Workspaces) w.Members.RemoveAll(m => m.Account.Equals(removed.Name, StringComparison.OrdinalIgnoreCase));
        });
        return removed;
    }

    // ---- workspaces ----

    public HostedWorkspace? Workspace(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        Reload();
        lock (_lock) return _data.Workspaces.FirstOrDefault(w => w.Id == id);
    }

    public HostedWorkspace AddWorkspace(string name, string? description, string? createdBy, IEnumerable<WorkspaceMember> members)
    {
        name = Clean(name, 60) ?? throw new ArgumentException("Give the workspace a name.");
        HostedWorkspace? made = null;
        Update(d =>
        {
            if (d.Workspaces.Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already a workspace called {name}.");
            string id;
            do id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(); while (d.Workspaces.Any(w => w.Id == id));
            var list = members.GroupBy(m => m.Account, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
                .Where(m => d.Accounts.Any(a => a.Name.Equals(m.Account, StringComparison.OrdinalIgnoreCase))).ToList();
            d.Workspaces.Add(made = new HostedWorkspace { Id = id, Name = name, Description = Clean(description, 200), Created = DateTimeOffset.UtcNow, CreatedBy = createdBy, Members = list });
        });
        return made!;
    }

    public void UpdateWorkspace(string id, Action<HostedWorkspace> change) => Update(d =>
    {
        var w = d.Workspaces.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("No such workspace.");
        change(w);
    });

    public void RenameWorkspace(string id, string name, string? description) => Update(d =>
    {
        name = Clean(name, 60) ?? throw new ArgumentException("Give the workspace a name.");
        if (d.Workspaces.Any(w => w.Id != id && w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"There is already a workspace called {name}.");
        var w = d.Workspaces.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("No such workspace.");
        w.Name = name;
        w.Description = Clean(description, 200);
    });

    public void RemoveWorkspace(string id) => Update(d => d.Workspaces.RemoveAll(w => w.Id == id));

    public IEnumerable<(HostedWorkspace Workspace, WorkspaceAccess Access)> WorkspacesOf(string account) =>
        Workspaces.SelectMany(w => w.Members.Where(m => m.Account.Equals(account, StringComparison.OrdinalIgnoreCase)).Select(m => (w, m.Access)));

    // ---- apps signed in ----

    public (string Token, AppToken Record) AddToken(string account, string? device, string? ip)
    {
        var token = "rmat_" + RelayStore.NewToken();
        var rec = new AppToken
        {
            Id = RelayStore.NewId(), Hash = RelayStore.Hash(token), Account = account, Device = Clean(device, 80),
            Created = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow, LastIp = ip,
        };
        Update(d =>
        {
            d.Tokens.RemoveAll(t => DateTimeOffset.UtcNow - t.LastSeen >= TokenIdle);
            d.Tokens.Add(rec);
        });
        return (token, rec);
    }

    public AppToken? TokenFor(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith("rmat_")) return null;
        var h = RelayStore.Hash(token);
        Reload();
        lock (_lock)
            return _data.Tokens.FirstOrDefault(t => FixedEquals(t.Hash, h) && DateTimeOffset.UtcNow - t.LastSeen < TokenIdle);
    }

    /// <summary>Notes the app was here; written at most every few minutes, so polling doesn't rewrite the file each time.</summary>
    public void TouchToken(AppToken t, string? ip)
    {
        if (DateTimeOffset.UtcNow - t.LastSeen < TimeSpan.FromMinutes(5) && t.LastIp == ip) return;
        try { Update(d => { if (d.Tokens.FirstOrDefault(x => x.Id == t.Id) is { } x) { x.LastSeen = DateTimeOffset.UtcNow; x.LastIp = ip; } }); }
        catch (IOException) { }
    }

    public bool RemoveToken(string id) { var n = false; Update(d => n = d.Tokens.RemoveAll(t => t.Id == id) > 0); return n; }
    public int RemoveTokensOf(string account) { var n = 0; Update(d => n = d.Tokens.RemoveAll(t => t.Account.Equals(account, StringComparison.OrdinalIgnoreCase))); return n; }

    // ---- backups ----

    public void AddBackup(BackupRecord b) => Update(d => d.Backups.Add(b));
    public void RemoveBackups(Func<BackupRecord, bool> which) => Update(d => d.Backups.RemoveAll(b => which(b)));

    public void AddRestoreTest(RestoreTest t) => Update(d =>
    {
        d.RestoreTests.Add(t);
        if (d.RestoreTests.Count > 30) d.RestoreTests.RemoveRange(0, d.RestoreTests.Count - 30);
    });

    // ---- plumbing ----

    public static string NewDbPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');

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
