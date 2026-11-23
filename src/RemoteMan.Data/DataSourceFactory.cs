using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

public static class DataSourceFactory
{
    /// <summary>
    /// Rewrites one database server address, e.g. to a local gateway forwarder (127.0.0.1:port). Called once per
    /// server when the source is created; the database client then connects to the rewritten address.
    /// </summary>
    public delegate (string Host, int Port) Route(string host, int port);

    public static IDataSource Create(DataSourceConfig cfg, Route? route = null)
    {
        if (cfg.ConnectionStrings.Count == 0) throw new ArgumentException("No connection string configured.");
        var strings = cfg.ConnectionStrings.Where(cs => !string.IsNullOrWhiteSpace(cs)).ToList();
        // PostgreSQL primary + standby: one multi-host connection that always lands on the writable
        // server, whichever of the two that is after a failover (see deploy/postgres).
        if (cfg.Kind == DataSourceKind.PostgreSql && strings.Count > 0) strings = [PostgresPair(strings)];
        var members = strings
            .Select(cs => cfg.UseWindowsLogin ? WithWindowsLogin(cfg.Kind, cs) : cs)
            .Select(cs => route is null ? cs : RouteHosts(cfg.Kind, cs, route))
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

    /// <summary>
    /// Every server named in a connection string, passed through <paramref name="route"/>. PostgreSQL and MySQL
    /// keep their multi-host lists (each host gets its own route), SQL Server its "host,port". SQLite is unchanged.
    /// TLS still checks the real server name: the host the client sees is only where the bytes go.
    /// </summary>
    public static string RouteHosts(DataSourceKind kind, string cs, Route route)
    {
        switch (kind)
        {
            case DataSourceKind.PostgreSql:
            {
                var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
                var hosts = (b.Host ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(h => SplitHostPort(h, b.Port)).Select(hp => route(hp.Host, hp.Port)).Select(r => $"{r.Host}:{r.Port}");
                b.Host = string.Join(",", hosts);
                // The certificate names the real server, not 127.0.0.1.
                if (b.SslMode is Npgsql.SslMode.VerifyFull) b.SslMode = Npgsql.SslMode.VerifyCA;
                return b.ConnectionString;
            }
            case DataSourceKind.MySql:
            {
                var b = new MySqlConnector.MySqlConnectionStringBuilder(cs);
                // MySqlConnector has one port for every server, and each forwarder has its own, so only the first
                // server goes through the gateway (failover between MySQL servers then needs a direct path).
                var first = b.Server.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? b.Server;
                var r = route(first, (int)b.Port);
                b.Server = r.Host;
                b.Port = (uint)r.Port;
                return b.ConnectionString;
            }
            case DataSourceKind.SqlServer:
            {
                var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
                var ds = b.DataSource.Trim();
                var proto = ds.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? "tcp:" : "";
                ds = ds[proto.Length..];
                var comma = ds.LastIndexOf(',');
                var host = comma > 0 ? ds[..comma] : ds;
                var port = comma > 0 && int.TryParse(ds[(comma + 1)..], out var p) ? p : 1433;
                var rs = route(host.Split('\\')[0], port);
                b.DataSource = $"tcp:{rs.Host},{rs.Port}";
                b.TrustServerCertificate = true;
                return b.ConnectionString;
            }
            default:
                return cs;
        }
    }

    private static (string Host, int Port) SplitHostPort(string h, int fallbackPort)
    {
        if (h.StartsWith('[') && h.IndexOf("]:", StringComparison.Ordinal) is var e and > 0 && int.TryParse(h[(e + 2)..], out var p6)) return (h[1..e], p6);
        if (h.StartsWith('[') && h.EndsWith(']')) return (h[1..^1], fallbackPort);
        var i = h.LastIndexOf(':');
        return i > 0 && h.IndexOf(':') == i && int.TryParse(h[(i + 1)..], out var p) ? (h[..i], p) : (h, fallbackPort);
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
