using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;

namespace RemoteMan.Core.Chat;

/// <summary>Messages written while the database was unreachable, kept on this PC until they are sent.</summary>
public interface IChatOutbox
{
    IReadOnlyList<StoredChatMessage> Load();
    void Save(IReadOnlyList<StoredChatMessage> pending);
}

/// <summary>
/// Everything chat does, without any UI: the rooms you are in, their messages, unread counts,
/// sending (with an outbox while offline), read markers and deleting old messages.
/// Message text is encrypted with the team vault key before it reaches the database.
/// Events are raised on background threads.
/// </summary>
public sealed class ChatHub : IAsyncDisposable
{
    /// <summary>How far back a fresh start loads (by message number, across all rooms).</summary>
    public const int HistoryWindow = 3000;
    private const string Encrypted = "v1:";
    private const string PlainPrefix = "p:";

    private readonly IDataSource _source;
    private readonly IChatSource _chat;
    private readonly ISecretProtector? _protector;
    private readonly IChatOutbox? _outbox;
    private readonly Func<string> _me;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _pullLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<Guid, ChatRoom> _rooms = new();
    private readonly Dictionary<Guid, List<ChatMessage>> _messages = new();
    private readonly Dictionary<Guid, long> _reads = new();
    private readonly List<StoredChatMessage> _pending = new();
    private readonly HashSet<string> _users = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSeq;
    private bool _started;
    private Timer? _retry;

    public ChatHub(IDataSource source, Func<string> me, ISecretProtector? protector, IChatOutbox? outbox = null)
    {
        _source = source;
        _chat = source as IChatSource ?? throw new ArgumentException("This data source has no chat.", nameof(source));
        _me = me;
        _protector = protector;
        _outbox = outbox;
    }

    public string Me => _me();

    /// <summary>Delete messages older than this when starting (null keeps everything).</summary>
    public TimeSpan? Retention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Rooms, messages or unread counts changed.</summary>
    public event EventHandler? Changed;
    /// <summary>A message from someone else arrived after the start (for notifications).</summary>
    public event EventHandler<ChatMessage>? MessageArrived;

