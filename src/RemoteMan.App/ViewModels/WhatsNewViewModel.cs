using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Updates;
using RemoteMan.Core.Model;
using RemoteMan.Core.Updates;

namespace RemoteMan.App.ViewModels;

/// <summary>One change on the What's new tab: its title, the user-facing points under it, and when it shipped.</summary>
public sealed record WhatsNewRow(string Title, IReadOnlyList<string> Notes, string Meta, bool IsNew)
{
    public bool HasNotes => Notes.Count > 0;
}

/// <summary>
/// The "What's new" tab: opens by itself on the first start after an update, and from More > What's new.
/// Not a connection: no presence, not reopened at the next start, closed like any other tab.
/// </summary>
public sealed partial class WhatsNewViewModel : SessionViewModel
{
    public const string NewsTag = "NEWS";
    private static readonly Guid TabId = new("5b0c1f0e-6a2d-4c51-9d1e-7f3a8e2c4b61");

    public WhatsNewViewModel(ChangelogFile file, IReadOnlySet<string> newKeys, string currentVersion, string? previousVersion)
        : base(new Entry { Id = TabId, Kind = EntryKind.Folder, Name = "What's new" }, new HostEndpoint(""), RemoteMan.Protocols.ResolvedCredential.None, false)
    {
        Title = "What's new";
        var rows = file.Items.Select(i => Row(i, newKeys.Contains(i.Key))).ToList();
        Fresh = rows.Where(r => r.IsNew).ToList();
        Earlier = rows.Where(r => !r.IsNew).ToList();
        Heading = $"RemoteMan {currentVersion}";
        Subtitle = Fresh.Count == 0
            ? (rows.Count == 0 ? "This build has no list of changes. Installed builds carry one." : "Recent changes")
            : previousVersion is { Length: > 0 } prev
                ? $"Updated from {prev} · {Fresh.Count} {(Fresh.Count == 1 ? "change" : "changes")}"
                : $"{Fresh.Count} {(Fresh.Count == 1 ? "change" : "changes")} in this update";
    }

    public override bool IsConnection => false;
    public override string KindTag => NewsTag;

    public string Heading { get; }
    public string Subtitle { get; }
    /// <summary>What this update brought (empty when reopened in a build that wasn't an update).</summary>
    public IReadOnlyList<WhatsNewRow> Fresh { get; }
    public IReadOnlyList<WhatsNewRow> Earlier { get; }
    public bool HasFresh => Fresh.Count > 0;
    public bool HasEarlier => Earlier.Count > 0;
    public string EarlierTitle => HasFresh ? "Earlier changes" : "Recent changes";

    [RelayCommand]
    private void OpenReleases()
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.Instance.ReleasesUrl) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private static WhatsNewRow Row(ChangelogItem i, bool isNew)
    {
        var meta = new List<string>();
        if (i.Version is { Length: > 0 } v) meta.Add(v.Contains("-ci.", StringComparison.Ordinal) ? $"Test build {v}" : $"Version {v}");
        if (i.Date != default) meta.Add(i.Date.ToLocalTime().ToString("d MMM yyyy"));
        return new WhatsNewRow(i.Title, i.Notes, string.Join(" · ", meta), isNew);
    }
}
