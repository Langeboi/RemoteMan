using System.Data.Common;
using Microsoft.Data.Sqlite;
using Tether.Core.Storage;

namespace Tether.Data;

/// <summary>Default, zero-setup local store: one file in %APPDATA%\Tether.</summary>
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
        CREATE TABLE IF NOT EXISTS tether_entries (
            id TEXT PRIMARY KEY, parent_id TEXT NULL, kind INTEGER NOT NULL, name TEXT NOT NULL,
            payload TEXT NULL, revision INTEGER NOT NULL, updated_at INTEGER NOT NULL,
            updated_by TEXT NULL, deleted INTEGER NOT NULL DEFAULT 0)
        """,
        "CREATE INDEX IF NOT EXISTS ix_tether_entries_revision ON tether_entries (revision)",
        """
        CREATE TABLE IF NOT EXISTS tether_presence (
            user_name TEXT NOT NULL, machine TEXT NOT NULL, entry_id TEXT NOT NULL,
            state INTEGER NOT NULL, last_seen INTEGER NOT NULL, PRIMARY KEY (user_name, machine))
        """,
        "CREATE TABLE IF NOT EXISTS tether_meta (id INTEGER PRIMARY KEY, rev INTEGER NOT NULL)",
        "INSERT OR IGNORE INTO tether_meta (id, rev) VALUES (1, 0)",
    ];

    protected override string NextRevisionSql => "UPDATE tether_meta SET rev = rev + 1 WHERE id = 1 RETURNING rev";
}
