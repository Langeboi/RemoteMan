namespace Tether.Core.Storage;

/// <summary>
/// User-facing data source definition (saved in %APPDATA%\Tether\datasources.json).
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

    /// <summary>Shared sources encrypt secrets with a key derived from this vault passphrase
    /// (stored locally with DPAPI), so every member of the team can decrypt.</summary>
    public bool UseSharedVaultKey { get; set; }
}
