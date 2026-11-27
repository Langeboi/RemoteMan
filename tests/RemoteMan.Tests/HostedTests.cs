using Npgsql;
using RemoteMan.Gateway;
using RemoteMan.Gateway.Server.Hosted;
using Xunit;

namespace RemoteMan.Tests;

/// <summary>
/// Hosted workspaces: the account list, sign-in and app tokens (no database needed), and, when REMOTEMAN_TEST_PG_ADMIN
/// holds a superuser connection string to a scratch PostgreSQL server, the per-workspace databases, access and backups.
/// </summary>
public sealed class HostedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-hosted-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly string? AdminCs = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG_ADMIN");

    public HostedTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private HostedStore NewStore() => new(Path.Combine(_dir, "hosted.json"));

    [Theory]
    [InlineData("mikkel", true)]
    [InlineData("anna_2", true)]
    [InlineData("Mikkel", false)]
    [InlineData("2anna", false)]
    [InlineData("pg_admin", false)]
    [InlineData("rmw_x", false)]
    [InlineData("postgres", false)]
    [InlineData("a b", false)]
    public void AccountNamesAreUsableAsDatabaseLogins(string name, bool ok)
    {
        var ex = Record.Exception(() => HostedStore.CheckAccountName(name));
        Assert.Equal(ok, ex is null);
    }

    [Fact]
    public void AdminNamesBecomeSafeLogins()
    {
        Assert.Equal("lange", HostedStore.DbLoginFor("Lange"));
        Assert.Equal("a_b", HostedStore.DbLoginFor("a.b"));
        Assert.Equal("u_1st", HostedStore.DbLoginFor("1st"));
        Assert.Equal("u_postgres", HostedStore.DbLoginFor("postgres"));
    }

    [Fact]
    public void AccountsWorkspacesAndTokensPersist()
    {
        var s = NewStore();
        var a = s.AddAccount("Mikkel", "Mikkel S", "first-password-1", mustChange: true);
        Assert.Equal("mikkel", a.Name);
        Assert.Throws<ArgumentException>(() => s.AddAccount("mikkel", null, "another-password", false));
        var w = s.AddWorkspace("Networking class", "CCNA", "lange",
            [new WorkspaceMember { Account = "mikkel", Access = WorkspaceAccess.Read }, new WorkspaceMember { Account = "nobody" }]);
        Assert.Single(w.Members);
        Assert.Throws<ArgumentException>(() => s.AddWorkspace("networking CLASS", null, null, []));
        Assert.StartsWith("rmw_", w.Database);

        var (token, rec) = s.AddToken("mikkel", "mikkel-pc", "10.0.0.5");
        Assert.StartsWith("rmat_", token);

        // A second instance reads the file: what the console wrote, the app's sign-in sees.
        var again = NewStore();
        Assert.Equal(rec.Id, again.TokenFor(token)!.Id);
        Assert.Null(again.TokenFor(token + "x"));
        Assert.Null(again.TokenFor(null));
        Assert.Equal(WorkspaceAccess.Read, again.WorkspacesOf("MIKKEL").Single().Access);

        again.RemoveAccount("mikkel");
        var third = NewStore();
        Assert.Null(third.TokenFor(token));
        Assert.Empty(third.Workspace(w.Id)!.Members);
    }

    [Fact]
    public void SignInChecksPasswordCodeAndTurnedOff()
    {
        var relay = new RelayStore(Path.Combine(_dir, "relay.json"));
        relay.AddAdmin("Lange", "admin-password-1");
        var store = NewStore();
        var h = new HostedService(store, new HostedDb(new HostedDbOptions { AdminConnectionString = "Host=127.0.0.1" }), relay,
            new HostedOptions { BackupDir = Path.Combine(_dir, "backups"), Log = _ => { } });
        store.AddAccount("anna", null, "anna-password-1", mustChange: true);

        Assert.Null(h.Authenticate("anna", "wrong-password", null).Account);
        var ok = h.Authenticate("anna", "anna-password-1", null).Account;
        Assert.NotNull(ok);
        Assert.True(ok!.MustChangePassword);

        // The first password change clears "must change"; the same password again is refused.
        Assert.Throws<ArgumentException>(() => h.ChangeOwnPassword(ok, "anna-password-1", "anna-password-1"));
        h.ChangeOwnPassword(ok, "anna-password-1", "anna-own-password");
        Assert.False(store.Account("anna")!.MustChangePassword);
        Assert.NotNull(h.Authenticate("anna", "anna-own-password", null).Account);

        store.UpdateAccount("anna", a => a.Disabled = true);
        var off = h.Authenticate("anna", "anna-own-password", null);
        Assert.Null(off.Account);
        Assert.NotNull(off.Error);

        // A console admin signs in with the console password and gets an account of their own.
        var admin = h.Authenticate("lange", "admin-password-1", null).Account;
        Assert.NotNull(admin);
        Assert.True(admin!.ConsoleAdmin);
        Assert.Equal("lange", admin.DbLogin);
    }

    // ---- against a real PostgreSQL (skipped without REMOTEMAN_TEST_PG_ADMIN) ----

    [Fact]
    public async Task WorkspaceAccessAndBackupRoundTrip()
    {
        if (AdminCs is null) return;
        var relay = new RelayStore(Path.Combine(_dir, "relay.json"));
        relay.AddAdmin("Lange", "admin-password-1");
        var store = NewStore();
        var db = new HostedDb(new HostedDbOptions { AdminConnectionString = AdminCs, NoTls = true });
        var h = new HostedService(store, db, relay, new HostedOptions { BackupDir = Path.Combine(_dir, "backups"), Log = _ => { } });
        await db.PrepareServerAsync();

        var editor = "t" + Guid.NewGuid().ToString("N")[..10];
        var reader = "t" + Guid.NewGuid().ToString("N")[..10];
        await h.AddAccountAsync(editor, null, "editor-password-1", false, [], "lange");
        await h.AddAccountAsync(reader, null, "reader-password-1", false, [], "lange");
        var w = await h.AddWorkspaceAsync("Test " + editor, null,
            [new WorkspaceMember { Account = editor, Access = WorkspaceAccess.Edit }, new WorkspaceMember { Account = reader, Access = WorkspaceAccess.Read }],
            null, "lange");
        try
        {
            string Cs(string who) => new NpgsqlConnectionStringBuilder(db.AppConnectionString(w, store.Account(who)!)) { Host = new NpgsqlConnectionStringBuilder(AdminCs).Host, Port = new NpgsqlConnectionStringBuilder(AdminCs).Port }.ConnectionString;

            await using (var c = new NpgsqlConnection(Cs(editor)))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand("INSERT INTO remoteman_entries (id, kind, name, revision, updated_at, updated_by, deleted) VALUES ('e1', 1, 'router', 1, 1, session_user, 0)", c);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var c = new NpgsqlConnection(Cs(reader)))
            {
                await c.OpenAsync();
                await using var read = new NpgsqlCommand("SELECT count(*) FROM remoteman_entries", c);
                Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);
                await using var write = new NpgsqlCommand("DELETE FROM remoteman_entries", c);
                await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
            }

            var backup = await h.BackUpAsync(w, "manual", "lange");
            Assert.Equal(1L, backup.Rows["remoteman_entries"]);
            var test = await h.TestRestoresAsync([backup]);
            Assert.True(test.Ok, test.Detail);
            var copy = await h.RestoreAsCopyAsync(backup, "lange");
            Assert.Equal(1L, (await db.StatsAsync(copy)).Entries);
            await h.DeleteWorkspaceAsync(copy.Id, "lange");
        }
        finally
        {
            await h.DeleteWorkspaceAsync(w.Id, "lange");
            await h.RemoveAccountAsync(editor, "lange");
            await h.RemoveAccountAsync(reader, "lange");
        }
    }
}
