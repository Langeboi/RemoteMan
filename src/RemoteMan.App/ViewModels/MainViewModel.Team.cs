using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>One line under a person in the Team list: a connection, what they're looking at, or a reservation.</summary>
/// <param name="Glyph">Kind tag for the protocol icon ("RDP", "SSH", …), "lock" for a reservation, or null.</param>
public sealed record TeamLine(Guid? EntryId, string Text, string? Right, string? Glyph, bool Dim = false);

/// <summary>Someone online (one per person and PC) in the title bar's Team list.</summary>
public sealed record TeamMember(string User, string Machine, string Initials, string Color, IReadOnlyList<TeamLine> Lines)
{
    public string ShortUser => TeamText.Short(User);
}

/// <summary>A row in the Team list's Activity tab.</summary>
public sealed partial class ActivityItem : ObservableObject
{
    public required string User { get; init; }
    public required string Verb { get; init; }
    public required string When { get; init; }
    public required Entry Snapshot { get; init; }
    public string Initials => Presence.InitialsOf(User);
    public string Color => PresenceChip.ColorFor(User);
    public string ShortUser => TeamText.Short(User);
    [ObservableProperty] private bool _canRestore;
}

/// <summary>Working together: the Team list, sessions in presence, reservations, the connect heads-up, history and merging.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<TeamMember> TeamMembers { get; } = new();
    public ObservableCollection<ActivityItem> Activity { get; } = new();

    [ObservableProperty] private bool _teamShowsActivity;
    [ObservableProperty] private string _teamEmptyText = "";
    [ObservableProperty] private string _activityEmptyText = "";
    /// <summary>"3 online" beside the faces in the title bar; empty for a local data source.</summary>
    [ObservableProperty] private string _onlineCount = "";

    /// <summary>Reserve dialog: (entry) → (until, note), or null when cancelled.</summary>
    public Func<Entry, (DateTimeOffset Until, string? Note)?>? ReserveDialog { get; set; }

    /// <summary>
    /// Shown when someone saved the entry while you edited it and both of you changed the same field.
    /// Returns the conflict paths to take their value for, or null to go back to editing.
    /// </summary>
    public Func<MergeResult, string, IReadOnlyList<string>?>? MergeDialog { get; set; }

    /// <summary>Opens the change history of an entry.</summary>
    public Action<Entry>? HistoryDialog { get; set; }

    private bool _teamHooked;

    /// <summary>Called once the store is open: every tab you open or close goes into your presence.</summary>
    private void HookTeam()
    {
        if (_teamHooked) return;
        _teamHooked = true;
        Sessions.CollectionChanged += OnSessionsChanged;
        EditorWindowHooks();
    }

    private void EditorWindowHooks() =>
        RemoteMan.App.Views.EntryEditorWindow.OpenHistory = (owner, e) => { if (_store?.Get(e.Id) is { } live) RemoteMan.App.Views.HistoryWindow.Show(owner, this, live); };

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_store is null) return;
        var open = Sessions.Where(s => s.Entry.Revision > 0).Select(s => new PresenceSession(s.Entry.Id, s.OpenedAt)).ToList();
        _ = _store.SetMySessionsAsync(open);
        // Last tab closed: you're back to looking at the selected entry, not connected to anything.
        if (Sessions.Count == 0) _ = _store.SetMyPresenceAsync(Selected?.Id, PresenceState.Viewing);
    }

    // ---------------- Team list ----------------

    private void RefreshTeam()
    {
        TeamMembers.Clear();
        if (_store is null || !IsSharedSource) { OnlineCount = ""; return; }
        var now = DateTimeOffset.UtcNow;
        var people = _store.Presence.Where(p => p.IsAlive(now) && !(p.User == _store.User && p.Machine == _store.Machine))
            .GroupBy(p => (p.User, p.Machine)).Select(g => g.OrderByDescending(p => p.LastSeen).First())
            .OrderBy(p => p.User, StringComparer.OrdinalIgnoreCase).ToList();
        var reservations = _store.Entries.Where(e => e.Reservation is { } r && r.IsActive(now)).ToList();

        foreach (var p in people)
        {
            var lines = new List<TeamLine>();
            foreach (var s in p.Sessions ?? [])
                if (_store.Get(s.EntryId) is { } e)
                    lines.Add(new TeamLine(e.Id, e.Name, TeamText.Duration(now - s.Since), EntryNode.KindTagFor(e.Kind)));
            if (p.EntryId != Guid.Empty && _store.Get(p.EntryId) is { } focus && lines.All(l => l.EntryId != focus.Id))
                lines.Add(new TeamLine(focus.Id, (p.State == PresenceState.Editing ? "editing " : "looking at ") + focus.Name, null, null, Dim: true));
            foreach (var e in reservations.Where(e => string.Equals(e.Reservation!.By, p.User, StringComparison.OrdinalIgnoreCase)))
                lines.Add(new TeamLine(e.Id, $"{e.Name} reserved until {TeamText.Clock(e.Reservation!.Until)}", null, "lock"));
            TeamMembers.Add(new TeamMember(p.User, p.Machine, p.Initials, PresenceChip.ColorFor(p.User), lines));
        }
        var users = people.Select(p => p.User).Append(_store.User).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        OnlineCount = $"{users} online";
        TeamEmptyText = TeamMembers.Count == 0 ? "Nobody else is online right now." : "";
    }

    /// <summary>Fills the Activity tab: the latest changes by anyone, with Restore on deleted entries.</summary>
    [RelayCommand]
    private async Task LoadActivityAsync()
    {
        if (_store is null) return;
        if (!_store.SupportsHistory || !_store.IsOnline) { Activity.Clear(); ActivityEmptyText = "Activity needs the database, which can't be reached right now."; return; }
        IReadOnlyList<HistoryRecord> recent;
        try { recent = await _store.GetActivityAsync(80); }
        catch (Exception ex) { ActivityEmptyText = ex.Message; return; }
        Activity.Clear();
        var newestPerEntry = new HashSet<Guid>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        foreach (var h in recent.Where(h => h.Snapshot.Kind != EntryKind.Snippet))
        {
            var isNewest = newestPerEntry.Add(h.Snapshot.Id);
            // A folder delete tombstones everything inside; the folder's line is enough.
            if (h.Action == HistoryAction.Deleted && h.Snapshot.ParentId is { } p && recent.Any(x => x.Snapshot.Id == p && x.Action == HistoryAction.Deleted
                    && (x.Snapshot.UpdatedAt - h.Snapshot.UpdatedAt).Duration() < TimeSpan.FromMinutes(1))) continue;
            Activity.Add(new ActivityItem
            {
                User = h.Snapshot.UpdatedBy ?? "someone",
                Verb = TeamText.Describe(h, h.Snapshot.Name),
                When = TeamText.When(h.Snapshot.UpdatedAt),
                Snapshot = h.Snapshot,
                CanRestore = isNewest && h.Action == HistoryAction.Deleted && _store.Get(h.Snapshot.Id) is null && h.Snapshot.UpdatedAt > cutoff,
            });
            if (Activity.Count >= 40) break;
        }
        ActivityEmptyText = Activity.Count == 0 ? "No changes recorded yet." : "";
    }

    partial void OnTeamShowsActivityChanged(bool value) { if (value) _ = LoadActivityAsync(); }

    [RelayCommand]
    private async Task RestoreActivityAsync(ActivityItem? item)
    {
        if (item is null) return;
        item.CanRestore = false;
        Message = await RestoreAsync(item.Snapshot);
        await LoadActivityAsync();
    }

    /// <summary>Selects an entry in the tree (from the Team list), opening its folders.</summary>
    [RelayCommand]
    private void JumpToEntry(Guid? id)
    {
        if (id is not { } entryId || _store?.Get(entryId) is null) return;
        if (SearchText.Length > 0) SearchText = "";
        if (PanelMode != "nav") PanelMode = "nav";
        for (var f = _store.Get(entryId)?.ParentId; f is { } fid; f = _store.Get(fid)?.ParentId)
            if (_nodes.TryGetValue(fid, out var folder)) folder.IsExpanded = true;
        if (_nodes.TryGetValue(entryId, out var n)) { n.IsSelected = true; Selected = n; }
    }

    // ---------------- history and restore ----------------

    [RelayCommand]
    private void ShowHistory()
    {
        if (Selected?.Entry is { } e && e.Id != FavoritesId) HistoryDialog?.Invoke(e);
    }

    /// <summary>Restores an old version (or a deleted entry). Returns what happened, for the status bar.</summary>
    public async Task<string> RestoreAsync(Entry snapshot)
    {
        if (_store is null) return "";
        if (!_store.IsOnline) return "Offline: restore needs the database";
        try
        {
            var n = await _store.RestoreAsync(snapshot);
            return n > 1 ? $"Restored {snapshot.Name} with {n - 1} entries inside" : $"Restored {snapshot.Name}";
        }
        catch (ConcurrencyException) { return $"{snapshot.Name} was changed meanwhile; open the history again to see the latest"; }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------------- reservations ----------------

    public async Task ReserveAsync(Entry e)
    {
        if (_store is null || ReserveDialog?.Invoke(e) is not { } pick) return;
        if (!_store.IsOnline) { Message = "Offline: entries can't be reserved until the database is back"; return; }
        try
        {
            await _store.ReserveAsync(e.Id, pick.Until, pick.Note);
            Message = $"Reserved {e.Name} until {TeamText.Clock(pick.Until)}";
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    public async Task ReleaseAsync(Entry e)
    {
        if (_store is null) return;
        try { await _store.ReleaseAsync(e.Id); Message = $"Released {e.Name}"; }
        catch (Exception ex) { Message = ex.Message; }
    }

    /// <summary>Your own reservation on the entry, if it's still running.</summary>
    public bool IsReservedByMe(Entry e) => _store is not null && e.Reservation is { } r && r.IsActive(DateTimeOffset.UtcNow)
        && string.Equals(r.By, _store.User, StringComparison.OrdinalIgnoreCase);

    public bool CanReserve => IsSharedSource;

    // ---------------- heads-up before connecting ----------------

    /// <summary>
    /// Asks before connecting to an entry someone reserved, or to an RDP entry someone is connected to
    /// (one session per user: the same login signs them out). True means go ahead.
    /// </summary>
    private bool ConfirmTeamBeforeConnect(Entry e)
    {
        if (_store is null || e.Revision == 0 || Choose is null) return true;
        var live = _store.Get(e.Id) ?? e;
        if (_store.ReservedByOthers(live) is { } r)
        {
            var text = $"Until {TeamText.Clock(r.Until)} ({TeamText.Remaining(r.Until)})." + (r.Note is { Length: > 0 } note ? $"\n\n\"{note}\"" : "");
            if (Choose($"{live.Name} is reserved by {TeamText.Short(r.By)}", text, ["Connect anyway", "Cancel"]) != 0) return false;
        }
        if (live.Kind == EntryKind.Rdp && _store.ConnectedTo(live.Id).FirstOrDefault() is { } p)
        {
            var since = p.ConnectedSince is { } s ? $"Connected for {TeamText.Duration(DateTimeOffset.UtcNow - s)} from {p.Machine}. " : $"Connected from {p.Machine}. ";
            var who = TeamText.Short(p.User);
            if (Choose($"{who} is connected to {live.Name}", since + $"RDP allows one session per user, so if you use the same login, {who} will be signed out.",
                    ["Connect anyway", "Cancel"]) != 0) return false;
        }
        return true;
    }

    // ---------------- saving over someone else's save ----------------

    /// <summary>
    /// Someone saved the entry while you were editing it. Their changes to fields you didn't touch are kept,
    /// yours too; only fields you both changed ask. Returns the saved entry, or null when you chose to keep
    /// editing (the merged version is then reopened in the editor).
    /// </summary>
    private async Task<(Entry? Saved, Entry? EditAgain)> SaveMergedAsync(Entry @base, Entry mine)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _store!.PullAsync();
            if (_store.Get(mine.Id) is not { } theirs) { Message = $"{mine.Name} was deleted by someone else while you edited it"; return (null, null); }
            var merge = EntryDiff.Merge(@base, mine, theirs, id => _store.Get(id)?.Name);
            var toSave = merge.Merged;
            if (merge.Conflicts.Count > 0)
            {
                var useTheirs = MergeDialog?.Invoke(merge, TeamText.Short(theirs.UpdatedBy));
                if (useTheirs is null) return (null, merge.Merged);
                toSave = merge.Resolve(useTheirs);
            }
            try
            {
                var saved = await _store.SaveAsync(toSave);
                var kept = merge.KeptTheirs.Select(c => c.Label.ToLowerInvariant()).Distinct().ToList();
                Message = kept.Count > 0
                    ? $"Saved. {TeamText.Short(theirs.UpdatedBy)} had changed it meanwhile; their {string.Join(", ", kept)} change was kept too"
                    : $"Saved {saved.Name}";
                return (saved, null);
            }
            catch (ConcurrencyException) { @base = theirs; mine = toSave; /* yet another save came in: merge again */ }
        }
        Message = $"{mine.Name} keeps changing; your edit was not saved. Try again in a moment.";
        return (null, null);
    }
}
