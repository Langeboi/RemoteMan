using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

public static class DataSourceFactory
{
    public static IDataSource Create(DataSourceConfig cfg)
    {
        if (cfg.ConnectionStrings.Count == 0) throw new ArgumentException("No connection string configured.");
        var strings = cfg.ConnectionStrings.Where(cs => !string.IsNullOrWhiteSpace(cs)).ToList();
        // PostgreSQL primary + standby: one multi-host connection that always lands on the writable
        // server, whichever of the two that is after a failover (see deploy/postgres).
        if (cfg.Kind == DataSourceKind.PostgreSql && strings.Count > 0) strings = [PostgresPair(strings)];
        var members = strings
            .Select(cs => cfg.UseWindowsLogin ? WithWindowsLogin(cfg.Kind, cs) : cs)
            .Select(cs => Single(cfg.Kind, cs, cfg.PollMilliseconds)).ToList();
        return members.Count == 1 ? members[0] : new RedundantDataSource(members);
    }

    /// <summary>
    /// Several PostgreSQL servers (one per line, or "Host=a,b" in one line) become one connection string
    /// listing every host with "Target Session Attributes=primary": Npgsql then connects to whichever
    /// server currently accepts writes. A standby that was promoted is picked up on the next connection,
    /// with no failover logic of our own. A single host is returned unchanged.
    /// </summary>
    public static string PostgresPair(IReadOnlyList<string> connectionStrings)
    {
        var first = new Npgsql.NpgsqlConnectionStringBuilder(connectionStrings[0]);
        var hosts = new List<string>();
        foreach (var cs in connectionStrings)
        {
            var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
            foreach (var h in (b.Host ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // "host:port" keeps its own port; a bare host gets this line's port.
                var withPort = h.Contains(':') && !h.StartsWith('[') && h.Count(c => c == ':') == 1 || h.StartsWith('[') && h.Contains("]:")
                    ? h : $"{h}:{b.Port}";
                if (!hosts.Contains(withPort, StringComparer.OrdinalIgnoreCase)) hosts.Add(withPort);
            }
        }
        if (hosts.Count <= 1) return connectionStrings[0];
        first.Host = string.Join(",", hosts);
        if (first.TargetSessionAttributes is null) first.TargetSessionAttributes = "primary";
        // Don't wait the default 15 s on a server that is down before trying the next one.
        if (!connectionStrings[0].Contains("Timeout", StringComparison.OrdinalIgnoreCase)) first.Timeout = 5;
        return first.ConnectionString;
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
