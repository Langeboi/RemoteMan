using System.Data.Common;
using System.Net.Sockets;
using RemoteMan.Core.Chat;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

/// <summary>
/// Client-side failover across several servers holding the same database (for example
/// a PostgreSQL primary + hot standby promoted on failure, or two SQL Server nodes).
/// Calls go to the active member; on a connection-level failure the next member becomes
/// active and subscribers get a full-resync signal. Replication itself is the database's job.
/// </summary>
public sealed class RedundantDataSource : IDataSource, IChatSource
{
    private readonly IReadOnlyList<IDataSource> _members;
    private int _active;
    private CancellationToken _feedToken;
    private bool _feedStarted;

    public RedundantDataSource(IReadOnlyList<IDataSource> members)
    {
        if (members.Count == 0) throw new ArgumentException("At least one data source is required.");
        _members = members;
        foreach (var m in members)
        {
            m.Changed += (s, e) => { if (ReferenceEquals(s, Active)) Changed?.Invoke(this, e); };
            m.ConnectionChanged += (s, up) => { if (ReferenceEquals(s, Active)) ConnectionChanged?.Invoke(this, up); };
        }
    }

    public IDataSource Active => _members[_active];
    public string DisplayName => _members.Count == 1 ? Active.DisplayName : $"{Active.DisplayName} (+{_members.Count - 1} standby)";
    public DataSourceKind Kind => Active.Kind;
    public bool SupportsPresence => Active.SupportsPresence;
    public bool IsConnected => Active.IsConnected;
    public string? DatabaseUser => Active.DatabaseUser;
    public event EventHandler<bool>? ConnectionChanged;

    public event EventHandler<ChangeSignal>? Changed;
    public event EventHandler<IDataSource>? FailedOver;

    private static bool IsConnectionFailure(Exception ex) =>
        ex is DbException or SocketException or TimeoutException or IOException ||
        (ex.InnerException is not null && IsConnectionFailure(ex.InnerException));

