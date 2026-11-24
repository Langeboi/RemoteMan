using System.Data.Common;
using Npgsql;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

/// <summary>
/// Recommended shared backend. Writes call pg_notify inside the transaction, which
/// PostgreSQL delivers to every listening client the instant the transaction commits,
/// so other users see changes in milliseconds with no polling load on the server.
/// </summary>
public sealed class PostgresDataSource : SqlDataSource
{
    private const string Channel = "remoteman";
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;

    public PostgresDataSource(string connectionString, int pollMs = 5000) : base(connectionString, pollMs) { }

    public override string DisplayName => $"PostgreSQL ({new NpgsqlConnectionStringBuilder(ConnectionString).Host})";
    public override DataSourceKind Kind => DataSourceKind.PostgreSql;

    protected override void ClearPool() { try { NpgsqlConnection.ClearPool((NpgsqlConnection)CreateConnection()); } catch { } }
    protected override DbConnection CreateConnection() => new NpgsqlConnection(_connectionString ??= WithAppName(ConnectionString));

    private string? _connectionString;

    // Names the app in the server's login history (the admin page shows "RemoteMan 0.1.0" instead of nothing).
    private static string WithAppName(string cs)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(cs);
            if (string.IsNullOrEmpty(b.ApplicationName))
                b.ApplicationName = "RemoteMan " + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "");
            return b.ConnectionString;
        }
        catch (ArgumentException)
        {
            return cs;
        }
    }

    protected override IEnumerable<string> SchemaStatements =>
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

    protected override IEnumerable<(string, bool)> MigrationStatements =>
    [
        ("ALTER TABLE remoteman_entries ADD COLUMN IF NOT EXISTS created_at BIGINT NULL", false),
        ("ALTER TABLE remoteman_entries ADD COLUMN IF NOT EXISTS created_by VARCHAR(200) NULL", false),
    ];

    protected override string? CurrentUserSql => "SELECT session_user";
    protected override bool IsPermissionDenied(DbException ex) => ex is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege };

    protected override string? ServerNowSql => "SELECT CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 1000 AS BIGINT)";
    protected override string NextCounterSql(int id) => $"UPDATE remoteman_meta SET rev = rev + 1 WHERE id = {id} RETURNING rev";
    protected override string? AfterEntryWriteSql => $"SELECT pg_notify('{Channel}', 'e:' || @rev::text)";
    protected override string? AfterPresenceWriteSql => $"SELECT pg_notify('{Channel}', 'p')";
    protected override string? AfterChatWriteSql => $"SELECT pg_notify('{Channel}', 'c:' || @rev::text)";
    protected override string? AfterShareWriteSql => $"SELECT pg_notify('{Channel}', 's')";
    protected override string? ShareTableSql => """
        CREATE TABLE IF NOT EXISTS remoteman_shares (
            id VARCHAR(36) PRIMARY KEY, invitee VARCHAR(200) NOT NULL, host_user VARCHAR(200) NOT NULL, state INT NOT NULL,
            updated_at BIGINT NOT NULL, expires_at BIGINT NOT NULL, payload TEXT NULL)
        """;

    protected override string? ChatFilesTableSql => """
        CREATE TABLE IF NOT EXISTS remoteman_chat_files (
            id VARCHAR(36) PRIMARY KEY, room_id VARCHAR(36) NOT NULL, sender VARCHAR(200) NOT NULL,
            sent_at BIGINT NOT NULL, size BIGINT NOT NULL, data BYTEA NOT NULL)
        """;

    protected override string? SettingsTableSql => """
        CREATE TABLE IF NOT EXISTS remoteman_settings (
            name VARCHAR(100) PRIMARY KEY, value TEXT NOT NULL, updated_at BIGINT NOT NULL, updated_by VARCHAR(200) NULL)
        """;
    protected override string? AfterSettingsWriteSql => $"SELECT pg_notify('{Channel}', 'g')";

    // remoteman-db servers know who is an admin; a plain PostgreSQL server doesn't have the function.
    protected override async Task<bool?> IsServerAdminAsync(DbConnection c, CancellationToken ct)
    {
        try
        {
            await using var cmd = Cmd(c, "SELECT is_admin FROM remoteman_admin.me()");
            return await cmd.ExecuteScalarAsync(ct) is bool b ? b : null;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.UndefinedFunction
                                               or PostgresErrorCodes.InsufficientPrivilege)
        {
            return null;
        }
    }

    public override async Task StartChangeFeedAsync(CancellationToken ct = default)
    {
        // Slow safety-net poll in case a notification is missed during a reconnect.
        await base.StartChangeFeedAsync(ct);
        _listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = Task.Run(() => ListenLoopAsync(_listenCts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { KeepAlive = 15, Pooling = false };
                await using var conn = new NpgsqlConnection(csb.ConnectionString);
                await conn.OpenAsync(ct);
                conn.Notification += (_, e) =>
                {
                    if (e.Payload.StartsWith("e:") && long.TryParse(e.Payload.AsSpan(2), out var rev))
                        RaiseChanged(new ChangeSignal(ChangeSignalKind.Entries, rev));
                    else if (e.Payload == "p")
                        RaiseChanged(new ChangeSignal(ChangeSignalKind.Presence));
                    else if (e.Payload.StartsWith("c:") && long.TryParse(e.Payload.AsSpan(2), out var seq))
                        RaiseChanged(new ChangeSignal(ChangeSignalKind.Chat, seq));
                    else if (e.Payload == "s")
                        RaiseChanged(new ChangeSignal(ChangeSignalKind.Shares));
                    else if (e.Payload == "g")
                        RaiseChanged(new ChangeSignal(ChangeSignalKind.Settings));
                };
                await using (var cmd = new NpgsqlCommand($"LISTEN {Channel}", conn)) await cmd.ExecuteNonQueryAsync(ct);
                // Catch up on anything that happened while we were not listening.
                failures = 0;
                SetConnected(true);
                RaiseChanged(new ChangeSignal(ChangeSignalKind.Entries));
                RaiseChanged(new ChangeSignal(ChangeSignalKind.Chat));
                while (!ct.IsCancellationRequested) await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                // The listening connection dropped: reconnect at once, and only show offline when that fails too.
                if (++failures == 1) continue;
                SetConnected(false);
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _listenCts?.Cancel();
        if (_listenTask is not null) { try { await _listenTask; } catch { } }
        await base.DisposeAsync();
    }
}
