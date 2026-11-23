using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteMan.Core.Updates;

/// <summary>
/// One change in the "What's new" tab: usually one merged pull request, which on the test branch is one test build.
/// <see cref="Key"/> identifies it across builds ("pr:46", or the commit for a change pushed without a PR).
/// </summary>
public sealed record ChangelogItem
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>User-facing points from the pull request's "What's new" section; empty shows the title alone.</summary>
    public List<string> Notes { get; init; } = new();
    public DateTimeOffset Date { get; init; }
    /// <summary>The build that shipped it ("0.1.0-ci.97"), when one was published from that commit.</summary>
    public string? Version { get; init; }
    public int? Pr { get; init; }
}

/// <summary>The change list baked into each build by CI (build/changelog.py), newest first.</summary>
public sealed class ChangelogFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string? Version { get; init; }
    public List<ChangelogItem> Items { get; init; } = new();

    public static ChangelogFile Parse(string json)
    {
        try { return JsonSerializer.Deserialize<ChangelogFile>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }
}

/// <summary>What the last run of RemoteMan knew: its version and the changes its own list held.</summary>
public sealed class ChangelogState
{
    public string? Version { get; set; }
    public List<string> Seen { get; set; } = new();
    /// <summary>What the update to <see cref="Version"/> brought, so reopening the tab can still mark it.</summary>
    public List<string> NewKeys { get; set; } = new();
    public string? PreviousVersion { get; set; }
}

/// <summary>Decides when the "What's new" tab opens by itself, and what it lists.</summary>
public static class Changelog
{
    /// <summary>
    /// The changes to show on this start (empty = don't open the tab) and the state to save.
    /// Opens only on the first start of a new version: never on a fresh install, never twice for the same version.
    /// Lists every change the previous version didn't have, so skipped builds are covered too.
    /// </summary>
    public static (IReadOnlyList<ChangelogItem> Show, ChangelogState Save) OnStart(ChangelogFile file, string currentVersion, ChangelogState? state, bool freshInstall)
    {
        // A build without a list (run from the build folder): nothing to show, and keep what we knew.
        if (file.Items.Count == 0) return ([], state ?? new ChangelogState());

        var save = new ChangelogState { Version = currentVersion, Seen = file.Items.Select(i => i.Key).ToList() };
        if (freshInstall) return ([], save);
        if (state is null || state.Seen.Count == 0)
            // Updated from a version before the tab existed: we don't know what it had, so show this build's change.
            return ([file.Items[0]], save);
        if (string.Equals(state.Version, currentVersion, StringComparison.OrdinalIgnoreCase)) return ([], save);

        var seen = state.Seen.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (file.Items.Where(i => !seen.Contains(i.Key)).ToList(), save);
    }
}
