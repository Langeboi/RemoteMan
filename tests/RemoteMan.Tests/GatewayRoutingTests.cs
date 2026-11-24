using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class GatewayRoutingTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"remoteman-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task Gateway_site_comes_from_the_nearest_folder_that_sets_one()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var school = await store.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "School", Connection = new ConnectionSettings { GatewaySite = "school" } });
        var lab = await store.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "Lab", ParentId = school.Id });
        var local = await store.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "On my desk", ParentId = school.Id, Connection = new ConnectionSettings { GatewaySite = "" } });
        var dc = await store.SaveAsync(new Entry { Kind = EntryKind.Rdp, Name = "dc01", ParentId = lab.Id });
        var own = await store.SaveAsync(new Entry { Kind = EntryKind.Ssh, Name = "fw", ParentId = lab.Id, Connection = new ConnectionSettings { GatewaySite = "home", GatewayAlways = true } });
        var pi = await store.SaveAsync(new Entry { Kind = EntryKind.Ssh, Name = "pi", ParentId = local.Id });
        var loose = await store.SaveAsync(new Entry { Kind = EntryKind.Ssh, Name = "loose" });

        Assert.Equal(("school", false), store.GatewayFor(store.Get(dc.Id)!));
        Assert.Equal(("home", true), store.GatewayFor(store.Get(own.Id)!));
        Assert.Null(store.GatewayFor(store.Get(pi.Id)!));     // "None" on a folder stops the search
        Assert.Null(store.GatewayFor(store.Get(loose.Id)!));
        // The data source's site covers what no folder sets, but not what says None.
        Assert.Equal(("school", false), store.GatewayFor(store.Get(loose.Id)!, "school"));
        Assert.Equal(("home", true), store.GatewayFor(store.Get(own.Id)!, "school"));
        Assert.Null(store.GatewayFor(store.Get(pi.Id)!, "school"));
    }

    private static (string, int) Fwd(string host, int port) => ("127.0.0.1", port + 10000);

    [Fact]
    public void Postgres_pair_keeps_one_forwarder_per_server()
    {
        var cs = DataSourceFactory.PostgresPair(["Host=10.0.0.11;Port=5432;Database=remoteman;Username=anna;Password=x", "Host=10.0.0.12:5433"]);
        var routed = new Npgsql.NpgsqlConnectionStringBuilder(DataSourceFactory.RouteHosts(DataSourceKind.PostgreSql, cs, Fwd));
        Assert.Equal("127.0.0.1:15432,127.0.0.1:15433", routed.Host);
        Assert.Equal("primary", routed.TargetSessionAttributes);
        Assert.Equal("anna", routed.Username);
        Assert.True(routed.Timeout >= DataSourceFactory.GatewayConnectSeconds);   // a tunnel plus the login takes longer than 5 s
    }

    [Fact]
    public void Sql_server_and_mysql_are_routed_too()
    {
        var sql = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            DataSourceFactory.RouteHosts(DataSourceKind.SqlServer, "Data Source=sql01\\TEAM;Initial Catalog=rm;User ID=a;Password=b", Fwd));
        Assert.Equal("tcp:127.0.0.1,11433", sql.DataSource);
        Assert.Equal(DataSourceFactory.GatewayConnectSeconds, sql.ConnectTimeout);
        var my = new MySqlConnector.MySqlConnectionStringBuilder(
            DataSourceFactory.RouteHosts(DataSourceKind.MySql, "Server=db1,db2;Port=3306;Database=rm;User ID=a;Password=b", Fwd));
        Assert.Equal("127.0.0.1", my.Server);
        Assert.Equal(13306u, my.Port);
    }
}
