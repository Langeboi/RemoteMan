namespace RemoteMan.Data;

/// <summary>
/// RemoteMan's PostgreSQL tables, in one place. The app creates them when it connects (<see cref="PostgresDataSource"/>),
/// and the gateway relay's hosted workspaces create them up front and back them up (this file is linked into
/// RemoteMan.Gateway.Server, so keep it free of other dependencies).
/// </summary>
public static class PostgresSchema
{
    public static readonly string[] Tables =
    [
        """
        CREATE TABLE IF NOT EXISTS remoteman_entries (
            id VARCHAR(36) PRIMARY KEY, parent_id VARCHAR(36) NULL, kind INT NOT NULL, name VARCHAR(400) NOT NULL,
            payload TEXT NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL,
            updated_by VARCHAR(200) NULL, deleted INT NOT NULL DEFAULT 0)
        """,
        "CREATE INDEX IF NOT EXISTS ix_remoteman_entries_revision ON remoteman_entries (revision)",
        """
        CREATE TABLE IF NOT EXISTS remoteman_presence (
            user_name VARCHAR(200) NOT NULL, machine VARCHAR(200) NOT NULL, entry_id VARCHAR(36) NOT NULL,
            state INT NOT NULL, last_seen BIGINT NOT NULL, PRIMARY KEY (user_name, machine))
        """,
        "CREATE TABLE IF NOT EXISTS remoteman_meta (id INT PRIMARY KEY, rev BIGINT NOT NULL)",
        "INSERT INTO remoteman_meta (id, rev) VALUES (1, 0) ON CONFLICT (id) DO NOTHING",
        // Chat: row 2 of the meta table numbers the messages.
        "INSERT INTO remoteman_meta (id, rev) VALUES (2, 0) ON CONFLICT (id) DO NOTHING",
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_rooms (
            id VARCHAR(36) PRIMARY KEY, kind INT NOT NULL, name VARCHAR(200) NULL, members TEXT NULL,
            created_by VARCHAR(200) NULL, updated_at BIGINT NOT NULL, deleted INT NOT NULL DEFAULT 0)
        """,
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_messages (
            seq BIGINT PRIMARY KEY, id VARCHAR(36) NOT NULL UNIQUE, room_id VARCHAR(36) NOT NULL,
            sender VARCHAR(200) NOT NULL, sent_at BIGINT NOT NULL, body TEXT NOT NULL)
        """,
        "CREATE INDEX IF NOT EXISTS ix_remoteman_chat_messages_sent ON remoteman_chat_messages (sent_at)",
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_reads (
            user_name VARCHAR(200) NOT NULL, room_id VARCHAR(36) NOT NULL, last_read BIGINT NOT NULL, PRIMARY KEY (user_name, room_id))
        """,
    ];

    /// <summary>Upgrades for databases made by older versions (idempotent).</summary>
    public static readonly string[] Migrations =
    [
        "ALTER TABLE remoteman_entries ADD COLUMN IF NOT EXISTS created_at BIGINT NULL",
        "ALTER TABLE remoteman_entries ADD COLUMN IF NOT EXISTS created_by VARCHAR(200) NULL",
    ];

    public const string Shares = """
        CREATE TABLE IF NOT EXISTS remoteman_shares (
            id VARCHAR(36) PRIMARY KEY, invitee VARCHAR(200) NOT NULL, host_user VARCHAR(200) NOT NULL, state INT NOT NULL,
            updated_at BIGINT NOT NULL, expires_at BIGINT NOT NULL, payload TEXT NULL)
        """;

    public const string ChatFiles = """
        CREATE TABLE IF NOT EXISTS remoteman_chat_files (
            id VARCHAR(36) PRIMARY KEY, room_id VARCHAR(36) NOT NULL, sender VARCHAR(200) NOT NULL,
            sent_at BIGINT NOT NULL, size BIGINT NOT NULL, data BYTEA NOT NULL)
        """;

    public const string Settings = """
        CREATE TABLE IF NOT EXISTS remoteman_settings (
            name VARCHAR(100) PRIMARY KEY, value TEXT NOT NULL, updated_at BIGINT NOT NULL, updated_by VARCHAR(200) NULL)
        """;

    /// <summary>Every statement that makes a complete, current database, in order.</summary>
    public static IEnumerable<string> All => [.. Tables, .. Migrations, Shares, ChatFiles, Settings];

    /// <summary>
    /// The tables a backup keeps, in an order that restores cleanly. Presence (who is online now) and shared-session
    /// invites (ten minutes long) are left out: they mean nothing a day later.
    /// </summary>
    public static readonly string[] BackupTables =
    [
        "remoteman_meta", "remoteman_entries", "remoteman_settings",
        "remoteman_chat_rooms", "remoteman_chat_messages", "remoteman_chat_reads", "remoteman_chat_files",
    ];
}
