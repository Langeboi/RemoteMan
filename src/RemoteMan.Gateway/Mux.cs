using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace RemoteMan.Gateway;

/// <summary>
/// Frames of the multiplexed protocol. Each is one binary WebSocket message: <c>op (1) | stream id (u32 BE) | payload</c>.
/// Text messages on the same WebSocket are the connector's control lines ("allow …", "open &lt;id&gt;"), untouched.
/// </summary>
internal enum MuxOp : byte
{
    /// <summary>Opens a stream. From RemoteMan the payload is the site's name; from the relay to a connector it's empty.</summary>
    Open = 1,
    /// <summary>The stream is connected end to end (informational: the opener may already be sending).</summary>
    Accept = 2,
    /// <summary>The stream can't be opened: u16 HTTP-like status, then a message for people.</summary>
    Refuse = 3,
    /// <summary>One message of the stream (one sealed <see cref="SecureChannel"/> frame, or a handshake message).</summary>
    Data = 4,
    /// <summary>The sender won't send more on this stream (it still receives).</summary>
    End = 5,
    /// <summary>The stream is gone, both ways. Optional payload: 1 = its connector went away (a reconnect lands on another).</summary>
    Reset = 6,
    /// <summary>u32 BE: the receiver consumed this many more payload bytes; the sender may send that much more.</summary>
    Credit = 7,
}

internal delegate void MuxFrameHandler(MuxOp op, uint id, ReadOnlySpan<byte> payload);

/// <summary>
/// One WebSocket carrying many streams (RemoteMan ↔ relay, or relay ↔ connector), so a new tunnel costs one round
/// trip on a connection that's already open instead of a new TLS and WebSocket handshake at both ends. Frames are
/// queued and written by one writer, so a slow stream never blocks the reader; each stream has its own window
/// (<see cref="Window"/> bytes in flight), so one busy stream can't starve the others or flood memory.
/// </summary>
internal sealed class MuxSession
{
    public const int MaxPayload = 64 * 1024;
    /// <summary>Bytes a stream may have in flight before the receiver grants more (~20 MB/s at 50 ms round trip).</summary>
    public const int Window = 1024 * 1024;

    private readonly WebSocket _ws;
    private readonly Channel<Outgoing> _out = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly uint _parity;
    private uint _nextId;
    private int _aborted;

    private readonly record struct Outgoing(byte[]? Buffer, int Length, WebSocketMessageType Type, bool Pooled);

    /// <param name="ws">The WebSocket.</param>
    /// <param name="isWebSocketClient">Whether this end opened the WebSocket: it numbers its streams odd, the other end even.</param>
    public MuxSession(WebSocket ws, bool isWebSocketClient)
    {
        _ws = ws;
        _parity = isWebSocketClient ? 1u : 0u;
        _nextId = isWebSocketClient ? 1u : 2u;
    }

    /// <summary>Binary frames, on the reader (must not block; copy the payload to keep it).</summary>
    public MuxFrameHandler? OnFrame { get; set; }
    /// <summary>Text messages, on the reader.</summary>
    public Action<string>? OnText { get; set; }
    /// <summary>The WebSocket is gone (raised once).</summary>
    public event Action? Closed;

    /// <summary>Whatever the owner keeps per stream (the relay's bridges).</summary>
    public ConcurrentDictionary<uint, object> Streams { get; } = new();

    public bool IsOpen => Volatile.Read(ref _aborted) == 0;

    public uint NewId() => Interlocked.Add(ref _nextId, 2) - 2;

    /// <summary>Whether <paramref name="id"/> is one this end numbers (and so the other end may not open).</summary>
    public bool IsMine(uint id) => (id & 1) == _parity;

