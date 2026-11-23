using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Ai;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteMan");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string LocalDb => Path.Combine(Root, "local.db");
    public static string KnownHosts => Path.Combine(Root, "known_hosts.json");
    public static string AiWebData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteMan", "AiWeb");
    public static string WebViewData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteMan", "WebView2");
}

/// <summary>Per-user settings. Data sources are listed here; the entries live in the sources.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<DataSourceConfig> DataSources { get; set; } = new();
    public string? ActiveDataSource { get; set; }
    public int FallbackTimeoutMs { get; set; } = 2000;
    /// <summary>Data sources that already got the built-in snippets, so deleted ones stay deleted.</summary>
    public List<string> SnippetsSeeded { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();

    // Per-user UI state (not shared through the data source).
    public List<Guid> Favorites { get; set; } = new();
    public List<Guid> Recent { get; set; } = new();
    /// <summary>When each recent entry was last opened, for the Dashboard.</summary>
    public Dictionary<Guid, DateTimeOffset> RecentAt { get; set; } = new();
    /// <summary>Dashboard widgets turned off (tiles, protocols, zabbix, favorites, recent, folders, checks).</summary>
    public List<string> DashboardHidden { get; set; } = new();
    /// <summary>Sessions you opened (when and which kind), for the dashboard's connection charts. Last 60 days, this PC only.</summary>
    public List<RemoteMan.Core.Model.ConnectEvent> ConnectLog { get; set; } = new();
    /// <summary>Open Zabbix problems every half hour over the last day, for the dashboard trend.</summary>
    public List<RemoteMan.Core.Model.ProblemSample> ZabbixTrend { get; set; } = new();
    /// <summary>Two panes side by side. Off by default (single pane); a new name so earlier test builds' saved "on" is dropped.</summary>
    public bool SplitPane { get; set; }

    /// <summary>Whether to reopen the tabs and windows that were open when RemoteMan last closed.</summary>
    public ReopenMode ReopenSessions { get; set; } = ReopenMode.Ask;

    /// <summary>Colour theme (see <see cref="Themes"/>). Takes effect on the next start.</summary>
    public string Theme { get; set; } = Themes.DefaultId;
    /// <summary>Light and shadow on the controls (Flat, Subtle, Raised). Takes effect on the next start.</summary>
    public Shading Shading { get; set; } = Shading.Subtle;
    /// <summary>SSH and web tabs draw sRGB colours unmanaged, so they look saturated on wide-gamut screens. Takes effect on the next start.</summary>
    public bool VividColors { get; set; } = true;
    /// <summary>What was open at the last exit (kept up to date while running, so a crash still leaves it).</summary>
    public List<OpenSessionRecord> LastSessions { get; set; } = new();

    /// <summary>Delete chat messages older than this many days (0 keeps them). Any PC with a shorter setting deletes for everyone.</summary>
    public int ChatRetentionDays { get; set; } = 365;
    /// <summary>Shared sessions: relay "host:port" for teammates on other networks. Empty = the database server on port 47120.</summary>
    public string? ShareRelay { get; set; }
    /// <summary>Whether the chat drawer was open at the last exit.</summary>
    public bool ChatOpen { get; set; }

    /// <summary>This PC's Zabbix settings. The data source's shared settings win (see <see cref="IntegrationSettings"/>);
    /// these are what earlier versions saved, moved up the first time a data source has none, and what the setup window fills in.</summary>
    public ZabbixConfig Zabbix { get; set; } = new();
    /// <summary>This PC's Oxidized settings; see <see cref="Zabbix"/>.</summary>
    public OxidizedConfig Oxidized { get; set; } = new();
    /// <summary>Data sources where you chose to keep this PC's integration settings instead of sharing them (so you aren't asked again).</summary>
    public List<string> IntegrationsKeptLocal { get; set; } = new();
    /// <summary>Data sources the setup window changed the integrations for: they are saved there the next time it opens.</summary>
    public List<string> IntegrationsPending { get; set; } = new();

    /// <summary>AI assistant providers (keys are not stored here; see <see cref="Secrets"/>).</summary>
    public AiSettings Ai { get; set; } = new();

    public DataSourceConfig Active =>
        DataSources.FirstOrDefault(d => d.Name == ActiveDataSource) ?? DataSources[0];

    public static AppSettings Load()
    {
        AppSettings s;
        try { s = File.Exists(AppPaths.Settings) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), Json) ?? new() : new(); }
        catch (JsonException) { s = new(); }
        if (s.DataSources.Count == 0)
            s.DataSources.Add(new DataSourceConfig { Name = "Local", Kind = DataSourceKind.Sqlite, ConnectionStrings = { AppPaths.LocalDb } });
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Json));
    }
}

