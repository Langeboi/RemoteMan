using System.Data.Common;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

/// <summary>
/// Settings the whole team shares (the Zabbix and Oxidized connections), one JSON row per setting in
/// <c>remoteman_settings</c>. On a server set up with remoteman-db the table belongs to postgres and a
/// trigger lets only admins change it; elsewhere anyone who can edit entries can change it.
/// </summary>
public abstract partial class SqlDataSource
{
    /// <summary>Creates <c>remoteman_settings</c> if it isn't there; null where shared settings aren't offered.</summary>
    protected virtual string? SettingsTableSql => null;
    /// <summary>Statement run after a setting changes (PostgreSQL: NOTIFY so everyone picks it up at once).</summary>
    protected virtual string? AfterSettingsWriteSql => null;
    /// <summary>Whether the server says this login is a RemoteMan admin; null when the server can't tell (no remoteman-db functions).</summary>
    protected virtual Task<bool?> IsServerAdminAsync(DbConnection c, CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <summary>The settings table exists (created here, or by the server's setup).</summary>
    protected bool SettingsReady { get; private set; }
    public bool SupportsSettings => SettingsTableSql is not null && SettingsReady;
    public bool CanChangeSettings { get; private set; }

    private long _seenSettingsStamp = -1;

    private async Task InitializeSettingsAsync(DbConnection c, CancellationToken ct)
    {
        if (SettingsTableSql is not { } sql) return;
        try { await using var cmd = Cmd(c, sql); await cmd.ExecuteNonQueryAsync(ct); SettingsReady = true; }
        catch (DbException) { SettingsReady = await TableExistsAsync(c, "remoteman_settings", ct); }
        if (!SettingsReady) { CanChangeSettings = false; return; }
        bool? admin = null;
        try { admin = await IsServerAdminAsync(c, ct); }
        catch (DbException) { }
        CanChangeSettings = admin ?? !IsReadOnly;
    }

    private static async Task<bool> TableExistsAsync(DbConnection c, string table, CancellationToken ct)
    {
        try { await using var probe = Cmd(c, $"SELECT COUNT(*) FROM {table} WHERE 1 = 0"); await probe.ExecuteScalarAsync(ct); return true; }
        catch (DbException) { return false; }
    }

    public async Task<IReadOnlyList<SharedSetting>> GetSettingsAsync(CancellationToken ct = default)
    {
        if (!SupportsSettings) return [];
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT name, value, updated_at, updated_by FROM remoteman_settings");
        var list = new List<SharedSetting>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SharedSetting(r.GetString(0), r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(2))),
                r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public async Task PutSettingAsync(string name, string value, string user, CancellationToken ct = default)
    {
        if (!SupportsSettings) throw new NotSupportedException("This data source has no shared settings.");
        if (!CanChangeSettings) throw SettingsDenied();
        try { await PutSettingCoreAsync(name, value, DatabaseUser ?? user, ct); }
        // Not a connection problem, so it must not take the data source offline or fail over.
        catch (DbException ex) when (IsPermissionDenied(ex)) { CanChangeSettings = false; throw SettingsDenied(); }
    }

    private Exception SettingsDenied() => new UnauthorizedAccessException(
        $"Only database admins can change these settings{(DatabaseUser is { } u ? $" ({u} isn't one)" : "")}. Ask an admin, or have one give you Admin access in Data sources > Users.");

    private async Task PutSettingCoreAsync(string name, string value, string user, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var args = new (string, object?)[] { ("@name", name), ("@value", value), ("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("@by", user) };
        int n;
        await using (var upd = Cmd(c, "UPDATE remoteman_settings SET value=@value, updated_at=@at, updated_by=@by WHERE name=@name", tx, args))
            n = await upd.ExecuteNonQueryAsync(ct);
        if (n == 0)
        {
            await using var ins = Cmd(c, "INSERT INTO remoteman_settings (name, value, updated_at, updated_by) VALUES (@name, @value, @at, @by)", tx, args);
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (AfterSettingsWriteSql is { } notify)
        {
            await using var nc = Cmd(c, notify, tx);
            await nc.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Called from the poll loop: raises Changed when a setting was added or changed.</summary>
    private async Task PollSettingsAsync(DbConnection c, CancellationToken ct)
    {
        if (!SupportsSettings) return;
        try
        {
            await using var cmd = Cmd(c, "SELECT COALESCE(MAX(updated_at), 0) + COUNT(*) FROM remoteman_settings");
            var stamp = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            var before = Interlocked.Exchange(ref _seenSettingsStamp, stamp);
            if (before != -1 && stamp != before) RaiseChanged(new ChangeSignal(ChangeSignalKind.Settings));
        }
        catch (DbException) { }
    }
}
