using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Services;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>One open problem in the entry card. Tag picks the glyph: high, warn or info.</summary>
public sealed record ZabbixProblemRow(string Tag, string Name, string Meta);

/// <summary>Zabbix: health glyphs on the tree, problems in the entry card, linking and import.</summary>
public sealed partial class MainViewModel
{
    private ZabbixMonitor? _zabbix;

    // --- dialogs supplied by the window ---
    /// <summary>Pick a Zabbix host for an entry. Returns the hostid, "" to unlink, or null when cancelled.</summary>
    public Func<ZabbixSnapshot, Entry, string?>? ZabbixLinkDialog { get; set; }
    public Func<ZabbixSnapshot, EntryStore, ZabbixImportPlan?>? ZabbixImportDialog { get; set; }
    /// <summary>Opens Zabbix settings; true when saved.</summary>
    public Func<bool>? ZabbixSettingsDialog { get; set; }

    public ZabbixSnapshot? ZabbixSnapshot => _zabbix?.Latest;
    public RemoteMan.Core.Security.ISecretProtector? Protector => _protector;

    /// <summary>Imports go under the selected folder (or the selected entry's folder), else the root.</summary>
    public Guid? ImportParentFolder() =>
        Selected?.Entry is { } s ? (s.Kind == EntryKind.Folder ? s.Id : s.ParentId) : null;
    public ObservableCollection<ZabbixProblemRow> SelectedProblems { get; } = new();

    [ObservableProperty] private bool _zabbixActive;
    [ObservableProperty] private string _zabbixStatusText = "";
    /// <summary>Glyph in the status bar: warn, high, unreach when Zabbix can't be reached, or none when all is well.</summary>
    [ObservableProperty] private string? _zabbixStatusTag;
    /// <summary>The Z in the status bar: ok (connected), bad (unreachable) or wait (connecting).</summary>
    [ObservableProperty] private string _zabbixConnState = "wait";
    [ObservableProperty] private string _zabbixRouteText = "";

    [ObservableProperty] private bool _selectedZabbixVisible;
    [ObservableProperty] private bool _selectedZabbixLinked;
    [ObservableProperty] private string _selectedZabbixSummary = "";
    [ObservableProperty] private string? _selectedZabbixTag;
    [ObservableProperty] private string _selectedZabbixMatch = "";

    public bool SelectedZabbixUnlinked => SelectedZabbixVisible && !SelectedZabbixLinked;
    partial void OnSelectedZabbixVisibleChanged(bool value) => OnPropertyChanged(nameof(SelectedZabbixUnlinked));
    partial void OnSelectedZabbixLinkedChanged(bool value) => OnPropertyChanged(nameof(SelectedZabbixUnlinked));

