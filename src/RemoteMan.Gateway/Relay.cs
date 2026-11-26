using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteMan.Gateway;

/// <summary>
/// The meeting point. Connectors and RemoteMan both connect to it; it pairs a RemoteMan tunnel with a fresh
/// WebSocket from one of the site's connectors and copies messages between them. Everything it copies is
/// end-to-end encrypted, so it never sees the traffic. Hosting (Kestrel, TLS, auth headers) lives in
/// RemoteMan.Gateway.Server; this class only needs authenticated WebSockets.
/// </summary>
public sealed partial class Relay
{
    private sealed class Link(string site, string name, string key, MuxSession session, string? from)
    {
        /// <summary>The connector takes tunnels as streams on its control connection.</summary>
        public bool Mux { get; init; }
        public MuxSession Session { get; } = session;
        public string? From { get; } = from;
        public string? Version { get; init; }
        public string? Allow { get; init; }
        public string? TokenId { get; init; }
        public string Site { get; } = site;
        public string Name { get; } = name;
        public string Key { get; } = key;
        public int Active;
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
    }

    private sealed record Pending(string Site, TaskCompletionSource<WebSocket> Socket, TaskCompletionSource Done);

    private readonly RelayStore _store;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, List<Link>> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<long, TunnelInfo> _live = new();
    private long _nextTunnel;
    private long _bytes, _tunnelsTotal;
    private int _tunnels;

    public Relay(RelayStore store, Action<string>? log = null)
    {
        _store = store;
        _log = log ?? Console.WriteLine;
    }

    public RelayStore Store => _store;
    public TimeSpan PairTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsOnline(string site) => _links.TryGetValue(site, out var l) && Snapshot(l).Length > 0;

    /// <summary>Something the admin console's activity list shows: connectors coming and going, tunnels, refusals.</summary>
    public event Action<GatewayEvent>? Activity;

    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    private void Emit(string kind, string? site, string? subject, string text) =>
        Activity?.Invoke(new GatewayEvent(DateTimeOffset.UtcNow, kind, site, subject, text));

    /// <summary>A connector's control connection. Returns when it goes away.</summary>
    public async Task ServeConnectorAsync(SiteRecord site, string name, string key, WebSocket ws, CancellationToken ct, string? from = null,
        string? version = null, string? allow = null, string? tokenId = null, bool mux = false)
    {
        if (_store.AddKey(site.Name, key))
        {
            _log($"Site {site.Name}: new connector key {SecureChannel.Fingerprint(Convert.FromBase64String(key))} from {name}");
            Emit("connector.key", site.Name, name, $"New connector key {SecureChannel.Fingerprint(Convert.FromBase64String(key))[..16]} recorded for {site.Name}");
        }
        _store.RecordConnector(site.Name, name, key, from, version, allow, tokenId);
        var session = new MuxSession(ws, isWebSocketClient: false);
        var link = new Link(site.Name, name, key, session, from) { Version = version, Allow = allow, TokenId = tokenId ?? "site", Mux = mux };
        session.OnFrame = (op, id, payload) => FromConnector(link, op, id, payload);
        var list = _links.GetOrAdd(site.Name, _ => []);
        lock (list) list.Add(link);
        _log($"Site {site.Name}: connector {name} online{(from is null ? "" : $" from {from}")}");
        Emit("connector.online", site.Name, name, $"Connector {name} online{(from is null ? "" : $" from {from}")}");
        // The site's resources from the console; a connector that predates them ignores the message.
        if (_store.Site(site.Name)?.Resources is { } res) SendControl(link, "allow " + res);
        try { await session.RunAsync(ct); }
        finally
        {
            lock (list) list.Remove(link);
            ConnectorGone(link);
            _store.RecordConnector(site.Name, name, key, from, version, allow, tokenId, leaving: true);
            _log($"Site {site.Name}: connector {name} offline");
            Emit("connector.offline", site.Name, name, $"Connector {name} offline");
        }
    }

    /// <summary>Sends the site's resources (or "allow " for none) to its connectors that are online now.</summary>
    public async Task PushResourcesAsync(string site)
    {
        if (!_links.TryGetValue(site, out var list)) return;
        var res = _store.Site(site)?.Resources ?? "";
        foreach (var link in Snapshot(list)) SendControl(link, "allow " + res);
        await Task.CompletedTask;
    }