    public bool Send(MuxOp op, uint id, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("Payload too large.", nameof(payload));
        var buf = ArrayPool<byte>.Shared.Rent(5 + payload.Length);
        buf[0] = (byte)op;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1), id);
        payload.CopyTo(buf.AsSpan(5));
        if (_out.Writer.TryWrite(new Outgoing(buf, 5 + payload.Length, WebSocketMessageType.Binary, true))) return true;
        ArrayPool<byte>.Shared.Return(buf);
        return false;
    }

    public bool SendCredit(uint id, int n)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, n);
        return Send(MuxOp.Credit, id, b);
    }

    public bool SendRefuse(uint id, HttpStatusCode status, string message)
    {
        var text = Encoding.UTF8.GetBytes(message.Length > 500 ? message[..500] : message);
        var b = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)status);
        text.CopyTo(b, 2);
        return Send(MuxOp.Refuse, id, b);
    }

    public static (HttpStatusCode Status, string Message) ReadRefuse(ReadOnlySpan<byte> payload) =>
        payload.Length < 2 ? (HttpStatusCode.BadGateway, "The relay refused the tunnel.")
            : ((HttpStatusCode)BinaryPrimitives.ReadUInt16BigEndian(payload), Encoding.UTF8.GetString(payload[2..]));

    public bool SendText(string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        return _out.Writer.TryWrite(new Outgoing(b, b.Length, WebSocketMessageType.Text, false));
    }

    /// <summary>Closes the WebSocket politely once what's queued is sent.</summary>
    public void Close() => _out.Writer.TryWrite(new Outgoing(null, 0, WebSocketMessageType.Close, false));

    /// <summary>Reads until the WebSocket closes or breaks (or <paramref name="ct"/>), then raises <see cref="Closed"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var reg = ct.Register(Abort);
        var writer = Task.Run(WriteLoopAsync);
        try { await ReadLoopAsync(); }
        catch (Exception) { }   // a broken or aborted WebSocket ends the session like a close
        finally
        {
            Abort();
            try { await writer; } catch { }
        }
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
        _out.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _ws.Abort(); } catch { }
        try { Closed?.Invoke(); } catch { }
    }

    private async Task ReadLoopAsync()
    {
        var buf = new byte[5 + MaxPayload];
        while (true)
        {
            var n = 0;
            ValueWebSocketReceiveResult r;
            while (true)
            {
                if (n == buf.Length) throw new GatewayException("Message too large.");
                r = await _ws.ReceiveAsync(buf.AsMemory(n), _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                n += r.Count;
                if (r.EndOfMessage) break;
            }
            if (r.MessageType == WebSocketMessageType.Text) { OnText?.Invoke(Encoding.UTF8.GetString(buf, 0, n)); continue; }
            if (n < 5) continue;
            OnFrame?.Invoke((MuxOp)buf[0], BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(1)), buf.AsSpan(5, n - 5));
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var o in _out.Reader.ReadAllAsync(_cts.Token))
            {
                if (o.Type == WebSocketMessageType.Close)
                {
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token);
                    return;
                }
                try { await _ws.SendAsync(o.Buffer.AsMemory(0, o.Length), o.Type, true, _cts.Token); }
                finally { if (o.Pooled) ArrayPool<byte>.Shared.Return(o.Buffer!); }
            }
        }
        catch (Exception) { Abort(); }
    }
}

/// <summary>RemoteMan's or a connector's end of a multiplexed connection to the relay: its streams.</summary>
public sealed class MuxEndpoint : IDisposable
{
    private readonly ConcurrentDictionary<uint, MuxStream> _streams = new();

    public MuxEndpoint(WebSocket ws, bool isWebSocketClient)
    {
        Session = new MuxSession(ws, isWebSocketClient) { OnFrame = OnFrame };
        Session.Closed += () =>
        {
            if (_streams.IsEmpty) return;
            foreach (var s in _streams.Values) s.Fail(new GatewayException("The connection to the gateway relay dropped."));
            Lost?.Invoke();
        };
    }

    internal MuxSession Session { get; }

    /// <summary>A stream the other end opened (the relay, on a connector), with the Open frame's payload as text.</summary>
    public Func<MuxStream, string, Task>? Incoming { get; init; }
    /// <summary>Text messages on the WebSocket (a connector's control lines).</summary>
    public Action<string>? OnText { get => Session.OnText; init => Session.OnText = value; }

    public bool IsOpen => Session.IsOpen;
    public int OpenStreams => _streams.Count;

    /// <summary>
    /// Open tunnels ended because their connector or this connection went away, not because either end closed
    /// them: sessions should reconnect now (they'll get another connector, or a new connection).
    /// </summary>
    public event Action? Lost;

    public Task RunAsync(CancellationToken ct) => Session.RunAsync(ct);

    /// <summary>
    /// Opens a stream; the caller can send at once (the relay holds what arrives before the far end is connected).
    /// A refusal shows up as a <see cref="RelayRefusedException"/> from the stream's first receive.
    /// </summary>
    public MuxStream Open(string payload)
    {
        var id = Session.NewId();
        var s = new MuxStream(this, id);
        _streams[id] = s;
        if (!Session.Send(MuxOp.Open, id, Encoding.UTF8.GetBytes(payload))) s.Fail(new GatewayException("The connection to the gateway relay dropped."));
        return s;
    }

    internal void Remove(uint id) => _streams.TryRemove(id, out _);

