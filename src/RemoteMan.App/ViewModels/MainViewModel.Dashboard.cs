using System.Diagnostics;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.App.Views;

namespace RemoteMan.App.ViewModels;

/// <summary>The Dashboard tab: pinned first in the left pane, filled from the entries and the last Zabbix poll.</summary>
public sealed partial class MainViewModel
{
    public DashboardViewModel Dashboard { get; private set; } = null!;

    /// <summary>Called once from the constructor: the dashboard is the first tab and the one shown at startup.</summary>
    private void CreateDashboard()
    {
        var d = Dashboard = new DashboardViewModel
        {
            ConnectAction = id => { if (_store?.Get(id) is { IsConnectable: true } e) _ = ConnectEntryAsync(e); },
            RevealAction = RevealEntry,
            OpenZabbixAction = OpenZabbixHome,
            OpenBackupAction = OpenDashboardBackup,
            OpenOxidizedAction = OpenOxidizedHome,
            RefreshBackupsAction = () => _ = RefreshOxidizedAsync(_oxCts?.Token ?? default),
        };
        foreach (var (key, _) in d.Widgets) d.SetShown(key, !_settings.DashboardHidden.Contains(key));
        d.WidgetToggled = (key, on) =>
        {
            if (on) _settings.DashboardHidden.Remove(key);
            else if (!_settings.DashboardHidden.Contains(key)) _settings.DashboardHidden.Add(key);
            TrySaveSettings();
        };
        d.Pane = 0;
        Sessions.Add(d);
        LeftSessions.Add(d);
        _leftActive = d;
        _activeSession = d;
        d.IsActive = true;
    }

    /// <summary>Selects an entry in the tree, opening the folders above it (and leaving a search or another panel if needed).</summary>
    public void RevealEntry(Guid id)
    {
        if (_store?.Get(id) is null) return;
        if (!_nodes.ContainsKey(id))
        {
            if (SearchText.Length > 0) SearchText = "";
            if (!_nodes.ContainsKey(id) && PanelMode != "nav") PanelMode = "nav";
        }
        if (!_nodes.TryGetValue(id, out var node)) return;
        for (var p = node.Entry.ParentId; p is { } pid && _nodes.TryGetValue(pid, out var parent); p = parent.Entry.ParentId)
            parent.IsExpanded = true;
        node.IsSelected = true;
        Selected = node;
        RevealRequested?.Invoke(this, node);
    }

    /// <summary>The window scrolls the tree to the node and focuses it.</summary>
    public event EventHandler<EntryNode>? RevealRequested;

