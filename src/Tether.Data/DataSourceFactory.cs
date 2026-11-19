using Tether.Core.Storage;

namespace Tether.Data;

public static class DataSourceFactory
{
    public static IDataSource Create(DataSourceConfig cfg)
    {
        if (cfg.ConnectionStrings.Count == 0) throw new ArgumentException("No connection string configured.");
        var members = cfg.ConnectionStrings.Select(cs => Single(cfg.Kind, cs, cfg.PollMilliseconds)).ToList();
        return members.Count == 1 ? members[0] : new RedundantDataSource(members);
    }

    private static IDataSource Single(DataSourceKind kind, string cs, int pollMs) => kind switch
    {
        DataSourceKind.Sqlite => new SqliteDataSource(cs, pollMs),
        DataSourceKind.PostgreSql => new PostgresDataSource(cs),
        DataSourceKind.SqlServer => new SqlServerDataSource(cs, Math.Min(pollMs, 500)),
        DataSourceKind.MySql => new MySqlDataSource(cs, Math.Min(pollMs, 500)),
        _ => throw new NotSupportedException(kind.ToString()),
    };
}
