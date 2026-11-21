namespace RemoteMan.Core.Model;

/// <summary>One session opened, as kept in the local connection log (per user, never shared).</summary>
public sealed record ConnectEvent(DateTimeOffset At, string Kind);

/// <summary>Open Zabbix problems at one moment, for the 24 hour trend.</summary>
public sealed record ProblemSample(DateTimeOffset At, int Warning, int High);

/// <summary>Series for the dashboard charts. Pure functions of the inputs, all in local time.</summary>
public static class DashboardCharts
{
    /// <summary>Sessions per day, oldest first, ending today. Each day also counts sessions per kind.</summary>
    public static IReadOnlyList<(DateTime Day, int Count, IReadOnlyDictionary<string, int> ByKind)> PerDay(
        IEnumerable<ConnectEvent> log, DateTimeOffset now, int days = 14)
    {
        var today = now.ToLocalTime().Date;
        var first = today.AddDays(-(days - 1));
        var groups = log.Select(e => (Day: e.At.ToLocalTime().Date, e.Kind)).Where(e => e.Day >= first && e.Day <= today)
            .ToLookup(e => e.Day);
        return Enumerable.Range(0, days).Select(i =>
        {
            var d = first.AddDays(i);
            var byKind = groups[d].GroupBy(e => e.Kind).OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());
            return (d, groups[d].Count(), (IReadOnlyDictionary<string, int>)byKind);
        }).ToList();
    }

    /// <summary>Sessions by weekday (0 = Monday) and hour over the last few weeks.</summary>
    public static int[,] Heat(IEnumerable<ConnectEvent> log, DateTimeOffset now, int weeks = 8)
    {
        var grid = new int[7, 24];
        var from = now - TimeSpan.FromDays(7 * weeks);
        foreach (var e in log.Where(e => e.At >= from && e.At <= now))
        {
            var t = e.At.ToLocalTime();
            grid[((int)t.DayOfWeek + 6) % 7, t.Hour]++;
        }
        return grid;
    }

    /// <summary>
    /// Entries (connectable, not deleted) that existed at the end of each week, oldest first, ending now.
    /// Entries without a creation time (made before it was recorded) count from the start.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset End, int Total)> Growth(IEnumerable<Entry> entries, DateTimeOffset now, int weeks = 26)
    {
        var created = entries.Where(e => !e.Deleted && e.IsConnectable).Select(e => e.CreatedAt).ToList();
        return Enumerable.Range(0, weeks).Select(i =>
        {
            var end = now - TimeSpan.FromDays(7 * (weeks - 1 - i));
            return (end, created.Count(c => c is null || c <= end));
        }).ToList();
    }

    /// <summary>Adds a session to the log and drops what is older than the charts look back (plus a hard cap).</summary>
    public static void Record(List<ConnectEvent> log, ConnectEvent e, int keepDays = 60, int max = 3000)
    {
        log.Add(e);
        var cutoff = e.At - TimeSpan.FromDays(keepDays);
        log.RemoveAll(x => x.At < cutoff);
        if (log.Count > max) log.RemoveRange(0, log.Count - max);
    }

    /// <summary>
    /// Keeps one sample per half hour for the last 24 hours: a new poll in the same half hour replaces the previous one.
    /// Returns true when a new half hour started (worth saving to disk).
    /// </summary>
    public static bool Sample(List<ProblemSample> samples, ProblemSample s)
    {
        static long Slot(DateTimeOffset t) => t.ToUnixTimeSeconds() / 1800;
        var fresh = samples.Count == 0 || Slot(samples[^1].At) != Slot(s.At);
        if (!fresh) samples[^1] = s; else samples.Add(s);
        var cutoff = s.At - TimeSpan.FromHours(24.5);
        samples.RemoveAll(x => x.At < cutoff);
        return fresh;
    }
}
