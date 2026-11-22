using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RemoteMan.Core.Sharing;

/// <summary>
/// A guest's side of a shared session. Tries the host's own addresses first (all at once, the
/// first to answer wins), then the relay. TLS pins the host's certificate from the invite.
/// </summary>
public sealed class ShareClient : IAsyncDisposable
{
    private readonly ShareLink _link;
    private readonly ConcurrentDictionary<int, TcpClient> _tunnels = new();
    private TcpListener? _tunnelListener;
    private int _nextTunnel;

    private ShareClient(ShareLink link, WelcomeMessage welcome, string via)
    {
        _link = link;
        Welcome = welcome;
        State = welcome.State;
        Cols = welcome.Cols; Rows = welcome.Rows;
        Via = via;
    }

    public WelcomeMessage Welcome { get; }
    public string MyId => Welcome.YourId;
    public ShareSnapshot State { get; private set; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    /// <summary>"direct" or "relay", for the status bar.</summary>
    public string Via { get; }
    public bool HasControl => State.ControllerId == MyId;

    public event Action<string>? Output;
    public event Action<int, int>? SizeChanged;
    public event Action<ShareSnapshot>? StateChanged;
    /// <summary>Someone asks for control while I have it (participant).</summary>
    public event Action<ShareParticipant>? ControlRequested;
    /// <summary>The link ended: the reason, or null when it just dropped.</summary>
    public event Action<string?>? Ended;

    public static async Task<ShareClient> ConnectAsync(ShareInvite invite, string user, string machine, bool readOnly,
        IEnumerable<string> extraRelays, CancellationToken ct = default)
    {
        var (stream, via) = await OpenAsync(invite, extraRelays, ct);
        var ssl = await ShareNet.ClientTlsAsync(stream, invite.Thumbprint, ct);
        var link = new ShareLink(ssl);
        link.Send(ShareWire.FrameJson(ShareFrame.Hello, new HelloMessage(invite.Ticket, user, machine, readOnly)));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        var first = await ShareWire.ReadAsync(ssl, wait.Token);
        if (first is { Type: ShareFrame.Ended } end)
        {
            await link.DisposeAsync();
            throw new ShareRefusedException(ShareWire.Text(end.Payload));
        }
        if (first is not { Type: ShareFrame.Welcome } w)
        {
            await link.DisposeAsync();
            throw new IOException("The host didn't answer as expected.");
        }
        var client = new ShareClient(link, ShareWire.ReadJson<WelcomeMessage>(w.Payload), via);
        _ = Task.Run(client.ReadLoopAsync);
        return client;
    }

    private static async Task<(Stream Stream, string Via)> OpenAsync(ShareInvite invite, IEnumerable<string> extraRelays, CancellationToken ct)
    {
        // Direct: race every address the host listed.
        var direct = invite.Addresses
            .Select(a => ShareNet.TryParseEndpoint(a, ShareNet.DefaultPort, out var h, out var p) ? (h, p) : default)
            .Where(x => x.h is not null).ToList();
        if (direct.Count > 0)
        {
            using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tries = direct.Select(d => ShareNet.ConnectTcpAsync(d.h, d.p, TimeSpan.FromSeconds(3), race.Token)).ToList();
            while (tries.Count > 0)
            {
                var done = await Task.WhenAny(tries);
                tries.Remove(done);
                if (done.IsCompletedSuccessfully)
                {
                    race.Cancel();
                    foreach (var other in tries) _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                    return (done.Result.GetStream(), "direct");
                }
            }
        }

        // Relay: the host's, then ours (usually the same database server).
        var relays = new List<string>();
        if (invite.Relay is { Length: > 0 } r) relays.Add(r);
        relays.AddRange(extraRelays.Where(x => !string.IsNullOrWhiteSpace(x)));
        Exception? last = null;
        foreach (var relay in relays.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ShareNet.TryParseEndpoint(relay, ShareNet.RelayPort, out var h, out var p)) continue;
            TcpClient? tcp = null;
            try
            {
                tcp = await ShareNet.ConnectTcpAsync(h, p, TimeSpan.FromSeconds(4), ct);
                var s = tcp.GetStream();
                await ShareNet.WriteLineAsync(s, $"JOIN {invite.ShareId:N}", ct);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(10));
                var answer = await ShareNet.ReadLineAsync(s, wait.Token);
                if (answer == "OK") return (s, "relay");
                last = new IOException(answer == "NOHOST" ? "The host isn't registered with the relay." : $"Relay said: {answer}");
            }
            catch (Exception ex) { last = ex; }
            tcp?.Dispose();
        }
        throw new ShareUnreachableException(direct.Count, relays.Count, last);
    }

    private async Task ReadLoopAsync()
    {
        string? reason = null;
        try
        {
            while (true)
            {
                var f = await _link.ReadAsync();
                if (f is not { } fr) break;
                switch (fr.Type)
                {
                    case ShareFrame.Output: Output?.Invoke(ShareWire.Text(fr.Payload)); break;
                    case ShareFrame.Size:
                        var size = ShareWire.ReadJson<SizeMessage>(fr.Payload);
                        Cols = size.Cols; Rows = size.Rows;
                        SizeChanged?.Invoke(size.Cols, size.Rows);
                        break;
                    case ShareFrame.State:
                        State = ShareWire.ReadJson<ShareSnapshot>(fr.Payload);
                        StateChanged?.Invoke(State);
                        break;
                    case ShareFrame.RequestControl:
                        if (State.Participants.FirstOrDefault(p => p.Id == ShareWire.Text(fr.Payload)) is { } who) ControlRequested?.Invoke(who);
                        break;
                    case ShareFrame.TunnelData:
                        var (id, data) = ShareWire.ReadTunnel(fr.Payload);
                        if (_tunnels.TryGetValue(id, out var t))
                        {
                            try { await t.GetStream().WriteAsync(data); } catch (Exception) { CloseTunnel(id, tell: true); }
                        }
                        break;
                    case ShareFrame.TunnelClose: CloseTunnel(ShareWire.ReadTunnel(fr.Payload).Id, tell: false); break;
                    case ShareFrame.Ended: reason = ShareWire.Text(fr.Payload); goto done;
                }
            }
        }
        catch (Exception) { }
        done:
        _link.Close();
        Ended?.Invoke(reason);
    }

    public void SendInput(string text) { if (HasControl && text.Length > 0) _link.Send(ShareWire.Frame(ShareFrame.Input, text)); }
    public void RequestControl() => _link.Send(ShareWire.Frame(ShareFrame.RequestControl));
    public void Give(string participantId) => _link.Send(ShareWire.Frame(ShareFrame.GiveControl, participantId));
    /// <summary>Hands control back to the host.</summary>
    public void GiveBack() { if (State.Host is { } h) Give(h.Id); }

    /// <summary>
    /// RDP: a local port that tunnels to the host's Windows Desktop Sharing port through this link,
    /// so the viewer works the same whether we reached the host directly or through the relay.
    /// </summary>
    public int StartTunnel()
    {
        if (_tunnelListener is not null) return ((IPEndPoint)_tunnelListener.LocalEndpoint).Port;
        var l = _tunnelListener = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            while (!_link.IsClosed)
            {
                TcpClient tcp;
                try { tcp = await l.AcceptTcpClientAsync(_link.Closing); } catch (Exception) { return; }
                tcp.NoDelay = true;
                var id = Interlocked.Increment(ref _nextTunnel);
                _tunnels[id] = tcp;
                _link.Send(ShareWire.TunnelFrame(ShareFrame.TunnelOpen, id));
                _ = Task.Run(async () =>
                {
                    var buf = new byte[64 * 1024];
                    try
                    {
                        var s = tcp.GetStream();
                        while (true)
                        {
                            var n = await s.ReadAsync(buf, _link.Closing);
                            if (n == 0) break;
                            _link.Send(ShareWire.TunnelFrame(ShareFrame.TunnelData, id, buf.AsSpan(0, n)));
                        }
                    }
                    catch (Exception) { }
                    CloseTunnel(id, tell: true);
                });
            }
        });
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private void CloseTunnel(int id, bool tell)
    {
        if (!_tunnels.TryRemove(id, out var t)) return;
        try { t.Dispose(); } catch (Exception) { }
        if (tell) _link.Send(ShareWire.TunnelFrame(ShareFrame.TunnelClose, id));
    }

    public async Task LeaveAsync()
    {
        await _link.CloseAfterAsync(ShareWire.Frame(ShareFrame.Leave));
    }

    public async ValueTask DisposeAsync()
    {
        try { _tunnelListener?.Stop(); } catch (Exception) { }
        foreach (var id in _tunnels.Keys.ToList()) CloseTunnel(id, tell: false);
        if (!_link.IsClosed) await LeaveAsync();
        await _link.DisposeAsync();
    }
}

public sealed class ShareRefusedException(string reason) : Exception(reason);

public sealed class ShareUnreachableException(int direct, int relays, Exception? last)
    : Exception(relays == 0
        ? $"Couldn't reach the host on any of its {direct} address{(direct == 1 ? "" : "es")}, and there's no relay to fall back on."
        : $"Couldn't reach the host directly or through the relay.", last);
