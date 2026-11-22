namespace RemoteMan.Core.Chat;

/// <summary>
/// Chat tables in a shared data source. Implemented by every SQL backend; the local
/// SQLite file has nobody to talk to, so the app hides chat there (<see cref="SupportsChat"/>).
/// New messages raise the data source's Changed event with <c>ChangeSignalKind.Chat</c>.
/// </summary>
public interface IChatSource
{
    bool SupportsChat { get; }

    Task<IReadOnlyList<ChatRoom>> GetChatRoomsAsync(CancellationToken ct = default);
    Task SaveChatRoomAsync(ChatRoom room, CancellationToken ct = default);

    /// <summary>Stores a message and returns it with its sequence number. A message id that is
    /// already stored (an outbox retry after a lost reply) is not stored twice.</summary>
    Task<StoredChatMessage> PostChatMessageAsync(StoredChatMessage message, CancellationToken ct = default);

    /// <summary>Messages with Seq &gt; <paramref name="sinceSeq"/>, oldest first.</summary>
    Task<IReadOnlyList<StoredChatMessage>> GetChatMessagesAsync(long sinceSeq, CancellationToken ct = default);

    /// <summary>The newest sequence number handed out (0 when there are no messages yet).</summary>
    Task<long> GetChatSeqAsync(CancellationToken ct = default);

    /// <summary>Per room, the last message this user has read.</summary>
    Task<IReadOnlyDictionary<Guid, long>> GetChatReadsAsync(string user, CancellationToken ct = default);
    Task SetChatReadAsync(string user, Guid roomId, long seq, CancellationToken ct = default);

    /// <summary>Everyone who has used chat on this data source (they get a read row for Everyone when they start).</summary>
    Task<IReadOnlyList<string>> GetChatUsersAsync(CancellationToken ct = default);

    /// <summary>Deletes messages sent before <paramref name="before"/>; returns how many.</summary>
    Task<int> DeleteChatMessagesAsync(DateTimeOffset before, CancellationToken ct = default);
}
