using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteMan");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string LocalDb => Path.Combine(Root, "local.db");
    public static string KnownHosts => Path.Combine(Root, "known_hosts.json");
    public static string WebViewData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteMan", "WebView2");
}

/// <summary>Per-user settings. Data sources are listed here; the entries live in the sources.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<DataSourceConfig> DataSources { get; set; } = new();
    public string? ActiveDataSource { get; set; }
    public int FallbackTimeoutMs { get; set; } = 2000;

    // Per-user UI state (not shared through the data source).
    public List<Guid> Favorites { get; set; } = new();
    public List<Guid> Recent { get; set; } = new();
    public bool SplitView { get; set; } = true;

    public ZabbixConfig Zabbix { get; set; } = new();

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
    /// <summary>Show the green check on healthy entries (the pulse button above the tree).</summary>
    public bool ShowOk { get; set; } = true;

    public bool IsConfigured => Enabled && Url.Length > 0 && !string.IsNullOrEmpty(ProtectedToken);

    public ZabbixMatchOptions MatchOptions => new() { ByAddress = MatchByAddress, ByName = MatchByName, IgnoreBracketSuffix = IgnoreBracketSuffix };
}
