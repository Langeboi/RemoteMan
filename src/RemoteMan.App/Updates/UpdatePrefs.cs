using System.IO;
using System.Text.Json;
using RemoteMan.App.Services;

namespace RemoteMan.App.Updates;

/// <summary>Updater preferences, kept in their own file so they never collide with app settings.</summary>
public sealed class UpdatePrefs
{
    private static string FilePath => Path.Combine(AppPaths.Root, "updates.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool AutoCheck { get; set; } = true;
    public bool IncludePrereleases { get; set; }
    public string? SkippedVersion { get; set; }
    public DateTimeOffset? LastCheck { get; set; }
    /// <summary>The version that ran last time, to tell the user when an update has been applied.</summary>
    public string? LastRunVersion { get; set; }

    public static UpdatePrefs Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<UpdatePrefs>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }
}