    private void OpenZabbixHome()
    {
        var url = ZabbixCfg.Url;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(ZabbixClient.ProblemsUrl(url)) { UseShellExecute = true }); }
        catch (Exception ex) { Message = ex.Message; }
    }

    private static string KindName(EntryKind k) => k switch
    {
        EntryKind.Rdp => "RDP",
        EntryKind.Ssh => "SSH",
        EntryKind.Vnc => "VNC",
        EntryKind.Web => "Web",
        EntryKind.Telnet => "Telnet",
        EntryKind.Serial => "Serial",
        EntryKind.Sftp => "SFTP",
        EntryKind.Ftp => "FTP",
        EntryKind.Proxmox => "Proxmox",
        EntryKind.PowerShell => "PowerShell",
        EntryKind.WinBox => "WinBox",
        _ => k.ToString(),
    };

    private static string HostOf(Entry e) =>
        e.Kind == EntryKind.Web ? e.Web?.Url ?? e.Endpoints.FirstOrDefault()?.ToString() ?? ""
        : e.Kind == EntryKind.Serial ? e.Serial?.PortName ?? ""
        : e.Endpoints.FirstOrDefault()?.ToString() ?? "";

    private string FolderName(Entry e) => e.ParentId is { } p && _store?.Get(p) is { Kind: EntryKind.Folder } f ? f.Name : "";

    private static string When(DateTimeOffset t, DateTimeOffset now)
    {
        var local = t.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "yesterday";
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd");
        return local.ToString("d MMM");
    }

    /// <summary>Redoes every widget. Cheap (one pass over the entries), so it runs after every tree rebuild and Zabbix poll.</summary>
    private void RefreshDashboard()
    {
        var d = Dashboard;
        if (d is null) return;
        var now = DateTimeOffset.Now;
        d.SourceName = DataSourceName;
        d.SourceOk = DataSourceOk;
        d.SourceState = DataSourceText;
        d.Updated = $"updated {now:HH:mm}";
        if (_store is null) return;

        var entries = _store.Entries.ToList();
        var s = DashboardStats.Compute(entries, now);
        var snap = ZabbixActive ? ZabbixSnapshot : null;
        var byId = entries.Where(e => !e.Deleted).ToDictionary(e => e.Id);

        string? Health(Entry e) => snap is null || !e.IsConnectable ? null : TagFor(snap.HealthFor(e).State);
        DashEntry Row(Entry e, string side) => new(e.Id, e.Name, EntryNode.KindTagFor(e.Kind), HostOf(e), side, Health(e));

        // Totals
        d.Tiles.Clear();
        d.Tiles.Add(new DashTile("Entries", s.Entries.ToString("N0"), s.AddedThisWeek > 0 ? $"+{s.AddedThisWeek} this week" : "none added this week"));
        d.Tiles.Add(new DashTile("Folders", s.Folders.ToString("N0"), s.MaxFolderDepth > 0 ? $"deepest {s.MaxFolderDepth} level{(s.MaxFolderDepth == 1 ? "" : "s")}" : "no folders yet"));
        d.Tiles.Add(new DashTile("Credentials", s.Credentials.ToString("N0"), $"{s.UsingSavedCredential} entr{(s.UsingSavedCredential == 1 ? "y uses" : "ies use")} one"));
        // Active sessions: yours, plus teammates' on a shared data source (their presence says what they have open)
        var open = Sessions.Where(x => x.IsConnection).ToList();
        IReadOnlyList<Presence> team = _store.Source.SupportsPresence && _store.Source.Kind != DataSourceKind.Sqlite ? _store.TeamSessions() : [];
        var active = open.Count + team.Count;
        string activeSub;
        if (team.Count == 0)
            activeSub = open.Count == 0 ? "nothing connected" : string.Join(", ", open.GroupBy(x => x.KindTag).Select(g => $"{g.Count()} {g.Key}"));
        else
        {
            var people = team.Select(p => p.User).Distinct().Count();
            activeSub = $"{open.Count} yours, {team.Count} by {people} teammate{(people == 1 ? "" : "s")}";
        }
        d.Tiles.Add(new DashTile("Active sessions", active.ToString("N0"), activeSub));
        if (snap is not null)
        {
            var linked = entries.Count(e => !e.Deleted && e.IsConnectable && snap.HealthFor(e).IsLinked);
            d.Tiles.Add(new DashTile("In Zabbix", linked.ToString("N0"), $"of {s.Entries} entries linked"));
        }
        else d.Tiles.Add(new DashTile("Favorites", FavoriteEntries(byId).Count().ToString(), "star an entry to pin it here"));

        // Protocols
        d.Protocols.Clear();
        var maxKind = s.ByKind.Count > 0 ? s.ByKind[0].Count : 1;
        foreach (var k in s.ByKind) d.Protocols.Add(new DashBar(null, EntryNode.KindTagFor(k.Kind), KindName(k.Kind), k.Count, (double)k.Count / maxKind));
        d.ProtocolTotal = $"{s.Entries} total";
        // Donut: the five biggest kinds, the rest folded into Other.
        var slices = s.ByKind.Take(s.ByKind.Count > 6 ? 5 : 6).Select((k, i) =>
            new ChartSlice(KindName(k.Kind), k.Count, i < DashChart.Series.Length ? DashChart.Series[i] : DashChart.Other)).ToList();
        var rest = s.ByKind.Skip(slices.Count).ToList();
        if (rest.Count > 0) slices.Add(new ChartSlice("Other", rest.Sum(k => k.Count), DashChart.Other, string.Join(", ", rest.Select(k => KindName(k.Kind)))));
        d.ProtocolSlices = slices;

        RefreshDashboardCharts(entries, now);

        // Folders
        d.Folders.Clear();
        var maxFolder = s.TopFolders.Count > 0 ? s.TopFolders[0].Count : 1;
        foreach (var f in s.TopFolders) d.Folders.Add(new DashBar(f.Id, "DIR", f.Name, f.Count, (double)f.Count / maxFolder));
        d.HasFolders = d.Folders.Count > 0;

        // Favorites: your own stars and the shared Favorite flag
        d.Favorites.Clear();
        foreach (var e in FavoriteEntries(byId).Take(12)) d.Favorites.Add(Row(e, FolderName(e)));
        d.HasFavorites = d.Favorites.Count > 0;

        // Recent
        d.Recent.Clear();
        foreach (var id in _settings.Recent)
        {
            if (!byId.TryGetValue(id, out var e)) continue;
            var side = _settings.RecentAt.TryGetValue(id, out var t) ? When(t, now) : "";
            d.Recent.Add(Row(e, side) with { Detail = FolderName(e) });
            if (d.Recent.Count == 8) break;
        }
        d.HasRecent = d.Recent.Count > 0;

        // Housekeeping
        var wasOpen = d.Checks.Where(c => c.IsOpen).Select(c => c.Label).ToHashSet();
        d.Checks.Clear();
        DashCheck Check(string label, bool warn, IEnumerable<Guid> ids)
        {
            var rows = ids.Where(byId.ContainsKey).Select(id => byId[id])
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => Row(e, FolderName(e))).ToList();
            return new DashCheck(label, warn, rows) { IsOpen = wasOpen.Contains(label) && rows.Count > 0 };
        }
        d.Checks.Add(Check("Linked credential is missing", true, s.Checks.MissingCredential));
        d.Checks.Add(Check("Same host in more than one entry", true, s.Checks.DuplicateHosts));
        d.Checks.Add(Check("No saved login (asks every time)", false, s.Checks.NoLogin));
        d.Checks.Add(Check("Entries with fallback hosts", false, s.Checks.WithFallbacks));
        d.Checks.Add(Check("Credentials no entry uses", false, s.Checks.UnusedCredentials));
        if (snap is not null)
            d.Checks.Add(Check("Not found in Zabbix", false,
                entries.Where(e => !e.Deleted && e.HasHosts && !snap.HealthFor(e).IsLinked).Select(e => e.Id)));

        RefreshDashboardZabbix(snap, entries);
        RefreshDashboardBackups(entries, now);
    }


    private static string TagName(string tag) => tag switch
    {
        "WEB" => "web", "TEL" => "Telnet", "SER" => "serial", "PVE" => "Proxmox", "PS" => "PowerShell", "WBX" => "WinBox", _ => tag,
    };

    /// <summary>Connections per day, the weekday/hour heat, entry growth and the Zabbix trend.</summary>
    private void RefreshDashboardCharts(List<Entry> entries, DateTimeOffset now)
    {
        var d = Dashboard;
        var log = _settings.ConnectLog;

        var days = DashboardCharts.PerDay(log, now);
        d.Connections = days.Select((x, i) =>
        {
            var label = i == days.Count - 1 ? "today" : i % 2 == 1 ? x.Day.ToString("ddd d") : null;
            var detail = x.Count == 0 ? "nothing opened" : string.Join(", ", x.ByKind.Select(k => $"{k.Value} {TagName(k.Key)}"));
            return new ChartColumn(x.Count, label, $"{x.Day:ddd d MMM} · {x.Count} session{(x.Count == 1 ? "" : "s")}\n{detail}", i == days.Count - 1);
        }).ToList();
        var total = days.Sum(x => x.Count);
        d.HasConnections = total > 0;
        d.ConnectionsMeta = $"last 14 days · {total} session{(total == 1 ? "" : "s")}";

        var heat = DashboardCharts.Heat(log, now);
        d.Heat = heat;
        d.HasHeat = heat.Cast<int>().Any(v => v > 0);

        var growth = DashboardCharts.Growth(entries, now);
        d.Growth = growth.Select((g, i) =>
        {
            var weeksAgo = growth.Count - 1 - i;
            var label = i == 0 || i == growth.Count - 1 || i % 5 == 0 ? g.End.ToLocalTime().ToString("MMM") : null;
            return new ChartPoint([g.Total], label, $"{(weeksAgo == 0 ? "This week" : weeksAgo == 1 ? "Last week" : $"{weeksAgo} weeks ago")} · {g.Total} entries");
        }).ToList();
        var added = growth[^1].Total - growth[0].Total;
        d.GrowthMeta = added > 0 ? $"last 6 months · +{added}" : "last 6 months";

        var trend = _settings.ZabbixTrend.Where(x => x.At >= now - TimeSpan.FromHours(24)).ToList();
        d.ZabbixTrend = trend.Select((x, i) =>
        {
            var hoursAgo = (now - x.At).TotalHours;
            var label = i == 0 ? $"{Math.Round(hoursAgo)}h ago" : i == trend.Count - 1 ? "now" : null;
            return new ChartPoint([x.Warning, x.High], label, $"{x.At.ToLocalTime():HH:mm}\n{x.High} high or worse, {x.Warning} warning");
        }).ToList();
        d.HasZabbixTrend = trend.Count >= 2;
    }

    /// <summary>
    /// Oxidized: the state donut (ok, stale, failing, never), devices that need a look, configs that changed this week,
    /// and every device with its last backup. Each device is paired with the entry that matches it, when there is one.
    /// </summary>
    private void RefreshDashboardBackups(List<Entry> entries, DateTimeOffset now)
    {
        var d = Dashboard;
        d.BackupsOn = OxidizedActive;
        d.BackupAttention.Clear();
        d.BackupChanges.Clear();
        d.BackupAll.Clear();
        if (OxidizedSnapshot is not { } ox)
        {
            d.BackupsHasData = false;
            d.BackupsWaiting = OxidizedStatusText.Length > 0 && !OxidizedStatusText.EndsWith("connecting…") ? OxidizedStatusText : "Waiting for Oxidized to answer…";
            d.BackupsMeta = "";
            d.BackupSlices = [];
            d.BackupDevices = d.BackupsOk = d.BackupsStale = d.BackupsFailing = d.BackupsNever = d.BackupsChanged = d.BackupsUnlinked = 0;
            return;
        }
        d.BackupsHasData = true;

        var entryFor = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => !e.Deleted && e.IsConnectable))
            if (ox.Matcher.Match(e) is { } m) entryFor.TryAdd(m.Node.FullName, e);

        var counts = ox.CountByState(now);
        var week = now - TimeSpan.FromDays(7);
        d.BackupDevices = ox.Nodes.Count;
        d.BackupsOk = counts["ok"];
        d.BackupsStale = counts["stale"];
        d.BackupsFailing = counts["fail"];
        d.BackupsNever = counts["never"];
        d.BackupsChanged = ox.Nodes.Count(n => n.LastChanged is { } c && c >= week);
        d.BackupsUnlinked = ox.Nodes.Count(n => !entryFor.ContainsKey(n.FullName));
        var failedFetch = OxidizedStatusText.Contains("(showing data from") ? " · unreachable, last data" : "";
        d.BackupsMeta = $"{ZabbixLessHost(OxidizedCfg.Url)} · {ox.FetchedAt.ToLocalTime():HH:mm}{failedFetch}";
        d.BackupSlices =
        [
            new ChartSlice("Backed up", d.BackupsOk, RemoteMan.App.Services.Themes.GetBrush("Ok")),
            new ChartSlice("Over a day old", d.BackupsStale, RemoteMan.App.Services.Themes.GetBrush("Muted"), "The last run worked, but Oxidized hasn't backed it up for more than a day"),
            new ChartSlice("Failing", d.BackupsFailing, RemoteMan.App.Services.Themes.GetBrush("Warn"), "The last run couldn't fetch the config (no connection, login or timeout)"),
            new ChartSlice("Not backed up yet", d.BackupsNever, DashChart.Other),
        ];

        DashBackup Row(RemoteMan.Core.Integrations.Oxidized.OxidizedNode n, string side, string state)
        {
            entryFor.TryGetValue(n.FullName, out var e);
            var where = e is null
                ? string.Join(" · ", new[] { "no entry", n.Group, n.Model }.Where(x => !string.IsNullOrEmpty(x)))
                : string.Join(" · ", new[] { FolderName(e), n.Model }.Where(x => !string.IsNullOrEmpty(x)));
            var hover = $"{n.FullName}{(n.Ip is { Length: > 0 } ip ? $" ({ip})" : "")}\n" +
                $"Last run: {n.StatusText}{(n.LastRun is { } r ? $", {r.ToLocalTime():d MMM yyyy HH:mm}" : "")}" +
                (n.LastChanged is { } c ? $"\nConfig last changed {c.ToLocalTime():d MMM yyyy HH:mm}" : "") +
                (e is null ? "\nDouble-click to open it in Oxidized" : "\nClick to select the entry, double-click for its config backups");
            return new DashBackup(e?.Id, n.FullName, e?.Name ?? n.Name, where, side, state, hover);
        }
        string LastRun(RemoteMan.Core.Integrations.Oxidized.OxidizedNode n) => n.LastRun is { } t ? RemoteMan.Core.Integrations.Oxidized.OxidizedSnapshot.Age(t, now) : "";

        // Needs a look: failing (newest first), then stale (oldest first), then never backed up.
        var attention = ox.Nodes.Select(n => (Node: n, State: n.StateAt(now))).Where(x => x.State != "ok")
            .OrderBy(x => x.State switch { "fail" => 0, "stale" => 1, _ => 2 })
            .ThenBy(x => x.State == "fail" ? -(x.Node.LastRun?.ToUnixTimeSeconds() ?? 0) : x.Node.LastRun?.ToUnixTimeSeconds() ?? 0)
            .ThenBy(x => x.Node.Name, StringComparer.OrdinalIgnoreCase).ToList();
        const int shown = 6;
        foreach (var (n, state) in attention.Take(shown))
        {
            var side = state switch
            {
                "fail" => n.StatusText + (n.LastRun is not null ? " · " + LastRun(n) : ""),
                "stale" => "last " + LastRun(n),
                _ => "never",
            };
            d.BackupAttention.Add(Row(n, side, state));
        }
        d.BackupAttentionMore = attention.Count > shown ? $"and {attention.Count - shown} more" : "";
        d.NoBackupAttention = attention.Count == 0;

        var changes = ox.Nodes.Where(n => n.LastChanged is { } c && c >= week).OrderByDescending(n => n.LastChanged).ToList();
        foreach (var n in changes.Take(shown))
            d.BackupChanges.Add(Row(n, When(n.LastChanged!.Value, now), "changed"));
        d.BackupChangesMore = changes.Count > shown ? $"and {changes.Count - shown} more" : "";
        d.NoBackupChanges = changes.Count == 0;

        // Every device: problems on top, then by name.
        foreach (var n in ox.Nodes.OrderBy(n => n.StateAt(now) switch { "fail" => 0, "stale" => 1, "never" => 2, _ => 3 })
                     .ThenBy(n => n.FullName, StringComparer.OrdinalIgnoreCase))
        {
            var state = n.StateAt(now);
            d.BackupAll.Add(Row(n, state == "never" ? "never" : state == "fail" ? $"{n.StatusText} · {LastRun(n)}" : LastRun(n), state));
        }
    }

    /// <summary>Dashboard: double-click on a device opens the Config backups tab of its entry, or its page in Oxidized.</summary>
    private void OpenDashboardBackup(DashBackup b)
    {
        if (b.EntryId is { } id && _store?.Get(id) is not null)
        {
            RevealEntry(id);
            _ = OpenConfigBackupsCommand.ExecuteAsync(null);
            return;
        }
        if (OxidizedSnapshot?.Nodes.FirstOrDefault(n => n.FullName == b.NodeName) is not { } node) return;
        try { Process.Start(new ProcessStartInfo(RemoteMan.Core.Integrations.Oxidized.OxidizedClient.NodePageUrl(OxidizedCfg.Url, node)) { UseShellExecute = true }); }
        catch (Exception ex) { Message = ex.Message; }
    }

    private void OpenOxidizedHome()
    {
        try { Process.Start(new ProcessStartInfo(RemoteMan.Core.Integrations.Oxidized.OxidizedClient.BaseUri(OxidizedCfg.Url).ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { Message = ex.Message; }
    }

    private static string ZabbixLessHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>One sample per half hour of open problems (warning, and high or worse), kept for a day.</summary>
    private void SampleZabbixTrend()
    {
        if (ZabbixSnapshot is not { } snap) return;
        var enabled = snap.Hosts.Where(h => h.Enabled && !h.InMaintenance).Select(h => h.HostId).ToHashSet();
        var open = snap.Problems.Where(p => enabled.Contains(p.HostId)).ToList();
        var high = open.Count(p => p.Severity >= ZabbixSeverity.High);
        var warn = open.Count(p => p.Severity >= ZabbixSeverity.Warning && p.Severity < ZabbixSeverity.High);
        if (DashboardCharts.Sample(_settings.ZabbixTrend, new ProblemSample(snap.FetchedAt, warn, high))) TrySaveSettings();
    }

    private IEnumerable<Entry> FavoriteEntries(Dictionary<Guid, Entry> byId) =>
        _settings.Favorites.Where(byId.ContainsKey).Select(id => byId[id]).Where(e => e.IsConnectable);

    private void RefreshDashboardZabbix(ZabbixSnapshot? snap, IReadOnlyCollection<Entry> entries)
    {
        var d = Dashboard;
        d.ZabbixOn = ZabbixActive;
        d.Problems.Clear();
        d.ZabbixHasData = snap is not null;
        if (snap is null)
        {
            d.ZabbixWaiting = ZabbixConnState == "bad" ? ZabbixStatusText : "Waiting for Zabbix to answer…";
            d.ZabbixMeta = "";
            return;
        }

        int ok = 0, warn = 0, high = 0, unreach = 0;
        foreach (var h in snap.Hosts.Where(h => h.Enabled))
        {
            switch (snap.HealthOf(h).State)
            {
                case ZabbixHealthState.Ok or ZabbixHealthState.Maintenance: ok++; break;
                case ZabbixHealthState.Warning: warn++; break;
                case ZabbixHealthState.High: high++; break;
                case ZabbixHealthState.Unreachable: unreach++; break;
            }
        }
        d.ZabbixOk = ok; d.ZabbixWarn = warn; d.ZabbixHigh = high; d.ZabbixUnreach = unreach;
        var stale = ZabbixConnState == "bad" ? " · unreachable, last data" : "";
        d.ZabbixMeta = $"{ZabbixRouteHost()} · {snap.FetchedAt.ToLocalTime():HH:mm:ss}{stale}";

        // Which entry each Zabbix host belongs to (first match wins).
        var entryFor = new Dictionary<string, Entry>();
        foreach (var e in entries.Where(e => !e.Deleted && e.IsConnectable))
            if (snap.HealthFor(e).Host is { } host) entryFor.TryAdd(host.HostId, e);

        var now = DateTimeOffset.UtcNow;
        var enabled = snap.Hosts.Where(h => h.Enabled && !h.InMaintenance).ToDictionary(h => h.HostId);
        var problems = snap.Problems.Where(p => enabled.ContainsKey(p.HostId))
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since).ToList();
        foreach (var p in problems.Take(6))
        {
            var tag = p.Severity >= ZabbixSeverity.High ? "high" : p.Severity >= ZabbixSeverity.Warning ? "warn" : "info";
            entryFor.TryGetValue(p.HostId, out var e);
            var who = e?.Name ?? enabled[p.HostId].DisplayName;
            var folder = e is null ? "no entry" : FolderName(e);
            var ack = p.Acknowledged ? (p.AcknowledgedBy is { } by ? $"acked by {by}" : "acknowledged") : "not acknowledged";
            d.Problems.Add(new DashProblem(tag, p.Name, string.Join(" · ", new[] { who, folder, ack }.Where(x => x.Length > 0)),
                ZabbixSnapshot.Age(p.Since, now).Replace(" ago", ""), e?.Id));
        }
        // Hosts Zabbix can't reach, with no problem of their own, after the problems.
        foreach (var h in enabled.Values.Where(h => snap.HealthOf(h).State == ZabbixHealthState.Unreachable && !problems.Any(p => p.HostId == h.HostId)).Take(Math.Max(0, 6 - d.Problems.Count)))
        {
            entryFor.TryGetValue(h.HostId, out var e);
            d.Problems.Add(new DashProblem("unreach", "Zabbix can't reach this host", e is null ? h.DisplayName : $"{e.Name} · {FolderName(e)}".TrimEnd(' ', '·'), "", e?.Id));
        }
        var hidden = problems.Count - Math.Min(problems.Count, 6);
        d.ProblemsMore = hidden > 0 ? $"and {hidden} more in Zabbix" : "";
        d.NoProblems = d.Problems.Count == 0;
    }

    private string ZabbixRouteHost()
    {
        try { return ZabbixClient.ApiEndpoint(ZabbixCfg.Url).Host; }
        catch (ZabbixApiException) { return ZabbixCfg.Url; }
    }
}
