using System.Text.Json.Serialization;

namespace RemoteMan.Core.Storage;

/// <summary>
/// User-facing data source definition (saved in %APPDATA%\RemoteMan\datasources.json).
/// <see cref="ConnectionStrings"/> holds one or more servers: the first is preferred, the
/// others are used when it is unreachable (e.g. an Always On listener + a DR replica,
/// or a Postgres primary + standby).
/// </summary>
public sealed class DataSourceConfig
{
    public string Name { get; set; } = "Local";
    public DataSourceKind Kind { get; set; } = DataSourceKind.Sqlite;
    public List<string> ConnectionStrings { get; set; } = new();

    /// <summary>Polling interval for backends without push notifications (MySQL, SQL Server without Service Broker).</summary>
    public int PollMilliseconds { get; set; } = 1000;

    /// <summary>Single sign-on to the database with the current Windows account (SQL Server integrated
    /// security, PostgreSQL Kerberos/SSPI, MySQL Windows auth / MariaDB GSSAPI). No database password is stored.</summary>
    public bool UseWindowsLogin { get; set; }

    /// <summary>Shared sources encrypt secrets with a key derived from this vault passphrase
    /// (stored locally with DPAPI), so every member of the team can decrypt.</summary>
    public bool UseSharedVaultKey { get; set; }

    /// <summary>RemoteMan gateway site the database servers are reached through when they don't answer directly
    /// (off-site, behind CG-NAT). Null = direct only.</summary>
    public string? GatewaySite { get; set; }
    /// <summary>Always use the gateway, without trying the servers directly first.</summary>
    public bool GatewayAlways { get; set; }

    /// <summary>A workspace on a RemoteMan account (hosted on the relay's database server). Added, renamed and removed
    /// by the account's sign-in, not by hand. Null = a data source you set up yourself.</summary>
    public HostedLink? Hosted { get; set; }

    /// <summary>The account's database password for a hosted workspace. Never written to settings.json: it comes from
    /// the account file (DPAPI-protected) when RemoteMan starts, and from the server at each check.</summary>
    [JsonIgnore] public string? RuntimePassword { get; set; }

    /// <summary>The hosted server's CA certificate (a PEM file), for full TLS verification. Null = encrypted, not verified.</summary>
    [JsonIgnore] public string? RuntimeRootCertificate { get; set; }
}

/// <summary>Where a hosted workspace came from: the RemoteMan server, the account and the workspace's id there.</summary>
public sealed class HostedLink
{
    public string Server { get; set; } = "";
    public string Account { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    /// <summary>"admin", "edit" or "read".</summary>
    public string Access { get; set; } = "edit";
    public string? Description { get; set; }

    [JsonIgnore] public string AccessLabel => Access switch { "admin" => "Admin", "read" => "Read only", _ => "Can edit" };
}
