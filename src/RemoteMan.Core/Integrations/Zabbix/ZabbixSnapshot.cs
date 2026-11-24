using RemoteMan.Core.Model;

namespace RemoteMan.Core.Integrations.Zabbix;

/// <summary>One poll's worth of Zabbix data, and the health of each entry derived from it.</summary>
public sealed class ZabbixSnapshot
{
    private readonly ZabbixMatcher _matcher;
    private readonly ILookup<string, ZabbixProblem> _problemsByHost;

    public ZabbixSnapshot(IReadOnlyList<ZabbixHost> hosts, IReadOnlyList<ZabbixProblem> problems, DateTimeOffset fetchedAt,
        ZabbixMatchOptions? options = null)
    {
        Hosts = hosts;
        // Suppressed problems never count: not on entry badges, counts, the dashboard or notifications.
        Problems = problems.Where(p => !p.Suppressed).ToList();
        FetchedAt = fetchedAt;
        _matcher = new ZabbixMatcher(hosts, options);
        _problemsByHost = Problems.ToLookup(p => p.HostId);
    }

    public IReadOnlyList<ZabbixHost> Hosts { get; }
    public IReadOnlyList<ZabbixProblem> Problems { get; }
    public DateTimeOffset FetchedAt { get; }
    public ZabbixMatcher Matcher => _matcher;

    public int ProblemHostCount => Hosts.Count(h => h.Enabled && HealthOf(h).IsBad);

    public ZabbixHealth HealthFor(Entry entry)
    {
        var m = _matcher.Match(entry);
        return m is null ? ZabbixHealth.Unlinked : HealthOf(m.Host, m.Reason);
    }

    public ZabbixHealth HealthOf(ZabbixHost host, string? reason = null)
    {
        var problems = _problemsByHost[host.HostId]
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since).ToList();
        if (!host.Enabled) return new ZabbixHealth(ZabbixHealthState.None, host, reason, problems);

        var worst = problems.Count > 0 ? problems.Max(p => p.Severity) : ZabbixSeverity.NotClassified;
        var state =
            host.InMaintenance ? ZabbixHealthState.Maintenance :
            worst >= ZabbixSeverity.High ? ZabbixHealthState.High :
            worst >= ZabbixSeverity.Warning ? ZabbixHealthState.Warning :
            host.Unreachable ? ZabbixHealthState.Unreachable :
            ZabbixHealthState.Ok;
        return new ZabbixHealth(state, host, reason, problems);
    }

    /// <summary>"14m ago", "2h 10m ago", "3d ago".</summary>
    public static string Age(DateTimeOffset since, DateTimeOffset now)
    {
        var d = now - since;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1)) return d.Minutes == 0 ? $"{(int)d.TotalHours}h ago" : $"{(int)d.TotalHours}h {d.Minutes}m ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
