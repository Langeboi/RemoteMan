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
    public async Task KeepsCreatedAndLastModifiedSeparately()
    {
        await using var a = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await a.OpenAsync();
        var e = await a.SaveAsync(Ssh("box"));
        Assert.Equal("alice", e.CreatedBy);
        Assert.NotNull(e.CreatedAt);

        await Task.Delay(20);
        await using var b = new EntryStore(new SqliteDataSource(_db), "bob", "pc2");
        await b.OpenAsync();
        var edit = EntryStore.Clone(b.Get(e.Id)!); edit.Name = "box-renamed";
        await b.SaveAsync(edit);

        await using var c = new EntryStore(new SqliteDataSource(_db), "carol", "pc3");
        await c.OpenAsync();
        var seen = c.Get(e.Id)!;
        Assert.Equal("alice", seen.CreatedBy);
        Assert.Equal("bob", seen.UpdatedBy);
        Assert.Equal(e.CreatedAt, seen.CreatedAt);
        Assert.True(seen.UpdatedAt > seen.CreatedAt);
    }

    [Fact]
    public async Task UpgradesDatabaseFromBeforeHistoryColumns()
    {
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE remoteman_entries (id TEXT PRIMARY KEY, parent_id TEXT NULL, kind INTEGER NOT NULL, name TEXT NOT NULL, payload TEXT NULL, revision INTEGER NOT NULL, updated_at INTEGER NOT NULL, updated_by TEXT NULL, deleted INTEGER NOT NULL DEFAULT 0);" +
                              "INSERT INTO remoteman_entries VALUES ('" + Guid.NewGuid() + "', NULL, 2, 'old', NULL, 1, 0, 'x', 0);";
            cmd.ExecuteNonQuery();
        }
        await using var s = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await s.OpenAsync();
        var old = Assert.Single(s.Entries);
        Assert.Null(old.CreatedBy);
        var fresh = await s.SaveAsync(Ssh("new"));
        Assert.Equal("alice", fresh.CreatedBy);
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
    [Fact]
    public void WindowsLoginRewritesConnectionStrings()
    {
        var sql = DataSourceFactory.WithWindowsLogin(DataSourceKind.SqlServer, "Server=db;Database=rm;User ID=sa;Password=x");
        Assert.Contains("Integrated Security=True", sql);
        Assert.DoesNotContain("Password", sql);
        Assert.DoesNotContain("User ID", sql);
        var pg = DataSourceFactory.WithWindowsLogin(DataSourceKind.PostgreSql, "Host=db;Database=rm;Username=lange;Password=x");
        Assert.DoesNotContain("Password", pg);
        Assert.Contains("Username=lange", pg);
        Assert.Contains("Username=", DataSourceFactory.WithWindowsLogin(DataSourceKind.PostgreSql, "Host=db;Database=rm"));
        Assert.DoesNotContain("x", DataSourceFactory.WithWindowsLogin(DataSourceKind.MySql, "Server=db;User ID=u;Password=x").Replace("Server", ""));
    }

    [Fact]
    public async Task WindowsLoginFlagRoundTripsAndLinkingClearsIt()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var rdp = await store.SaveAsync(new Entry { Kind = EntryKind.Rdp, Name = "dc", UseWindowsLogin = true, Endpoints = { new HostEndpoint("dc", 3389) } });
        Assert.True(EntryStore.Clone(rdp).UseWindowsLogin);
        var cred = await store.SaveAsync(new Entry { Kind = EntryKind.Credential, Name = "admin", Credential = new() { UserName = "a" } });
        var linked = await store.LinkCredentialAsync(rdp.Id, cred.Id);
        Assert.False(linked.UseWindowsLogin);
        Assert.Equal(cred.Id, linked.CredentialId);
    }

    [Fact]
    public async Task GoesOfflineQuietlyAndCatchesUpWhenTheDatabaseReturns()
    {
        var flaky = new FlakySource(new SqliteDataSource(_db, pollMs: 100));
        await using var a = new EntryStore(flaky, "alice", "pc1") { ReconnectInterval = TimeSpan.FromMilliseconds(200) };
        await a.OpenAsync();
        Assert.True(a.IsOnline);
        var seen = new List<bool>();
        a.OnlineChanged += (_, up) => { lock (seen) seen.Add(up); };

        flaky.Down = true;
        await Assert.ThrowsAsync<DataSourceOfflineException>(() => a.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "x" }));
        Assert.False(a.IsOnline);
        // Already offline: refused at once, without waiting on the network.
        await Assert.ThrowsAsync<DataSourceOfflineException>(() => a.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "y" }));

        // Someone else keeps working while we are cut off.
        await using (var b = new EntryStore(new SqliteDataSource(_db), "bob", "pc2"))
        {
            await b.OpenAsync();
            await b.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "made while you were offline" });
        }

        flaky.Down = false;
        await WaitFor(() => a.IsOnline && a.Entries.Any(e => e.Name == "made while you were offline"));
        lock (seen) Assert.Equal(new[] { false, true }, seen);
        await a.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "after" });
    }

    [Fact]
    public async Task OpensOfflineFromCacheWhenTheDatabaseIsDown()
    {
        var cache = new MemoryCache();
        await using (var first = new EntryStore(new SqliteDataSource(_db), "alice", "pc1", cache))
        {
            await first.OpenAsync();
            await first.SaveAsync(new Entry { Kind = EntryKind.Ssh, Name = "web01", Endpoints = { new HostEndpoint("web01", 22) } });
        } // dispose flushes the cache

        var flaky = new FlakySource(new SqliteDataSource(_db, pollMs: 100)) { Down = true };
        await using var store = new EntryStore(flaky, "alice", "pc1", cache) { ReconnectInterval = TimeSpan.FromMilliseconds(200) };
        await store.OpenAsync(); // no exception
        Assert.False(store.IsOnline);
        Assert.NotNull(store.LastError);
        Assert.Contains(store.Entries, e => e.Name == "web01");

        flaky.Down = false;
        await WaitFor(() => store.IsOnline);
        Assert.Contains(store.Entries, e => e.Name == "web01");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(50);
        }
    }

    private sealed class MemoryCache : IEntryCache
    {
        private List<Entry>? _saved;
        public IReadOnlyList<Entry>? Load() => _saved;
        public void Save(IReadOnlyList<Entry> entries) => _saved = entries.Select(EntryStore.Clone).ToList();
    }

    /// <summary>Wraps a real source and simulates the network going away.</summary>
    private sealed class FlakySource(IDataSource inner) : IDataSource
    {
        public volatile bool Down;
        private T Check<T>(T value) => Down ? throw new IOException("network is down") : value;
        private async Task<T> Go<T>(Func<Task<T>> f) { await Task.Yield(); Check(0); return await f(); }
        private async Task Go(Func<Task> f) { await Task.Yield(); Check(0); await f(); }

        public string DisplayName => "flaky";
        public DataSourceKind Kind => inner.Kind;
        public bool SupportsPresence => inner.SupportsPresence;
        public string? DatabaseUser => inner.DatabaseUser;
        public bool IsConnected => !Down;
        public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
        public event EventHandler<ChangeSignal>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }
        public Task InitializeAsync(CancellationToken ct = default) => Go(() => inner.InitializeAsync(ct));
        public Task<IReadOnlyList<Entry>> LoadAllAsync(CancellationToken ct = default) => Go(() => inner.LoadAllAsync(ct));
        public Task<IReadOnlyList<Entry>> GetChangesAsync(long since, CancellationToken ct = default) => Go(() => inner.GetChangesAsync(since, ct));
        public Task<Entry> SaveAsync(Entry e, string user, long? exp = null, CancellationToken ct = default) => Go(() => inner.SaveAsync(e, user, exp, ct));
        public Task DeleteAsync(Guid id, string user, CancellationToken ct = default) => Go(() => inner.DeleteAsync(id, user, ct));
        public Task HeartbeatPresenceAsync(Presence p, CancellationToken ct = default) => Go(() => inner.HeartbeatPresenceAsync(p, ct));
        public Task ClearPresenceAsync(string u, string m, CancellationToken ct = default) => Go(() => inner.ClearPresenceAsync(u, m, ct));
        public Task<IReadOnlyList<Presence>> GetPresenceAsync(CancellationToken ct = default) => Go(() => inner.GetPresenceAsync(ct));
        public Task StartChangeFeedAsync(CancellationToken ct = default) => inner.StartChangeFeedAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task LoginIsInheritedFromFoldersAndOldEntriesKeepTheirLogin()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var cred = await store.SaveAsync(new Entry { Kind = EntryKind.Credential, Name = "admin", Credential = new() { UserName = "a" } });
        var top = await store.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "DC", Login = LoginSource.Saved, CredentialId = cred.Id });
        var sub = await store.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "Hyper-V", ParentId = top.Id, Login = LoginSource.Inherited });
        var rdp = await store.SaveAsync(new Entry { Kind = EntryKind.Rdp, Name = "hv01", ParentId = sub.Id, Login = LoginSource.Inherited });
        Assert.Equal((LoginSource.Saved, cred.Id), (store.ResolveLogin(rdp).Source, store.ResolveLogin(rdp).Holder!.Id));

        var own = await store.SaveAsync(new Entry { Kind = EntryKind.Ssh, Name = "web", Login = LoginSource.Embedded,
            Credential = new() { Type = CredentialType.SshKey, UserName = "root", PrivateKeyPath = "~/.ssh/id" } });
        var back = EntryStore.Clone(store.Get(own.Id)!);
        Assert.Equal(CredentialType.SshKey, back.Credential!.Type);
        Assert.Equal(LoginSource.Embedded, store.ResolveLogin(back).Source);

        // Payload written by an older version: linked credential, or the SSO flag.
        var old = new Entry();
        EntryJson.ApplyPayload(old, $$"""{"CredentialId":"{{cred.Id}}"}""");
        Assert.Equal(LoginSource.Saved, old.Login);
        EntryJson.ApplyPayload(old, """{"UseWindowsLogin":true}""");
        Assert.True(old.UseWindowsLogin);
    }
}

public class EndpointTests
{
    [Theory]
    [InlineData("youtube.com", "https://youtube.com/")]
    [InlineData(" www.youtube.com/watch?v=x ", "https://www.youtube.com/watch?v=x")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080/")]
    [InlineData("https://", "https://")]
    public void WebUrlsGetAScheme(string input, string expected) => Assert.Equal(expected, WebSettings.NormalizeUrl(input));

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