    /// <summary>Closes the control connections of a site's connectors (after its token was revoked or the site removed).</summary>
    public async Task DisconnectAsync(string site, string? tokenId = null)
    {
        if (!_links.TryGetValue(site, out var list)) return;
        foreach (var link in Snapshot(list).Where(l => tokenId is null || l.TokenId == tokenId)) link.Session.Close();
        await Task.CompletedTask;
    }

    /// <summary>Ends the tunnels a device has open (after it was removed or paused).</summary>
    public void DropDevice(string device)
    {
        foreach (var t in _live.Values.Where(t => string.Equals(t.Device, device, StringComparison.OrdinalIgnoreCase))) t.Stop();
        // Its shared connection too: it reconnects, and the relay refuses it if it's no longer allowed.
        foreach (var (s, d) in _deviceSessions)
            if (string.Equals(d, device, StringComparison.OrdinalIgnoreCase)) s.Abort();
    }

    private static void SendControl(Link link, string text) => link.Session.SendText(text);

    /// <summary>
    /// Asks one of the site's connectors (the least busy) for a tunnel. Null when none is online or none answers
    /// in time; the caller turns that into 503/504 before upgrading RemoteMan's request.
    /// </summary>
    public async Task<(WebSocket Socket, TaskCompletionSource Done)?> RequestTunnelAsync(string site, CancellationToken ct) =>
        await RequestTunnelFromAsync(site, ct) is var (socket, done, _) ? (socket, done) : null;

    /// <summary>Like <see cref="RequestTunnelAsync"/>, and says which connector answered.</summary>
    public async Task<(WebSocket Socket, TaskCompletionSource Done, string Connector)?> RequestTunnelFromAsync(string site, CancellationToken ct)
    {
        if (!_links.TryGetValue(site, out var list)) return null;
        foreach (var link in Snapshot(list).OrderBy(l => Volatile.Read(ref l.Active)).Take(2))
            if (await RequestFromAsync(link, ct) is var (socket, done)) return (socket, done, link.Name);
        return null;
    }

