using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace RemoteMan.Gateway;

/// <summary>
/// The relay's side of multiplexing: devices keep one WebSocket (<see cref="GatewayProtocol.MuxPath"/>) with all
/// their tunnels as streams, and connectors that offer it get their tunnels as streams on their control connection.
/// The relay pairs each device stream with a connector stream (or, for an older connector, a WebSocket of its own)
/// and forwards frames between them; flow control runs end to end, and the relay checks nobody sends beyond it.
/// </summary>
public sealed partial class Relay
{
    private sealed class Bridge
    {
        public required MuxSession Client { get; init; }
        public required uint ClientId { get; init; }
        public required TunnelInfo Info { get; init; }
        public MuxSession? Conn;
        public uint ConnId;
        public Link? Link;
        public LegacyBridge? Legacy;
        public TaskCompletionSource<bool>? Accept;
        /// <summary>Frames from the device before the connector side is ready (the handshake is sent right away).</summary>
        public List<(MuxOp Op, byte[] Payload)>? Pending = [];
        /// <summary>What each side may still send before the other grants more (checked, never trusted).</summary>
        public long Up = MuxSession.Window, Down = MuxSession.Window;
        public bool ClientEnded, ConnEnded, Started, Done;
        public long LiveId;
        public CancellationTokenSource? Stopper;
    }

    private readonly ConcurrentDictionary<MuxSession, string> _deviceSessions = new();

    /// <summary>A device's shared connection. Returns when it goes away.</summary>
    public async Task ServeDeviceAsync(string token, string device, WebSocket ws, string? from, CancellationToken ct)
    {
        var s = new MuxSession(ws, isWebSocketClient: false);
        s.OnFrame = (op, id, payload) => FromClient(s, token, from, op, id, payload);
        _deviceSessions[s] = device;
        try { await s.RunAsync(ct); }
        finally
        {
            _deviceSessions.TryRemove(s, out _);
            foreach (var b in s.Streams.Values.OfType<Bridge>()) Kill(b, toClient: false);
        }
    }

    private void FromClient(MuxSession s, string token, string? from, MuxOp op, uint id, ReadOnlySpan<byte> payload)
    {
        if (op == MuxOp.Open)
        {
            if (s.IsMine(id) || s.Streams.ContainsKey(id)) { s.Send(MuxOp.Reset, id); return; }
            var siteName = Encoding.UTF8.GetString(payload);
            // Checked per stream, so a device removed or paused since it connected is refused at once.
            if (_store.DeviceByToken(token) is not { } device) { s.SendRefuse(id, HttpStatusCode.Unauthorized, RelayRefusedException.Describe(HttpStatusCode.Unauthorized)); return; }
            if (_store.Site(siteName) is not { } site) { s.SendRefuse(id, HttpStatusCode.NotFound, RelayRefusedException.Describe(HttpStatusCode.NotFound)); return; }
            if (!device.MayUse(site.Name)) { s.SendRefuse(id, HttpStatusCode.Forbidden, RelayRefusedException.Describe(HttpStatusCode.Forbidden)); return; }
            if (!IsOnline(site.Name)) { s.SendRefuse(id, HttpStatusCode.ServiceUnavailable, RelayRefusedException.Describe(HttpStatusCode.ServiceUnavailable)); return; }
            _store.TouchDevice(device.Name, from);
            var b = new Bridge { Client = s, ClientId = id, Info = new TunnelInfo { Device = device.Name, Site = site.Name, From = from } };
            s.Streams[id] = b;
            _ = ConnectAsync(b, site.Name);
            return;
        }
        if (!s.Streams.TryGetValue(id, out var o) || o is not Bridge br) return;
        switch (op)
        {
            case MuxOp.Data:
            case MuxOp.End:
                lock (br)
                {
                    if (br.Done) return;
                    if (op == MuxOp.Data)
                    {
                        if ((br.Up -= payload.Length) < 0) { _log($"Site {br.Info.Site}: {br.Info.Device} sent past its window; tunnel closed"); KillLocked(br); return; }
                        Count(br, payload.Length);
                    }
                    else br.ClientEnded = true;
                    if (br.Pending is { } pending) pending.Add((op, payload.ToArray()));
                    else if (br.Conn is { } conn) conn.Send(op, br.ConnId, payload);
                    else br.Legacy?.Enqueue(op == MuxOp.End ? null : payload.ToArray());
                }
                MaybeFinish(br);
                break;
            case MuxOp.Credit when payload.Length >= 4:
                var n = BinaryPrimitives.ReadInt32BigEndian(payload);
                lock (br)
                {
                    br.Down += n;
                    br.Conn?.SendCredit(br.ConnId, n);
                }
                br.Legacy?.Signal();
                break;
            case MuxOp.Reset:
                Kill(br, toClient: false);
                break;
        }
    }