    public bool IsStarted => _started;

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var p in _outbox?.Load() ?? []) _pending.Add(p);
        _source.Changed += OnSourceChanged;
        try
        {
            if (Retention is { } keep) await _chat.DeleteChatMessagesAsync(DateTimeOffset.UtcNow - keep, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* old messages go next time */ }

        var reads = await _chat.GetChatReadsAsync(Me, ct);
        lock (_gate) foreach (var (k, v) in reads) _reads[k] = v;
        // Being in Everyone's read table is what puts you in everyone's contact list.
        if (!reads.ContainsKey(ChatRoom.EveryoneId))
        {
            try { await _chat.SetChatReadAsync(Me, ChatRoom.EveryoneId, 0, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* a read-only login: you can still read along */ }
        }

        var top = await _chat.GetChatSeqAsync(ct);
        _lastSeq = Math.Max(0, top - HistoryWindow);
        await PullAsync(notify: false, ct);
        _started = true;
        _retry = new Timer(_ => { if (HasPending) _ = FlushAsync(); }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        try { await FlushAsync(ct); } catch (UnauthorizedAccessException) { }
    }

    // ---------------- reading ----------------

    /// <summary>Everyone first, then groups, then direct conversations, newest activity first within each.</summary>
    public IReadOnlyList<ChatRoom> Rooms
    {
        get
        {
            lock (_gate)
            {
                var me = Me;
                return _rooms.Values.Where(r => !r.Deleted && r.Includes(me)).Prepend(ChatRoom.Everyone)
                    .OrderBy(r => r.Kind == ChatRoomKind.Direct ? 2 : r.Kind == ChatRoomKind.Group ? 1 : 0)
                    .ThenByDescending(r => LastActivity(r.Id))
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public ChatRoom? Room(Guid id)
    {
        if (id == ChatRoom.EveryoneId) return ChatRoom.Everyone;
        lock (_gate) return _rooms.GetValueOrDefault(id);
    }

    /// <summary>Everyone who has used chat here, and anyone who has written a message, except you.</summary>
    public IReadOnlyList<string> Users
    {
        get { lock (_gate) return _users.Where(u => !ChatRoom.SameUser(u, Me)).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public IReadOnlyList<ChatMessage> Messages(Guid roomId)
    {
        lock (_gate)
        {
            var list = _messages.TryGetValue(roomId, out var l) ? l.ToList() : [];
            foreach (var p in _pending.Where(p => p.RoomId == roomId)) list.Add(Decode(p) with { Pending = true });
            return list;
        }
    }

    public ChatMessage? LastMessage(Guid roomId)
    {
        lock (_gate)
        {
            var pend = _pending.LastOrDefault(p => p.RoomId == roomId);
            if (pend is not null) return Decode(pend) with { Pending = true };
            return _messages.TryGetValue(roomId, out var l) && l.Count > 0 ? l[^1] : null;
        }
    }

    private DateTimeOffset LastActivity(Guid roomId) =>
        _messages.TryGetValue(roomId, out var l) && l.Count > 0 ? l[^1].SentAt : _rooms.GetValueOrDefault(roomId)?.UpdatedAt ?? DateTimeOffset.MinValue;

    public int Unread(Guid roomId)
    {
        lock (_gate)
        {
            var read = _reads.GetValueOrDefault(roomId);
            return _messages.TryGetValue(roomId, out var l) ? l.Count(m => m.Seq > read && !ChatRoom.SameUser(m.Sender, Me)) : 0;
        }
    }

    public int TotalUnread => Rooms.Sum(r => Unread(r.Id));

    public bool HasPending { get { lock (_gate) return _pending.Count > 0; } }

    // ---------------- writing ----------------

    /// <summary>The direct conversation with <paramref name="other"/>; stored when the first message goes out.</summary>
    public ChatRoom DirectWith(string other)
    {
        var id = ChatRoom.DirectId(Me, other);
        lock (_gate) return _rooms.GetValueOrDefault(id) ?? ChatRoom.Direct(Me, other);
    }

    public async Task<ChatRoom> CreateGroupAsync(string name, IEnumerable<string> members, CancellationToken ct = default)
    {
        var all = members.Append(Me).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var room = new ChatRoom(Guid.NewGuid(), ChatRoomKind.Group, name.Trim(), all, Me, DateTimeOffset.UtcNow);
        await _chat.SaveChatRoomAsync(room, ct);
        lock (_gate) _rooms[room.Id] = room;
        Changed?.Invoke(this, EventArgs.Empty);
        return room;
    }

    /// <summary>Takes you out of a group (the group stays for the others).</summary>
    public async Task LeaveGroupAsync(Guid roomId, CancellationToken ct = default)
    {
        var room = Room(roomId);
        if (room is not { Kind: ChatRoomKind.Group }) return;
        var left = room with { Members = room.Members.Where(m => !ChatRoom.SameUser(m, Me)).ToList(), UpdatedAt = DateTimeOffset.UtcNow };
        if (left.Members.Count == 0) left = left with { Deleted = true };
        await _chat.SaveChatRoomAsync(left, ct);
        lock (_gate) _rooms[room.Id] = left;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sends a message (body with <see cref="ChatLink"/> tokens). While offline it waits in the outbox
    /// and shows as pending; it goes out on the next successful contact.</summary>
    public async Task SendAsync(ChatRoom room, string body, CancellationToken ct = default)
    {
        body = body.Trim();
        if (body.Length == 0) return;
        var stored = new StoredChatMessage(0, Guid.NewGuid(), room.Id, Me, DateTimeOffset.UtcNow, Encode(body));
        lock (_gate)
        {
            if (room.Kind != ChatRoomKind.Everyone && !_rooms.ContainsKey(room.Id)) _rooms[room.Id] = room;
            _pending.Add(stored);
            SaveOutbox();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await FlushAsync(ct);
    }

    /// <summary>Sends what is waiting in the outbox. Stops at the first connection failure.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (true)
        {
            StoredChatMessage next;
            lock (_gate) { if (_pending.Count == 0) return; next = _pending[0]; }
            try
            {
                var room = Room(next.RoomId);
                // A direct room is stored the first time either side writes in it.
                if (room is { Kind: ChatRoomKind.Direct } && !await RoomStoredAsync(room.Id, ct))
                    await _chat.SaveChatRoomAsync(room, ct);
                var saved = await _chat.PostChatMessageAsync(next, ct);
                lock (_gate)
                {
                    _pending.RemoveAll(p => p.Id == next.Id);
                    SaveOutbox();
                    Add(Decode(saved));
                    // What you write yourself counts as read.
                    _reads[saved.RoomId] = Math.Max(_reads.GetValueOrDefault(saved.RoomId), saved.Seq);
                }
                _ = SetReadQuietlyAsync(saved.RoomId, saved.Seq);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception) when (ct.IsCancellationRequested || _cts.IsCancellationRequested) { return; }
            catch (UnauthorizedAccessException)
            {
                // The login may not write: retrying won't help, so the message is dropped and the sender told.
                lock (_gate) { _pending.RemoveAll(p => p.Id == next.Id); SaveOutbox(); }
                Changed?.Invoke(this, EventArgs.Empty);
                throw;
            }
            catch (Exception) { return; /* offline: the retry timer tries again */ }
        }
    }

    private readonly HashSet<Guid> _storedRooms = new();
    private async Task<bool> RoomStoredAsync(Guid id, CancellationToken ct)
    {
        lock (_gate) if (_storedRooms.Contains(id)) return true;
        var rooms = await _chat.GetChatRoomsAsync(ct);
        lock (_gate) foreach (var r in rooms) _storedRooms.Add(r.Id);
        lock (_gate) return _storedRooms.Contains(id);
    }

    public async Task MarkReadAsync(Guid roomId, CancellationToken ct = default)
    {
        long top;
        lock (_gate)
        {
            top = _messages.TryGetValue(roomId, out var l) && l.Count > 0 ? l[^1].Seq : 0;
            if (top <= _reads.GetValueOrDefault(roomId)) return;
            _reads[roomId] = top;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await SetReadQuietlyAsync(roomId, top, ct);
    }

    private async Task SetReadQuietlyAsync(Guid roomId, long seq, CancellationToken ct = default)
    {
        try { await _chat.SetChatReadAsync(Me, roomId, seq, ct); }
        catch (Exception) { /* only the unread count on your other PCs is affected */ }
    }

    // ---------------- sync ----------------

    private async void OnSourceChanged(object? sender, ChangeSignal signal)
    {
        if (signal.Kind != ChangeSignalKind.Chat || !_started) return;
        if (signal.Revision is { } seq && seq <= Interlocked.Read(ref _lastSeq)) return;
        try { await PullAsync(notify: true, _cts.Token); await FlushAsync(_cts.Token); }
        catch (Exception) { /* the next signal or poll catches up */ }
    }

    /// <summary>Loads rooms and the messages since the last one seen.</summary>
    public async Task PullAsync(bool notify = true, CancellationToken ct = default)
    {
        await _pullLock.WaitAsync(ct);
        try
        {
            var rooms = await _chat.GetChatRoomsAsync(ct);
            var users = await _chat.GetChatUsersAsync(ct);
            var since = Interlocked.Read(ref _lastSeq);
            var fresh = await _chat.GetChatMessagesAsync(since, ct);
            var arrived = new List<ChatMessage>();
            lock (_gate)
            {
                foreach (var r in rooms) { _rooms[r.Id] = r; _storedRooms.Add(r.Id); }
                foreach (var u in users) _users.Add(u);
                foreach (var s in fresh)
                {
                    var m = Decode(s);
                    _users.Add(m.Sender);
                    if (!Add(m)) continue;
                    if (!ChatRoom.SameUser(m.Sender, Me) && (Room(m.RoomId)?.Includes(Me) ?? false)) arrived.Add(m);
                }
                if (fresh.Count > 0) Interlocked.Exchange(ref _lastSeq, Math.Max(since, fresh[^1].Seq));
            }
            Changed?.Invoke(this, EventArgs.Empty);
            if (notify) foreach (var m in arrived) MessageArrived?.Invoke(this, m);
        }
        finally { _pullLock.Release(); }
    }

    /// <summary>Adds a message in seq order; false when it is already there.</summary>
    private bool Add(ChatMessage m)
    {
        if (!_messages.TryGetValue(m.RoomId, out var list)) _messages[m.RoomId] = list = new List<ChatMessage>();
        if (list.Any(x => x.Id == m.Id)) return false;
        var i = list.Count;
        while (i > 0 && list[i - 1].Seq > m.Seq) i--;
        list.Insert(i, m);
        return true;
    }

    // ---------------- encryption ----------------

    private string Encode(string body) => _protector is null ? PlainPrefix + body : Encrypted + _protector.Protect(body);

    private ChatMessage Decode(StoredChatMessage s)
    {
        string body;
        var unreadable = false;
        if (s.Payload.StartsWith(PlainPrefix)) body = s.Payload[PlainPrefix.Length..];
        else if (s.Payload.StartsWith(Encrypted) && _protector is not null)
        {
            try { body = _protector.Unprotect(s.Payload[Encrypted.Length..]); }
            catch (Exception) { body = "This message was written with a different vault passphrase and can't be read here."; unreadable = true; }
        }
        else { body = "This message can't be read by this version of RemoteMan."; unreadable = true; }
        return new ChatMessage(s.Seq, s.Id, s.RoomId, s.Sender, s.SentAt, body, Unreadable: unreadable);
    }

    private void SaveOutbox()
    {
        try { _outbox?.Save(_pending.ToList()); } catch (Exception) { /* kept in memory still */ }
    }

    public ValueTask DisposeAsync()
    {
        _source.Changed -= OnSourceChanged;
        _cts.Cancel();
        _retry?.Dispose();
        return ValueTask.CompletedTask;
    }
}
