using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public string? DisplayName { get; set; }

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