    /// <summary>A WebSocket of its own from one connector (the original protocol: "open &lt;id&gt;", then /v1/accept/&lt;id&gt;).</summary>
    private async Task<(WebSocket Socket, TaskCompletionSource Done)?> RequestFromAsync(Link link, CancellationToken ct)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var p = new Pending(link.Site, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[id] = p;
        try
        {
            if (!link.Session.SendText("open " + id)) throw new WebSocketException("The connector's control connection is closed.");
            var socket = await p.Socket.Task.WaitAsync(PairTimeout, ct);
            Interlocked.Increment(ref link.Active);
            _ = p.Done.Task.ContinueWith(_ => Interlocked.Decrement(ref link.Active), TaskScheduler.Default);
            return (socket, p.Done);
        }
        catch (Exception ex) when (ex is TimeoutException or WebSocketException)
        {
            _log($"Site {link.Site}: connector {link.Name} didn't answer");
            p.Done.TrySetResult();
            return null;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>A connector's end of a tunnel. Returns when the tunnel is over (the request must stay open until then).</summary>
    public async Task ServeAcceptAsync(SiteRecord site, string id, WebSocket ws)
    {
        if (!_pending.TryGetValue(id, out var p) || !p.Site.Equals(site.Name, StringComparison.OrdinalIgnoreCase) || !p.Socket.TrySetResult(ws))
        {
            await CloseQuietly(ws);
            return;
        }
        await p.Done.Task;
    }

    /// <summary>Copies messages both ways until both sides have closed, then completes <paramref name="done"/>.</summary>
    public async Task SpliceAsync(WebSocket client, WebSocket connector, TaskCompletionSource done, CancellationToken ct, TunnelInfo? info = null)
    {
        Interlocked.Increment(ref _tunnels);
        Interlocked.Increment(ref _tunnelsTotal);
        var id = Interlocked.Increment(ref _nextTunnel);
        info ??= new TunnelInfo();
        _live[id] = info;
        if (info.Device is not null)
            Emit("tunnel.open", info.Site, info.Device, $"{info.Device} opened a tunnel to {info.Site}{(info.Connector is null ? "" : $" via {info.Connector}")}");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            info.Stopper = cts;
            if (info.Stopped) cts.Cancel();
            var a = CopyAsync(client, connector, cts, info);
            var b = CopyAsync(connector, client, cts, info);
            await Task.WhenAll(a, b);
        }
        finally
        {
            _live.TryRemove(id, out _);
            if (info.Device is not null)
                Emit("tunnel.close", info.Site, info.Device,
                    $"{info.Device}'s tunnel to {info.Site} closed after {Duration(DateTimeOffset.UtcNow - info.Started)}, {Bytes(info.BytesTotal)}");
            Interlocked.Decrement(ref _tunnels);
            await CloseQuietly(client);
            await CloseQuietly(connector);
            done.TrySetResult();
        }
    }

    private async Task CopyAsync(WebSocket from, WebSocket to, CancellationTokenSource cts, TunnelInfo info)
    {
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var r = await from.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await to.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                    return;
                }
                Interlocked.Add(ref _bytes, r.Count);
                info.Add(r.Count);
                await to.SendAsync(buf.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // One side broke off without a close: end the other direction too.
            cts.Cancel();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    public IEnumerable<SiteInfo> SitesFor(DeviceRecord device) =>
        _store.Sites.Where(s => device.MayUse(s.Name)).Select(s =>
        {
            var online = _links.TryGetValue(s.Name, out var l) ? Snapshot(l) : [];
            return new SiteInfo(s.Name, online.Length > 0, online.Length, s.Keys.ToList());
        });

    public object Status() => new
    {
        tunnels = Volatile.Read(ref _tunnels),
        tunnelsTotal = Interlocked.Read(ref _tunnelsTotal),
        bytes = Interlocked.Read(ref _bytes),
        sites = _store.Sites.Select(s => new
        {
            name = s.Name,
            connectors = (_links.TryGetValue(s.Name, out var l) ? Snapshot(l) : []).Select(c => new
            {
                name = c.Name, since = c.Since, active = Volatile.Read(ref c.Active), from = c.From,
                key = SecureChannel.Fingerprint(Convert.FromBase64String(c.Key)),
            }),
        }),
        devices = _store.Devices.Select(d => new { name = d.Name, sites = d.Sites, created = d.Created }),
    };

    /// <summary>Everything the admin console shows live: connectors online per site and the tunnels open now.</summary>
    public RelaySnapshot Snapshot() => new(
        Volatile.Read(ref _tunnels), Interlocked.Read(ref _tunnelsTotal), Interlocked.Read(ref _bytes), Started,
        _links.SelectMany(kv => Snapshot(kv.Value)).Select(c => new LiveConnector(
            c.Site, c.Name, SecureChannel.Fingerprint(Convert.FromBase64String(c.Key)), c.Key, c.From, c.Version, c.Allow, c.Since, Volatile.Read(ref c.Active), c.TokenId)).ToList(),
        _live.Values.Select(t => new LiveTunnel(t.Device, t.Site, t.Connector, t.From, t.Started, t.BytesTotal)).ToList());

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : t.TotalMinutes < 60 ? $"{t.TotalMinutes:0} min" : t.TotalHours < 48 ? $"{t.TotalHours:0.#} h" : $"{t.TotalDays:0} days";

    public static string Bytes(long n) =>
        n < 1024 ? $"{n} B" : n < 1 << 20 ? $"{n / 1024.0:0.#} KB" : n < 1 << 30 ? $"{n / 1048576.0:0.#} MB" : $"{n / 1073741824.0:0.##} GB";

    private static Link[] Snapshot(List<Link> list) { lock (list) return list.ToArray(); }

    private static async Task CloseQuietly(WebSocket ws)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        }
        catch { }
    }
}

/// <summary>One tunnel being spliced: who, where, and how much it carried.</summary>
public sealed class TunnelInfo
{
    private long _bytes;
    public string? Device { get; init; }
    public string? Site { get; init; }
    public string? Connector { get; set; }
    public string? From { get; init; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public long BytesTotal => Interlocked.Read(ref _bytes);
    internal void Add(int n) => Interlocked.Add(ref _bytes, n);
    internal CancellationTokenSource? Stopper;
    internal bool Stopped;
    internal void Stop() { Stopped = true; try { Stopper?.Cancel(); } catch (ObjectDisposedException) { } }
}

public sealed record GatewayEvent(DateTimeOffset At, string Kind, string? Site, string? Subject, string Text, string? Actor = null);

public sealed record LiveConnector(string Site, string Name, string Fingerprint, string Key, string? From, string? Version, string? Allow, DateTimeOffset Since, int Active, string? TokenId);
public sealed record LiveTunnel(string? Device, string? Site, string? Connector, string? From, DateTimeOffset Started, long Bytes);
public sealed record RelaySnapshot(int Tunnels, long TunnelsTotal, long Bytes, DateTimeOffset Started, List<LiveConnector> Connectors, List<LiveTunnel> Live);
