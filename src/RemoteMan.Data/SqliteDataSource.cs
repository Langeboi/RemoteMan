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
        """
        CREATE TABLE IF NOT EXISTS remoteman_history (
            revision INTEGER PRIMARY KEY, entry_id TEXT NOT NULL, parent_id TEXT NULL, kind INTEGER NOT NULL, name TEXT NOT NULL,
            payload TEXT NULL, changed_at INTEGER NOT NULL, changed_by TEXT NULL, deleted INTEGER NOT NULL DEFAULT 0,
            action INTEGER NOT NULL DEFAULT 0, fields TEXT NULL)
        """,
        "CREATE INDEX IF NOT EXISTS ix_remoteman_history_entry ON remoteman_history (entry_id, revision)",
    ];

    protected override IEnumerable<(string, bool)> MigrationStatements =>
    [
        ("ALTER TABLE remoteman_entries ADD COLUMN created_at INTEGER NULL", true),
        ("ALTER TABLE remoteman_entries ADD COLUMN created_by TEXT NULL", true),
        ("ALTER TABLE remoteman_presence ADD COLUMN sessions TEXT NULL", true),
    ];

    protected override string NextRevisionSql => "UPDATE remoteman_meta SET rev = rev + 1 WHERE id = 1 RETURNING rev";
}
