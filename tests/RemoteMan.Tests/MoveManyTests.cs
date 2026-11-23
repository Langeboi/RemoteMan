using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

/// <summary>Moving several picked entries at once (Ctrl+click / Shift+click in the tree).</summary>
public class MoveManyTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"remoteman-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static Entry Folder(string name, Guid? parent = null) => new() { Kind = EntryKind.Folder, Name = name, ParentId = parent };
    private static Entry Ssh(string name, Guid? parent = null) => new() { Kind = EntryKind.Ssh, Name = name, ParentId = parent, Ssh = new SshSettings() };

    [Fact]
    public async Task LeavesOutEntriesInsideAPickedFolderAndOnesAlreadyThere()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var target = await store.SaveAsync(Folder("Target"));
        var lab = await store.SaveAsync(Folder("Lab"));
        var inLab = await store.SaveAsync(Ssh("in-lab", lab.Id));
        var loose = await store.SaveAsync(Ssh("loose"));
        var already = await store.SaveAsync(Ssh("already", target.Id));

        var moving = store.MovableForMove([lab.Id, inLab.Id, loose.Id, already.Id], target.Id).Select(e => e.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Lab", "loose" }, moving);
    }

    [Fact]
    public async Task NeverMovesAFolderIntoItselfOrBelowIt()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var lab = await store.SaveAsync(Folder("Lab"));
        var sub = await store.SaveAsync(Folder("Sub", lab.Id));
        var box = await store.SaveAsync(Ssh("box"));

        // Lab can't go into its own subfolder; box can.
        var moving = store.MovableForMove([lab.Id, box.Id], sub.Id);
        Assert.Equal("box", Assert.Single(moving).Name);
        Assert.False(store.CanMove(lab.Id, lab.Id));
        Assert.False(store.CanMove(box.Id, box.Id));
        Assert.Empty(store.MovableForMove([lab.Id], sub.Id));
    }

    [Fact]
    public async Task SavingTheMovableSetPutsThemAllInTheFolder()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var target = await store.SaveAsync(Folder("Target"));
        var a = await store.SaveAsync(Ssh("a"));
        var b = await store.SaveAsync(Ssh("b"));
        var c = await store.SaveAsync(Ssh("c"));

        foreach (var e in store.MovableForMove([a.Id, b.Id, c.Id], target.Id))
        {
            var copy = EntryStore.Clone(e);
            copy.ParentId = target.Id;
            await store.SaveAsync(copy);
        }

        Assert.All(new[] { a.Id, b.Id, c.Id }, id => Assert.Equal(target.Id, store.Get(id)!.ParentId));
        Assert.Empty(store.MovableForMove([a.Id, b.Id, c.Id], target.Id));
    }
}
