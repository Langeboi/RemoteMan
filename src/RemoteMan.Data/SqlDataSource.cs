using System.Data;
using System.Data.Common;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

/// <summary>
/// Shared implementation for every SQL backend. Design choices that keep sync fast:
/// <list type="bullet">
/// <item>One table, one indexed <c>revision</c> column. "What changed?" is a range scan.</item>
/// <item>Revisions come from a one-row counter (<c>remoteman_meta</c>) updated inside the
/// write transaction. The row lock is held until commit, so revisions become visible in
/// order and a reader can never skip a change that commits late.</item>
/// <item>Deletes are soft (tombstones) so they propagate like any other change.</item>
/// <item>Clients detect changes by NOTIFY (PostgreSQL) or by polling that single counter
/// row, which costs well under a millisecond.</item>
/// </list>
/// </summary>
public abstract partial class SqlDataSource : IDataSource
{
    protected readonly string ConnectionString;
    private readonly int _pollMs;
    private CancellationTokenSource? _feedCts;
    private Task? _feedTask;
    private long _seenRevision = -1;
    private long _seenPresenceStamp = -1;
    private long _seenChatSeq = -1;
    private long _seenSharesStamp = -1;

    protected SqlDataSource(string connectionString, int pollMs)
    {
        ConnectionString = connectionString;
        _pollMs = Math.Max(100, pollMs);
    }

    public abstract string DisplayName { get; }
    public abstract DataSourceKind Kind { get; }
    public virtual bool SupportsPresence => true;

    public event EventHandler<ChangeSignal>? Changed;
    protected void RaiseChanged(ChangeSignal s) => Changed?.Invoke(this, s);

    private int _connected = 1;
    public bool IsConnected => Volatile.Read(ref _connected) == 1;
    public event EventHandler<bool>? ConnectionChanged;
    protected void SetConnected(bool value)
    {
        if (Interlocked.Exchange(ref _connected, value ? 1 : 0) != (value ? 1 : 0)) ConnectionChanged?.Invoke(this, value);
    }

    protected abstract DbConnection CreateConnection();
    protected abstract IEnumerable<string> SchemaStatements { get; }
    /// <summary>Statement that increments a counter row of remoteman_meta (1 = entry revisions, 2 = chat messages) and returns the new value.</summary>
    protected abstract string NextCounterSql(int id);
    protected string NextRevisionSql => NextCounterSql(1);
    protected virtual string? AfterEntryWriteSql => null;
    protected virtual string? AfterPresenceWriteSql => null;
    /// <summary>Statement run after an invite changes (PostgreSQL: NOTIFY so the invitee sees it at once).</summary>
    protected virtual string? AfterShareWriteSql => null;
    /// <summary>Creates <c>remoteman_shares</c> (shared-session invites) if it isn't there; null where sharing isn't offered.</summary>
    protected virtual string? ShareTableSql => null;