    private void FromConnector(Link link, MuxOp op, uint id, ReadOnlySpan<byte> payload)
    {
        if (op == MuxOp.Open) { link.Session.Send(MuxOp.Reset, id); return; }   // only the relay opens streams here
        if (!link.Session.Streams.TryGetValue(id, out var o) || o is not Bridge b) return;
        switch (op)
        {
            case MuxOp.Accept: b.Accept?.TrySetResult(true); break;
            case MuxOp.Refuse: b.Accept?.TrySetResult(false); break;
            case MuxOp.Data:
            case MuxOp.End:
                lock (b)
                {
                    if (b.Done) return;
                    if (op == MuxOp.Data)
                    {
                        if ((b.Down -= payload.Length) < 0) { _log($"Site {b.Info.Site}: connector {link.Name} sent past its window; tunnel closed"); KillLocked(b); return; }
                        Count(b, payload.Length);
                    }
                    else b.ConnEnded = true;
                    b.Client.Send(op, b.ClientId, payload);
                }
                MaybeFinish(b);
                break;
            case MuxOp.Credit when payload.Length >= 4:
                var n = BinaryPrimitives.ReadInt32BigEndian(payload);
                lock (b)
                {
                    b.Up += n;
                    b.Client.SendCredit(b.ClientId, n);
                }
                break;
            case MuxOp.Reset:
                Kill(b, toConn: false);
                break;
        }
    }

    /// <summary>Pairs the device's stream with a connector: the least busy first, then the next one.</summary>
    private async Task ConnectAsync(Bridge b, string site)
    {
        try
        {
            var links = _links.TryGetValue(site, out var list) ? Snapshot(list).OrderBy(l => Volatile.Read(ref l.Active)).Take(2).ToList() : [];
            foreach (var link in links)
            {
                if (b.Done) return;
                if (link.Mux)
                {
                    var connId = link.Session.NewId();
                    var accept = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (b) { b.Accept = accept; b.ConnId = connId; }
                    link.Session.Streams[connId] = b;
                    var ok = link.Session.Send(MuxOp.Open, connId);
                    try { ok = ok && await accept.Task.WaitAsync(PairTimeout); }
                    catch (TimeoutException) { ok = false; }
                    if (ok)
                    {
                        lock (b)
                        {
                            if (b.Done) { link.Session.Send(MuxOp.Reset, connId); link.Session.Streams.TryRemove(connId, out _); return; }
                            b.Conn = link.Session;
                            b.Link = link;
                            foreach (var (op, payload) in b.Pending!) link.Session.Send(op, connId, payload);
                            b.Pending = null;
                            Begin(b, link, countActive: true);
                            b.Client.Send(MuxOp.Accept, b.ClientId);
                        }
                        MaybeFinish(b);
                        return;
                    }
                    link.Session.Streams.TryRemove(connId, out _);
                    link.Session.Send(MuxOp.Reset, connId);
                    _log($"Site {site}: connector {link.Name} didn't answer");
                }
                else if (await RequestFromAsync(link, CancellationToken.None) is var (socket, done))
                {
                    var legacy = new LegacyBridge(this, b, socket, done);
                    lock (b)
                    {
                        if (b.Done) { done.TrySetResult(); socket.Abort(); return; }
                        b.Legacy = legacy;
                        b.Link = link;
                        foreach (var (op, payload) in b.Pending!) legacy.Enqueue(op == MuxOp.End ? null : payload);
                        b.Pending = null;
                        Begin(b, link, countActive: false);   // RequestFromAsync counted it
                        b.Client.Send(MuxOp.Accept, b.ClientId);
                    }
                    legacy.Start();
                    return;
                }
            }
            lock (b)
            {
                if (b.Done) return;
                b.Client.SendRefuse(b.ClientId, HttpStatusCode.GatewayTimeout, RelayRefusedException.Describe(HttpStatusCode.GatewayTimeout));
                b.Pending = null;
                Finish(b);
            }
        }
        catch (Exception ex)
        {
            _log($"Site {site}: {ex.GetBaseException().Message}");
            Kill(b);
        }
    }

    private void Begin(Bridge b, Link link, bool countActive)
    {
        b.Started = true;
        b.Info.Connector = link.Name;
        if (countActive) Interlocked.Increment(ref link.Active);
        Interlocked.Increment(ref _tunnels);
        Interlocked.Increment(ref _tunnelsTotal);
        b.LiveId = Interlocked.Increment(ref _nextTunnel);
        _live[b.LiveId] = b.Info;
        b.Stopper = new CancellationTokenSource();
        b.Info.Stopper = b.Stopper;
        b.Stopper.Token.Register(() => Kill(b));
        if (b.Info.Stopped) b.Stopper.Cancel();
        Emit("tunnel.open", b.Info.Site, b.Info.Device, $"{b.Info.Device} opened a tunnel to {b.Info.Site} via {link.Name}");
    }

    private void Count(Bridge b, int n)
    {
        Interlocked.Add(ref _bytes, n);
        b.Info.Add(n);
    }

