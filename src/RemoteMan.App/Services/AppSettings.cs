using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>Two panes side by side. Off by default (single pane); a new name so earlier test builds' saved "on" is dropped.</summary>
    public bool SplitPane { get; set; }

    public ZabbixConfig Zabbix { get; set; } = new();

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
