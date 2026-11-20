using System.Data.Common;
using MySqlConnector;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

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
        CREATE TABLE IF NOT EXISTS remoteman_entries (
            id VARCHAR(36) NOT NULL PRIMARY KEY, parent_id VARCHAR(36) NULL, kind INT NOT NULL, name VARCHAR(400) NOT NULL,
            payload LONGTEXT NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL,
            updated_by VARCHAR(200) NULL, deleted INT NOT NULL DEFAULT 0,
            INDEX ix_remoteman_entries_revision (revision)) CHARACTER SET utf8mb4
        """,
        """
        CREATE TABLE IF NOT EXISTS remoteman_presence (
            user_name VARCHAR(200) NOT NULL, machine VARCHAR(200) NOT NULL, entry_id VARCHAR(36) NOT NULL,
            state INT NOT NULL, last_seen BIGINT NOT NULL, PRIMARY KEY (user_name, machine)) CHARACTER SET utf8mb4
        """,
        "CREATE TABLE IF NOT EXISTS remoteman_meta (id INT NOT NULL PRIMARY KEY, rev BIGINT NOT NULL)",
        "INSERT IGNORE INTO remoteman_meta (id, rev) VALUES (1, 0)",
        """
        CREATE TABLE IF NOT EXISTS remoteman_history (
            revision BIGINT NOT NULL PRIMARY KEY, entry_id VARCHAR(36) NOT NULL, parent_id VARCHAR(36) NULL, kind INT NOT NULL, name VARCHAR(400) NOT NULL,
            payload LONGTEXT NULL, changed_at BIGINT NOT NULL, changed_by VARCHAR(200) NULL, deleted INT NOT NULL DEFAULT 0,
            action INT NOT NULL DEFAULT 0, fields TEXT NULL,
            INDEX ix_remoteman_history_entry (entry_id, revision)) CHARACTER SET utf8mb4
        """,
    ];

    protected override IEnumerable<(string, bool)> MigrationStatements =>
    [
        ("ALTER TABLE remoteman_entries ADD COLUMN created_at BIGINT NULL", true),
        ("ALTER TABLE remoteman_entries ADD COLUMN created_by VARCHAR(200) NULL", true),
        ("ALTER TABLE remoteman_presence ADD COLUMN sessions TEXT NULL", true),
    ];

    // USER() is the name the client logged in with (CURRENT_USER() may be a wildcard account).
    protected override string? CurrentUserSql => "SELECT SUBSTRING_INDEX(USER(), '@', 1)";

    // LAST_INSERT_ID(expr) stores the value per-connection, so this is race free.
    protected override string NextRevisionSql => "UPDATE remoteman_meta SET rev = LAST_INSERT_ID(rev + 1) WHERE id = 1; SELECT LAST_INSERT_ID();";
}
