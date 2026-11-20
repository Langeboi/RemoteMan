using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

/// <summary>Working together: history and restore, merging concurrent edits, sessions in presence, reservations.</summary>
public class TeamTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"remoteman-team-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private async Task<EntryStore> Open(string user, string machine = "pc")
    {
        var s = new EntryStore(new SqliteDataSource(_db), user, machine);
        await s.OpenAsync();
        return s;
    }

    private static Entry Rdp(string name, string host = "10.0.30.10") => new()
    {
        Kind = EntryKind.Rdp, Name = name, Rdp = new RdpSettings(),
        Endpoints = { new HostEndpoint(host, 3389) },
    };

    [Fact]
    public async Task EverySaveIsKeptWithWhoAndWhatChanged()
    {
        await using var a = await Open("alice");
        var e = await a.SaveAsync(Rdp("dc01"));
        var edit = EntryStore.Clone(e);
        edit.Endpoints = [new HostEndpoint("10.0.30.11", 3389)];
        edit.Rdp!.AdminSession = true;
        await a.SaveAsync(edit);

        var history = await a.GetHistoryAsync(e.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(HistoryAction.Edited, history[0].Action);
        Assert.Equal(HistoryAction.Added, history[1].Action);
        Assert.Equal(["Endpoints", "Rdp.AdminSession"], history[0].Fields.OrderBy(x => x));
        Assert.Equal("alice", history[0].Snapshot.UpdatedBy);

        var changes = EntryDiff.Diff(history[1].Snapshot, history[0].Snapshot);
        var hosts = Assert.Single(changes, c => c.Path == "Endpoints");
        Assert.Equal("Hosts", hosts.Label);
        Assert.Equal("10.0.30.10:3389", hosts.Before);
        Assert.Equal("10.0.30.11:3389", hosts.After);
        Assert.Equal("RDP: admin session", changes.Single(c => c.Path == "Rdp.AdminSession").Label);
    }

    [Fact]
    public async Task PasswordsShowAsChangedNeverAsValues()
    {
        var before = new Entry { Kind = EntryKind.Credential, Name = "admin", Credential = new() { UserName = "a", ProtectedPassword = "D1secret-old" } };
        var after = EntryStore.Clone(before);
        after.Credential!.ProtectedPassword = "D1secret-new";
        var c = Assert.Single(EntryDiff.Diff(before, after));
        Assert.Equal("Password", c.Label);
        Assert.True(c.IsSecret);
        Assert.DoesNotContain("secret", c.Before + c.After);
    }

    [Fact]
    public async Task RestoringAnOldVersionMakesANewSave()
    {
        await using var a = await Open("alice");
        var v1 = await a.SaveAsync(Rdp("dc01"));
        var v2 = EntryStore.Clone(v1); v2.Name = "renamed";
        await a.SaveAsync(v2);

        var old = (await a.GetHistoryAsync(v1.Id)).Last().Snapshot;
        await a.RestoreAsync(old);
        Assert.Equal("dc01", a.Get(v1.Id)!.Name);
        Assert.Equal(3, (await a.GetHistoryAsync(v1.Id)).Count);
    }

    [Fact]
    public async Task DeletedFolderComesBackWithWhatWasInIt()
    {
        await using var a = await Open("alice");
        var folder = await a.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "Lab 3" });
        var child = Rdp("dc01"); child.ParentId = folder.Id;
        await a.SaveAsync(child);
        var keep = await a.SaveAsync(Rdp("elsewhere"));
        await a.DeleteAsync(folder.Id);
        Assert.Single(a.Entries);

        // A fresh client, which never saw the folder, restores it from the activity list.
        await using var b = await Open("bob", "pc2");
        var deleted = (await b.GetActivityAsync()).First(h => h.Action == HistoryAction.Deleted && h.Snapshot.Id == folder.Id);
        Assert.Equal(2, await b.RestoreAsync(deleted.Snapshot));
        Assert.Equal(3, b.Entries.Count());
        Assert.Equal(folder.Id, b.Get(child.Id)!.ParentId);
        Assert.Equal(HistoryAction.Restored, (await b.GetHistoryAsync(folder.Id))[0].Action);
    }

    [Fact]
    public async Task ConcurrentEditsToDifferentFieldsMergeByThemselves()
    {
        await using var a = await Open("alice");
        await using var b = await Open("bob", "pc2");
        var e = await a.SaveAsync(Rdp("dc01"));
        await b.PullAsync();

        var bobBase = EntryStore.Clone(b.Get(e.Id)!);
        var bobEdit = EntryStore.Clone(bobBase); bobEdit.Description = "Lab 3 domain controller";

        var aliceEdit = EntryStore.Clone(e); aliceEdit.Endpoints = [new HostEndpoint("10.0.30.10", 3390)];
        await a.SaveAsync(aliceEdit);

        await Assert.ThrowsAsync<ConcurrencyException>(() => b.SaveAsync(bobEdit));
        await b.PullAsync();
        var merge = EntryDiff.Merge(bobBase, bobEdit, b.Get(e.Id)!);
        Assert.Empty(merge.Conflicts);
        Assert.Single(merge.KeptTheirs);
        Assert.Single(merge.KeptMine);
        var saved = await b.SaveAsync(merge.Merged);
        Assert.Equal("Lab 3 domain controller", saved.Description);
        Assert.Equal(3390, saved.Endpoints[0].Port);
    }

    [Fact]
    public void SameFieldChangedTwiceIsAConflictThatKeepsYoursUntilResolved()
    {
        var baseE = Rdp("dc01");
        var mine = EntryStore.Clone(baseE); mine.Endpoints = [new HostEndpoint("10.0.30.12", 3389)]; mine.Name = "DC01";
        var theirs = EntryStore.Clone(baseE); theirs.Endpoints = [new HostEndpoint("10.0.30.11", 3389)]; theirs.Rdp!.SmartSizing = false; theirs.Revision = 7;

        var m = EntryDiff.Merge(baseE, mine, theirs);
        var c = Assert.Single(m.Conflicts);
        Assert.Equal("Endpoints", c.Path);
        Assert.Equal("10.0.30.12:3389", c.Mine);
        Assert.Equal("10.0.30.11:3389", c.Theirs);
        Assert.Equal("10.0.30.12", m.Merged.Endpoints[0].Host);
        Assert.Equal("DC01", m.Merged.Name);
        Assert.False(m.Merged.Rdp!.SmartSizing);
        Assert.Equal(7, m.Merged.Revision);

        var resolved = m.Resolve(["Endpoints"]);
        Assert.Equal("10.0.30.11", resolved.Endpoints[0].Host);
        Assert.Equal("DC01", resolved.Name);
        Assert.False(resolved.Rdp!.SmartSizing);
    }

    [Fact]
    public async Task EveryOpenConnectionShowsNotOnlyTheTabInFront()
    {
        await using var a = await Open("alice");
        await using var b = await Open("bob", "pc2");
        var dc = await a.SaveAsync(Rdp("dc01"));
        var pve = await a.SaveAsync(Rdp("pve01"));

        await a.SetMySessionsAsync([new PresenceSession(dc.Id, DateTimeOffset.UtcNow.AddMinutes(-70)), new PresenceSession(pve.Id, DateTimeOffset.UtcNow)]);
        await a.SetMyPresenceAsync(pve.Id, PresenceState.Connected);
        await b.SetMyPresenceAsync(null);

        var onDc = Assert.Single(b.ConnectedTo(dc.Id));
        Assert.Equal("alice", onDc.User);
        Assert.Equal(PresenceState.Connected, onDc.State);
        Assert.True(onDc.ConnectedSince < DateTimeOffset.UtcNow.AddMinutes(-60));
        Assert.Single(b.ConnectedTo(pve.Id));

        // Closing every tab: nothing connected, even with nothing selected.
        await a.SetMyPresenceAsync(null);
        await a.SetMySessionsAsync([]);
        await b.SetMyPresenceAsync(null);
        Assert.Empty(b.ConnectedTo(dc.Id));
    }

    [Fact]
    public async Task ReservationSyncsAndOnlyWarnsOthers()
    {
        await using var a = await Open("alice");
        await using var b = await Open("bob", "pc2");
        var e = await a.SaveAsync(Rdp("fw-edge"));
        await a.ReserveAsync(e.Id, DateTimeOffset.UtcNow.AddHours(1), "  Upgrading firmware ");
        await b.PullAsync();

        var r = b.ReservedByOthers(b.Get(e.Id)!);
        Assert.NotNull(r);
        Assert.Equal("alice", r!.By);
        Assert.Equal("Upgrading firmware", r.Note);
        Assert.Null(a.ReservedByOthers(a.Get(e.Id)!));

        await a.ReleaseAsync(e.Id);
        await b.PullAsync();
        Assert.Null(b.ReservedByOthers(b.Get(e.Id)!));
        Assert.Equal(["Reservation"], (await a.GetHistoryAsync(e.Id))[0].Fields);
    }

    [Fact]
    public void InitialsReadWell()
    {
        Assert.Equal("ML", Presence.InitialsOf("mikkel.lange"));
        Assert.Equal("SA", Presence.InitialsOf("sara"));
        Assert.Equal("LA", Presence.InitialsOf("LAB\\lange"));
    }
}
