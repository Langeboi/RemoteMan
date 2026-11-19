using System.Data.Common;
using System.Net.Sockets;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.Data;

/// <summary>
/// Client-side failover across several servers holding the same database (for example
/// a PostgreSQL primary + hot standby promoted on failure, or two SQL Server nodes).
/// Calls go to the active member; on a connection-level failure the next member becomes
/// active and subscribers get a full-resync signal. Replication itself is the database's job.
/// </summary>
public sealed class RedundantDataSource : IDataSource
{
    private readonly IReadOnlyList<IDataSource> _members;
    private int _active;
    private CancellationToken _feedToken;
    private bool _feedStarted;

    public RedundantDataSource(IReadOnlyList<IDataSource> members)
    {
        if (members.Count == 0) throw new ArgumentException("At least one data source is required.");
        _members = members;
        foreach (var m in members) m.Changed += (s, e) => { if (ReferenceEquals(s, Active)) Changed?.Invoke(this, e); };
    }

    public IDataSource Active => _members[_active];
    public string DisplayName => _members.Count == 1 ? Active.DisplayName : $"{Active.DisplayName} (+{_members.Count - 1} standby)";
    public DataSourceKind Kind => Active.Kind;
    public bool SupportsPresence => Active.SupportsPresence;

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
        Changed?.Invoke(this, new ChangeSignal(ChangeSignalKind.Entries));
    }

    public Task InitializeAsync(CancellationToken ct = default) => Run(m => m.InitializeAsync(ct));
    public Task<IReadOnlyList<Entry>> LoadAllAsync(CancellationToken ct = default) => Run(m => m.LoadAllAsync(ct));
    public Task<IReadOnlyList<Entry>> GetChangesAsync(long since, CancellationToken ct = default) => Run(m => m.GetChangesAsync(since, ct));
    public Task<Entry> SaveAsync(Entry e, string user, long? expected = null, CancellationToken ct = default) => Run(m => m.SaveAsync(e, user, expected, ct));
    public Task DeleteAsync(Guid id, string user, CancellationToken ct = default) => Run(m => m.DeleteAsync(id, user, ct));
    public Task HeartbeatPresenceAsync(Presence p, CancellationToken ct = default) => Run(m => m.HeartbeatPresenceAsync(p, ct));
    public Task ClearPresenceAsync(string user, string machine, CancellationToken ct = default) => Run(m => m.ClearPresenceAsync(user, machine, ct));
    public Task<IReadOnlyList<Presence>> GetPresenceAsync(CancellationToken ct = default) => Run(m => m.GetPresenceAsync(ct));

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
