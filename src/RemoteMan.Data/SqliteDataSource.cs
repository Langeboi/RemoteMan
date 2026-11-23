using System.Data.Common;
using Microsoft.Data.Sqlite;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

/// <summary>Default, zero-setup local store: one file in %APPDATA%\RemoteMan.</summary>
public sealed class SqliteDataSource : SqlDataSource
{
    private readonly string _path;

    public SqliteDataSource(string path, int pollMs = 1000)
        : base(new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, DefaultTimeout = 10 }.ToString(), pollMs)
    {
        _path = path;
    }

    public override string DisplayName => $"Local ({Path.GetFileName(_path)})";
    public override DataSourceKind Kind => DataSourceKind.Sqlite;

    protected override DbConnection CreateConnection() => new SqliteConnection(ConnectionString);

    public override Task InitializeAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return base.InitializeAsync(ct);
    }

    protected override IEnumerable<string> SchemaStatements =>
    [
        "PRAGMA journal_mode=WAL",
        """
        CREATE TABLE IF NOT EXISTS remoteman_entries (
            id TEXT PRIMARY KEY, parent_id TEXT NULL, kind INTEGER NOT NULL, name TEXT NOT NULL,
            payload TEXT NULL, revision INTEGER NOT NULL, updated_at INTEGER NOT NULL,
            updated_by TEXT NULL, deleted INTEGER NOT NULL DEFAULT 0)
        """,
        "CREATE INDEX IF NOT EXISTS ix_remoteman_entries_revision ON remoteman_entries (revision)",
        """
        CREATE TABLE IF NOT EXISTS remoteman_presence (
            user_name TEXT NOT NULL, machine TEXT NOT NULL, entry_id TEXT NOT NULL,
            state INTEGER NOT NULL, last_seen INTEGER NOT NULL, PRIMARY KEY (user_name, machine))
        """,
        "CREATE TABLE IF NOT EXISTS remoteman_meta (id INTEGER PRIMARY KEY, rev INTEGER NOT NULL)",
        "INSERT OR IGNORE INTO remoteman_meta (id, rev) VALUES (1, 0)",
        // Chat tables exist here too (tests use them), but the app only offers chat on shared databases.
        "INSERT OR IGNORE INTO remoteman_meta (id, rev) VALUES (2, 0)",
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_rooms (
            id TEXT PRIMARY KEY, kind INTEGER NOT NULL, name TEXT NULL, members TEXT NULL,
            created_by TEXT NULL, updated_at INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0)
        """,
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_messages (
            seq INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, room_id TEXT NOT NULL,
            sender TEXT NOT NULL, sent_at INTEGER NOT NULL, body TEXT NOT NULL)
        """,
        "CREATE INDEX IF NOT EXISTS ix_remoteman_chat_messages_sent ON remoteman_chat_messages (sent_at)",
        """
        CREATE TABLE IF NOT EXISTS remoteman_chat_reads (
            user_name TEXT NOT NULL, room_id TEXT NOT NULL, last_read INTEGER NOT NULL, PRIMARY KEY (user_name, room_id))
        """,
    ];

    protected override IEnumerable<(string, bool)> MigrationStatements =>
    [
        ("ALTER TABLE remoteman_entries ADD COLUMN created_at INTEGER NULL", true),
        ("ALTER TABLE remoteman_entries ADD COLUMN created_by TEXT NULL", true),
    ];

    protected override string? SettingsTableSql => """
        CREATE TABLE IF NOT EXISTS remoteman_settings (
            name TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL, updated_by TEXT NULL)
        """;

    protected override string NextCounterSql(int id) => $"UPDATE remoteman_meta SET rev = rev + 1 WHERE id = {id} RETURNING rev";
    public override bool SupportsChat => false;
}
