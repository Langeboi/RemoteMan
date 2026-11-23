using RemoteMan.App.Services;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// Remembers which sessions are open and offers to reopen them at the next start. The list is saved whenever a
/// tab or session window opens, closes or changes pane, and frozen once the app starts shutting down (closing the
/// app disposes the sessions, which must not empty the list). Because it is always current, a crash keeps it too.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Asks whether to reopen the listed sessions. Returns (reopen, don't ask again).</summary>
    public Func<IReadOnlyList<string>, (bool Reopen, bool Remember)>? AskReopen { get; set; }

    /// <summary>Sessions shown in their own window (not in <see cref="Sessions"/>).</summary>
    private readonly List<SessionViewModel> _windowSessions = new();
    private bool _shuttingDown;
    private bool _restoring;
    private bool _trackingStarted;

    public ReopenMode ReopenSessions
    {
        get => _settings.ReopenSessions;
        set { _settings.ReopenSessions = value; TrySaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>The colour theme to use from the next start.</summary>
    public string Theme
    {
        get => _settings.Theme;
        set { _settings.Theme = value; TrySaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>Unmanaged (saturated) colours in SSH and web tabs, from the next start.</summary>
    public bool VividColors
    {
        get => _settings.VividColors;
        set { _settings.VividColors = value; TrySaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>Light and shadow on the controls, from the next start.</summary>
    public Shading Shading
    {
        get => _settings.Shading;
        set { _settings.Shading = value; TrySaveSettings(); OnPropertyChanged(); }
    }

    private void StartTrackingOpenSessions()
    {
        if (_trackingStarted) return;
        _trackingStarted = true;
        Sessions.CollectionChanged += (_, _) => SaveOpenSessions();
    }

    private void TrackWindowSession(SessionViewModel s)
    {
        _windowSessions.Add(s);
        s.Closed += closed => { if (!_shuttingDown && _windowSessions.Remove(closed)) SaveOpenSessions(); };
        SaveOpenSessions();
    }

    /// <summary>Called by the main window as it closes, before the sessions are torn down.</summary>
    public void FreezeOpenSessions()
    {
        if (_shuttingDown) return;
        // Mid-reopen the list is still the old one; keep it.
        SaveOpenSessions();
        _shuttingDown = true;
    }

    private void SaveOpenSessions()
    {
        if (_shuttingDown || _restoring || _store is null) return;
        var list = new List<OpenSessionRecord>();
        void Add(SessionViewModel s, int pane, bool active)
        {
            // Only saved entries: quick connects and Oxidized/Zabbix tabs have nothing to reopen from.
            if (!s.IsConnection || s is ConfigBackupsSessionViewModel || _store.Get(s.Entry.Id) is null) return;
            list.Add(new OpenSessionRecord { EntryId = s.Entry.Id, Pane = pane, Active = active });
        }
        foreach (var s in LeftSessions) Add(s, 0, s == LeftActive);
        foreach (var s in RightSessions) Add(s, 1, s == RightActive);
        foreach (var s in _windowSessions) Add(s, 0, false);
        if (list.Count == _settings.LastSessions.Count && list.Zip(_settings.LastSessions).All(p =>
                p.First.EntryId == p.Second.EntryId && p.First.Pane == p.Second.Pane && p.First.Active == p.Second.Active))
            return;
        _settings.LastSessions = list;
        TrySaveSettings();
    }

    /// <summary>After the data source opens: ask (or just do it, per the setting) and reopen what was open last time.</summary>
    private void OfferReopen()
    {
        if (_store is null) return;
        var saved = _settings.LastSessions
            .Where(r => _store.Get(r.EntryId) is { IsConnectable: true })
            .ToList();
        StartTrackingOpenSessions();
        if (saved.Count == 0 || _settings.ReopenSessions == ReopenMode.Never) return;

        if (_settings.ReopenSessions == ReopenMode.Ask)
        {
            if (AskReopen is null) return;
            var names = saved.Select(r => _store.Get(r.EntryId)!.Name).ToList();
            var (reopen, remember) = AskReopen(names);
            if (remember) ReopenSessions = reopen ? ReopenMode.Always : ReopenMode.Never;
            if (!reopen) return;
        }
        _ = ReopenAsync(saved);
    }

    private async Task ReopenAsync(List<OpenSessionRecord> saved)
    {
        _restoring = true;
        var opened = new List<(OpenSessionRecord Record, SessionViewModel? Session)>();
        try
        {
            if (saved.Any(r => r.Pane == 1) && !IsSplit) IsSplit = true;
            foreach (var r in saved)
            {
                if (_shuttingDown) return;
                if (_store?.Get(r.EntryId) is not { } e) continue;
                FocusedPane = IsSplit ? r.Pane : 0;
                var before = Sessions.Count;
                try { await ConnectEntryAsync(e); }
                catch (Exception ex) { Message = $"{e.Name}: {ex.Message}"; }
                opened.Add((r, Sessions.Count > before ? Sessions[^1] : null));
            }
            // Bring back the tab that was showing in each pane.
            foreach (var (r, s) in opened)
                if (r.Active && s is not null && Sessions.Contains(s)) ActiveSession = s;
            var focused = opened.FirstOrDefault(o => o.Record.Active && o.Record.Pane == 0).Session;
            if (focused is not null && Sessions.Contains(focused)) ActiveSession = focused;
            if (string.IsNullOrEmpty(Message)) Message = opened.Count == 1 ? "Reopened 1 session from last time" : $"Reopened {opened.Count} sessions from last time";
        }
        finally
        {
            _restoring = false;
            SaveOpenSessions();
        }
    }
}
