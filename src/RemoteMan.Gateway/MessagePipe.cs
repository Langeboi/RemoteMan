using System.Net.WebSockets;

namespace RemoteMan.Gateway;

/// <summary>
/// Whole messages both ways: what <see cref="SecureChannel"/> runs over. A WebSocket of its own (the original
/// one-connection-per-tunnel protocol) or one stream of a shared, multiplexed connection (<see cref="MuxStream"/>).
/// </summary>
public interface IMessagePipe : IDisposable
{
    /// <summary>Sends one message.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);

    /// <summary>
    /// The next whole message, copied into <paramref name="into"/>. Returns its length, or -1 when the other side
    /// has finished sending. Throws <see cref="GatewayException"/> when the message doesn't fit or the pipe broke.
    /// </summary>
    ValueTask<int> ReceiveAsync(Memory<byte> into, CancellationToken ct);

    /// <summary>Nothing more will be sent (the other side still may).</summary>
    Task CloseOutputAsync();
}

/// <summary>One WebSocket as a message pipe.</summary>
public sealed class WebSocketPipe(WebSocket ws) : IMessagePipe
{
    public WebSocket Socket { get; } = ws;

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
        Socket.SendAsync(message, WebSocketMessageType.Binary, true, ct);

    public async ValueTask<int> ReceiveAsync(Memory<byte> into, CancellationToken ct)
    {
        var n = 0;
        while (true)
        {
            if (n == into.Length) throw new GatewayException("Message too large.");
            var r = await Socket.ReceiveAsync(into[n..], ct);
            if (r.MessageType == WebSocketMessageType.Close) return -1;
            n += r.Count;
            if (r.EndOfMessage) return n;
        }
    }

    public async Task CloseOutputAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        }
        catch { }
    }

    public void Dispose() => Socket.Dispose();
}
