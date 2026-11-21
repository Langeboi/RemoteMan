using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class PostgresPairTests
{
    [Fact]
    public void TwoLinesBecomeOneConnectionThatFindsThePrimary()
    {
        var cs = DataSourceFactory.PostgresPair([
            "Host=10.0.0.11;Database=remoteman;Username=remoteman;Password=x",
            "Host=10.0.0.12;Port=5433;Database=remoteman;Username=remoteman;Password=x"]);
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("10.0.0.11:5432,10.0.0.12:5433", b.Host);
        Assert.Equal("primary", b.TargetSessionAttributes);
        Assert.Equal(5, b.Timeout);
        Assert.Equal("remoteman", b.Database);
    }

    [Fact]
    public void OneLineWithTwoHostsGetsPrimaryTargeting()
    {
        var cs = DataSourceFactory.PostgresPair(["Host=pg1:5432,pg2:5432;Database=rm;Username=u;Password=p;Timeout=10"]);
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("pg1:5432,pg2:5432", b.Host);
        Assert.Equal("primary", b.TargetSessionAttributes);
        Assert.Equal(10, b.Timeout);
    }

    [Fact]
    public void SingleServerIsLeftAlone()
    {
        const string cs = "Host=pg1;Database=rm;Username=u;Password=p";
        Assert.Equal(cs, DataSourceFactory.PostgresPair([cs]));
    }
}
