using System.Collections.Concurrent;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Storage;

/// <summary>
/// In-memory mirror of a data source that the UI binds to. Applies incremental
/// changes as other users make them, and keeps presence fresh.
/// </summary>
public sealed class EntryStore : IAsyncDisposable
{
    private readonly IDataSource _source;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private long _lastRevision;
    private IReadOnlyList<Presence> _presence = Array.Empty<Presence>();
    private Timer? _heartbeat;
    private Presence? _myPresence;

    public EntryStore(IDataSource source, string user, string machine)
    {
        _source = source;
        User = user;
        Machine = machine;
        _source.Changed += OnSourceChanged;
    }

    public string User { get; }
    public string Machine { get; }
    public IDataSource Source => _source;
    public long LastRevision => Interlocked.Read(ref _lastRevision);

    public IEnumerable<Entry> Entries => _entries.Values.Where(e => !e.Deleted);
    public IReadOnlyList<Presence> Presence => _presence;

    /// <summary>Raised (on a background thread) with the entries that changed remotely or locally.</summary>
    public event EventHandler<IReadOnlyList<Entry>>? EntriesChanged;
    public event EventHandler? PresenceChanged;

    public Entry? Get(Guid id) => _entries.TryGetValue(id, out var e) && !e.Deleted ? e : null;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        await _source.InitializeAsync(ct);
        var all = await _source.LoadAllAsync(ct);
        foreach (var e in all) _entries[e.Id] = e;
        _lastRevision = all.Count == 0 ? 0 : all.Max(e => e.Revision);
        await _source.StartChangeFeedAsync(_cts.Token);
        if (_source.SupportsPresence)
        {
            _heartbeat = new Timer(_ => _ = HeartbeatAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }
    }

    public async Task<Entry> SaveAsync(Entry entry, CancellationToken ct = default)
    {
        long? expected = _entries.TryGetValue(entry.Id, out var existing) ? existing.Revision : null;
        var saved = await _source.SaveAsync(entry, User, expected, ct);
        Apply(new[] { saved });
        return saved;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _source.DeleteAsync(id, User, ct);
        await PullAsync(ct);
    }

    /// <summary>Ctrl+click linking: attach a credential entry to a connection entry.</summary>
    public async Task<Entry> LinkCredentialAsync(Guid connectionId, Guid credentialId, CancellationToken ct = default)
    {
        var conn = Get(connectionId) ?? throw new InvalidOperationException("Connection entry not found.");
        var cred = Get(credentialId) ?? throw new InvalidOperationException("Credential entry not found.");
        if (!conn.IsConnectable) throw new InvalidOperationException($"{conn.Name} is not a connection entry.");
        if (cred.Kind != EntryKind.Credential) throw new InvalidOperationException($"{cred.Name} is not a credential.");
        var copy = Clone(conn);
        copy.CredentialId = cred.Id;
        return await SaveAsync(copy, ct);
    }

    public IEnumerable<Presence> PresenceFor(Guid entryId)
    {
        var now = DateTimeOffset.UtcNow;
        return _presence.Where(p => p.EntryId == entryId && p.IsAlive(now) && !(p.User == User && p.Machine == Machine));
    }

    /// <summary>Tell others what this user is doing. Pass null to clear.</summary>
    public Task SetMyPresenceAsync(Guid? entryId, PresenceState state = PresenceState.Viewing)
    {
        _myPresence = entryId is null ? null : new Presence(User, Machine, entryId.Value, state, DateTimeOffset.UtcNow);
        return HeartbeatAsync();
    }

    private async Task HeartbeatAsync()
    {
        if (!_source.SupportsPresence) return;
        try
        {
            if (_myPresence is { } p)
                await _source.HeartbeatPresenceAsync(p with { LastSeen = DateTimeOffset.UtcNow }, _cts.Token);
            else
                await _source.ClearPresenceAsync(User, Machine, _cts.Token);
            await RefreshPresenceAsync();
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception) { /* transient: next tick retries */ }
    }

    private async Task RefreshPresenceAsync()
    {
        _presence = await _source.GetPresenceAsync(_cts.Token);
        PresenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnSourceChanged(object? sender, ChangeSignal signal)
    {
        try
        {
            if (signal.Kind == ChangeSignalKind.Presence) await RefreshPresenceAsync();
            else if (signal.Revision is null || signal.Revision > LastRevision) await PullAsync(_cts.Token);
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception) { /* the next signal or poll will catch up */ }
    }

    public async Task PullAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var changes = await _source.GetChangesAsync(LastRevision, ct);
            if (changes.Count > 0) Apply(changes);
        }
        finally { _syncLock.Release(); }
    }

    private void Apply(IReadOnlyList<Entry> changes)
    {
        var applied = new List<Entry>(changes.Count);
        foreach (var e in changes)
        {
            _entries.AddOrUpdate(e.Id, e, (_, old) => old.Revision >= e.Revision ? old : e);
            applied.Add(e);
            long cur;
            while ((cur = Interlocked.Read(ref _lastRevision)) < e.Revision &&
                   Interlocked.CompareExchange(ref _lastRevision, e.Revision, cur) != cur) { }
        }
        EntriesChanged?.Invoke(this, applied);
    }

    public static Entry Clone(Entry e)
    {
        var c = new Entry
        {
            Id = e.Id, ParentId = e.ParentId, Kind = e.Kind, Name = e.Name,
            Revision = e.Revision, UpdatedAt = e.UpdatedAt, UpdatedBy = e.UpdatedBy, Deleted = e.Deleted,
        };
        EntryJson.ApplyPayload(c, EntryJson.ToPayload(e));
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_heartbeat is not null) await _heartbeat.DisposeAsync();
        try { await _source.ClearPresenceAsync(User, Machine); } catch { }
        _source.Changed -= OnSourceChanged;
        await _source.DisposeAsync();
    }
}
