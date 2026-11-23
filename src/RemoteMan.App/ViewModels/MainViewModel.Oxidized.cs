using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Services;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>Oxidized: last backup on the entry card, a mark on entries whose backups fail, and the Config backups tab.</summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _oxCts;
    private OxidizedSnapshot? _oxSnapshot;

    // --- dialogs supplied by the window ---
    /// <summary>Pick an Oxidized node for an entry. Returns the node's full name, "" to unlink, or null when cancelled.</summary>
    public Func<OxidizedSnapshot, Entry, string?>? OxidizedLinkDialog { get; set; }
    /// <summary>Opens Oxidized settings; true when saved.</summary>
    public Func<bool>? OxidizedSettingsDialog { get; set; }

    public OxidizedSnapshot? OxidizedSnapshot => _oxSnapshot;

    [ObservableProperty] private bool _oxidizedActive;
    [ObservableProperty] private string _oxidizedStatusText = "";

    [ObservableProperty] private bool _selectedOxidizedVisible;
    [ObservableProperty] private bool _selectedOxidizedLinked;
    /// <summary>ok, fail or never: the dot colour on the entry card.</summary>
    [ObservableProperty] private string _selectedOxidizedState = "never";
    [ObservableProperty] private string _selectedOxidizedText = "";
    [ObservableProperty] private string _selectedOxidizedTip = "";

    public bool SelectedOxidizedUnlinked => SelectedOxidizedVisible && !SelectedOxidizedLinked;
    partial void OnSelectedOxidizedVisibleChanged(bool value) => OnPropertyChanged(nameof(SelectedOxidizedUnlinked));
    partial void OnSelectedOxidizedLinkedChanged(bool value) => OnPropertyChanged(nameof(SelectedOxidizedUnlinked));

    /// <summary>A connected client with the current settings. The caller disposes it.</summary>
    public Task<OxidizedClient> ConnectOxidizedAsync(CancellationToken ct = default)
    {
        var cfg = OxidizedCfg;
        return OxidizedConnector.ConnectAsync(cfg, _integrations?.OxidizedPassword, _store, _protector, ct);
    }

    /// <summary>(Re)starts the node list refresh with the current settings. Called on open and after the settings change.</summary>
    public Task StartOxidizedAsync()
    {
        StopOxidized();
        var cfg = OxidizedCfg;
        _oxSnapshot = null;
        OxidizedActive = cfg.IsConfigured;
        OxidizedStatusText = OxidizedActive ? "Oxidized: connecting…" : "";
        ApplyOxidized();
        _ = RunOxidizedFeedSyncAsync();
        if (!OxidizedActive) return Task.CompletedTask;

        var cts = _oxCts = new CancellationTokenSource();
        var interval = TimeSpan.FromMinutes(Math.Clamp(cfg.PollMinutes, 1, 120));
        // In the background, so a slow or unreachable Oxidized never holds up opening the app.
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                await RefreshOxidizedAsync(cts.Token);
                while (await timer.WaitForNextTickAsync(cts.Token)) await RefreshOxidizedAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        });
        return Task.CompletedTask;
    }

    private void StopOxidized()
    {
        _oxCts?.Cancel();
        _oxCts?.Dispose();
        _oxCts = null;
    }

    /// <summary>Fetches the node list now (also after Back up now in a backups tab).</summary>
    public async Task RefreshOxidizedAsync(CancellationToken ct = default)
    {
        var cfg = OxidizedCfg;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(60));
            using var client = await ConnectOxidizedAsync(linked.Token);
            var nodes = await client.GetNodesAsync(linked.Token);
            var snap = new OxidizedSnapshot(nodes, cfg.MatchOptions, DateTimeOffset.UtcNow);
            OnUi(() =>
            {
                if (ct.IsCancellationRequested) return;
                _oxSnapshot = snap;
                var failing = snap.FailingCount;
                OxidizedStatusText = $"Oxidized: {nodes.Count} device{(nodes.Count == 1 ? "" : "s")}" +
                    (failing > 0 ? $", {failing} failing" : ", all backed up") + $" · {snap.FetchedAt.ToLocalTime():HH:mm}";
                ApplyOxidized();
                _ = RunOxidizedFeedSyncAsync();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                if (ct.IsCancellationRequested) return;
                OxidizedStatusText = _oxSnapshot is { } last
                    ? $"Oxidized: {ex.Message} (showing data from {last.FetchedAt.ToLocalTime():HH:mm})"
                    : $"Oxidized: {ex.Message}";
                RefreshOxidizedCard();
            });
        }
    }

    /// <summary>Marks entries whose last backup failed, and refreshes the card.</summary>
    private void ApplyOxidized()
    {
        var snap = _oxSnapshot;
        foreach (var n in _nodes.Values)
        {
            if (n.IsFolder || snap is null || !n.Entry.IsConnectable) { n.BackupFailed = false; n.BackupTip = null; continue; }
            var m = snap.Matcher.Match(n.Entry);
            n.BackupFailed = m?.Node.IsFailing == true;
            n.BackupTip = m is { Node: var node } && node.IsFailing
                ? $"Oxidized: last backup {node.StatusText}" + (node.LastRun is { } t ? $" ({t.ToLocalTime():d MMM HH:mm})" : "")
                : null;
        }
        RefreshOxidizedCard();
        RefreshDashboard();
    }

    private void RefreshOxidizedCard()
    {
        try { RefreshOxidizedCardCore(); }
        finally { if (!_refreshingProperties) RefreshOverview(); }
    }

    private void RefreshOxidizedCardCore()
    {
        var e = Selected?.Entry;
        SelectedOxidizedVisible = OxidizedActive && e is { IsConnectable: true } && _oxSnapshot is not null;
        if (!SelectedOxidizedVisible || e is null || _oxSnapshot is not { } snap)
        {
            SelectedOxidizedLinked = false;
            return;
        }
        var m = snap.Matcher.Match(e);
        SelectedOxidizedLinked = m is not null;
        if (m is null) { SelectedOxidizedText = ""; SelectedOxidizedTip = ""; return; }
        var node = m.Node;
        var now = DateTimeOffset.UtcNow;
        SelectedOxidizedState = node.IsOk ? "ok" : node.IsFailing ? "fail" : "never";
        SelectedOxidizedText = node.IsOk
            ? (node.LastRun is { } t ? $"Backed up {OxidizedSnapshot.Age(t, now)}" : "Backed up")
            : node.IsFailing ? $"Last backup {node.StatusText}" + (node.LastRun is { } f ? $", {OxidizedSnapshot.Age(f, now)}" : "")
            : "Not backed up yet";
        SelectedOxidizedTip = $"{node.FullName} ({node.Model}, {node.Ip}). {m.Reason}" +
            (node.LastChanged is { } c ? $". Config last changed {c.ToLocalTime():d MMM yyyy HH:mm}" : "");
    }

    // ---------------- commands ----------------

    /// <summary>Opens (or switches to) the Config backups tab for the selected entry.</summary>
    [RelayCommand]
    private async Task OpenConfigBackupsAsync()
    {
        if (Selected?.Entry is not { IsConnectable: true } e) return;
        if (!OxidizedActive) { Message = "Set up Oxidized first (More, Oxidized settings)"; return; }
        if (_oxSnapshot is null) await RefreshOxidizedAsync(_oxCts?.Token ?? default);
        if (_oxSnapshot is not { } snap) { Message = OxidizedStatusText.Length > 0 ? OxidizedStatusText : "Oxidized hasn't answered yet"; return; }

        var existing = Sessions.OfType<ConfigBackupsSessionViewModel>().FirstOrDefault(s => s.Entry.Id == e.Id);
        if (existing is not null) { ActiveSession = existing; return; }

        var m = snap.Matcher.Match(e);
        if (m is null)
        {
            if (Choose?.Invoke("No Oxidized device for this entry", $"Oxidized has no device that matches {e.Name} by address or name. Pick it from the list?",
                    ["Pick device…", "Cancel"]) != 0) return;
            await LinkToOxidizedAsync();
            if (_store?.Get(e.Id) is { } updated) e = updated;
            m = _oxSnapshot?.Matcher.Match(e);
            if (m is null) return;
        }
        var s = new ConfigBackupsSessionViewModel(e, m.Node, OxidizedCfg.Url, ConnectOxidizedAsync, () => RefreshOxidizedAsync(_oxCts?.Token ?? default));
        TrackFailures(s);
        AddSession(s);
        Message = "";
    }

    [RelayCommand]
    private void OpenInOxidized()
    {
        if (_oxSnapshot is not { } snap || Selected?.Entry is not { } e || snap.Matcher.Match(e) is not { } m) return;
        try { Process.Start(new ProcessStartInfo(OxidizedClient.NodePageUrl(OxidizedCfg.Url, m.Node)) { UseShellExecute = true }); }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task LinkToOxidizedAsync()
    {
        if (_store is null || Selected?.Entry is not { IsConnectable: true } e) return;
        if (_oxSnapshot is not { } snap) { Message = OxidizedActive ? "Oxidized hasn't answered yet" : "Set up Oxidized first (More, Oxidized settings)"; return; }
        if (OxidizedLinkDialog?.Invoke(snap, e) is not { } node) return;
        if (!_store.IsOnline) { Message = "Offline: the link can be saved once the database is back"; return; }
        var copy = EntryStore.Clone(e);
        copy.OxidizedNode = node.Length == 0 ? null : node;
        try
        {
            await _store.SaveAsync(copy);
            Message = node.Length == 0 ? $"{e.Name} unlinked from Oxidized" : $"{e.Name} linked to Oxidized device {node}";
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task OxidizedSettingsAsync()
    {
        if (OxidizedSettingsDialog?.Invoke() == true) await StartOxidizedAsync();
    }
}
