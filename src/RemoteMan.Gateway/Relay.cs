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
public sealed class Relay
{
    private sealed class Link(string site, string name, string key, WebSocket control, string? from)
    {
        public string? From { get; } = from;
        public string Site { get; } = site;
        public string Name { get; } = name;
        public string Key { get; } = key;
        public WebSocket Control { get; } = control;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int Active;
        public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
    }

    private sealed record Pending(string Site, TaskCompletionSource<WebSocket> Socket, TaskCompletionSource Done);

    private readonly RelayStore _store;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, List<Link>> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
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

    /// <summary>A connector's control connection. Returns when it goes away.</summary>
    public async Task ServeConnectorAsync(SiteRecord site, string name, string key, WebSocket ws, CancellationToken ct, string? from = null)
    {
        if (_store.AddKey(site.Name, key))
            _log($"Site {site.Name}: new connector key {SecureChannel.Fingerprint(Convert.FromBase64String(key))} from {name}");
        var link = new Link(site.Name, name, key, ws, from);
        var list = _links.GetOrAdd(site.Name, _ => []);
        lock (list) list.Add(link);
        _log($"Site {site.Name}: connector {name} online{(from is null ? "" : $" from {from}")}");
        try
        {
            var buf = new byte[64];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally
        {
            lock (list) list.Remove(link);
            _log($"Site {site.Name}: connector {name} offline");
            await CloseQuietly(ws);
        }
    }

    /// <summary>
    /// Asks one of the site's connectors (the least busy) for a tunnel. Null when none is online or none answers
    /// in time; the caller turns that into 503/504 before upgrading RemoteMan's request.
    /// </summary>
    public async Task<(WebSocket Socket, TaskCompletionSource Done)?> RequestTunnelAsync(string site, CancellationToken ct)
    {
        if (!_links.TryGetValue(site, out var list)) return null;
        foreach (var link in Snapshot(list).OrderBy(l => Volatile.Read(ref l.Active)).Take(2))
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var p = new Pending(site, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending[id] = p;
            try
            {
                await link.SendLock.WaitAsync(ct);
                try { await link.Control.SendAsync(Encoding.ASCII.GetBytes("open " + id), WebSocketMessageType.Text, true, ct); }
                finally { link.SendLock.Release(); }
                var socket = await p.Socket.Task.WaitAsync(PairTimeout, ct);
                Interlocked.Increment(ref link.Active);
                _ = p.Done.Task.ContinueWith(_ => Interlocked.Decrement(ref link.Active), TaskScheduler.Default);
                return (socket, p.Done);
            }
            catch (Exception ex) when (ex is TimeoutException or WebSocketException)
            {
                _log($"Site {site}: connector {link.Name} didn't answer");
                p.Done.TrySetResult();
            }
            finally { _pending.TryRemove(id, out _); }
        }
        return null;
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
    public async Task SpliceAsync(WebSocket client, WebSocket connector, TaskCompletionSource done, CancellationToken ct)
    {
        Interlocked.Increment(ref _tunnels);
        Interlocked.Increment(ref _tunnelsTotal);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var a = CopyAsync(client, connector, cts);
            var b = CopyAsync(connector, client, cts);
            await Task.WhenAll(a, b);
        }
        finally
        {
            Interlocked.Decrement(ref _tunnels);
            await CloseQuietly(client);
            await CloseQuietly(connector);
            done.TrySetResult();
        }
    }

    private async Task CopyAsync(WebSocket from, WebSocket to, CancellationTokenSource cts)
    {
        var buf = new byte[64 * 1024];
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
                await to.SendAsync(buf.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // One side broke off without a close: end the other direction too.
            cts.Cancel();
        }
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
