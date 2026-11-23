using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteMan.Core.Sharing;

/// <summary>
/// The host's side of a shared session. Listens for teammates directly (LAN, VPN) and, when a
/// relay is set, also through the relay, so teammates on other networks get in without opening
/// anything. Every link is TLS with a throwaway certificate whose fingerprint travels in the invite.
/// The host is the hub: it keeps the participant list and the control rules (<see cref="ShareControl"/>),
/// fans terminal output out to everyone and only accepts keystrokes from whoever has control.
/// </summary>
public sealed class ShareHost : IAsyncDisposable
{
    private readonly X509Certificate2 _cert = ShareNet.CreateCertificate();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Guest> _guests = new();
    private readonly HashSet<string> _invited = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _history = new();
    private const int HistoryChars = 256 * 1024;
    private TcpListener? _listener;
    private string? _giveOnJoin;
    private int _cols = 80, _rows = 24;

    public ShareHost(ShareKind kind, string entryName, string user, string machine, bool canAskForControl = true)
    {
        Kind = kind;
        EntryName = entryName;
        Control = new ShareControl(new ShareParticipant("host-" + Guid.NewGuid().ToString("N")[..8], user, machine, IsHost: true), canAskForControl);
    }

    public Guid ShareId { get; } = Guid.NewGuid();
    public ShareKind Kind { get; }
    public string EntryName { get; }
    public string Ticket { get; } = ShareWire.NewTicket();
    public string Thumbprint => ShareNet.Thumbprint(_cert);
    public ShareControl Control { get; }
    public string HostId => Control.Host.Id;
    public int Port { get; private set; }
    public IReadOnlyList<string> Addresses { get; private set; } = [];
    /// <summary>"host:port" of the relay once registered there; null without one.</summary>
    public string? Relay { get; private set; }
    public bool RelayConnected { get; private set; }

    /// <summary>RDP: what guests' viewers connect to (Windows Desktop Sharing invitation) and the local port tunnels go to.</summary>
    public string? RdpConnectionString { get; set; }
    public string? RdpPassword { get; set; }
    public int RdpPort { get; set; }
    /// <summary>RDP: other places the sharer listens (from its invitation), tried when the loopback port doesn't answer.</summary>
    public IReadOnlyList<(string Address, int Port)> RdpListeners { get; set; } = [];

    /// <summary>Participants or control changed (raised on a pool thread).</summary>
    public event Action<ShareSnapshot>? StateChanged;
    /// <summary>A guest asks for control.</summary>
    public event Action<ShareParticipant>? ControlRequested;
    /// <summary>Keystrokes from the guest who has control.</summary>
    public event Action<string>? Input;
    public event Action<ShareParticipant>? Joined;
    public event Action<ShareParticipant>? Left;

    public bool HostHasControl => Control.HasControl(HostId);

    /// <summary>Lets this user in with the ticket. <paramref name="startsWithControl"/> gives them control when they join.</summary>
    public void Invite(string user, bool startsWithControl = false)
    {
        lock (_invited) _invited.Add(user);
        if (startsWithControl) _giveOnJoin = user;
    }