    /// <summary>(Re)starts polling with the current settings. Called on open and after the settings change.</summary>
    public async Task StartZabbixAsync()
    {
        await StopZabbixAsync();
        var cfg = _settings.Zabbix;
        var token = cfg.IsConfigured ? ZabbixConnector.LoadToken(cfg) : null;
        if (token is null)
        {
            ZabbixActive = false;
            ZabbixStatusText = "";
            ZabbixStatusTag = null;
            ApplyZabbix();
            return;
        }

        var monitor = new ZabbixMonitor(ct => ZabbixConnector.ConnectAsync(cfg, token, _store, _protector, ct))
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(cfg.PollSeconds, 10, 3600)),
            MinimumSeverity = cfg.MinimumSeverity,
            MatchOptions = cfg.MatchOptions,
        };
        monitor.Updated += (_, _) => OnUi(() => { ZabbixConnState = "ok"; ApplyZabbix(); });
        monitor.Failed += (_, err) => OnUi(() =>
        {
            ZabbixConnState = "bad";
            ZabbixStatusTag = "unreach";
            ZabbixStatusText = ZabbixSnapshot is { } last ? $"Zabbix: {err} (showing data from {last.FetchedAt.ToLocalTime():HH:mm})" : $"Zabbix: {err}";
        });
        _zabbix = monitor;
        ZabbixActive = true;
        ZabbixStatusTag = null;
        ZabbixStatusText = "Zabbix: connecting…";
        ZabbixConnState = "wait";
        var host = cfg.Url;
        try { host = ZabbixClient.ApiEndpoint(cfg.Url).Host; } catch (ZabbixApiException) { }
        var jump = cfg.JumpHostEntryId is { } jid ? _store?.Get(jid)?.Name : null;
        ZabbixRouteText = jump is null ? $"{host}, refreshed every {monitor.Interval.TotalSeconds:0} s" : $"{host} through jump host {jump}, refreshed every {monitor.Interval.TotalSeconds:0} s";
        monitor.Start();
    }

    private async Task StopZabbixAsync()
    {
        var m = _zabbix;
        _zabbix = null;
        if (m is not null) await m.DisposeAsync();
    }

    /// <summary>Puts the latest Zabbix health on every row and folder, and refreshes the card and status bar.</summary>
    private void ApplyZabbix()
    {
        var snap = ZabbixSnapshot;
        int Walk(IEnumerable<EntryNode> nodes, out bool high)
        {
            var bad = 0;
            high = false;
            foreach (var n in nodes)
            {
                if (n.IsFolder)
                {
                    n.ProblemCount = Walk(n.Children, out var h);
                    n.ProblemCountHigh = h;
                    n.Health = null;
                    n.HealthTip = n.ProblemCount > 0 ? $"{n.ProblemCount} host{(n.ProblemCount == 1 ? "" : "s")} with problems" : null;
                    bad += n.ProblemCount;
                    high |= h;
                    continue;
                }
                var health = snap is null || !n.Entry.IsConnectable ? ZabbixHealth.Unlinked : snap.HealthFor(n.Entry);
                var tag = TagFor(health.State);
                n.Health = tag;
                n.HealthTip = health.IsLinked ? $"Zabbix: {health.Summary}" : null;
                if (health.IsBad) { bad++; high |= health.State == ZabbixHealthState.High; }
            }
            return bad;
        }
        Walk(Roots, out _);

        if (snap is not null)
        {
            var hosts = snap.ProblemHostCount;
            var problems = snap.Problems.Count(p => !p.Suppressed && snap.Hosts.Any(h => h.HostId == p.HostId && h.Enabled));
            var worst = snap.Hosts.Where(h => h.Enabled).Select(h => snap.HealthOf(h).State).DefaultIfEmpty(ZabbixHealthState.Ok).Max();
            ZabbixStatusTag = worst switch { ZabbixHealthState.High => "high", ZabbixHealthState.Warning => "warn", _ => null };
            ZabbixStatusText = problems == 0 && hosts == 0
                ? $"Zabbix: all {snap.Hosts.Count} hosts OK · {snap.FetchedAt.ToLocalTime():HH:mm:ss}"
                : $"Zabbix: {problems} problem{(problems == 1 ? "" : "s")} on {hosts} host{(hosts == 1 ? "" : "s")} · {snap.FetchedAt.ToLocalTime():HH:mm:ss}";
        }
        RefreshZabbixCard();
    }

    /// <summary>Glyphs only for errors and warnings: OK and maintenance show nothing.</summary>
    internal static string? TagFor(ZabbixHealthState s) => s switch
    {
        ZabbixHealthState.Warning => "warn",
        ZabbixHealthState.High => "high",
        ZabbixHealthState.Unreachable => "unreach",
        _ => null,
    };

    private void RefreshZabbixCard()
    {
        SelectedProblems.Clear();
        var e = Selected?.Entry;
        SelectedZabbixVisible = ZabbixActive && e is { IsConnectable: true } && ZabbixSnapshot is not null;
        if (!SelectedZabbixVisible || e is null || ZabbixSnapshot is not { } snap)
        {
            SelectedZabbixLinked = false;
            return;
        }
        var h = snap.HealthFor(e);
        SelectedZabbixLinked = h.IsLinked;
        SelectedZabbixSummary = h.Summary;
        SelectedZabbixTag = TagFor(h.State);
        SelectedZabbixMatch = h.MatchReason ?? "";
        var now = DateTimeOffset.UtcNow;
        foreach (var p in h.Problems.Take(8))
        {
            var tag = p.Severity >= ZabbixSeverity.High ? "high" : p.Severity >= ZabbixSeverity.Warning ? "warn" : "info";
            var ack = p.Acknowledged ? (p.AcknowledgedBy is { } by ? $"acked by {by}" : "acknowledged") : "not acknowledged";
            SelectedProblems.Add(new ZabbixProblemRow(tag, p.Name, $"{SeverityName(p.Severity)} · {ZabbixSnapshot.Age(p.Since, now)}, {ack}{(p.Suppressed ? " · suppressed" : "")}"));
        }
        if (h.Problems.Count > 8)
            SelectedProblems.Add(new ZabbixProblemRow("info", $"and {h.Problems.Count - 8} more", "Open in Zabbix to see them all"));
    }

    private static string SeverityName(ZabbixSeverity s) => s switch
    {
        ZabbixSeverity.NotClassified => "Not classified",
        ZabbixSeverity.Information => "Info",
        _ => s.ToString(),
    };

    // ---------------- commands ----------------

    [RelayCommand]
    private void OpenInZabbix()
    {
        if (ZabbixSnapshot is not { } snap || Selected?.Entry is not { } e || snap.HealthFor(e).Host is not { } host) return;
        try { Process.Start(new ProcessStartInfo(ZabbixClient.ProblemsUrl(_settings.Zabbix.Url, host.HostId)) { UseShellExecute = true }); }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task LinkToZabbixAsync()
    {
        if (_store is null || Selected?.Entry is not { IsConnectable: true } e) return;
        if (ZabbixSnapshot is not { } snap) { Message = ZabbixActive ? "Zabbix hasn't answered yet" : "Set up Zabbix first (More, Zabbix settings)"; return; }
        if (ZabbixLinkDialog?.Invoke(snap, e) is not { } hostId) return;
        if (!_store.IsOnline) { Message = "Offline: the link can be saved once the database is back"; return; }
        var copy = EntryStore.Clone(e);
        copy.ZabbixHostId = hostId.Length == 0 ? null : hostId;
        try
        {
            await _store.SaveAsync(copy);
            Message = hostId.Length == 0 ? $"{e.Name} unlinked from Zabbix" : $"{e.Name} linked to {snap.Matcher.GetHost(hostId)?.DisplayName}";
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task ImportFromZabbixAsync()
    {
        if (_store is null) return;
        if (ZabbixSnapshot is not { } snap) { Message = ZabbixActive ? "Zabbix hasn't answered yet" : "Set up Zabbix first (More, Zabbix settings)"; return; }
        if (!_store.IsOnline) { Message = "Offline: entries can't be created until the database is back"; return; }
        if (ZabbixImportDialog?.Invoke(snap, _store) is not { } plan) return;
        try
        {
            foreach (var f in plan.NewFolders) await _store.SaveAsync(f);
            foreach (var e in plan.NewEntries) await _store.SaveAsync(e);
            foreach (var e in plan.Linked) await _store.SaveAsync(e);
            Message = $"Imported {plan.NewEntries.Count} entr{(plan.NewEntries.Count == 1 ? "y" : "ies")} from Zabbix"
                      + (plan.Linked.Count > 0 ? $", linked {plan.Linked.Count} existing" : "");
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task ZabbixSettingsAsync()
    {
        if (ZabbixSettingsDialog?.Invoke() == true) await StartZabbixAsync();
    }
}
