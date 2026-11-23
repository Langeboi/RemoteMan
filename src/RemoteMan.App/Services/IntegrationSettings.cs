using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Services;

/// <summary>Where the Zabbix or Oxidized settings in use came from.</summary>
public enum IntegrationOrigin
{
    /// <summary>Not set up anywhere.</summary>
    None,
    /// <summary>The data source's shared settings (everyone on it uses them).</summary>
    DataSource,
    /// <summary>This PC's own settings from before they moved into the data source, used until shared ones are saved.</summary>
    ThisPc,
}

/// <summary>
/// The Zabbix and Oxidized connections of the open data source. They live in the data source (remoteman_settings),
/// so everyone on a shared database gets them; the token and password inside are encrypted with the data source's
/// vault key like every other secret there. A local copy keeps them working while the database is unreachable.
/// Settings saved on this PC by earlier versions move up the first time the data source has none.
/// </summary>
public sealed class IntegrationSettings
{
    public const string ZabbixKey = "integration.zabbix";
    public const string OxidizedKey = "integration.oxidized";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly AppSettings _app;
    private readonly DataSourceConfig _cfg;
    private readonly EntryStore _store;
    private readonly ISecretProtector _protector;
    private readonly SettingsCache _cache;

    public IntegrationSettings(AppSettings app, DataSourceConfig cfg, EntryStore store, ISecretProtector protector)
    {
        _app = app;
        _cfg = cfg;
        _store = store;
        _protector = protector;
        _cache = new SettingsCache(cfg.Name);
    }

    public ZabbixConfig Zabbix { get; private set; } = new();
    /// <summary>The API token in plain text, or null when there is none (or it can't be decrypted here).</summary>
    public string? ZabbixToken { get; private set; }
    public IntegrationOrigin ZabbixOrigin { get; private set; }
    public SharedSetting? ZabbixRow { get; private set; }

    public OxidizedConfig Oxidized { get; private set; } = new();
    public string? OxidizedPassword { get; private set; }
    public IntegrationOrigin OxidizedOrigin { get; private set; }
    public SharedSetting? OxidizedRow { get; private set; }

    /// <summary>Set when a saved secret is there but this PC can't decrypt it (a different vault passphrase).</summary>
    public string? ZabbixProblem { get; private set; }
    public string? OxidizedProblem { get; private set; }

    public string SourceName => _cfg.Name;
    public bool IsLocalSource => _cfg.Kind == DataSourceKind.Sqlite;

    /// <summary>Whether saving goes through right now: online, the data source has the table, and this login may change it.</summary>
    public bool CanChange => _store.IsOnline && _store.Source.SupportsSettings && _store.Source.CanChangeSettings;

    /// <summary>Why the settings can't be saved now, in words for the settings windows; null when they can.</summary>
    public string? WhyNotChangeable =>
        CanChange ? null
        : !_store.IsOnline ? "The database is offline. These can be changed when it's back."
        : !_store.Source.SupportsSettings ? "This data source can't hold shared settings yet: someone with edit rights has to open it with this version first."
        : $"Only database admins can change these. {_store.User} has {(IsReadOnlyLogin ? "read-only" : "edit")} access; an admin can change that in Data sources > Users.";

    private bool IsReadOnlyLogin => _store.Source switch
    {
        RemoteMan.Data.SqlDataSource s => s.IsReadOnly,
        RemoteMan.Data.RedundantDataSource r => r.Active is RemoteMan.Data.SqlDataSource a && a.IsReadOnly,
        _ => false,
    };

    /// <summary>One line for the settings windows about where these settings are kept.</summary>
    public string Note(IntegrationOrigin origin, SharedSetting? row)
    {
        if (IsLocalSource) return $"Saved in the local data source \"{SourceName}\".";
        var who = row is { UpdatedBy: { Length: > 0 } by } ? $" Last changed by {by}, {row.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}." : "";
        return origin switch
        {
            IntegrationOrigin.ThisPc => CanChange
                ? $"These are this PC's own settings. Saving puts them in \"{SourceName}\" for everyone on it."
                : $"These are this PC's own settings, used until an admin saves shared ones in \"{SourceName}\".",
            _ => $"Shared: everyone on \"{SourceName}\" uses these settings.{who}",
        };
    }

