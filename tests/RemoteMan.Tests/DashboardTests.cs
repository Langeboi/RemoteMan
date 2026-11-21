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