    private void OnFrame(MuxOp op, uint id, ReadOnlySpan<byte> payload)
    {
        if (op == MuxOp.Open)
        {
            if (Session.IsMine(id) || _streams.ContainsKey(id) || Incoming is not { } incoming) { Session.Send(MuxOp.Reset, id); return; }
            var s = new MuxStream(this, id);
            _streams[id] = s;
            Session.Send(MuxOp.Accept, id);
            var text = Encoding.UTF8.GetString(payload);
            _ = Task.Run(async () =>
            {
                try { await incoming(s, text); }
                catch { }
                finally { s.Dispose(); }
            });
            return;
        }
        if (!_streams.TryGetValue(id, out var st)) return;
        switch (op)
        {
            case MuxOp.Data: st.OnData(payload.ToArray()); break;
            case MuxOp.End: st.OnEnd(); break;
            case MuxOp.Credit when payload.Length >= 4: st.OnCredit(BinaryPrimitives.ReadInt32BigEndian(payload)); break;
            case MuxOp.Refuse:
                var (status, message) = MuxSession.ReadRefuse(payload);
                st.Fail(new RelayRefusedException(status, message.Length > 0 ? message : RelayRefusedException.Describe(status)));
                break;
            case MuxOp.Reset when payload.Length > 0 && payload[0] == 1:
                Lost?.Invoke();
                st.Fail(new GatewayException("The site's connector went away."));
                break;
            case MuxOp.Reset: st.Fail(new GatewayException("The other end closed the tunnel.")); break;
        }
    }

    public void Dispose() => Session.Abort();
}

/// <summary>One tunnel on a shared connection, as a message pipe with its own flow control.</summary>
public sealed class MuxStream : IMessagePipe
{
    private readonly MuxEndpoint _e;
    private readonly Channel<byte[]> _in = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly object _lock = new();
    private TaskCompletionSource? _creditWaiter;
    private long _credit = MuxSession.Window;
    private int _consumed;
    private Exception? _fault;
    private int _endedOut, _endedIn, _disposed;

    internal MuxStream(MuxEndpoint e, uint id)
    {
        _e = e;
        Id = id;
    }

    public uint Id { get; }

    internal void OnData(byte[] data) => _in.Writer.TryWrite(data);

    internal void OnEnd()
    {
        Volatile.Write(ref _endedIn, 1);
        _in.Writer.TryComplete();
        if (Volatile.Read(ref _endedOut) == 1) _e.Remove(Id);
    }

    internal void OnCredit(int n)
    {
        TaskCompletionSource? w;
        lock (_lock) { _credit += n; w = _creditWaiter; _creditWaiter = null; }
        w?.TrySetResult();
    }

    internal void Fail(Exception ex)
    {
        TaskCompletionSource? w;
        lock (_lock) { _fault ??= ex; w = _creditWaiter; _creditWaiter = null; }
        w?.TrySetResult();
        _in.Writer.TryComplete();
        _e.Remove(Id);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (message.Length > MuxSession.MaxPayload) throw new GatewayException("Message too large.");
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_fault is { } f) throw f;
                if (_endedOut == 1) throw new GatewayException("The tunnel was already closed for sending.");
                if (_credit >= message.Length) { _credit -= message.Length; break; }
                _creditWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _creditWaiter.Task;
            }
            await wait.WaitAsync(ct);
        }
        if (!_e.Session.Send(MuxOp.Data, Id, message.Span)) throw new GatewayException("The connection to the gateway relay dropped.");
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> into, CancellationToken ct)
    {
        while (true)
        {
            // What arrived before a reset is still delivered (an error reply right before the far end hung up).
            if (_in.Reader.TryRead(out var d))
            {
                if (d.Length > into.Length) throw new GatewayException("Message too large.");
                d.CopyTo(into);
                // Grant more once a quarter of the window is used up, so the sender never stalls on a healthy line.
                if ((_consumed += d.Length) >= MuxSession.Window / 4)
                {
                    _e.Session.SendCredit(Id, _consumed);
                    _consumed = 0;
                }
                return d.Length;
            }
            if (!await _in.Reader.WaitToReadAsync(ct))
            {
                if (Volatile.Read(ref _fault) is { } f2) throw f2;
                return -1;
            }
        }
    }

    public Task CloseOutputAsync()
    {
        if (Interlocked.Exchange(ref _endedOut, 1) == 0 && Volatile.Read(ref _fault) is null)
        {
            _e.Session.Send(MuxOp.End, Id);
            if (Volatile.Read(ref _endedIn) == 1) _e.Remove(Id);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var clean = Volatile.Read(ref _endedOut) == 1 && Volatile.Read(ref _endedIn) == 1;
        if (!clean && Volatile.Read(ref _fault) is null) _e.Session.Send(MuxOp.Reset, Id);
        Fail(new ObjectDisposedException(nameof(MuxStream)));
    }
}