    /// <summary>
    /// Reads the settings from the data source (or the local copy when offline). When the data source has none yet
    /// and this PC has settings from an earlier version, they are moved up: at once for a local file, after
    /// <paramref name="askToShare"/> says yes for a shared database (no: keep them on this PC; null or no question: ask another time).
    /// Returns a status line when something moved.
    /// </summary>
    public async Task<string?> LoadAsync(Func<string, bool?>? askToShare, CancellationToken ct = default)
    {
        IReadOnlyList<SharedSetting>? rows = null;
        var live = false;
        if (_store.IsOnline && _store.Source.SupportsSettings)
        {
            try
            {
                rows = await _store.Source.GetSettingsAsync(ct);
                live = true;
                _cache.Save(rows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        rows ??= _cache.Load() ?? [];

        var z = rows.FirstOrDefault(r => r.Name == ZabbixKey);
        var o = rows.FirstOrDefault(r => r.Name == OxidizedKey);
        string? moved = null;

        // Settings from before they moved into the data source, or from the setup window.
        var legacyZ = _app.Zabbix.Url.Length > 0 ? _app.Zabbix : null;
        var legacyO = _app.Oxidized.Url.Length > 0 ? _app.Oxidized : null;
        var pushZ = legacyZ is not null && (z is null || _app.IntegrationsPending.Contains(SourceName));
        var pushO = legacyO is not null && (o is null || _app.IntegrationsPending.Contains(SourceName));
        var pending = _app.IntegrationsPending.Contains(SourceName);
        if (live && (pushZ || pushO) && CanChange && (pending || !_app.IntegrationsKeptLocal.Contains(SourceName)))
        {
            var what = pushZ && pushO ? "Zabbix and Oxidized settings" : pushZ ? "Zabbix settings" : "Oxidized settings";
            bool? share = IsLocalSource || pending ? true : askToShare?.Invoke(what);
            if (share == true)
            {
                try
                {
                    if (pushZ) z = await PutAsync(ZabbixKey, ToShared(legacyZ!, ZabbixConnector.LoadToken(legacyZ!)), ct);
                    if (pushO) o = await PutAsync(OxidizedKey, ToShared(legacyO!, OxidizedConnector.LoadPassword(legacyO!)), ct);
                    moved = IsLocalSource ? null : $"Your {what} are now saved in {SourceName}, so everyone on it gets them";
                    _cache.Save(await _store.Source.GetSettingsAsync(ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { moved = $"Couldn't share your {what}: {ex.Message}"; }
            }
            else if (share == false)
            {
                _app.IntegrationsKeptLocal.Add(SourceName);
                TrySave();
            }
        }
        if (live && _app.IntegrationsPending.Remove(SourceName) && !CanChange)
            moved = $"Only database admins can change the Zabbix and Oxidized settings in {SourceName}, so your setup choices weren't saved there";
        if (live) TrySave();

        // Zabbix
        ZabbixRow = z;
        ZabbixProblem = null;
        if (z is not null && Parse<ZabbixConfig>(z.Value) is { } zc)
        {
            Zabbix = zc;
            ZabbixToken = Decrypt(zc.ProtectedToken, out var bad);
            if (bad) ZabbixProblem = "The saved Zabbix token can't be decrypted with this data source's vault passphrase.";
            ZabbixOrigin = IntegrationOrigin.DataSource;
        }
        else if (legacyZ is not null)
        {
            Zabbix = legacyZ;
            ZabbixToken = ZabbixConnector.LoadToken(legacyZ);
            ZabbixOrigin = IntegrationOrigin.ThisPc;
        }
        else { Zabbix = new(); ZabbixToken = null; ZabbixOrigin = IntegrationOrigin.None; }

        // Oxidized
        OxidizedRow = o;
        OxidizedProblem = null;
        if (o is not null && Parse<OxidizedConfig>(o.Value) is { } oc)
        {
            Oxidized = oc;
            OxidizedPassword = Decrypt(oc.ProtectedPassword, out var bad);
            if (bad) OxidizedProblem = "The saved Oxidized password can't be decrypted with this data source's vault passphrase.";
            OxidizedOrigin = IntegrationOrigin.DataSource;
        }
        else if (legacyO is not null)
        {
            Oxidized = legacyO;
            OxidizedPassword = OxidizedConnector.LoadPassword(legacyO);
            OxidizedOrigin = IntegrationOrigin.ThisPc;
        }
        else { Oxidized = new(); OxidizedPassword = null; OxidizedOrigin = IntegrationOrigin.None; }

        return moved;
    }

    /// <summary>Saves Zabbix settings for everyone. <paramref name="newToken"/> null keeps the saved token.</summary>
    public async Task SaveZabbixAsync(ZabbixConfig cfg, string? newToken, CancellationToken ct = default)
    {
        var token = newToken is { Length: > 0 } t ? t.Trim() : ZabbixToken;
        ZabbixRow = await PutAsync(ZabbixKey, ToShared(cfg, token), ct);
        Zabbix = Parse<ZabbixConfig>(ZabbixRow.Value)!;
        ZabbixToken = token;
        ZabbixOrigin = IntegrationOrigin.DataSource;
        ZabbixProblem = null;
        await RefreshCacheAsync(ct);
    }

    /// <summary>Saves Oxidized settings for everyone. <paramref name="newPassword"/> null keeps the saved password.</summary>
    public async Task SaveOxidizedAsync(OxidizedConfig cfg, string? newPassword, CancellationToken ct = default)
    {
        var password = cfg.UserName is null ? null : newPassword is { Length: > 0 } p ? p : OxidizedPassword;
        OxidizedRow = await PutAsync(OxidizedKey, ToShared(cfg, password), ct);
        Oxidized = Parse<OxidizedConfig>(OxidizedRow.Value)!;
        OxidizedPassword = password;
        OxidizedOrigin = IntegrationOrigin.DataSource;
        OxidizedProblem = null;
        await RefreshCacheAsync(ct);
    }

    /// <summary>Changes whenever the Zabbix settings in use change (to restart polling only when needed).</summary>
    public string ZabbixStamp => JsonSerializer.Serialize(Zabbix, Json) + "\n" + ZabbixToken;
    public string OxidizedStamp => JsonSerializer.Serialize(Oxidized, Json) + "\n" + OxidizedPassword;

    private async Task<SharedSetting> PutAsync(string key, string value, CancellationToken ct)
    {
        await _store.Source.PutSettingAsync(key, value, _store.User, ct);
        return new SharedSetting(key, value, DateTimeOffset.UtcNow, _store.Source.DatabaseUser ?? _store.User);
    }

    private async Task RefreshCacheAsync(CancellationToken ct)
    {
        try { _cache.Save(await _store.Source.GetSettingsAsync(ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    /// <summary>The JSON stored in the data source: the settings with the secret encrypted by the vault key.</summary>
    private string ToShared(ZabbixConfig cfg, string? token)
    {
        var copy = Clone(cfg);
        copy.ProtectedToken = string.IsNullOrEmpty(token) ? null : _protector.Protect(token.Trim());
        return JsonSerializer.Serialize(copy, Json);
    }

    private string ToShared(OxidizedConfig cfg, string? password)
    {
        var copy = Clone(cfg);
        copy.ProtectedPassword = string.IsNullOrEmpty(password) || copy.UserName is null ? null : _protector.Protect(password);
        return JsonSerializer.Serialize(copy, Json);
    }

    private static T Clone<T>(T cfg) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(cfg, Json), Json)!;

    private static T? Parse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }

    private string? Decrypt(string? protectedText, out bool failed)
    {
        failed = false;
        if (string.IsNullOrEmpty(protectedText)) return null;
        try { return _protector.Unprotect(protectedText); }
        catch (Exception) { failed = true; return null; }
    }

    private void TrySave()
    {
        try { _app.Save(); } catch (IOException) { }
    }

    /// <summary>The data source's settings rows as last read, so the integrations work offline. Encrypted for this Windows user.</summary>
    private sealed class SettingsCache(string dataSourceName)
    {
        private static readonly byte[] Entropy = "RemoteMan settings cache v1"u8.ToArray();

        private readonly string _path = Path.Combine(AppPaths.Root, "cache",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataSourceName)))[..16] + ".settings.bin");

        public IReadOnlyList<SharedSetting>? Load()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<List<SharedSetting>>(json);
            }
            catch (Exception) { return null; }
        }

        public void Save(IReadOnlyList<SharedSetting> rows)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllBytes(_path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(rows), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception) { }
        }
    }
}