public enum ReopenMode { Ask, Always, Never }

/// <summary>One tab or session window that was open: the entry, which pane, and whether it was the shown tab.</summary>
public sealed class OpenSessionRecord
{
    public Guid EntryId { get; set; }
    public int Pane { get; set; }
    public bool Active { get; set; }
}

/// <summary>Zabbix connection, stored in the data source for everyone on it. The links between entries and Zabbix hosts live on the entries.</summary>
public sealed class ZabbixConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    /// <summary>API token: encrypted with the data source's vault key when stored there, DPAPI-protected in this PC's own settings.</summary>
    public string? ProtectedToken { get; set; }
    /// <summary>SSH entry to tunnel the API calls through; null = connect directly.</summary>
    public Guid? JumpHostEntryId { get; set; }
    public bool IgnoreCertificateErrors { get; set; } = true;
    public int PollSeconds { get; set; } = 30;
    public ZabbixSeverity MinimumSeverity { get; set; } = ZabbixSeverity.Warning;
    public bool MatchByAddress { get; set; } = true;
    public bool MatchByName { get; set; } = true;
    public bool IgnoreBracketSuffix { get; set; } = true;

    public bool IsConfigured => Enabled && Url.Length > 0 && !string.IsNullOrEmpty(ProtectedToken);

    public ZabbixMatchOptions MatchOptions => new() { ByAddress = MatchByAddress, ByName = MatchByName, IgnoreBracketSuffix = IgnoreBracketSuffix };
}

/// <summary>Oxidized connection (config backups of network gear), stored in the data source for everyone on it. Hand-made links live on the entries.</summary>
public sealed class OxidizedConfig
{
    public bool Enabled { get; set; }
    /// <summary>Oxidized's web page, e.g. https://oxidized.lab.local or http://10.0.0.5:8888.</summary>
    public string Url { get; set; } = "";
    /// <summary>Basic auth in front of Oxidized (nginx, Apache). Empty = no login.</summary>
    public string? UserName { get; set; }
    /// <summary>Encrypted with the data source's vault key when stored there, DPAPI-protected in this PC's own settings.</summary>
    public string? ProtectedPassword { get; set; }
    public Guid? JumpHostEntryId { get; set; }
    public bool IgnoreCertificateErrors { get; set; } = true;
    /// <summary>How often the node list (last backup and status) is refreshed.</summary>
    public int PollMinutes { get; set; } = 5;
    public bool MatchByAddress { get; set; } = true;
    public bool MatchByName { get; set; } = true;
    public bool IgnoreBracketSuffix { get; set; } = true;

    // --- Oxidized's device list from the RemoteMan server (read by rmserver/oxidized.py too) ---
    /// <summary>"None" or "TopFolder": the Oxidized group of entries that aren't in Oxidized yet.</summary>
    public string FeedGroup { get; set; } = nameof(OxidizedFeedGroup.None);
    /// <summary>Oxidized model for entries no tag, icon or "Oxidized model" field gives one; empty = Oxidized's own default.</summary>
    public string? FeedDefaultModel { get; set; }
    /// <summary>SSH entries with this tag aren't sent.</summary>
    public string? FeedExcludeTag { get; set; } = OxidizedFeed.DefaultLeaveOutTag;
    /// <summary>Send each entry's login (sealed for the server) so Oxidized logs in with it.</summary>
    public bool FeedSendLogins { get; set; } = true;
    /// <summary>New SSH entries go to Oxidized by themselves; off = only entries ticked in the device list.</summary>
    public bool FeedAutoAdd { get; set; } = true;

    public bool IsConfigured => Enabled && Url.Length > 0;

    public OxidizedFeedOptions FeedOptions => new(
        Enum.TryParse<OxidizedFeedGroup>(FeedGroup, true, out var g) ? g : OxidizedFeedGroup.None,
        FeedDefaultModel, FeedExcludeTag, FeedSendLogins);

    public OxidizedMatchOptions MatchOptions => new() { ByAddress = MatchByAddress, ByName = MatchByName, IgnoreBracketSuffix = IgnoreBracketSuffix };
}

/// <summary>Automatic daily backup of the active data source to an encrypted .rmx file.</summary>
public sealed class BackupSettings
{
    public bool Enabled { get; set; }
    /// <summary>Empty = %APPDATA%\RemoteMan\backups.</summary>
    public string? Folder { get; set; }
    public int Keep { get; set; } = 14;
    public bool IncludeSecrets { get; set; } = true;
    public DateTimeOffset? LastBackup { get; set; }
}
