using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

public static class DataSourceFactory
{
    public static IDataSource Create(DataSourceConfig cfg)
    {
        if (cfg.ConnectionStrings.Count == 0) throw new ArgumentException("No connection string configured.");
        var members = cfg.ConnectionStrings
            .Select(cs => cfg.UseWindowsLogin ? WithWindowsLogin(cfg.Kind, cs) : cs)
            .Select(cs => Single(cfg.Kind, cs, cfg.PollMilliseconds)).ToList();
        return members.Count == 1 ? members[0] : new RedundantDataSource(members);
    }

    /// <summary>Rewrites a connection string to sign in as the current Windows user (no stored password).</summary>
    public static string WithWindowsLogin(DataSourceKind kind, string cs)
    {
        switch (kind)
        {
            case DataSourceKind.SqlServer:
            {
                var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs) { IntegratedSecurity = true };
                b.Remove("User ID"); b.Remove("Password");
                return b.ConnectionString;
            }
            case DataSourceKind.PostgreSql:
            {
                // Npgsql uses Kerberos/SSPI (GSSAPI) when the server asks for it and no password is given.
                var b = new Npgsql.NpgsqlConnectionStringBuilder(cs) { Password = null };
                if (string.IsNullOrEmpty(b.Username)) b.Username = Environment.UserName;
                return b.ConnectionString;
            }
            case DataSourceKind.MySql:
            {
                // MySqlConnector answers authentication_windows_client (MySQL) and auth_gssapi_client (MariaDB).
                var b = new MySqlConnector.MySqlConnectionStringBuilder(cs) { Password = "" };
                if (string.IsNullOrEmpty(b.UserID)) b.UserID = Environment.UserName;
                return b.ConnectionString;
            }
            default:
                return cs;
        }
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
