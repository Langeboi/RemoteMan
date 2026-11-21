using System.Diagnostics;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
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
        var url = _settings.Zabbix.Url;
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
        var open = Sessions.Where(x => !x.IsPinned).ToList();
        var openKinds = string.Join(", ", open.GroupBy(x => x.KindTag).Select(g => $"{g.Count()} {g.Key}"));
        d.Tiles.Add(new DashTile("Open tabs", open.Count.ToString(), open.Count == 0 ? "nothing connected" : openKinds));
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

    /// <summary>Oxidized: device counts, failing backups and configs that changed lately, with the entry each belongs to.</summary>
    private void RefreshDashboardBackups(List<Entry> entries, DateTimeOffset now)
    {
        var d = Dashboard;
        d.BackupsOn = OxidizedActive;
        d.BackupRows.Clear();
        if (OxidizedSnapshot is not { } ox)
        {
            d.BackupsMeta = OxidizedActive ? "waiting for Oxidized…" : "";
            d.BackupDevices = d.BackupsToday = d.BackupsFailing = d.BackupsChanged = 0;
            d.NoBackupRows = true;
            return;
        }
        var entryFor = new Dictionary<string, Entry>();
        foreach (var e in entries.Where(e => !e.Deleted && e.IsConnectable))
            if (ox.Matcher.Match(e) is { } m) entryFor.TryAdd(m.Node.FullName, e);

        d.BackupDevices = ox.Nodes.Count;
        d.BackupsToday = ox.Nodes.Count(n => n.IsOk && n.LastRun is { } t && t >= now - TimeSpan.FromHours(24));
        d.BackupsFailing = ox.FailingCount;
        d.BackupsChanged = ox.Nodes.Count(n => n.LastChanged is { } c && c >= now - TimeSpan.FromDays(7));
        d.BackupsMeta = $"{ZabbixLessHost(_settings.Oxidized.Url)} · {ox.FetchedAt.ToLocalTime():HH:mm}";

        DashEntry Row(RemoteMan.Core.Integrations.Oxidized.OxidizedNode n, string side, string? health)
        {
            entryFor.TryGetValue(n.FullName, out var e);
            return new DashEntry(e?.Id ?? Guid.Empty, e?.Name ?? n.Name, e is null ? "DIR" : EntryNode.KindTagFor(e.Kind),
                e is null ? "no entry" : FolderName(e), side, health);
        }
        foreach (var n in ox.Nodes.Where(n => n.IsFailing).OrderByDescending(n => n.LastRun).Take(5))
            d.BackupRows.Add(Row(n, $"{n.StatusText}{(n.LastRun is { } t ? " · " + When(t, now) : "")}", "high"));
        foreach (var n in ox.Nodes.Where(n => !n.IsFailing && n.LastChanged is { } c && c >= now - TimeSpan.FromDays(7))
                     .OrderByDescending(n => n.LastChanged).Take(Math.Max(0, 7 - d.BackupRows.Count)))
            d.BackupRows.Add(Row(n, $"changed {When(n.LastChanged!.Value, now)}", null));
        d.NoBackupRows = d.BackupRows.Count == 0;
    }

    private static string ZabbixLessHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>One sample per half hour of open problems (warning, and high or worse), kept for a day.</summary>
    private void SampleZabbixTrend()
    {
        if (ZabbixSnapshot is not { } snap) return;
        var enabled = snap.Hosts.Where(h => h.Enabled && !h.InMaintenance).Select(h => h.HostId).ToHashSet();
        var open = snap.Problems.Where(p => !p.Suppressed && enabled.Contains(p.HostId)).ToList();
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
        var problems = snap.Problems.Where(p => !p.Suppressed && enabled.ContainsKey(p.HostId))
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
        try { return ZabbixClient.ApiEndpoint(_settings.Zabbix.Url).Host; }
        catch (ZabbixApiException) { return _settings.Zabbix.Url; }
    }
}
