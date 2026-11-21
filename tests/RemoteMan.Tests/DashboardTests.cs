using RemoteMan.Core.Model;
using Xunit;

namespace RemoteMan.Tests;

public class DashboardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private static Entry Folder(string name, Entry? parent = null, LoginSource login = LoginSource.None, Guid? cred = null) =>
        new() { Kind = EntryKind.Folder, Name = name, ParentId = parent?.Id, Login = login, CredentialId = cred };

    private static Entry Conn(EntryKind kind, string name, Entry? parent, params string[] hosts)
    {
        var e = new Entry { Kind = kind, Name = name, ParentId = parent?.Id, Login = LoginSource.Inherited };
        foreach (var h in hosts) e.Endpoints.Add(new HostEndpoint(h));
        return e;
    }

    [Fact]
    public void Counts_kinds_folders_and_depth()
    {
        var key = new Entry { Kind = EntryKind.Credential, Name = "admin" };
        var net = Folder("Network", login: LoginSource.Saved, cred: key.Id);
        var sw = Folder("Switches", net, LoginSource.Inherited);
        var lab = Folder("Lab");
        var entries = new List<Entry>
        {
            key, net, sw, lab,
            Conn(EntryKind.Ssh, "sw1", sw, "10.0.0.2"),
            Conn(EntryKind.Ssh, "sw2", sw, "10.0.0.3", "10.0.1.3"),
            Conn(EntryKind.Web, "fw", net, "10.0.0.1"),
            Conn(EntryKind.Rdp, "dc01", lab, "10.0.10.10"),
            new() { Kind = EntryKind.Snippet, Name = "show run" },
            new() { Kind = EntryKind.Ssh, Name = "gone", Deleted = true },
        };
        entries[^3].CreatedAt = Now.AddDays(-2);

        var s = DashboardStats.Compute(entries, Now);

        Assert.Equal(4, s.Entries);
        Assert.Equal(3, s.Folders);
        Assert.Equal(1, s.Credentials);
        Assert.Equal(1, s.AddedThisWeek);
        Assert.Equal(2, s.MaxFolderDepth);
        Assert.Equal(EntryKind.Ssh, s.ByKind[0].Kind);
        Assert.Equal(2, s.ByKind[0].Count);
        Assert.Equal(("Network", 3), (s.TopFolders[0].Name, s.TopFolders[0].Count));
        Assert.Equal(("Lab", 1), (s.TopFolders[1].Name, s.TopFolders[1].Count));
        // Three inherit the folder's saved credential; dc01 inherits nothing.
        Assert.Equal(3, s.UsingSavedCredential);
        Assert.Single(s.Checks.NoLogin);
        Assert.Single(s.Checks.WithFallbacks);
        Assert.Empty(s.Checks.UnusedCredentials);
        Assert.Empty(s.Checks.MissingCredential);
    }

    [Fact]
    public void Flags_duplicates_missing_and_unused_credentials()
    {
        var spare = new Entry { Kind = EntryKind.Credential, Name = "spare" };
        var a = Conn(EntryKind.Ssh, "a", null, "Host1");
        var b = Conn(EntryKind.Ssh, "b", null, "host1");
        b.Endpoints[0] = new HostEndpoint("host1", 22);
        var r = Conn(EntryKind.Rdp, "same host, other kind", null, "host1");
        var broken = Conn(EntryKind.Ssh, "broken", null, "x");
        broken.Login = LoginSource.Saved;
        broken.CredentialId = Guid.NewGuid();

        var s = DashboardStats.Compute([spare, a, b, r, broken], Now);

        Assert.Equal(new[] { a.Id, b.Id }.Order(), s.Checks.DuplicateHosts.Order());
        Assert.Equal([broken.Id], s.Checks.MissingCredential);
        Assert.Equal([spare.Id], s.Checks.UnusedCredentials);
    }

    [Fact]
    public void Survives_a_parent_loop()
    {
        var f1 = Folder("f1");
        var f2 = Folder("f2", f1);
        f1.ParentId = f2.Id;
        var e = Conn(EntryKind.Ssh, "e", f2, "h");
        var s = DashboardStats.Compute([f1, f2, e], Now);
        Assert.Equal(1, s.Entries);
    }
}

public class DashboardChartTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero).ToLocalTime();

    [Fact]
    public void Per_day_counts_end_today_and_split_by_kind()
    {
        var log = new List<ConnectEvent>
        {
            new(Now, "SSH"), new(Now.AddMinutes(-5), "SSH"), new(Now.AddMinutes(-10), "RDP"),
            new(Now.AddDays(-1), "WEB"), new(Now.AddDays(-30), "SSH"),
        };
        var days = DashboardCharts.PerDay(log, Now);
        Assert.Equal(14, days.Count);
        Assert.Equal(Now.Date, days[^1].Day);
        Assert.Equal(3, days[^1].Count);
        Assert.Equal(2, days[^1].ByKind["SSH"]);
        Assert.Equal(1, days[^2].Count);
        Assert.Equal(4, days.Sum(d => d.Count));
    }

    [Fact]
    public void Heat_puts_monday_first()
    {
        var monday = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero).ToLocalTime();
        var grid = DashboardCharts.Heat([new ConnectEvent(monday, "SSH")], Now);
        Assert.Equal(1, grid[((int)monday.DayOfWeek + 6) % 7, monday.Hour]);
    }

    [Fact]
    public void Growth_counts_entries_without_a_date_from_the_start()
    {
        var old = new Entry { Kind = EntryKind.Ssh, Name = "old" };
        var mid = new Entry { Kind = EntryKind.Rdp, Name = "mid", CreatedAt = Now.AddDays(-30) };
        var folder = new Entry { Kind = EntryKind.Folder, Name = "f" };
        var g = DashboardCharts.Growth([old, mid, folder], Now);
        Assert.Equal(26, g.Count);
        Assert.Equal(1, g[0].Total);
        Assert.Equal(2, g[^1].Total);
    }

    [Fact]
    public void Samples_keep_one_per_half_hour_for_a_day()
    {
        var list = new List<ProblemSample>();
        Assert.True(DashboardCharts.Sample(list, new(Now.AddHours(-30), 1, 0)));
        Assert.True(DashboardCharts.Sample(list, new(new DateTimeOffset(2026, 11, 21, 11, 31, 0, TimeSpan.Zero), 2, 0)));
        Assert.False(DashboardCharts.Sample(list, new(new DateTimeOffset(2026, 11, 21, 11, 50, 0, TimeSpan.Zero), 3, 1)));
        Assert.Single(list);
        Assert.Equal(3, list[0].Warning);
    }

    [Fact]
    public void Log_drops_old_sessions()
    {
        var log = new List<ConnectEvent> { new(Now.AddDays(-90), "SSH") };
        DashboardCharts.Record(log, new(Now, "RDP"));
        Assert.Single(log);
    }
}