    protected async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var c = CreateConnection();
        await c.OpenAsync(ct);
        return c;
    }

    protected static DbCommand Cmd(DbConnection c, string sql, DbTransaction? tx = null, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    /// <summary>Query returning the login name of the current connection; null when the source has no logins.</summary>
    protected virtual string? CurrentUserSql => null;

    /// <summary>Idempotent upgrades for databases created by older versions. Errors are ignored where flagged.</summary>
    protected virtual IEnumerable<(string Sql, bool IgnoreErrors)> MigrationStatements => [];

    public string? DatabaseUser { get; private set; }

    /// <summary>True when the server refused a write because of the login's rights (a read-only login).</summary>
    protected virtual bool IsPermissionDenied(DbException ex) => false;

    /// <summary>The login can read but not change anything; known after <see cref="InitializeAsync"/>.</summary>
    public bool IsReadOnly { get; private set; }

    private Exception ReadOnlyError() => new UnauthorizedAccessException(
        $"Your database login{(DatabaseUser is { } u ? $" ({u})" : "")} can only read. Ask whoever runs the database for edit rights.");

    public virtual async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        // A read-only login can't create or upgrade the tables. That's fine as long as they exist:
        // it then works read-only. If they don't exist yet, someone with edit rights has to connect first.
        var denied = false;
        foreach (var sql in SchemaStatements)
        {
            try { await using var cmd = Cmd(c, sql); await cmd.ExecuteNonQueryAsync(ct); }
            catch (DbException ex) when (IsPermissionDenied(ex)) { denied = true; }
        }
        foreach (var (sql, ignore) in MigrationStatements)
        {
            try { await using var cmd = Cmd(c, sql); await cmd.ExecuteNonQueryAsync(ct); }
            catch (DbException ex) when (ignore || IsPermissionDenied(ex)) { denied |= !ignore; }
        }
        if (ShareTableSql is { } shares)
        {
            try { await using var cmd = Cmd(c, shares); await cmd.ExecuteNonQueryAsync(ct); SharesReady = true; }
            catch (DbException) { SharesReady = await SharesTableExistsAsync(c, ct); }
        }
        if (denied)
        {
            await using var probe = Cmd(c, "SELECT COUNT(*) FROM remoteman_meta");
            await probe.ExecuteScalarAsync(ct);   // throws if the tables aren't there (or can't be read)
        }
        IsReadOnly = denied;
        if (CurrentUserSql is { } who)
        {
            await using var cmd = Cmd(c, who);
            DatabaseUser = (await cmd.ExecuteScalarAsync(ct))?.ToString();
        }
    }

    // ---------------- shared-session invites ----------------

    /// <summary>The invites table exists (created here, or by someone with more rights).</summary>
    protected bool SharesReady { get; private set; }
    public virtual bool SupportsSharing => ShareTableSql is not null && SharesReady;

    private static async Task<bool> SharesTableExistsAsync(DbConnection c, CancellationToken ct)
    {
        try { await using var probe = Cmd(c, "SELECT COUNT(*) FROM remoteman_shares WHERE 1 = 0"); await probe.ExecuteScalarAsync(ct); return true; }
        catch (DbException) { return false; }
    }

    public async Task PutShareInviteAsync(RemoteMan.Core.Sharing.ShareInvite i, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Old invites are only clutter: anything that expired an hour ago goes.
        await using (var clean = Cmd(c, "DELETE FROM remoteman_shares WHERE expires_at < @old", tx, ("@old", now - 3_600_000)))
            await clean.ExecuteNonQueryAsync(ct);
        var args = new (string, object?)[]
        {
            ("@id", i.Id.ToString()), ("@invitee", i.Invitee), ("@host", i.HostUser), ("@state", (int)i.State),
            ("@at", now), ("@exp", i.ExpiresAt.ToUnixTimeMilliseconds()), ("@payload", i.ToPayload()),
        };
        int n;
        await using (var upd = Cmd(c, "UPDATE remoteman_shares SET invitee=@invitee, host_user=@host, state=@state, updated_at=@at, expires_at=@exp, payload=@payload WHERE id=@id", tx, args))
            n = await upd.ExecuteNonQueryAsync(ct);
        if (n == 0)
        {
            await using var ins = Cmd(c, "INSERT INTO remoteman_shares (id, invitee, host_user, state, updated_at, expires_at, payload) VALUES (@id, @invitee, @host, @state, @at, @exp, @payload)", tx, args);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await NotifySharesAsync(c, tx, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetShareInviteStateAsync(Guid inviteId, RemoteMan.Core.Sharing.ShareInviteState state, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var upd = Cmd(c, "UPDATE remoteman_shares SET state=@state, updated_at=@at WHERE id=@id", tx,
                         ("@state", (int)state), ("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("@id", inviteId.ToString())))
            await upd.ExecuteNonQueryAsync(ct);
        await NotifySharesAsync(c, tx, ct);
        await tx.CommitAsync(ct);
    }

    private async Task NotifySharesAsync(DbConnection c, DbTransaction tx, CancellationToken ct)
    {
        if (AfterShareWriteSql is not { } notify) return;
        await using var nc = Cmd(c, notify, tx);
        await nc.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RemoteMan.Core.Sharing.ShareInvite>> GetShareInvitesAsync(CancellationToken ct = default)
    {
        if (!SupportsSharing) return [];
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT id, invitee, host_user, state, expires_at, payload FROM remoteman_shares WHERE expires_at > @now", null,
            ("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var list = new List<RemoteMan.Core.Sharing.ShareInvite>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            try
            {
                list.Add(RemoteMan.Core.Sharing.ShareInvite.FromRow(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
                    Convert.ToInt32(r.GetValue(3)), Convert.ToInt64(r.GetValue(4)), r.IsDBNull(5) ? null : r.GetString(5)));
            }
            catch (System.Text.Json.JsonException) { }
        }
        return list;
    }

    private const string SelectColumns = "id, parent_id, kind, name, payload, revision, updated_at, updated_by, deleted, created_at, created_by";

    public async Task<IReadOnlyList<Entry>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, $"SELECT {SelectColumns} FROM remoteman_entries WHERE deleted = 0");
        return await ReadEntries(cmd, ct);
    }

    public async Task<IReadOnlyList<Entry>> GetChangesAsync(long sinceRevision, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, $"SELECT {SelectColumns} FROM remoteman_entries WHERE revision > @rev ORDER BY revision", null, ("@rev", sinceRevision));
        return await ReadEntries(cmd, ct);
    }

    private static async Task<IReadOnlyList<Entry>> ReadEntries(DbCommand cmd, CancellationToken ct)
    {
        var list = new List<Entry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var e = new Entry
            {
                Id = Guid.Parse(r.GetString(0)),
                ParentId = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
                Kind = (EntryKind)Convert.ToInt32(r.GetValue(2)),
                Name = r.GetString(3),
                Revision = Convert.ToInt64(r.GetValue(5)),
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(6))),
                UpdatedBy = r.IsDBNull(7) ? null : r.GetString(7),
                Deleted = Convert.ToInt32(r.GetValue(8)) != 0,
                CreatedAt = r.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(9))),
                CreatedBy = r.IsDBNull(10) ? null : r.GetString(10),
            };
            EntryJson.ApplyPayload(e, r.IsDBNull(4) ? null : r.GetString(4));
            list.Add(e);
        }
        return list;
    }

    public async Task<Entry> SaveAsync(Entry entry, string user, long? expectedRevision = null, CancellationToken ct = default)
    {
        if (IsReadOnly) throw ReadOnlyError();
        try { return await SaveCoreAsync(entry, user, expectedRevision, ct); }
        // Not a connection problem, so it must not take the data source offline.
        catch (DbException ex) when (IsPermissionDenied(ex)) { IsReadOnly = true; throw ReadOnlyError(); }
    }

    private async Task<Entry> SaveCoreAsync(Entry entry, string user, long? expectedRevision, CancellationToken ct)
    {
        // On a shared server, the login the server reports is who made the change.
        user = DatabaseUser ?? user;
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        long rev;
        await using (var next = Cmd(c, NextRevisionSql, tx))
            rev = Convert.ToInt64(await next.ExecuteScalarAsync(ct));

        if (expectedRevision is { } exp)
        {
            await using var check = Cmd(c, "SELECT revision FROM remoteman_entries WHERE id = @id", tx, ("@id", entry.Id.ToString()));
            var current = await check.ExecuteScalarAsync(ct);
            if (current is not null and not DBNull && Convert.ToInt64(current) != exp)
                throw new ConcurrencyException(entry.Id, exp, Convert.ToInt64(current));
        }

        var now = DateTimeOffset.UtcNow;
        var args = new (string, object?)[]
        {
            ("@id", entry.Id.ToString()), ("@parent", entry.ParentId?.ToString()), ("@kind", (int)entry.Kind),
            ("@name", entry.Name), ("@payload", EntryJson.ToPayload(entry)), ("@rev", rev),
            ("@at", now.ToUnixTimeMilliseconds()), ("@by", user), ("@del", entry.Deleted ? 1 : 0),
        };
        int n;
        await using (var upd = Cmd(c, "UPDATE remoteman_entries SET parent_id=@parent, kind=@kind, name=@name, payload=@payload, revision=@rev, updated_at=@at, updated_by=@by, deleted=@del WHERE id=@id", tx, args))
            n = await upd.ExecuteNonQueryAsync(ct);
        if (n == 0)
        {
            await using var ins = Cmd(c, "INSERT INTO remoteman_entries (id, parent_id, kind, name, payload, revision, updated_at, updated_by, deleted, created_at, created_by) VALUES (@id, @parent, @kind, @name, @payload, @rev, @at, @by, @del, @at, @by)", tx, args);
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (AfterEntryWriteSql is { } notify)
        {
            await using var nc = Cmd(c, notify, tx, ("@rev", rev));
            await nc.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        var saved = RemoteMan.Core.Storage.EntryStore.Clone(entry);
        saved.Revision = rev;
        saved.UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        saved.UpdatedBy = user;
        if (n == 0) { saved.CreatedAt = saved.UpdatedAt; saved.CreatedBy = user; }
        return saved;
    }

    public async Task DeleteAsync(Guid id, string user, CancellationToken ct = default)
    {
        // Tombstone the entry and everything under it.
        var all = await LoadAllAsync(ct);
        var doomed = new HashSet<Guid> { id };
        bool grew;
        do
        {
            grew = false;
            foreach (var e in all)
                if (e.ParentId is { } p && doomed.Contains(p) && doomed.Add(e.Id)) grew = true;
        } while (grew);

        foreach (var e in all.Where(e => doomed.Contains(e.Id)))
        {
            e.Deleted = true;
            await SaveAsync(e, user, null, ct);
        }
    }

    public async Task HeartbeatPresenceAsync(Presence p, CancellationToken ct = default)
    {
        // A login that may not write presence just doesn't show up as connected.
        try { await HeartbeatCoreAsync(p, ct); }
        catch (DbException ex) when (IsPermissionDenied(ex)) { }
    }

    private async Task HeartbeatCoreAsync(Presence p, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var args = new (string, object?)[]
        {
            ("@user", p.User), ("@machine", p.Machine), ("@entry", p.EntryId.ToString()),
            ("@state", (int)p.State), ("@seen", p.LastSeen.ToUnixTimeMilliseconds()),
        };
        // Update first; insert if missing (portable across all four dialects).
        int n;
        await using (var upd = Cmd(c, "UPDATE remoteman_presence SET entry_id=@entry, state=@state, last_seen=@seen WHERE user_name=@user AND machine=@machine", tx, args))
            n = await upd.ExecuteNonQueryAsync(ct);
        if (n == 0)
        {
            await using var ins = Cmd(c, "INSERT INTO remoteman_presence (user_name, machine, entry_id, state, last_seen) VALUES (@user, @machine, @entry, @state, @seen)", tx, args);
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (AfterPresenceWriteSql is { } notify)
        {
            await using var nc = Cmd(c, notify, tx);
            await nc.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task ClearPresenceAsync(string user, string machine, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "DELETE FROM remoteman_presence WHERE user_name=@user AND machine=@machine", null, ("@user", user), ("@machine", machine));
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (DbException ex) when (IsPermissionDenied(ex)) { }
    }

    public async Task<IReadOnlyList<Presence>> GetPresenceAsync(CancellationToken ct = default)
    {
        var cutoff = (DateTimeOffset.UtcNow - Presence.Ttl).ToUnixTimeMilliseconds();
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT user_name, machine, entry_id, state, last_seen FROM remoteman_presence WHERE last_seen > @cutoff", null, ("@cutoff", cutoff));
        var list = new List<Presence>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Presence(r.GetString(0), r.GetString(1), Guid.Parse(r.GetString(2)),
                (PresenceState)Convert.ToInt32(r.GetValue(3)), DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(4)))));
        return list;
    }

    public virtual Task StartChangeFeedAsync(CancellationToken ct = default)
    {
        _feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _feedTask = Task.Run(() => PollLoopAsync(_pollMs, _feedCts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Reads the counter row and the newest presence stamp; raises Changed when either moved.</summary>
    protected async Task PollLoopAsync(int intervalMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var c = await OpenAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    await using (var cmd = Cmd(c, "SELECT rev FROM remoteman_meta WHERE id = 1"))
                    {
                        var rev = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                        if (rev != Interlocked.Exchange(ref _seenRevision, rev)) RaiseChanged(new ChangeSignal(ChangeSignalKind.Entries, rev));
                    }
                    if (SupportsChat)
                    {
                        try
                        {
                            await using var cmd = Cmd(c, "SELECT rev FROM remoteman_meta WHERE id = 2");
                            if (await cmd.ExecuteScalarAsync(ct) is { } v and not DBNull)
                            {
                                var seq = Convert.ToInt64(v);
                                if (seq != Interlocked.Exchange(ref _seenChatSeq, seq)) RaiseChanged(new ChangeSignal(ChangeSignalKind.Chat, seq));
                            }
                        }
                        catch (DbException) { /* a database without chat tables yet: entries still sync */ }
                    }
                    await using (var cmd = Cmd(c, "SELECT COALESCE(MAX(last_seen), 0) + COUNT(*) FROM remoteman_presence"))
                    {
                        var stamp = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                        if (stamp != Interlocked.Exchange(ref _seenPresenceStamp, stamp)) RaiseChanged(new ChangeSignal(ChangeSignalKind.Presence));
                    }
                    if (SupportsSharing)
                    {
                        try
                        {
                            await using var cmd = Cmd(c, "SELECT COALESCE(MAX(updated_at), 0) + COUNT(*) FROM remoteman_shares");
                            var stamp = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                            if (stamp != Interlocked.Exchange(ref _seenSharesStamp, stamp)) RaiseChanged(new ChangeSignal(ChangeSignalKind.Shares));
                        }
                        catch (DbException) { }
                    }
                    SetConnected(true);
                    await Task.Delay(intervalMs, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                SetConnected(false);
                // Server unreachable: back off, the RedundantDataSource decides about failover.
                try { await Task.Delay(Math.Min(intervalMs * 5, 5000), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        _feedCts?.Cancel();
        if (_feedTask is not null) { try { await _feedTask; } catch { } }
        _feedCts?.Dispose();
    }
}
