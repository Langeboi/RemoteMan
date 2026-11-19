using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class StoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"remoteman-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static Entry Ssh(string name) => new()
    {
        Kind = EntryKind.Ssh, Name = name,
        Endpoints = { new HostEndpoint("10.0.0.1", 22), new HostEndpoint("backup.lab", 2222) },
        Ssh = new SshSettings(),
    };

    [Fact]
    public async Task RoundTripsEntryWithEndpointsAndSettings()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var saved = await store.SaveAsync(Ssh("pve"));
        Assert.True(saved.Revision > 0);

        await using var other = new EntryStore(new SqliteDataSource(_db), "bob", "pc2");
        await other.OpenAsync();
        var loaded = Assert.Single(other.Entries);
        Assert.Equal("pve", loaded.Name);
        Assert.Equal(2, loaded.Endpoints.Count);
        Assert.Equal(2222, loaded.Endpoints[1].Port);
        Assert.True(loaded.Ssh!.AutoAcceptHostKey);
        Assert.Equal("alice", loaded.UpdatedBy);
    }

    [Fact]
    public async Task SecondClientSeesChangesThroughChangeFeed()
    {
        await using var a = new EntryStore(new SqliteDataSource(_db, pollMs: 100), "alice", "pc1");
        await a.OpenAsync();
        await using var b = new EntryStore(new SqliteDataSource(_db, pollMs: 100), "bob", "pc2");
        await b.OpenAsync();

        var seen = new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.EntriesChanged += (_, list) => { foreach (var e in list) if (e.Name == "new-box") seen.TrySetResult(e); };

        await a.SaveAsync(Ssh("new-box"));
        var got = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new-box", got.Name);
    }

    [Fact]
    public async Task DeletesPropagateAsTombstones()
    {
        await using var a = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await a.OpenAsync();
        var folder = await a.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "Lab" });
        var child = Ssh("child"); child.ParentId = folder.Id;
        await a.SaveAsync(child);

        await using var b = new EntryStore(new SqliteDataSource(_db), "bob", "pc2");
        await b.OpenAsync();
        Assert.Equal(2, b.Entries.Count());

        await a.DeleteAsync(folder.Id);
        await b.PullAsync();
        Assert.Empty(b.Entries);
    }

    [Fact]
    public async Task StaleWriteIsRejected()
    {
        await using var a = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await a.OpenAsync();
        var e = await a.SaveAsync(Ssh("x"));

        await using var b = new EntryStore(new SqliteDataSource(_db), "bob", "pc2");
        await b.OpenAsync();

        var aEdit = EntryStore.Clone(e); aEdit.Name = "alice-edit";
        await a.SaveAsync(aEdit);

        var bEdit = EntryStore.Clone(b.Get(e.Id)!); bEdit.Name = "bob-edit";
        await Assert.ThrowsAsync<ConcurrencyException>(() => b.SaveAsync(bEdit));
    }

    [Fact]
    public async Task CtrlClickLinkingAttachesCredential()
    {
        await using var s = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await s.OpenAsync();
        var conn = await s.SaveAsync(Ssh("srv"));
        var cred = await s.SaveAsync(new Entry { Kind = EntryKind.Credential, Name = "root", Credential = new CredentialData { UserName = "root" } });

        var linked = await s.LinkCredentialAsync(conn.Id, cred.Id);
        Assert.Equal(cred.Id, linked.CredentialId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.LinkCredentialAsync(cred.Id, conn.Id));
    }

    [Fact]
    public async Task PresenceIsVisibleToOthers()
    {
        await using var a = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await a.OpenAsync();
        await using var b = new EntryStore(new SqliteDataSource(_db), "bob", "pc2");
        await b.OpenAsync();
        var e = await a.SaveAsync(Ssh("shared"));

        await a.SetMyPresenceAsync(e.Id, PresenceState.Connected);
        await b.SetMyPresenceAsync(null);
        var p = Assert.Single(b.PresenceFor(e.Id));
        Assert.Equal("alice", p.User);
        Assert.Equal(PresenceState.Connected, p.State);
        Assert.Empty(a.PresenceFor(e.Id)); // you don't see yourself
    }
}

public class EndpointTests
{
    [Fact]
    public async Task FallsBackToSecondaryWhenPrimaryFails()
    {
        var resolver = new EndpointResolver((host, port, ct) =>
            host == "dead" ? Task.FromException(new System.Net.Sockets.SocketException(10061)) : Task.CompletedTask);
        var entry = new Entry { Kind = EntryKind.Rdp, Endpoints = { new("dead"), new("alive", 3390) } };

        var r = await resolver.ResolveAsync(entry);
        Assert.Equal(new HostEndpoint("alive", 3390), r.Endpoint);
        Assert.True(r.UsedFallback);
        Assert.Equal(3389, r.Attempts[0].Endpoint.Port); // default port filled in
    }

    [Fact]
    public async Task TimeoutMovesOnQuickly()
    {
        var resolver = new EndpointResolver(async (host, port, ct) => { if (host == "slow") await Task.Delay(-1, ct); })
            { Timeout = TimeSpan.FromMilliseconds(150) };
        var entry = new Entry { Kind = EntryKind.Ssh, Endpoints = { new("slow"), new("fast") } };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await resolver.ResolveAsync(entry);
        Assert.Equal("fast", r.Endpoint!.Host);
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.Equal("timed out", r.Attempts[0].Error);
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", 0)]
    [InlineData("srv.lab:2222", "srv.lab", 2222)]
    [InlineData("[fe80::1]:22", "fe80::1", 22)]
    [InlineData("rdp://dc01:3390", "dc01", 3390)]
    [InlineData("fe80::1", "fe80::1", 0)]
    public void ParsesHostStrings(string text, string host, int port)
    {
        Assert.Equal(new HostEndpoint(host, port), EndpointResolver.Parse(text));
    }

    [Fact]
    public void SharedVaultSecretsRoundTripAndNeedPassphrase()
    {
        var p = new AesGcmSecretProtector("correct horse");
        var blob = p.Protect("s3cret!");
        Assert.Equal("s3cret!", new AesGcmSecretProtector("correct horse").Unprotect(blob));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new AesGcmSecretProtector("wrong").Unprotect(blob));
    }
}
