using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Sockets;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Storage;

/// <summary>
/// In-memory mirror of a data source that the UI binds to. Applies incremental
/// changes as other users make them, and keeps presence fresh.
/// <para>Offline mode: when the database can't be reached nothing is thrown at the user.
/// The store keeps serving the last known entries (from memory, or from the local cache
/// at startup), refuses edits with <see cref="DataSourceOfflineException"/>, and probes the
/// server every <see cref="ReconnectInterval"/>. When the probe succeeds it catches up on
/// every change made meanwhile and presence resumes.</para>
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
    private readonly IEntryCache? _cache;
    private Timer? _reconnect;
    private Timer? _cacheTimer;
    private int _online;          // 1 = online
    private int _probing;         // 1 = a reconnect probe is running
    private bool _initialized;    // schema checked and DatabaseUser known
    private bool _feedStarted;
    private bool _fromCache;      // entries came from the cache: do a full reload on reconnect

    public EntryStore(IDataSource source, string user, string machine, IEntryCache? cache = null)
    {
        _source = source;
        User = user;
        Machine = machine;
        _cache = cache;
        _source.Changed += OnSourceChanged;
        _source.ConnectionChanged += OnSourceConnectionChanged;
    }

    /// <summary>How often to try the database again while offline.</summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(15);

    public bool IsOnline => Volatile.Read(ref _online) == 1;

    /// <summary>Why the last connection attempt failed (shown as the indicator's tooltip).</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised (on a background thread) when the store goes offline or comes back.</summary>
    public event EventHandler<bool>? OnlineChanged;

    /// <summary>Who you are: the account you are logged in to the database with (Windows user for local files).</summary>
    public string User { get; private set; }
    public string Machine { get; }
    public IDataSource Source => _source;
    public long LastRevision => Interlocked.Read(ref _lastRevision);

    public IEnumerable<Entry> Entries => _entries.Values.Where(e => !e.Deleted);
    public IReadOnlyList<Presence> Presence => _presence;

    /// <summary>Raised (on a background thread) with the entries that changed remotely or locally.</summary>
    public event EventHandler<IReadOnlyList<Entry>>? EntriesChanged;
    public event EventHandler? PresenceChanged;
    /// <summary>Shared-session invites changed (someone invited, accepted, declined or ended).</summary>
    public event EventHandler? SharesChanged;

    public Entry? Get(Guid id) => _entries.TryGetValue(id, out var e) && !e.Deleted ? e : null;

    /// <summary>
    /// Connects and loads everything. If the server can't be reached the store opens
    /// offline instead (from the cache when there is one) and keeps retrying; only
    /// errors that retrying can't fix are thrown.
    /// </summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        try
        {
            await ConnectAndLoadAsync(full: true, ct);
        }
        catch (Exception ex) when (IsConnectionFailure(ex) && !ct.IsCancellationRequested)
        {
            LastError = Innermost(ex).Message;
            if (_cache?.Load() is { } cached)
            {
                foreach (var e in cached) _entries[e.Id] = e;
                _lastRevision = cached.Count == 0 ? 0 : cached.Max(e => e.Revision);
                _fromCache = true;
            }
            GoOffline();
        }
        if (_source.SupportsPresence)
            _heartbeat = new Timer(_ => _ = HeartbeatAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private async Task ConnectAndLoadAsync(bool full, CancellationToken ct)
    {
        if (!_initialized)
        {
            await _source.InitializeAsync(ct);
            if (!string.IsNullOrWhiteSpace(_source.DatabaseUser)) User = _source.DatabaseUser!;
            _initialized = true;
        }
        if (full)
        {
            var all = await _source.LoadAllAsync(ct);
            var live = all.Select(e => e.Id).ToHashSet();
            var gone = _entries.Values.Where(e => !live.Contains(e.Id)).ToList();
            foreach (var e in gone) { _entries.TryRemove(e.Id, out _); e.Deleted = true; }
            foreach (var e in all) _entries[e.Id] = e;
            Interlocked.Exchange(ref _lastRevision, all.Count == 0 ? 0 : all.Max(e => e.Revision));
            _fromCache = false;
            if (gone.Count > 0 || all.Count > 0) EntriesChanged?.Invoke(this, all.Concat(gone).ToList());
            ScheduleCacheSave();
        }
        else
        {
            await PullCoreAsync(ct);
        }
        if (!_feedStarted)
        {
            await _source.StartChangeFeedAsync(_cts.Token);
            _feedStarted = true;
        }
        GoOnline();
        if (_source.SupportsPresence) await HeartbeatAsync();
    }

    public async Task<Entry> SaveAsync(Entry entry, CancellationToken ct = default)
    {
        EnsureOnline();
        // Check against the revision the caller started editing from, not our live copy:
        // the change feed may already have pulled someone else's newer save into _entries.
        long? expected = _entries.ContainsKey(entry.Id) ? entry.Revision : null;
        try
        {
            var saved = await _source.SaveAsync(entry, User, expected, ct);
            Apply(new[] { saved });
            return saved;
        }
        catch (Exception ex) when (IsConnectionFailure(ex)) { GoOffline(); throw new DataSourceOfflineException(ex); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        EnsureOnline();
        try
        {
            await _source.DeleteAsync(id, User, ct);
            await PullAsync(ct);
        }
        catch (Exception ex) when (IsConnectionFailure(ex)) { GoOffline(); throw new DataSourceOfflineException(ex); }
    }

    private void EnsureOnline()
    {
        if (!IsOnline) throw new DataSourceOfflineException(null);
    }

    // ---------------- offline / reconnect ----------------

    public static bool IsConnectionFailure(Exception ex) => ex switch
    {
        ConcurrencyException or DataSourceOfflineException => false,
        DbException or SocketException or TimeoutException or IOException => true,
        OperationCanceledException => false,
        _ => ex.InnerException is { } inner && IsConnectionFailure(inner),
    };

    private static Exception Innermost(Exception ex) => ex.InnerException is { } i ? Innermost(i) : ex;

    private void GoOffline()
    {
        if (_cts.IsCancellationRequested) return;
        if (Interlocked.Exchange(ref _online, 0) == 1 || _reconnect is null)
        {
            _presence = Array.Empty<Presence>();
            PresenceChanged?.Invoke(this, EventArgs.Empty);
            OnlineChanged?.Invoke(this, false);
        }
        lock (_cts)
        {
            _reconnect ??= new Timer(_ => _ = ProbeAsync(), null, ReconnectInterval, ReconnectInterval);
        }
    }

    private void GoOnline()
    {
        lock (_cts)
        {
            _reconnect?.Dispose();
            _reconnect = null;
        }
        if (Interlocked.Exchange(ref _online, 1) == 0) OnlineChanged?.Invoke(this, true);
    }

    /// <summary>One background reconnect attempt; on success everything missed is pulled in.</summary>
    private async Task ProbeAsync()
    {
        if (IsOnline || Interlocked.Exchange(ref _probing, 1) == 1) return;
        try { await ConnectAndLoadAsync(full: _fromCache || !_initialized, _cts.Token); }
        catch (Exception ex) { LastError = Innermost(ex).Message; /* still offline: the timer tries again */ }
        finally { Volatile.Write(ref _probing, 0); }
    }

    /// <summary>Retry right away (for a "reconnect now" click).</summary>
    public Task ReconnectNowAsync() => ProbeAsync();

    private void OnSourceConnectionChanged(object? sender, bool up)
    {
        if (!up) { LastError = "connection lost"; GoOffline(); }
        else if (!IsOnline) _ = ProbeAsync();
    }

    private void ScheduleCacheSave()
    {
        if (_cache is null || _cts.IsCancellationRequested) return;
        lock (_cts)
        {
            _cacheTimer ??= new Timer(_ => SaveCache(), null, Timeout.Infinite, Timeout.Infinite);
            _cacheTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    private void SaveCache()
    {
        try { _cache?.Save(_entries.Values.Where(e => !e.Deleted).ToList()); }
        catch (Exception) { /* the cache is a convenience; never fail because of it */ }
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
        copy.Login = LoginSource.Saved;
        return await SaveAsync(copy, ct);
    }

    /// <summary>
    /// The entry that holds the login for <paramref name="entry"/>: follows "inherited" up the
    /// folders and "saved" to the credential entry. Returns the source kind that applies and the
    /// entry holding the data (null for none / Windows login).
    /// </summary>
    public (LoginSource Source, Entry? Holder) ResolveLogin(Entry entry)
    {
        if (entry.Kind == EntryKind.Credential) return (LoginSource.Embedded, entry);
        var e = entry;
        for (var hops = 0; hops < 64; hops++)
        {
            switch (e.Login)
            {
                case LoginSource.Inherited:
                    if (e.ParentId is { } p && Get(p) is { } parent) { e = parent; continue; }
                    return (LoginSource.None, null);
                case LoginSource.Saved:
                    return e.CredentialId is { } id && Get(id) is { Kind: EntryKind.Credential } c ? (LoginSource.Saved, c) : (LoginSource.None, null);
                case LoginSource.Embedded:
                    return e.Credential is null ? (LoginSource.None, null) : (LoginSource.Embedded, e);
                case LoginSource.WindowsLogin:
                    return (LoginSource.WindowsLogin, null);
                default:
                    return (LoginSource.None, null);
            }
        }
        return (LoginSource.None, null);
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
        if (!_source.SupportsPresence || !IsOnline) return;
        try
        {
            if (_myPresence is { } p)
                await _source.HeartbeatPresenceAsync(p with { LastSeen = DateTimeOffset.UtcNow }, _cts.Token);
            else
                await _source.ClearPresenceAsync(User, Machine, _cts.Token);
            await RefreshPresenceAsync();
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) when (IsConnectionFailure(ex)) { GoOffline(); }
        catch (Exception) { /* transient: next tick retries */ }
    }

    private async Task RefreshPresenceAsync()
    {
        _presence = await _source.GetPresenceAsync(_cts.Token);
        PresenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnSourceChanged(object? sender, ChangeSignal signal)
    {
        if (!IsOnline) { _ = ProbeAsync(); return; }
        try
        {
            if (signal.Kind == ChangeSignalKind.Presence) await RefreshPresenceAsync();
            else if (signal.Kind == ChangeSignalKind.Shares) SharesChanged?.Invoke(this, EventArgs.Empty);
            else if (signal.Revision is null || signal.Revision > LastRevision) await PullAsync(_cts.Token);
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) when (IsConnectionFailure(ex)) { GoOffline(); }
        catch (Exception) { /* the next signal or poll will catch up */ }
    }

    public async Task PullAsync(CancellationToken ct = default)
    {
        if (!IsOnline) return;
        await PullCoreAsync(ct);
    }

    private async Task PullCoreAsync(CancellationToken ct)
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
        ScheduleCacheSave();
    }

    /// <summary>
    /// The entry as it connects: a copy with every section ticked "Folder" taken from the nearest parent
    /// folder that sets it itself (a folder can pass on what it inherits). No folder: the defaults.
    /// </summary>
    public Entry Effective(Entry entry)
    {
        if (entry.Inherit == InheritSections.None) return entry;
        var copy = Clone(entry);
        foreach (var section in SettingsInheritance.All)
        {
            if (!entry.Inherit.HasFlag(section)) continue;
            var source = new Entry();
            var p = entry.ParentId;
            for (var hops = 0; p is { } pid && hops < 64; hops++)
            {
                if (Get(pid) is not { } folder) break;
                if (!folder.Inherit.HasFlag(section)) { source = folder; break; }
                p = folder.ParentId;
            }
            SettingsInheritance.Copy(section, source, copy);
        }
        return copy;
    }

    public static Entry Clone(Entry e)
    {
        var c = new Entry
        {
            Id = e.Id, ParentId = e.ParentId, Kind = e.Kind, Name = e.Name,
            Revision = e.Revision, UpdatedAt = e.UpdatedAt, UpdatedBy = e.UpdatedBy, Deleted = e.Deleted,
            CreatedAt = e.CreatedAt, CreatedBy = e.CreatedBy,
        };
        EntryJson.ApplyPayload(c, EntryJson.ToPayload(e));
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_heartbeat is not null) await _heartbeat.DisposeAsync();
        lock (_cts) { _reconnect?.Dispose(); _reconnect = null; }
        if (_cacheTimer is not null) { await _cacheTimer.DisposeAsync(); SaveCache(); }
        if (IsOnline) { try { await _source.ClearPresenceAsync(User, Machine); } catch { } }
        _source.Changed -= OnSourceChanged;
        _source.ConnectionChanged -= OnSourceConnectionChanged;
        await _source.DisposeAsync();
    }
}

/// <summary>Where the last known entries are kept so RemoteMan can open while the database is down.</summary>
public interface IEntryCache
{
    IReadOnlyList<Entry>? Load();
    void Save(IReadOnlyList<Entry> entries);
}

public sealed class DataSourceOfflineException(Exception? inner)
    : Exception("Offline: the database can't be reached, so changes can't be saved right now. RemoteMan keeps retrying in the background.", inner);
