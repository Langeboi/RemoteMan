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
        // Chat: row 2 of the meta table numbers the messages.
        "INSERT IGNORE INTO remoteman_meta (id, rev) VALUES (2, 0)",
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_rooms (
            id VARCHAR(36) NOT NULL PRIMARY KEY, kind INT NOT NULL, name VARCHAR(200) NULL, members TEXT NULL,
            created_by VARCHAR(200) NULL, updated_at BIGINT NOT NULL, deleted INT NOT NULL DEFAULT 0) CHARACTER SET utf8mb4
        """,
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_messages (
            seq BIGINT NOT NULL PRIMARY KEY, id VARCHAR(36) NOT NULL, room_id VARCHAR(36) NOT NULL,
            sender VARCHAR(200) NOT NULL, sent_at BIGINT NOT NULL, body MEDIUMTEXT NOT NULL,
            UNIQUE KEY ux_remoteman_chat_messages_id (id), INDEX ix_remoteman_chat_messages_sent (sent_at)) CHARACTER SET utf8mb4
        """,
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_reads (
            user_name VARCHAR(200) NOT NULL, room_id VARCHAR(36) NOT NULL, last_read BIGINT NOT NULL, PRIMARY KEY (user_name, room_id)) CHARACTER SET utf8mb4
        """,
    ];

    protected override IEnumerable<(string, bool)> MigrationStatements =>
    [
        ("ALTER TABLE remoteman_entries ADD COLUMN created_at BIGINT NULL", true),
        ("ALTER TABLE remoteman_entries ADD COLUMN created_by VARCHAR(200) NULL", true),
    ];

    // USER() is the name the client logged in with (CURRENT_USER() may be a wildcard account).
    protected override string? CurrentUserSql => "SELECT SUBSTRING_INDEX(USER(), '@', 1)";

    // LAST_INSERT_ID(expr) stores the value per-connection, so this is race free.
    protected override string NextCounterSql(int id) => $"UPDATE remoteman_meta SET rev = LAST_INSERT_ID(rev + 1) WHERE id = {id}; SELECT LAST_INSERT_ID();";
}
