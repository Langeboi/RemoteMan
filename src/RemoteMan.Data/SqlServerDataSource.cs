using System.Data.Common;
using Microsoft.Data.SqlClient;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

/// <summary>
/// Microsoft SQL Server / Azure SQL. Changes are detected by polling the one-row
/// revision counter (default every 500 ms); that is a clustered-key point read,
/// so even dozens of clients cost the server next to nothing. For redundancy, point
/// the first connection string at an Always On listener (MultiSubnetFailover=True)
/// or list a secondary server as the next connection string.
/// </summary>
public sealed class SqlServerDataSource : SqlDataSource
{
    public SqlServerDataSource(string connectionString, int pollMs = 500) : base(connectionString, pollMs) { }

    public override string DisplayName => $"SQL Server ({new SqlConnectionStringBuilder(ConnectionString).DataSource})";
    public override DataSourceKind Kind => DataSourceKind.SqlServer;

    protected override DbConnection CreateConnection() => new SqlConnection(ConnectionString);

    protected override IEnumerable<string> SchemaStatements =>
    [
        """
        IF OBJECT_ID(N'dbo.remoteman_entries', N'U') IS NULL
        CREATE TABLE dbo.remoteman_entries (
            id VARCHAR(36) NOT NULL PRIMARY KEY, parent_id VARCHAR(36) NULL, kind INT NOT NULL, name NVARCHAR(400) NOT NULL,
            payload NVARCHAR(MAX) NULL, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL,
            updated_by NVARCHAR(200) NULL, deleted INT NOT NULL DEFAULT 0)
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_remoteman_entries_revision')
        CREATE INDEX ix_remoteman_entries_revision ON dbo.remoteman_entries (revision)
        """,
        """
        IF OBJECT_ID(N'dbo.remoteman_presence', N'U') IS NULL
        CREATE TABLE dbo.remoteman_presence (
            user_name NVARCHAR(200) NOT NULL, machine NVARCHAR(200) NOT NULL, entry_id VARCHAR(36) NOT NULL,
            state INT NOT NULL, last_seen BIGINT NOT NULL, PRIMARY KEY (user_name, machine))
        """,
        """
        IF OBJECT_ID(N'dbo.remoteman_meta', N'U') IS NULL
        CREATE TABLE dbo.remoteman_meta (id INT NOT NULL PRIMARY KEY, rev BIGINT NOT NULL)
        """,
        "IF NOT EXISTS (SELECT 1 FROM dbo.remoteman_meta WHERE id = 1) INSERT INTO dbo.remoteman_meta (id, rev) VALUES (1, 0)",
        """
        IF OBJECT_ID(N'dbo.remoteman_history', N'U') IS NULL
        CREATE TABLE dbo.remoteman_history (
            revision BIGINT NOT NULL PRIMARY KEY, entry_id VARCHAR(36) NOT NULL, parent_id VARCHAR(36) NULL, kind INT NOT NULL, name NVARCHAR(400) NOT NULL,
            payload NVARCHAR(MAX) NULL, changed_at BIGINT NOT NULL, changed_by NVARCHAR(200) NULL, deleted INT NOT NULL DEFAULT 0,
            action INT NOT NULL DEFAULT 0, fields NVARCHAR(MAX) NULL)
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_remoteman_history_entry')
        CREATE INDEX ix_remoteman_history_entry ON dbo.remoteman_history (entry_id, revision)
        """,
    ];

    protected override IEnumerable<(string, bool)> MigrationStatements =>
    [
        ("IF COL_LENGTH('dbo.remoteman_entries', 'created_at') IS NULL ALTER TABLE dbo.remoteman_entries ADD created_at BIGINT NULL, created_by NVARCHAR(200) NULL", false),
        ("IF COL_LENGTH('dbo.remoteman_presence', 'sessions') IS NULL ALTER TABLE dbo.remoteman_presence ADD sessions NVARCHAR(MAX) NULL", false),
    ];

    /// <summary>DOMAIN\user with Windows authentication, the SQL login name otherwise.</summary>
    protected override string? CurrentUserSql => "SELECT SUSER_SNAME()";

    protected override string Top(string columns, string rest, int n) => $"SELECT TOP ({n}) {columns} {rest}";

    protected override string NextRevisionSql => "UPDATE dbo.remoteman_meta SET rev = rev + 1 OUTPUT inserted.rev WHERE id = 1";
}