    private void MaybeFinish(Bridge b)
    {
        lock (b) if (b.ClientEnded && b.ConnEnded) Finish(b);
    }

    /// <summary>Ends the tunnel at once: a Reset to whichever side still has it.</summary>
    private void Kill(Bridge b, bool toClient = true, bool toConn = true)
    {
        lock (b) KillLocked(b, toClient, toConn);
    }

    private void KillLocked(Bridge b, bool toClient = true, bool toConn = true, bool connectorGone = false)
    {
        if (b.Done) return;
        if (toClient) b.Client.Send(MuxOp.Reset, b.ClientId, connectorGone ? [1] : []);
        if (toConn && b.Conn is { } c) c.Send(MuxOp.Reset, b.ConnId);
        b.Accept?.TrySetResult(false);
        Finish(b);
    }

    /// <summary>Forgets the tunnel (under the bridge's lock).</summary>
    private void Finish(Bridge b)
    {
        if (b.Done) return;
        b.Done = true;
        b.Client.Streams.TryRemove(b.ClientId, out _);
        if (b.Conn is { } c) c.Streams.TryRemove(b.ConnId, out _);
        else if (b.Link is null && b.Accept is not null) b.Accept.TrySetResult(false);
        b.Legacy?.Abort();
        if (!b.Started) return;
        if (b.Link is { } link && b.Legacy is null) Interlocked.Decrement(ref link.Active);
        _live.TryRemove(b.LiveId, out _);
        Interlocked.Decrement(ref _tunnels);
        Emit("tunnel.close", b.Info.Site, b.Info.Device,
            $"{b.Info.Device}'s tunnel to {b.Info.Site} closed after {Duration(DateTimeOffset.UtcNow - b.Info.Started)}, {Bytes(b.Info.BytesTotal)}");
        var stopper = b.Stopper;
        _ = Task.Run(() => stopper?.Dispose());
    }

    /// <summary>A connector's control connection went away: its tunnels end, and the devices see them drop.</summary>
    private void ConnectorGone(Link link)
    {
        foreach (var b in link.Session.Streams.Values.OfType<Bridge>())
            lock (b) KillLocked(b, toConn: false, connectorGone: b.Started);
    }

    /// <summary>A device stream paired with an older connector's own WebSocket for the tunnel.</summary>
    private sealed class LegacyBridge(Relay relay, Bridge b, WebSocket ws, TaskCompletionSource done)
    {
        private readonly Channel<byte[]?> _up = Channel.CreateUnbounded<byte[]?>(new UnboundedChannelOptions { SingleReader = true });
        private TaskCompletionSource? _credit;
        private int _aborted;

        public void Enqueue(byte[]? message) => _up.Writer.TryWrite(message);

        public void Signal() => Interlocked.Exchange(ref _credit, null)?.TrySetResult();

        public void Start()
        {
            _ = Task.Run(WriteAsync);
            _ = Task.Run(ReadAsync);
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
            _up.Writer.TryComplete();
            Signal();
            try { ws.Abort(); } catch { }
            done.TrySetResult();
        }

        // Device → connector. The relay grants the device's credit itself, once the connector has taken the bytes.
        private async Task WriteAsync()
        {
            var granted = 0;
            try
            {
                await foreach (var m in _up.Reader.ReadAllAsync())
                {
                    if (m is null)
                    {
                        using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token);
                        continue;
                    }
                    await ws.SendAsync(m, WebSocketMessageType.Binary, true, CancellationToken.None);
                    granted += m.Length;
                    if (granted >= MuxSession.Window / 4 || !_up.Reader.TryPeek(out _))
                    {
                        lock (b) { b.Up += granted; b.Client.SendCredit(b.ClientId, granted); }
                        granted = 0;
                    }
                }
            }
            catch (Exception) { relay.Kill(b); }
        }

        // Connector → device, only as fast as the device grants.
        private async Task ReadAsync()
        {
            var buf = new byte[MuxSession.MaxPayload];
            var pipe = new WebSocketPipe(ws);
            try
            {
                while (true)
                {
                    var n = await pipe.ReceiveAsync(buf, CancellationToken.None);
                    if (n < 0)
                    {
                        lock (b) { if (b.Done) return; b.ConnEnded = true; b.Client.Send(MuxOp.End, b.ClientId); }
                        relay.MaybeFinish(b);
                        return;
                    }
                    while (true)
                    {
                        Task wait;
                        lock (b)
                        {
                            if (b.Done) return;
                            if (b.Down >= n)
                            {
                                b.Down -= n;
                                relay.Count(b, n);
                                b.Client.Send(MuxOp.Data, b.ClientId, buf.AsSpan(0, n));
                                break;
                            }
                            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            Volatile.Write(ref _credit, tcs);
                            wait = tcs.Task;
                        }
                        await wait;
                    }
                }
            }
            catch (Exception) { relay.Kill(b); }
        }
    }
}
