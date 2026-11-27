using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Services;

/// <summary>
/// This PC's sign-in to a RemoteMan account (hosted workspaces). Optional: without it RemoteMan works exactly as before.
/// Signed in, every workspace the account is a member of is a data source, added, renamed and removed by the server's
/// list, which is fetched at start and every minute. The token and the database password are DPAPI-protected in
/// account.json; settings.json only holds each workspace's connection string without the password.
/// </summary>
public sealed class HostedAccount
{
    public static HostedAccount Instance { get; } = new();
    private HostedAccount() { }

    private static string FilePath => Path.Combine(AppPaths.Root, "account.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private sealed class Saved
    {
        public string Server { get; set; } = "";
        public string Account { get; set; } = "";
        public string? FullName { get; set; }
        public bool Admin { get; set; }
        public bool MustChangePassword { get; set; }
        public string Token { get; set; } = "";
        public string? DbPassword { get; set; }
        public string? CaPem { get; set; }
        public DateTimeOffset? LastSync { get; set; }
    }

    private Saved? _saved;
    private string? _token;
    private string? _dbPassword;

    public bool IsSignedIn => _saved is not null && _token is not null;
    public string? Server => _saved?.Server;
    public string? Account => _saved?.Account;
    public string? FullName => _saved?.FullName;
    public bool IsAdmin => _saved?.Admin == true;
    public bool MustChangePassword => _saved?.MustChangePassword == true;
    public DateTimeOffset? LastSync => _saved?.LastSync;
    /// <summary>Why the last check failed (offline, server down). Null after a good check.</summary>
    public string? Problem { get; private set; }

    /// <summary>Sign-in state or the list changed. Raised on whichever thread made the change.</summary>
    public event Action? Changed;

    /// <summary>At start: reads account.json and gives the hosted data sources their password, so they open offline too.</summary>
    public void Load(AppSettings settings)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath), Json);
            if (s is null || s.Token.Length == 0) return;
            var dp = new DpapiSecretProtector();
            _token = dp.Unprotect(s.Token);
            _dbPassword = s.DbPassword is { Length: > 0 } p ? dp.Unprotect(p) : null;
            _saved = s;
        }
        catch (Exception) { _saved = null; _token = null; _dbPassword = null; }   // another Windows user's file: signed out
        Apply(settings);
    }

    private void Save()
    {
        if (_saved is null) { try { File.Delete(FilePath); } catch (IOException) { } return; }
        var dp = new DpapiSecretProtector();
        _saved.Token = dp.Protect(_token!);
        _saved.DbPassword = _dbPassword is null ? null : dp.Protect(_dbPassword);
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_saved, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Gives every hosted data source of this account its runtime password and CA file.</summary>
    private void Apply(AppSettings settings)
    {
        string? ca = null;
        if (_saved?.CaPem is { Length: > 0 } pem)
        {
            ca = Path.Combine(AppPaths.Root, "hosted-ca.pem");
            try { if (!File.Exists(ca) || File.ReadAllText(ca) != pem) File.WriteAllText(ca, pem); } catch (IOException) { }
        }
        foreach (var d in settings.DataSources.Where(d => d.Hosted is not null))
        {
            var mine = _saved is not null && SameServer(d.Hosted!.Server, _saved.Server) && d.Hosted.Account.Equals(_saved.Account, StringComparison.OrdinalIgnoreCase);
            d.RuntimePassword = mine ? _dbPassword : null;
            d.RuntimeRootCertificate = mine ? ca : null;
        }
    }

    private static bool SameServer(string a, string b) => a.TrimEnd('/').Equals(b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>"remoteman.example.com" or a full URL → the server's base URL.</summary>
    public static Uri ServerUri(string text)
    {
        text = text.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        var u = new Uri(text);
        return new Uri(u.GetLeftPart(UriPartial.Authority) + "/");
    }

    public sealed record SignInResult(bool Ok, bool NeedCode, bool MustChangePassword, string? Error);

    public async Task<SignInResult> SignInAsync(AppSettings settings, string server, string user, string password, string? code)
    {
        Uri baseUri;
        try { baseUri = ServerUri(server); }
        catch (UriFormatException) { return new(false, false, false, "That isn't a server address. Type it like remoteman.example.com."); }
        HttpResponseMessage r;
        try
        {
            r = await Http.PostAsJsonAsync(new Uri(baseUri, "v1/account/signin"),
                new { user = user.Trim(), password, code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(), device = Environment.MachineName }, Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { return new(false, false, false, $"Can't reach {baseUri.Host}: {ex.GetBaseException().Message}"); }
        using (r)
        {
            var body = await ReadAsync(r);
            if (r.StatusCode == HttpStatusCode.NotFound)
                return new(false, false, false, $"{baseUri.Host} doesn't have RemoteMan accounts turned on (or isn't a RemoteMan server).");
            if (!r.IsSuccessStatusCode)
                return new(false, body.TryGetProperty("needCode", out var nc) && nc.ValueKind == JsonValueKind.True, false,
                    Str(body, "error") ?? $"The server said {(int)r.StatusCode}.");
            // A different account than before: the old one's workspaces go first.
            if (_saved is not null && (!SameServer(_saved.Server, baseUri.ToString()) || !_saved.Account.Equals(Str(body, "account"), StringComparison.OrdinalIgnoreCase)))
                Forget(settings);
            _token = Str(body, "token");
            _saved = new Saved
            {
                Server = baseUri.ToString().TrimEnd('/'), Account = Str(body, "account") ?? user.Trim(), FullName = Str(body, "fullName"),
                MustChangePassword = body.TryGetProperty("mustChangePassword", out var m) && m.ValueKind == JsonValueKind.True,
            };
            Save();
        }
        await SyncAsync(settings);
        return new(true, false, MustChangePassword, null);
    }

    public async Task<string?> ChangePasswordAsync(AppSettings settings, string current, string next)
    {
        if (!IsSignedIn) return "Not signed in.";
        try
        {
            using var req = Request(HttpMethod.Post, "v1/account/password");
            req.Content = JsonContent.Create(new { current, next }, options: Json);
            using var r = await Http.SendAsync(req);
            if (!r.IsSuccessStatusCode) return Str(await ReadAsync(r), "error") ?? $"The server said {(int)r.StatusCode}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return $"Can't reach the server: {ex.GetBaseException().Message}"; }
        await SyncAsync(settings);
        return null;
    }

    /// <summary>Signs this PC out: the server forgets its token, and the workspaces and their local copies go.</summary>
    public async Task SignOutAsync(AppSettings settings)
    {
        if (IsSignedIn)
            try { using var req = Request(HttpMethod.Post, "v1/account/signout"); using var _ = await Http.SendAsync(req); } catch (Exception) { }
        Forget(settings);
    }

    /// <summary>What a check changed, for the status bar and for switching away from a workspace that went.</summary>
    public sealed record SyncResult(List<string> Added, List<string> Removed, List<(string From, string To)> Renamed, bool SignedOut, bool Changed)
    {
        public static readonly SyncResult None = new([], [], [], false, false);
    }

    private readonly SemaphoreSlim _sync = new(1, 1);

    /// <summary>
    /// Asks the server for the account's workspaces and makes the data source list match: new ones added, renamed ones
    /// renamed, ones you're no longer in removed (with their vault passphrase and offline copy). Offline = nothing changes.
    /// Call on the UI thread: it changes <paramref name="settings"/>.
    /// </summary>
    public async Task<SyncResult> SyncAsync(AppSettings settings)
    {
        if (!IsSignedIn) return SyncResult.None;
        await _sync.WaitAsync();
        try
        {
            JsonElement me;
            try
            {
                using var req = Request(HttpMethod.Get, "v1/account/me");
                using var r = await Http.SendAsync(req);
                if (r.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Signed out on the website, password reset, account removed or turned off.
                    var gone = Forget(settings);
                    Problem = "Signed out of RemoteMan. Sign in again under Data sources.";
                    Changed?.Invoke();
                    return new([], gone, [], true, true);
                }
                if (!r.IsSuccessStatusCode) { Problem = $"The RemoteMan server said {(int)r.StatusCode}."; Changed?.Invoke(); return SyncResult.None; }
                me = await ReadAsync(r);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Problem = $"Can't reach {new Uri(_saved!.Server).Host}: working from this PC's copies.";
                Changed?.Invoke();
                return SyncResult.None;
            }
            Problem = null;
            return Merge(settings, me);
        }
        finally { _sync.Release(); }
    }

    private SyncResult Merge(AppSettings settings, JsonElement me)
    {
        var s = _saved!;
        s.FullName = Str(me, "fullName");
        s.Admin = me.TryGetProperty("admin", out var ad) && ad.ValueKind == JsonValueKind.True;
        s.MustChangePassword = me.TryGetProperty("mustChangePassword", out var mc) && mc.ValueKind == JsonValueKind.True;
        s.CaPem = Str(me, "caPem");
        s.LastSync = DateTimeOffset.UtcNow;

        var added = new List<string>(); var removed = new List<string>(); var renamed = new List<(string, string)>();
        var changed = false;
        var seen = new HashSet<string>();
        if (me.TryGetProperty("workspaces", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var w in list.EnumerateArray())
            {
                var id = Str(w, "id"); var cs = Str(w, "connectionString");
                if (id is null || cs is null) continue;
                seen.Add(id);
                var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
                if (!string.IsNullOrEmpty(b.Password)) _dbPassword = b.Password;
                b.Password = null;
                var stored = b.ConnectionString;
                var name = Str(w, "name") ?? id;
                var existing = Mine(settings).FirstOrDefault(d => d.Hosted!.WorkspaceId == id);
                if (existing is null)
                {
                    existing = new DataSourceConfig
                    {
                        Name = UniqueName(settings, name, null), Kind = DataSourceKind.PostgreSql, ConnectionStrings = [stored],
                        UseSharedVaultKey = true, Hosted = new HostedLink { Server = s.Server, Account = s.Account, WorkspaceId = id },
                    };
                    settings.DataSources.Add(existing);
                    added.Add(existing.Name);
                    changed = true;
                }
                else
                {
                    var want = existing.Name == name ? name : UniqueName(settings, name, existing);
                    if (existing.Name != want)
                    {
                        if (settings.ActiveDataSource == existing.Name) settings.ActiveDataSource = want;
                        renamed.Add((existing.Name, want));
                        existing.Name = want;
                        changed = true;
                    }
                    if (existing.ConnectionStrings.Count != 1 || existing.ConnectionStrings[0] != stored) { existing.ConnectionStrings = [stored]; changed = true; }
                }
                var access = Str(w, "access") ?? "edit";
                if (existing.Hosted!.Access != access) { existing.Hosted.Access = access; changed = true; }
                existing.Hosted.Description = Str(w, "description");
            }
        // While the account must choose a password the server lists no workspaces: keep them until it has.
        if (!s.MustChangePassword)
            foreach (var d in Mine(settings).Where(d => !seen.Contains(d.Hosted!.WorkspaceId)).ToList())
            {
                RemoveSource(settings, d);
                removed.Add(d.Name);
                changed = true;
            }
        Save();
        Apply(settings);
        if (changed) settings.Save();
        Changed?.Invoke();
        return new(added, removed, renamed, false, changed);
    }

    private IEnumerable<DataSourceConfig> Mine(AppSettings settings) =>
        settings.DataSources.Where(d => d.Hosted is { } h && _saved is not null && SameServer(h.Server, _saved.Server)
            && h.Account.Equals(_saved.Account, StringComparison.OrdinalIgnoreCase));

    /// <summary>The workspace's name, or "name (RemoteMan)" when a data source you set up yourself already has it.</summary>
    private static string UniqueName(AppSettings settings, string name, DataSourceConfig? self)
    {
        bool Taken(string n) => settings.DataSources.Any(d => d != self && d.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        var alt = $"{name} (RemoteMan)";
        for (var i = 2; Taken(alt); i++) alt = $"{name} (RemoteMan {i})";
        return alt;
    }

    private static void RemoveSource(AppSettings settings, DataSourceConfig d)
    {
        settings.DataSources.Remove(d);
        Secrets.DeleteVaultPassphrase(d);
        new OfflineCache(Secrets.LocalKey(d)).Delete();
        if (settings.ActiveDataSource == d.Name || settings.DataSources.Count == 0)
        {
            if (settings.DataSources.Count == 0)
                settings.DataSources.Add(new DataSourceConfig { Name = "Local", Kind = DataSourceKind.Sqlite, ConnectionStrings = { AppPaths.LocalDb } });
            settings.ActiveDataSource = (settings.DataSources.FirstOrDefault(x => x.Hosted is null) ?? settings.DataSources[0]).Name;
        }
    }

    /// <summary>Forgets the account on this PC and removes its workspaces. Returns their names.</summary>
    private List<string> Forget(AppSettings settings)
    {
        var gone = new List<string>();
        if (_saved is not null)
            foreach (var d in Mine(settings).ToList()) { RemoveSource(settings, d); gone.Add(d.Name); }
        _saved = null; _token = null; _dbPassword = null;
        Save();
        Apply(settings);
        settings.Save();
        Changed?.Invoke();
        return gone;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, new Uri(new Uri(_saved!.Server.TrimEnd('/') + "/"), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return req;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        try { return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).Clone(); }
        catch (Exception) { return default; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
