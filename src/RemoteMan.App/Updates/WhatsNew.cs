using System.IO;
using System.Text.Json;
using RemoteMan.App.Services;
using RemoteMan.Core.Updates;

namespace RemoteMan.App.Updates;

/// <summary>
/// The "What's new" list: baked into each build by CI (build/changelog.py), checked once at startup.
/// Remembers in whatsnew.json what the last version knew, so the tab opens once after each update.
/// </summary>
public static class WhatsNew
{
    private static string StatePath => Path.Combine(AppPaths.Root, "whatsnew.json");
    private static ChangelogFile? _file;

    /// <summary>The changes this build knows about, newest first (empty in a build made without CI).</summary>
    public static ChangelogFile File => _file ??= Load();

    /// <summary>The changes to open the tab with on this start; empty when it shouldn't open.</summary>
    public static IReadOnlyList<ChangelogItem> Pending { get; private set; } = [];

    /// <summary>Version before this update, for the tab's subtitle ("Updated from …").</summary>
    public static string? PreviousVersion { get; private set; }

    /// <summary>Keys of the changes that are new in this update, marked "New" when the tab is reopened.</summary>
    public static IReadOnlySet<string> NewKeys { get; private set; } = new HashSet<string>();

    /// <summary>Call once at startup, before the main window. Opens nothing; the window reads <see cref="Pending"/>.</summary>
    public static void CheckOnStart(string currentVersion, bool freshInstall)
    {
        try
        {
            var state = LoadState();
            var (show, save) = Changelog.OnStart(File, currentVersion, state, freshInstall);
            Pending = show;
            if (show.Count > 0)
            {
                PreviousVersion = state?.Version;
                NewKeys = show.Select(i => i.Key).ToHashSet();
                save.NewKeys = NewKeys.ToList();
                save.PreviousVersion = PreviousVersion;
            }
            else if (state is not null && string.Equals(state.Version, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                // Same version as last time: More > What's new still marks what this update brought.
                NewKeys = state.NewKeys.ToHashSet();
                PreviousVersion = state.PreviousVersion;
                save.NewKeys = state.NewKeys;
                save.PreviousVersion = state.PreviousVersion;
            }
            if (File.Items.Count > 0) SaveState(save);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"What's new check failed: {ex.Message}");
        }
    }

    private static ChangelogFile Load()
    {
        using var stream = typeof(WhatsNew).Assembly.GetManifestResourceStream("RemoteMan.changelog.json");
        if (stream is null) return new ChangelogFile();
        using var reader = new StreamReader(stream);
        return ChangelogFile.Parse(reader.ReadToEnd());
    }

    private static ChangelogState? LoadState()
    {
        try { return System.IO.File.Exists(StatePath) ? JsonSerializer.Deserialize<ChangelogState>(System.IO.File.ReadAllText(StatePath)) : null; }
        catch (Exception) { return null; }
    }

    private static void SaveState(ChangelogState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            System.IO.File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