    public async Task StartAsync(IEnumerable<string> relays, int preferredPort = ShareNet.DefaultPort)
    {
        _listener = Listen(preferredPort) ?? Listen(0)!;
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Addresses = ShareNet.LocalAddresses().Select(a => $"{a}:{Port}").ToList();
        _ = Task.Run(AcceptLoopAsync);

        // The relay is optional: the first one that answers within a moment is used and kept registered.
        foreach (var r in relays.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ShareNet.TryParseEndpoint(r, ShareNet.RelayPort, out var h, out var p)) continue;
            try
            {
                var tcp = await ShareNet.ConnectTcpAsync(h, p, TimeSpan.FromSeconds(2), _cts.Token);
                Relay = $"{h}:{p}";
                _ = Task.Run(() => RelayLoopAsync(h, p, tcp));
                break;
            }
            catch (Exception) { /* no relay there */ }
        }
    }

    private static TcpListener? Listen(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            return l;
        }
        catch (SocketException) { return null; }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener is { } l)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            tcp.NoDelay = true;
            _ = Task.Run(() => ServeAsync(tcp.GetStream(), tcp));
        }
    }

    /// <summary>
    /// Keeps one control connection to the relay. For each guest the relay announces ("CONN id"),
    /// opens a second connection that the relay splices to that guest; TLS then runs end to end.
    /// </summary>
    private async Task RelayLoopAsync(string host, int port, TcpClient? first)
    {
        var tcp = first;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                tcp ??= await ShareNet.ConnectTcpAsync(host, port, TimeSpan.FromSeconds(5), _cts.Token);
                using (tcp)
                {
                    var s = tcp.GetStream();
                    await ShareNet.WriteLineAsync(s, $"HOST {ShareId:N}", _cts.Token);
                    if (await ShareNet.ReadLineAsync(s, _cts.Token) != "OK") throw new IOException("Relay refused.");
                    RelayConnected = true;
                    while (!_cts.IsCancellationRequested)
                    {
                        var line = await ShareNet.ReadLineAsync(s, _cts.Token) ?? throw new IOException("Relay closed.");
                        if (line.StartsWith("CONN ", StringComparison.Ordinal))
                        {
                            var conn = line[5..].Trim();
                            _ = Task.Run(() => AcceptViaRelayAsync(host, port, conn));
                        }
                        else if (line == "PING") await ShareNet.WriteLineAsync(s, "PONG", _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
            RelayConnected = false;
            tcp = null;
            try { await Task.Delay(5000, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task AcceptViaRelayAsync(string host, int port, string conn)
    {
        try
        {
            var tcp = await ShareNet.ConnectTcpAsync(host, port, TimeSpan.FromSeconds(5), _cts.Token);
            var s = tcp.GetStream();
            await ShareNet.WriteLineAsync(s, $"ACCEPT {ShareId:N} {conn}", _cts.Token);
            if (await ShareNet.ReadLineAsync(s, _cts.Token) != "OK") { tcp.Dispose(); return; }
            await ServeAsync(s, tcp);
        }
        catch (Exception) { }
    }

    private async Task ServeAsync(Stream raw, IDisposable owner)
    {
        Guest? guest = null;
        try
        {
            var ssl = new SslStream(raw, false);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            hs.CancelAfter(TimeSpan.FromSeconds(15));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert }, hs.Token);
            var link = new ShareLink(ssl);

            var first = await ShareWire.ReadAsync(ssl, hs.Token);
            if (first is not { Type: ShareFrame.Hello } f) { await link.DisposeAsync(); return; }
            var hello = ShareWire.ReadJson<HelloMessage>(f.Payload);
            bool invited;
            lock (_invited) invited = _invited.Contains(hello.User);
            if (!ShareWire.SameTicket(hello.Ticket, Ticket) || !invited)
            {
                await link.CloseAfterAsync(ShareWire.Frame(ShareFrame.Ended, "You're not invited to this session (or the invite expired)."));
                return;
            }

            var p = new ShareParticipant(Guid.NewGuid().ToString("N")[..10], hello.User, hello.Machine, false, hello.ReadOnly);
            guest = new Guest(p, link);
            Control.Join(p);
            _guests[p.Id] = guest;
            if (_giveOnJoin is { } g && string.Equals(g, p.User, StringComparison.OrdinalIgnoreCase) && !p.ReadOnly)
            {
                _giveOnJoin = null;
                Control.TryGive(HostId, p.Id);
            }

            string history;
            lock (_history) history = _history.ToString();
            link.Send(ShareWire.FrameJson(ShareFrame.Welcome, new WelcomeMessage(p.Id, Kind, EntryName, _cols, _rows, history,
                RdpConnectionString, RdpPassword, Control.Snapshot())));
            Joined?.Invoke(p);
            PublishState();

            while (!_cts.IsCancellationRequested)
            {
                var frame = await link.ReadAsync();
                if (frame is not { } fr || fr.Type == ShareFrame.Leave) break;
                Handle(guest, fr.Type, fr.Payload);
            }
        }
        catch (Exception) { }
        finally
        {
            if (guest is not null)
            {
                _guests.TryRemove(guest.P.Id, out _);
                foreach (var t in guest.Tunnels.Values) t.Dispose();
                await guest.Link.DisposeAsync();
                Control.Leave(guest.P.Id);
                Left?.Invoke(guest.P);
                PublishState();
            }
            owner.Dispose();
        }
    }

    private void Handle(Guest g, ShareFrame type, byte[] payload)
    {
        switch (type)
        {
            case ShareFrame.Input:
                if (Control.HasControl(g.P.Id)) Input?.Invoke(ShareWire.Text(payload));
                break;
            case ShareFrame.RequestControl:
                if (Control.MayAsk(g.P.Id))
                {
                    ControlRequested?.Invoke(g.P);
                    // A guest who has control hears about it too, so they can pass it on.
                    if (_guests.TryGetValue(Control.ControllerId, out var ctl)) ctl.Link.Send(ShareWire.Frame(ShareFrame.RequestControl, g.P.Id));
                }
                break;
            case ShareFrame.GiveControl:
                if (Control.TryGive(g.P.Id, ShareWire.Text(payload))) PublishState();
                break;
            case ShareFrame.TunnelOpen:
                OpenTunnel(g, ShareWire.ReadTunnel(payload).Id);
                break;
            case ShareFrame.TunnelData:
                var (id, data) = ShareWire.ReadTunnel(payload);
                if (g.Tunnels.TryGetValue(id, out var t)) t.Write(data);
                break;
            case ShareFrame.TunnelClose:
                if (g.Tunnels.TryRemove(ShareWire.ReadTunnel(payload).Id, out var closed)) closed.Dispose();
                break;
        }
    }

    // ---------------- RDP tunnel ----------------

    /// <summary>Connects a guest's tunnel to the local Windows Desktop Sharing port and pumps bytes back.</summary>
    private void OpenTunnel(Guest g, int id)
    {
        if (RdpPort <= 0) { g.Link.Send(ShareWire.TunnelFrame(ShareFrame.TunnelClose, id)); return; }
        var t = new Tunnel();
        g.Tunnels[id] = t;
        _ = Task.Run(async () =>
        {
            try
            {
                t.Tcp = await ConnectToSharerAsync();
                t.Ready.TrySetResult();
                var s = t.Tcp.GetStream();
                var buf = new byte[64 * 1024];
                while (true)
                {
                    var n = await s.ReadAsync(buf, _cts.Token);
                    if (n == 0) break;
                    g.Link.Send(ShareWire.TunnelFrame(ShareFrame.TunnelData, id, buf.AsSpan(0, n)));
                }
            }
            catch (Exception) { t.Ready.TrySetResult(); }
            if (g.Tunnels.TryRemove(id, out _)) g.Link.Send(ShareWire.TunnelFrame(ShareFrame.TunnelClose, id));
            t.Dispose();
        });
    }

    /// <summary>The sharer's port on loopback first, then the addresses its invitation lists (some Windows builds bind only those).</summary>
    private async Task<TcpClient> ConnectToSharerAsync()
    {
        var targets = new List<(string Address, int Port)> { ("127.0.0.1", RdpPort) };
        foreach (var l in RdpListeners)
        {
            if (l.Port > 0 && !targets.Contains(l)) targets.Add(l);
            if (l.Port > 0 && l.Port != RdpPort && !targets.Contains(("127.0.0.1", l.Port))) targets.Insert(1, ("127.0.0.1", l.Port));
        }
        Exception? last = null;
        foreach (var (address, port) in targets)
        {
            if (!IPAddress.TryParse(address, out var ip) || port <= 0) continue;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(3));
            var tcp = new TcpClient(ip.AddressFamily) { NoDelay = true };
            try { await tcp.ConnectAsync(ip, port, attempt.Token); return tcp; }
            catch (Exception ex) when (!_cts.IsCancellationRequested) { last = ex; tcp.Dispose(); }
        }
        throw last ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    private sealed class Tunnel : IDisposable
    {
        public TcpClient? Tcp { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _chain = Task.CompletedTask;
        private readonly object _gate = new();

        /// <summary>Writes in order, after the connection is up.</summary>
        public void Write(ReadOnlyMemory<byte> data)
        {
            var copy = data.ToArray();
            lock (_gate)
                _chain = _chain.ContinueWith(async _ =>
                {
                    await Ready.Task;
                    if (Tcp is { Connected: true } tcp) await tcp.GetStream().WriteAsync(copy);
                }, TaskScheduler.Default).Unwrap();
        }

        public void Dispose() { try { Tcp?.Dispose(); } catch (Exception) { } }
    }

    // ---------------- what the host does ----------------

    /// <summary>Terminal output from the host's session, to everyone.</summary>
    public void SendOutput(string text)
    {
        if (text.Length == 0) return;
        lock (_history)
        {
            _history.Append(text);
            if (_history.Length > HistoryChars) _history.Remove(0, _history.Length - HistoryChars);
        }
        Broadcast(ShareWire.Frame(ShareFrame.Output, text));
    }

    public void SetSize(int cols, int rows)
    {
        if (cols == _cols && rows == _rows) return;
        _cols = cols; _rows = rows;
        Broadcast(ShareWire.FrameJson(ShareFrame.Size, new SizeMessage(cols, rows)));
    }

    public void Give(string participantId) { if (Control.TryGive(HostId, participantId)) PublishState(); }

    public void TakeBack() { if (Control.TryTakeBack(HostId)) PublishState(); }

    /// <summary>The host switched away from the shared tab (RDP: the picture pauses).</summary>
    public void SetPaused(bool paused)
    {
        if (Control.Paused == paused) return;
        Control.Paused = paused;
        PublishState();
    }

    public void SetCanAsk(bool canAsk) { Control.CanAskForControl = canAsk; PublishState(); }

    public void PublishState()
    {
        var snap = Control.Snapshot();
        Broadcast(ShareWire.FrameJson(ShareFrame.State, snap));
        StateChanged?.Invoke(snap);
    }

    private void Broadcast(byte[] frame)
    {
        foreach (var g in _guests.Values) g.Link.Send(frame);
    }

    /// <summary>Ends the session for everyone.</summary>
    public async Task StopAsync(string reason = "The host stopped sharing.")
    {
        if (_cts.IsCancellationRequested) return;
        var bye = ShareWire.Frame(ShareFrame.Ended, reason);
        await Task.WhenAll(_guests.Values.Select(g => g.Link.CloseAfterAsync(bye)));
        _cts.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cert.Dispose();
    }

    private sealed class Guest(ShareParticipant p, ShareLink link)
    {
        public ShareParticipant P { get; } = p;
        public ShareLink Link { get; } = link;
        public ConcurrentDictionary<int, Tunnel> Tunnels { get; } = new();
    }
}
