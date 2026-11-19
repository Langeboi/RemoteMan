using RemoteMan.Core.Model;

namespace RemoteMan.Core.Storage;

/// <summary>
/// A place entries live: local SQLite file, or a shared SQL server.
/// Every write bumps a global, monotonically increasing revision so clients can
/// ask "what changed since revision N" with one indexed range query.
/// </summary>
public interface IDataSource : IAsyncDisposable
{
    string DisplayName { get; }
    DataSourceKind Kind { get; }
    bool SupportsPresence { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Entry>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Entries (including soft-deleted ones) with Revision &gt; <paramref name="sinceRevision"/>.</summary>
    Task<IReadOnlyList<Entry>> GetChangesAsync(long sinceRevision, CancellationToken ct = default);

    /// <summary>Insert or update. Returns the entry with its new revision.
    /// Throws <see cref="ConcurrencyException"/> when <paramref name="expectedRevision"/> is set and stale.</summary>
    Task<Entry> SaveAsync(Entry entry, string user, long? expectedRevision = null, CancellationToken ct = default);

    Task DeleteAsync(Guid id, string user, CancellationToken ct = default);

    Task HeartbeatPresenceAsync(Presence presence, CancellationToken ct = default);
    Task ClearPresenceAsync(string user, string machine, CancellationToken ct = default);
    Task<IReadOnlyList<Presence>> GetPresenceAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised when the backend tells us something changed (Postgres NOTIFY, SQL Server
    /// query notification) or a poll found a newer revision. The argument is a hint only;
    /// consumers call <see cref="GetChangesAsync"/>.
    /// </summary>
    event EventHandler<ChangeSignal>? Changed;

    /// <summary>Starts listening/polling for changes from other users.</summary>
    Task StartChangeFeedAsync(CancellationToken ct = default);
}

public enum DataSourceKind { Sqlite, PostgreSql, SqlServer, MySql }

public enum ChangeSignalKind { Entries, Presence }

public sealed record ChangeSignal(ChangeSignalKind Kind, long? Revision = null);

public sealed class ConcurrencyException(Guid id, long expected, long actual)
    : Exception($"Entry {id} was changed by someone else (expected revision {expected}, found {actual}).")
{
    public Guid EntryId { get; } = id;
    public long ActualRevision { get; } = actual;
}
