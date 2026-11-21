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
    /// <summary>What was open at the last exit (kept up to date while running, so a crash still leaves it).</summary>
    public List<OpenSessionRecord> LastSessions { get; set; } = new();

    public ZabbixConfig Zabbix { get; set; } = new();
    public OxidizedConfig Oxidized { get; set; } = new();

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

/// <summary>Per-user Zabbix connection. The links between entries and Zabbix hosts live on the entries, so they are shared.</summary>
public sealed class ZabbixConfig
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    /// <summary>API token, DPAPI-protected for this Windows user.</summary>
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

/// <summary>Per-user Oxidized connection (config backups of network gear). Hand-made links live on the entries.</summary>
public sealed class OxidizedConfig
{
    public bool Enabled { get; set; }
    /// <summary>Oxidized's web page, e.g. https://oxidized.lab.local or http://10.0.0.5:8888.</summary>
    public string Url { get; set; } = "";
    /// <summary>Basic auth in front of Oxidized (nginx, Apache). Empty = no login.</summary>
    public string? UserName { get; set; }
    /// <summary>DPAPI-protected for this Windows user.</summary>
    public string? ProtectedPassword { get; set; }
    public Guid? JumpHostEntryId { get; set; }
    public bool IgnoreCertificateErrors { get; set; } = true;
    /// <summary>How often the node list (last backup and status) is refreshed.</summary>
    public int PollMinutes { get; set; } = 5;
    public bool MatchByAddress { get; set; } = true;
    public bool MatchByName { get; set; } = true;
    public bool IgnoreBracketSuffix { get; set; } = true;

    public bool IsConfigured => Enabled && Url.Length > 0;

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
