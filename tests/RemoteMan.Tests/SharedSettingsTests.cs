using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class SharedSettingsTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"rm-settings-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task LocalFileKeepsSettingsAndReplacesThem()
    {
        await using var src = new SqliteDataSource(TempDb());
        await src.InitializeAsync();
        Assert.True(src.SupportsSettings);
        Assert.True(src.CanChangeSettings);
        Assert.Empty(await src.GetSettingsAsync());

        await src.PutSettingAsync("integration.zabbix", """{"Url":"https://a"}""", "me");
        await src.PutSettingAsync("integration.zabbix", """{"Url":"https://b"}""", "me");
        await src.PutSettingAsync("integration.oxidized", """{"Url":"http://ox"}""", "me");

        var rows = await src.GetSettingsAsync();
        Assert.Equal(2, rows.Count);
        var z = Assert.Single(rows, r => r.Name == "integration.zabbix");
        Assert.Equal("""{"Url":"https://b"}""", z.Value);
        Assert.Equal("me", z.UpdatedBy);
        Assert.True(DateTimeOffset.UtcNow - z.UpdatedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task AnotherClientHearsAboutAChange()
    {
        var path = TempDb();
        await using var a = new EntryStore(new SqliteDataSource(path, pollMs: 100), "a", "pc1");
        await a.OpenAsync();
        await using var b = new EntryStore(new SqliteDataSource(path, pollMs: 100), "b", "pc2");
        await b.OpenAsync();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.SettingsChanged += (_, _) => heard.TrySetResult();
        await Task.Delay(400);   // b's poll has seen the empty table

        await a.Source.PutSettingAsync("integration.zabbix", "{}", a.User);
        Assert.Same(heard.Task, await Task.WhenAny(heard.Task, Task.Delay(5000)));
    }

    [Fact]
    public async Task RedundantSourcePassesSettingsThrough()
    {
        await using var src = new RedundantDataSource([new SqliteDataSource(TempDb())]);
        await src.InitializeAsync();
        Assert.True(src.SupportsSettings);
        await src.PutSettingAsync("x", "1", "me");
        Assert.Equal("1", Assert.Single(await src.GetSettingsAsync()).Value);
    }

    /// <summary>
    /// Runs against a server set up with remoteman-db.sh when REMOTEMAN_TEST_PG_ADMIN and REMOTEMAN_TEST_PG_EDITOR hold
    /// connection strings for an admin and an editor login (REMOTEMAN_TEST_PG_READONLY optionally for a read-only one).
    /// </summary>
    [Fact]
    public async Task OnlyAdminsChangeSettingsOnARemoteManServer()
    {
        var adminCs = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG_ADMIN");
        var editorCs = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG_EDITOR");
        if (adminCs is null || editorCs is null) return;

        await using var admin = new EntryStore(new PostgresDataSource(adminCs, pollMs: 60_000), "x", "pc1");
        await admin.OpenAsync();
        await using var editor = new EntryStore(new PostgresDataSource(editorCs, pollMs: 60_000), "x", "pc2");
        await editor.OpenAsync();
        Assert.True(admin.Source.CanChangeSettings);
        Assert.False(editor.Source.CanChangeSettings);

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.SettingsChanged += (_, _) => heard.TrySetResult();
        var value = $$"""{"Url":"https://{{Guid.NewGuid():N}}"}""";
        await admin.Source.PutSettingAsync("integration.test", value, admin.User);
        Assert.Same(heard.Task, await Task.WhenAny(heard.Task, Task.Delay(5000)));
        Assert.Equal(value, (await editor.Source.GetSettingsAsync()).Single(r => r.Name == "integration.test").Value);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => editor.Source.PutSettingAsync("integration.test", "{}", editor.User));

        // Even a client that thinks it may (an older app) is stopped by the server, and the source stays online.
        var sneaky = new SneakyPostgres(editorCs);
        await sneaky.InitializeAsync();
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sneaky.ForcePutAsync("integration.test", "{}"));
        Assert.Contains("admin", ex.Message);
        Assert.True(editor.IsOnline);

        if (Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG_READONLY") is { } roCs)
        {
            await using var ro = new EntryStore(new PostgresDataSource(roCs, pollMs: 60_000), "x", "pc3");
            await ro.OpenAsync();
            Assert.False(ro.Source.CanChangeSettings);
            Assert.Equal(value, (await ro.Source.GetSettingsAsync()).Single(r => r.Name == "integration.test").Value);
        }
    }

    /// <summary>Writes the row directly, skipping the client-side admin check.</summary>
    private sealed class SneakyPostgres(string cs) : SqlDataSource(cs, 60_000)
    {
        public override string DisplayName => "sneaky";
        public override DataSourceKind Kind => DataSourceKind.PostgreSql;
        protected override System.Data.Common.DbConnection CreateConnection() => new Npgsql.NpgsqlConnection(ConnectionString);
        protected override IEnumerable<string> SchemaStatements => [];
        protected override string NextCounterSql(int id) => "";
        protected override string? SettingsTableSql => "SELECT 1";
        protected override bool IsPermissionDenied(System.Data.Common.DbException ex) =>
            ex is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.InsufficientPrivilege };
        protected override Task<bool?> IsServerAdminAsync(System.Data.Common.DbConnection c, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task ForcePutAsync(string name, string value) => PutSettingAsync(name, value, "sneaky");
    }
}
