using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class PostgresLoginsTests
{
    [Theory]
    [InlineData("anna", true)]
    [InlineData("_svc2", true)]
    [InlineData("Anna", false)]
    [InlineData("2anna", false)]
    [InlineData("an na", false)]
    [InlineData("o'x", false)]
    [InlineData("", false)]
    public void LoginNamesFollowTheServerRules(string name, bool ok) => Assert.Equal(ok, PostgresLogins.IsValidName(name));

    [Fact]
    public void GeneratedPasswordsAreLongAndNeedNoQuoting()
    {
        var a = PostgresLogins.GeneratePassword();
        var b = PostgresLogins.GeneratePassword();
        Assert.Equal(20, a.Length);
        Assert.NotEqual(a, b);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void TeammateConnectionStringKeepsBothServersAndSwapsTheLogin()
    {
        var admin = DataSourceFactory.PostgresPair([
            "Host=10.0.0.11;Database=remoteman;Username=remoteman;Password=shared",
            "Host=10.0.0.12;Database=remoteman;Username=remoteman;Password=shared"]);
        var cs = PostgresLogins.ConnectionStringFor(admin, "mikkel", "it's;secret");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("10.0.0.11:5432,10.0.0.12:5432", b.Host);
        Assert.Equal("mikkel", b.Username);
        Assert.Equal("it's;secret", b.Password);
        Assert.Equal("remoteman", b.Database);
        Assert.Equal("primary", b.TargetSessionAttributes);
        Assert.DoesNotContain("shared", cs);
    }

    [Fact]
    public void AccessNamesMatchTheServerFunctions()
    {
        foreach (var a in new[] { DbLoginAccess.ReadOnly, DbLoginAccess.Edit, DbLoginAccess.Admin })
            Assert.Equal(a, PostgresLogins.ParseAccess(PostgresLogins.AccessName(a)));
        Assert.Equal(DbLoginAccess.Shared, PostgresLogins.ParseAccess("shared"));
        Assert.Null(PostgresLogins.ParseAccess(null));
        Assert.Throws<ArgumentException>(() => PostgresLogins.AccessName(DbLoginAccess.Shared));
    }

    /// <summary>
    /// Runs against a real PostgreSQL when REMOTEMAN_TEST_PG_ADMIN holds a connection string for the shared
    /// login of a database prepared with remoteman-db.sh (so the remoteman_admin functions exist).
    /// </summary>
    [Fact]
    public async Task AdminAddsChangesAndRemovesALogin()
    {
        var cs = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG_ADMIN");
        if (cs is null) return;
        var admin = new PostgresLogins(cs);
        var self = await admin.GetSelfAsync();
        Assert.NotNull(self);
        Assert.True(self!.IsAdmin);

        var name = "t_" + Guid.NewGuid().ToString("N")[..8];
        var pw = PostgresLogins.GeneratePassword();
        await admin.AddAsync(name, pw, DbLoginAccess.Edit);
        Assert.Contains(await admin.ListAsync(), l => l.Name == name && l.Access == DbLoginAccess.Edit);

        var user = new PostgresLogins(PostgresLogins.ConnectionStringFor(cs, name, pw));
        var me = await user.GetSelfAsync();
        Assert.Equal(name, me!.Login);
        Assert.False(me.IsAdmin);
        await Assert.ThrowsAsync<DbLoginException>(() => user.ListAsync());

        await admin.SetAccessAsync(name, DbLoginAccess.ReadOnly);
        Assert.Contains(await admin.ListAsync(), l => l.Name == name && l.Access == DbLoginAccess.ReadOnly);
        await Assert.ThrowsAsync<DbLoginException>(() => admin.AddAsync("postgres", pw, DbLoginAccess.Admin));
        Assert.True(await admin.RemoveAsync(name));
        Assert.DoesNotContain(await admin.ListAsync(), l => l.Name == name);
    }
}
