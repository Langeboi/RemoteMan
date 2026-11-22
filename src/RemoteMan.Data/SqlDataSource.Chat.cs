using System.Data;
using System.Data.Common;
using RemoteMan.Core.Chat;

namespace RemoteMan.Data;

/// <summary>
/// Team chat in the same database as the entries. Messages are numbered by row 2 of
/// remoteman_meta inside the write transaction (the same trick as entry revisions), so
/// "new since N" is one range read and nobody can skip a message that commits late.
/// </summary>
public abstract partial class SqlDataSource : IChatSource
{
    public virtual bool SupportsChat => true;
    protected virtual string? AfterChatWriteSql => null;

    private static string Members(IReadOnlyList<string> m) => string.Join("\n", m);
    private static IReadOnlyList<string> Members(object v) =>
        v is DBNull or null ? [] : v.ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<IReadOnlyList<ChatRoom>> GetChatRoomsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT id, kind, name, members, created_by, updated_at, deleted FROM remoteman_chat_rooms");
        var list = new List<ChatRoom>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ChatRoom(Guid.Parse(r.GetString(0)), (ChatRoomKind)Convert.ToInt32(r.GetValue(1)),
                r.IsDBNull(2) ? null : r.GetString(2), Members(r.GetValue(3)), r.IsDBNull(4) ? null : r.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(5))), Convert.ToInt32(r.GetValue(6)) != 0));
        return list;
    }

    public async Task SaveChatRoomAsync(ChatRoom room, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var args = new (string, object?)[]
        {
            ("@id", room.Id.ToString()), ("@kind", (int)room.Kind), ("@name", room.Name), ("@members", Members(room.Members)),
            ("@by", room.CreatedBy), ("@at", room.UpdatedAt.ToUnixTimeMilliseconds()), ("@del", room.Deleted ? 1 : 0),
        };
        int n;
        await using (var upd = Cmd(c, "UPDATE remoteman_chat_rooms SET kind=@kind, name=@name, members=@members, updated_at=@at, deleted=@del WHERE id=@id", tx, args))
            n = await upd.ExecuteNonQueryAsync(ct);
        if (n == 0)
        {
            await using var ins = Cmd(c, "INSERT INTO remoteman_chat_rooms (id, kind, name, members, created_by, updated_at, deleted) VALUES (@id, @kind, @name, @members, @by, @at, @del)", tx, args);
            await ins.ExecuteNonQueryAsync(ct);
        }
        // Rooms change rarely; a chat signal makes the others reload their room list.
        await BumpChatAsync(c, tx, ct);
        await tx.CommitAsync(ct);
    }

    private async Task<long> BumpChatAsync(DbConnection c, DbTransaction tx, CancellationToken ct)
    {
        long seq;
        await using (var next = Cmd(c, NextCounterSql(2), tx))
            seq = Convert.ToInt64(await next.ExecuteScalarAsync(ct));
        if (AfterChatWriteSql is { } notify)
        {
            await using var nc = Cmd(c, notify, tx, ("@rev", seq));
            await nc.ExecuteNonQueryAsync(ct);
        }
        return seq;
    }

    public async Task<StoredChatMessage> PostChatMessageAsync(StoredChatMessage m, CancellationToken ct = default)
    {
        try { return await PostCoreAsync(m, ct); }
        catch (DbException ex) when (IsPermissionDenied(ex))
        {
            throw new UnauthorizedAccessException($"Your database login{(DatabaseUser is { } u ? $" ({u})" : "")} can only read, so it can't send chat messages.");
        }
    }

    private async Task<StoredChatMessage> PostCoreAsync(StoredChatMessage m, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // The number is taken first: that locks the counter row, so two retries of one message can't both insert.
        var seq = await BumpChatAsync(c, tx, ct);
        await using (var dup = Cmd(c, "SELECT seq FROM remoteman_chat_messages WHERE id = @id", tx, ("@id", m.Id.ToString())))
            if (await dup.ExecuteScalarAsync(ct) is { } existing and not DBNull)
            {
                await tx.RollbackAsync(ct);
                return m with { Seq = Convert.ToInt64(existing) };
            }
        await using (var ins = Cmd(c, "INSERT INTO remoteman_chat_messages (seq, id, room_id, sender, sent_at, body) VALUES (@seq, @id, @room, @sender, @at, @body)", tx,
            ("@seq", seq), ("@id", m.Id.ToString()), ("@room", m.RoomId.ToString()), ("@sender", m.Sender),
            ("@at", m.SentAt.ToUnixTimeMilliseconds()), ("@body", m.Payload)))
            await ins.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return m with { Seq = seq };
    }

    public async Task<IReadOnlyList<StoredChatMessage>> GetChatMessagesAsync(long sinceSeq, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT seq, id, room_id, sender, sent_at, body FROM remoteman_chat_messages WHERE seq > @seq ORDER BY seq", null, ("@seq", sinceSeq));
        var list = new List<StoredChatMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new StoredChatMessage(Convert.ToInt64(r.GetValue(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(4))), r.GetString(5)));
        return list;
    }

    public async Task<long> GetChatSeqAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT rev FROM remoteman_meta WHERE id = 2");
        return await cmd.ExecuteScalarAsync(ct) is { } v and not DBNull ? Convert.ToInt64(v) : 0;
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetChatReadsAsync(string user, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT room_id, last_read FROM remoteman_chat_reads WHERE user_name = @user", null, ("@user", user));
        var d = new Dictionary<Guid, long>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (Guid.TryParse(r.GetString(0), out var id)) d[id] = Convert.ToInt64(r.GetValue(1));
        return d;
    }

    public async Task SetChatReadAsync(string user, Guid roomId, long seq, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var args = new (string, object?)[] { ("@user", user), ("@room", roomId.ToString()), ("@seq", seq) };
        int n;
        // Never moves backwards (two PCs of the same person).
        await using (var upd = Cmd(c, "UPDATE remoteman_chat_reads SET last_read = @seq WHERE user_name = @user AND room_id = @room AND last_read < @seq", null, args))
            n = await upd.ExecuteNonQueryAsync(ct);
        if (n > 0) return;
        await using (var has = Cmd(c, "SELECT COUNT(*) FROM remoteman_chat_reads WHERE user_name = @user AND room_id = @room", null, args))
            if (Convert.ToInt64(await has.ExecuteScalarAsync(ct)) > 0) return;
        try
        {
            await using var ins = Cmd(c, "INSERT INTO remoteman_chat_reads (user_name, room_id, last_read) VALUES (@user, @room, @seq)", null, args);
            await ins.ExecuteNonQueryAsync(ct);
        }
        catch (DbException) when (!ct.IsCancellationRequested) { /* the other PC inserted it a moment ago */ }
    }

    public async Task<IReadOnlyList<string>> GetChatUsersAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "SELECT DISTINCT user_name FROM remoteman_chat_reads");
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<int> DeleteChatMessagesAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, "DELETE FROM remoteman_chat_messages WHERE sent_at < @at", null, ("@at", before.ToUnixTimeMilliseconds()));
        try { return await cmd.ExecuteNonQueryAsync(ct); }
        catch (DbException ex) when (IsPermissionDenied(ex)) { return 0; }
    }
}
