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

    protected override IEnumerable<string> SchemaStatements => PostgresSchema.Tables;

    protected override IEnumerable<(string, bool)> MigrationStatements => PostgresSchema.Migrations.Select(m => (m, false));

    protected override string? CurrentUserSql => "SELECT session_user";
    protected override bool IsPermissionDenied(DbException ex) => ex is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege };

    protected override string? ServerNowSql => "SELECT CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 1000 AS BIGINT)";
    protected override string NextCounterSql(int id) => $"UPDATE remoteman_meta SET rev = rev + 1 WHERE id = {id} RETURNING rev";
    protected override string? AfterEntryWriteSql => $"SELECT pg_notify('{Channel}', 'e:' || @rev::text)";
    protected override string? AfterPresenceWriteSql => $"SELECT pg_notify('{Channel}', 'p')";
    protected override string? AfterChatWriteSql => $"SELECT pg_notify('{Channel}', 'c:' || @rev::text)";
    protected override string? AfterShareWriteSql => $"SELECT pg_notify('{Channel}', 's')";
    protected override string? ShareTableSql => PostgresSchema.Shares;

    protected override string? ChatFilesTableSql => PostgresSchema.ChatFiles;

    protected override string? SettingsTableSql => PostgresSchema.Settings;
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
