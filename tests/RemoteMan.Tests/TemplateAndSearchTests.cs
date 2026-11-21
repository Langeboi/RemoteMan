using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class TemplateAndSearchTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"remoteman-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task TemplateRoundTripsAndMakesEntries()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var folder = await store.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "Firewalls" });
        var cred = await store.SaveAsync(new Entry { Kind = EntryKind.Credential, Name = "NET admin", Credential = new CredentialData { UserName = "admin" } });

        var t = EntryTemplates.Starters().Single(x => x.Name == "FortiGate");
        var proto = EntryTemplates.Prototype(t);
        proto.Web!.Url = "https://{host}:8443";
        proto.Login = LoginSource.Saved;
        proto.CredentialId = cred.Id;
        EntryTemplates.SetPrototype(t, proto);
        t.Template!.FolderId = folder.Id;
        var saved = await store.SaveAsync(t);

        await using var other = new EntryStore(new SqliteDataSource(_db), "bob", "pc2");
        await other.OpenAsync();
        var loaded = other.Get(saved.Id)!;
        Assert.Equal(EntryKind.Template, loaded.Kind);
        Assert.True(loaded.IsHidden);
        Assert.Equal(EntryKind.Web, loaded.Template!.Kind);
        Assert.Equal(EntryKind.Ssh, loaded.Template.CompanionKind);

        var made = EntryTemplates.NewEntry(loaded, fallbackParent: null, folderExists: id => other.Get(id) is not null);
        Assert.Equal(EntryKind.Web, made.Kind);
        Assert.Equal("", made.Name);
        Assert.Equal(folder.Id, made.ParentId);
        Assert.Equal(0, made.Revision);
        Assert.NotEqual(saved.Id, made.Id);
        Assert.Equal("https://{host}:8443", made.Web!.Url);
        Assert.Equal("fortigate", made.Icon);
        Assert.Equal(cred.Id, made.CredentialId);
        Assert.Equal(["Model", "Serial number"], made.Fields!.Select(f => f.Name));

        made.Name = "fw02";
        made.Web.Url = "https://10.0.0.9:8443";
        var cli = EntryTemplates.Companion(loaded, made)!;
        Assert.Equal("fw02 CLI", cli.Name);
        Assert.Equal(EntryKind.Ssh, cli.Kind);
        Assert.Equal(new HostEndpoint("10.0.0.9", 22), Assert.Single(cli.Endpoints));
        Assert.Equal(LoginSource.Saved, cli.Login);
        Assert.Equal(cred.Id, cli.CredentialId);
        Assert.Equal(folder.Id, cli.ParentId);
    }

    [Fact]
    public void TemplateFromEntryKeepsSettingsButNotTheHost()
    {
        var src = new Entry
        {
            Kind = EntryKind.Ssh, Name = "leaf1", ParentId = Guid.NewGuid(), Tags = "cisco",
            Endpoints = [new HostEndpoint("10.0.30.11", 2222), new HostEndpoint("10.0.31.11", 2222)],
            Ssh = new SshSettings { AllowLegacyAlgorithms = true, KexAlgorithms = "diffie-hellman-group14-sha1" },
            ZabbixHostId = "10084",
        };
        var t = EntryTemplates.FromEntry(src, "Leaf switch");
        var made = EntryTemplates.NewEntry(t, null);
        Assert.Equal(src.ParentId, made.ParentId);
        Assert.Equal(new HostEndpoint("", 2222), Assert.Single(made.Endpoints));
        Assert.Equal("diffie-hellman-group14-sha1", made.Ssh!.KexAlgorithms);
        Assert.Equal("cisco", made.Tags);
        Assert.Null(made.ZabbixHostId);
        Assert.Equal("https://{host}:8443/ng", EntryTemplates.WithHostPlaceholder("https://10.0.0.1:8443/ng"));
    }

    [Fact]
    public void SearchFindsNamesHostsTagsFieldsAndDescriptions()
    {
        var folder = new Entry { Kind = EntryKind.Folder, Name = "Core network" };
        var fw = new Entry
        {
            Kind = EntryKind.Web, Name = "fw01", ParentId = folder.Id, Tags = "firewall", Web = new WebSettings { Url = "https://10.0.0.1:8443" },
            Endpoints = [new HostEndpoint("10.0.1.1", 8443)],
            Fields = [new CustomField { Name = "Model", Value = "FortiGate 60F" }, new CustomField { Name = "Admin PIN", Value = "secret-gate", Secret = true }],
        };
        var sw = new Entry
        {
            Kind = EntryKind.Ssh, Name = "core-sw01", ParentId = folder.Id, Endpoints = [new HostEndpoint("10.0.0.2", 22)],
            Description = "Core switch.\nTe1/1/4 goes to the FortiGate firewall.",
        };
        var tpl = EntryTemplates.Starters()[0];
        var snippet = new Entry { Kind = EntryKind.Snippet, Name = "fortigate backup", Snippet = new SnippetData { Command = "x" } };
        var all = new[] { folder, fw, sw, tpl, snippet };

        var hits = GlobalSearch.Find(all, "fortigate");
        Assert.Equal([fw, sw], hits.Select(h => h.Entry));
        Assert.Equal("Field: Model", hits[0].Label);
        Assert.Equal("FortiGate", hits[0].Context.Substring(hits[0].MatchStart, hits[0].MatchLength));
        Assert.Equal(SearchField.Description, hits[1].Field);
        Assert.Equal("Core network", hits[1].Path);

        Assert.Empty(GlobalSearch.Find(all, "secret-gate"));
        Assert.Equal("Fallback", Assert.Single(GlobalSearch.Find(all, "10.0.1.1")).Label);
        Assert.Equal(sw, Assert.Single(GlobalSearch.Find(all, "host:10.0.0.2")).Entry);
        Assert.Equal(fw, Assert.Single(GlobalSearch.Find(all, "tag:fire")).Entry);
        Assert.Equal(fw, Assert.Single(GlobalSearch.Find(all, "field:admin pin")).Entry);
        Assert.Equal([folder, sw, fw], GlobalSearch.Find(all, "in:core").Select(h => h.Entry).OrderBy(e => e.Kind != EntryKind.Folder).ThenBy(e => e.Name));
        Assert.Equal(sw, Assert.Single(GlobalSearch.Find(all, "kind:ssh")).Entry);
        Assert.Equal(sw, GlobalSearch.Find(all, "core")[0].Entry);
    }
}
