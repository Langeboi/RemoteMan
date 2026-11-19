using System.Data.Common;
using MySqlConnector;
using Tether.Core.Storage;

namespace Tether.Data;

/// <summary>MySQL / MariaDB. Polls the revision counter (default 500 ms).</summary>
public sealed class MySqlDataSource : SqlDataSource
{
    public MySqlDataSource(string connectionString, int pollMs = 500) : base(connectionString, pollMs) { }

    public override string DisplayName => $"MySQL ({new MySqlConnectionStringBuilder(ConnectionString).Server})";
    public override DataSourceKind Kind => DataSourceKind.MySql;

    protected override DbConnection CreateConnection() => new MySqlConnection(ConnectionString);

    protected override IEnumerable<string> SchemaStatements =>
    [
        """
        CREATE TABLE IF NOT EXISTS tether_entries (
            id VARCHAR(36) NOT NULL PRIMARY KEY, parent_id VARCHAR(36) NULL, kind INT NOT NULL, name VARCHAR(400) NOT NULL,
            payload LONGTEXT NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL,
            updated_by VARCHAR(200) NULL, deleted INT NOT NULL DEFAULT 0,
            INDEX ix_tether_entries_revision (revision)) CHARACTER SET utf8mb4
        """,
        """
        CREATE TABLE IF NOT EXISTS tether_presence (
            user_name VARCHAR(200) NOT NULL, machine VARCHAR(200) NOT NULL, entry_id VARCHAR(36) NOT NULL,
            state INT NOT NULL, last_seen BIGINT NOT NULL, PRIMARY KEY (user_name, machine)) CHARACTER SET utf8mb4
        """,
        "CREATE TABLE IF NOT EXISTS tether_meta (id INT NOT NULL PRIMARY KEY, rev BIGINT NOT NULL)",
        "INSERT IGNORE INTO tether_meta (id, rev) VALUES (1, 0)",
    ];

    // LAST_INSERT_ID(expr) stores the value per-connection, so this is race free.
    protected override string NextRevisionSql => "UPDATE tether_meta SET rev = LAST_INSERT_ID(rev + 1) WHERE id = 1; SELECT LAST_INSERT_ID();";
}
