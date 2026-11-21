using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>Window layout: split view panes, the icon rail panels, favorites/recent and the online avatars.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<SessionViewModel> LeftSessions { get; } = new();
    public ObservableCollection<SessionViewModel> RightSessions { get; } = new();
    public ObservableCollection<PresenceChip> OnlineUsers { get; } = new();

    [ObservableProperty] private SessionViewModel? _leftActive;
    [ObservableProperty] private SessionViewModel? _rightActive;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RightPaneWidth), nameof(PaneGapWidth), nameof(LeftPaneTag), nameof(RightPaneTag))]
    private bool _isSplit;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LeftPaneTag), nameof(RightPaneTag))]
    private int _focusedPane;

    /// <summary>Which icon rail panel is showing: nav, fav, recent or cred.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PanelTitle), nameof(IsNavPanel))]
    private string _panelMode = "nav";

    [ObservableProperty] private string _quickHost = "";

    private bool _rearranging;

    public GridLength RightPaneWidth => IsSplit ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    public GridLength PaneGapWidth => new(IsSplit ? 4 : 0);
    /// <summary>"focused" draws the accent line on the pane's active tab and the accent border around it.</summary>
    public string LeftPaneTag => !IsSplit || FocusedPane == 0 ? "focused" : "";
    public string RightPaneTag => IsSplit && FocusedPane == 1 ? "focused" : "";

    public bool IsNavPanel => PanelMode == "nav";
    public string PanelTitle => PanelMode switch
    {
        "fav" => "Favorites",
        "recent" => "Recent",
        "cred" => "Credentials",
        _ => "Navigation",
    };

    /// <summary>Name of the open data source, shown above the tree.</summary>
    public string DataSourceName => $"{_settings.Active.Name} ({_settings.Active.Kind})";

    public string AppVersion { get; } = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");

    // ---------------- panes ----------------

    private ObservableCollection<SessionViewModel> PaneList(int pane) => pane == 0 ? LeftSessions : RightSessions;
    private SessionViewModel? PaneActive(int pane) => pane == 0 ? LeftActive : RightActive;

    private void SetPaneActive(int pane, SessionViewModel? s)
    {
        if (pane == 0) LeftActive = s; else RightActive = s;
    }

    /// <summary>New tabs open in the focused pane.</summary>
    private void AddSession(SessionViewModel s)
    {
        s.Pane = IsSplit ? FocusedPane : 0;
        Sessions.Add(s);
        PaneList(s.Pane).Add(s);
        ActiveSession = s;
    }

    partial void OnActiveSessionChanged(SessionViewModel? oldValue, SessionViewModel? newValue)
    {
        SessionStatus = newValue?.StatusText ?? "";
        if (_rearranging) return;
        if (newValue is not null)
        {
            SetPaneActive(newValue.Pane, newValue);
            FocusedPane = newValue.Pane;
            if (_store is not null && newValue.Entry.Revision > 0 && newValue is not ConfigBackupsSessionViewModel)
                _ = _store.SetMyPresenceAsync(newValue.Entry.Id, PresenceState.Connected);
        }
        UpdateShown();
    }

    partial void OnLeftActiveChanged(SessionViewModel? value) => PaneActiveChanged(0, value);
    partial void OnRightActiveChanged(SessionViewModel? value) => PaneActiveChanged(1, value);

    private void PaneActiveChanged(int pane, SessionViewModel? value)
    {
        if (_rearranging) return;
        UpdateShown();
        if (FocusedPane == pane) ActiveSession = value;
    }

    partial void OnFocusedPaneChanged(int value)
    {
        if (_rearranging) return;
        ActiveSession = PaneActive(value);
    }

    /// <summary>Only visible tabs render; the RDP view idles the others.</summary>
    private void UpdateShown()
    {
        foreach (var s in Sessions)
            s.IsActive = s == LeftActive || (IsSplit && s == RightActive);
    }

    [RelayCommand]
    private void CloseSession(SessionViewModel? s)
    {
        if (s is null || !Sessions.Contains(s)) return;
        var list = PaneList(s.Pane);
        var idx = list.IndexOf(s);
        var wasActive = PaneActive(s.Pane) == s;
        _rearranging = true;
        list.Remove(s);
        Sessions.Remove(s);
        _rearranging = false;
        s.IsActive = false;
        (s as IDisposable)?.Dispose();
        if (wasActive || PaneActive(s.Pane) is null || !list.Contains(PaneActive(s.Pane)!))
            SetPaneActive(s.Pane, list.Count == 0 ? null : list[Math.Clamp(idx, 0, list.Count - 1)]);
        ActiveSession = PaneActive(FocusedPane);
        UpdateShown();
    }

    /// <summary>Tab menu: send a tab to the other pane (turns split view on if needed).</summary>
    [RelayCommand]
    private void MoveToOtherPane(SessionViewModel? s)
    {
        if (s is null) return;
        if (!IsSplit) IsSplit = true;
        MoveSession(s, s.Pane == 0 ? 1 : 0);
        ActiveSession = s;
    }

    private void MoveSession(SessionViewModel s, int target)
    {
        if (s.Pane == target) return;
        _rearranging = true;
        var from = s.Pane;
        var fromList = PaneList(from);
        fromList.Remove(s);
        if (PaneActive(from) == s || PaneActive(from) is null) SetPaneActive(from, fromList.FirstOrDefault());
        s.Pane = target;
        PaneList(target).Add(s);
        SetPaneActive(target, s);
        _rearranging = false;
        UpdateShown();
    }

    partial void OnIsSplitChanged(bool value)
    {
        var keep = ActiveSession;
        if (!value)
        {
            foreach (var s in RightSessions.ToList()) MoveSession(s, 0);
            _rearranging = true;
            FocusedPane = 0;
            if (keep is not null) LeftActive = keep;
            _rearranging = false;
        }
        else if (LeftSessions.Count > 1)
        {
            // Turning split on: SSH goes right, the rest stays left (only when both kinds are open).
            var ssh = LeftSessions.Where(s => s is SshSessionViewModel).ToList();
            if (ssh.Count > 0 && ssh.Count < LeftSessions.Count)
                foreach (var s in ssh) MoveSession(s, 1);
            if (keep is not null) SetPaneActive(keep.Pane, keep);
        }
        _settings.SplitPane = value;
        TrySaveSettings();
        ActiveSession = keep is not null && Sessions.Contains(keep) ? keep : PaneActive(FocusedPane);
        UpdateShown();
    }

    // ---------------- icon rail ----------------

    partial void OnPanelModeChanged(string value) => RebuildTree();

    private IEnumerable<Entry> PanelEntries(List<Entry> all)
    {
        var byId = all.ToDictionary(e => e.Id);
        return PanelMode switch
        {
            "fav" => _settings.Favorites.Where(byId.ContainsKey).Select(id => byId[id]),
            "recent" => _settings.Recent.Where(byId.ContainsKey).Select(id => byId[id]),
            "cred" => all.Where(e => e.Kind == EntryKind.Credential).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => Enumerable.Empty<Entry>(),
        };
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (Selected?.Entry is not { } e || e.Kind == EntryKind.Folder) return;
        var removed = _settings.Favorites.Remove(e.Id);
        if (!removed) _settings.Favorites.Add(e.Id);
        TrySaveSettings();
        Message = removed ? $"Removed {e.Name} from favorites" : $"Added {e.Name} to favorites";
        if (PanelMode == "fav") RebuildTree();
    }

    private void RememberRecent(Entry e)
    {
        if (_store?.Get(e.Id) is null) return; // ad-hoc quick connects are not entries
        _settings.Recent.Remove(e.Id);
        _settings.Recent.Insert(0, e.Id);
        if (_settings.Recent.Count > 20) _settings.Recent.RemoveRange(20, _settings.Recent.Count - 20);
        TrySaveSettings();
        if (PanelMode == "recent") RebuildTree();
    }

    private void TrySaveSettings()
    {
        try { _settings.Save(); } catch (Exception ex) { Message = $"Could not save settings: {ex.Message}"; }
    }

    // ---------------- presence ----------------

    /// <summary>Avatars in the title bar: you first, then everyone else with a live heartbeat.</summary>
    private void RefreshOnlineUsers()
    {
        OnlineUsers.Clear();
        if (_store is null || !IsSharedSource) return;
        var now = DateTimeOffset.UtcNow;
        var me = new Presence(_store.User, Environment.MachineName, Guid.Empty, PresenceState.Viewing, now);
        OnlineUsers.Add(new PresenceChip(me.Initials, $"{_store.User} (you)", PresenceChip.ColorFor(_store.User)));
        foreach (var p in _store.Presence.Where(p => p.IsAlive(now) && p.User != _store.User).GroupBy(p => p.User).Select(g => g.First()).Take(6))
            OnlineUsers.Add(new PresenceChip(p.Initials, p.User, PresenceChip.ColorFor(p.User)));
    }
}