    private async Task<T> Run<T>(Func<IDataSource, Task<T>> op)
    {
        Exception? last = null;
        for (var tries = 0; tries < _members.Count; tries++)
        {
            var member = Active;
            try { return await op(member); }
            catch (ConcurrencyException) { throw; }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                last = ex;
                await FailOverAsync(member);
            }
        }
        throw new InvalidOperationException("No data source in the redundancy group is reachable.", last);
    }

    private Task Run(Func<IDataSource, Task> op) => Run(async m => { await op(m); return true; });

    private async Task FailOverAsync(IDataSource failed)
    {
        var idx = (_members.IndexOf(failed) + 1) % _members.Count;
        Interlocked.Exchange(ref _active, idx);
        try
        {
            await Active.InitializeAsync();
            if (_feedStarted) await Active.StartChangeFeedAsync(_feedToken);
        }
        catch { /* the next Run attempt will move on again */ }
        FailedOver?.Invoke(this, Active);
        ConnectionChanged?.Invoke(this, Active.IsConnected);
        Changed?.Invoke(this, new ChangeSignal(ChangeSignalKind.Entries));
        Changed?.Invoke(this, new ChangeSignal(ChangeSignalKind.Chat));
    }

    public Task InitializeAsync(CancellationToken ct = default) => Run(m => m.InitializeAsync(ct));
    public Task<IReadOnlyList<Entry>> LoadAllAsync(CancellationToken ct = default) => Run(m => m.LoadAllAsync(ct));
    public Task<IReadOnlyList<Entry>> GetChangesAsync(long since, CancellationToken ct = default) => Run(m => m.GetChangesAsync(since, ct));
    public Task<Entry> SaveAsync(Entry e, string user, long? expected = null, CancellationToken ct = default) => Run(m => m.SaveAsync(e, user, expected, ct));
    public Task DeleteAsync(Guid id, string user, CancellationToken ct = default) => Run(m => m.DeleteAsync(id, user, ct));
    public Task HeartbeatPresenceAsync(Presence p, CancellationToken ct = default) => Run(m => m.HeartbeatPresenceAsync(p, ct));
    public Task ClearPresenceAsync(string user, string machine, CancellationToken ct = default) => Run(m => m.ClearPresenceAsync(user, machine, ct));
    public Task<IReadOnlyList<Presence>> GetPresenceAsync(CancellationToken ct = default) => Run(m => m.GetPresenceAsync(ct));
    public bool SupportsSharing => Active.SupportsSharing;
    public Task PutShareInviteAsync(RemoteMan.Core.Sharing.ShareInvite i, CancellationToken ct = default) => Run(m => m.PutShareInviteAsync(i, ct));
    public Task SetShareInviteStateAsync(Guid id, RemoteMan.Core.Sharing.ShareInviteState st, CancellationToken ct = default) => Run(m => m.SetShareInviteStateAsync(id, st, ct));
    public Task<IReadOnlyList<RemoteMan.Core.Sharing.ShareInvite>> GetShareInvitesAsync(CancellationToken ct = default) => Run(m => m.GetShareInvitesAsync(ct));

    public bool SupportsSettings => Active.SupportsSettings;
    public bool CanChangeSettings => Active.CanChangeSettings;
    public Task<IReadOnlyList<SharedSetting>> GetSettingsAsync(CancellationToken ct = default) => Run(m => m.GetSettingsAsync(ct));
    public Task PutSettingAsync(string name, string value, string user, CancellationToken ct = default) => Run(m => m.PutSettingAsync(name, value, user, ct));

    // Chat goes to the active member too, with the same failover.
    private IChatSource ChatOf(IDataSource m) => m as IChatSource ?? throw new NotSupportedException($"{m.DisplayName} has no chat.");
    public bool SupportsChat => Active is IChatSource { SupportsChat: true };
    public Task<IReadOnlyList<ChatRoom>> GetChatRoomsAsync(CancellationToken ct = default) => Run(m => ChatOf(m).GetChatRoomsAsync(ct));
    public Task SaveChatRoomAsync(ChatRoom room, CancellationToken ct = default) => Run(m => ChatOf(m).SaveChatRoomAsync(room, ct));
    public Task<StoredChatMessage> PostChatMessageAsync(StoredChatMessage message, CancellationToken ct = default) => Run(m => ChatOf(m).PostChatMessageAsync(message, ct));
    public Task<IReadOnlyList<StoredChatMessage>> GetChatMessagesAsync(long sinceSeq, CancellationToken ct = default) => Run(m => ChatOf(m).GetChatMessagesAsync(sinceSeq, ct));
    public Task<long> GetChatSeqAsync(CancellationToken ct = default) => Run(m => ChatOf(m).GetChatSeqAsync(ct));
    public Task<IReadOnlyDictionary<Guid, long>> GetChatReadsAsync(string user, CancellationToken ct = default) => Run(m => ChatOf(m).GetChatReadsAsync(user, ct));
    public Task SetChatReadAsync(string user, Guid roomId, long seq, CancellationToken ct = default) => Run(m => ChatOf(m).SetChatReadAsync(user, roomId, seq, ct));
    public Task<IReadOnlyList<string>> GetChatUsersAsync(CancellationToken ct = default) => Run(m => ChatOf(m).GetChatUsersAsync(ct));
    public bool SupportsChatFiles => Active is IChatSource { SupportsChatFiles: true };
    public Task PostChatFileAsync(StoredChatFile file, CancellationToken ct = default) => Run(m => ChatOf(m).PostChatFileAsync(file, ct));
    public Task<StoredChatFile?> GetChatFileAsync(Guid id, CancellationToken ct = default) => Run(m => ChatOf(m).GetChatFileAsync(id, ct));
    public Task<int> DeleteChatMessagesAsync(DateTimeOffset before, CancellationToken ct = default) => Run(m => ChatOf(m).DeleteChatMessagesAsync(before, ct));

    public Task StartChangeFeedAsync(CancellationToken ct = default)
    {
        _feedToken = ct;
        _feedStarted = true;
        return Active.StartChangeFeedAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in _members) await m.DisposeAsync();
    }
}

internal static class ListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T item) where T : class
    {
        for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }
}
