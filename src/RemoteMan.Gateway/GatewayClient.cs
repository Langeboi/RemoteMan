using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace RemoteMan.Gateway;

/// <summary>RemoteMan's side of the gateway: lists the sites and opens end-to-end encrypted tunnels through the relay.</summary>
public sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly SemaphoreSlim _sitesLock = new(1, 1);
    private (DateTime At, List<SiteInfo> Sites)? _sites;

    public GatewayClient(Uri relay, string token, HttpMessageHandler? handler = null)
    {
        Relay = relay;
        _token = token;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public Uri Relay { get; }

    /// <summary>The sites this device may use, with whether a connector is online. Cached for 30 seconds.</summary>
    public async Task<List<SiteInfo>> GetSitesAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _sitesLock.WaitAsync(ct);
        try
        {
            if (!refresh && _sites is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(30)) return c.Sites;
            using var req = new HttpRequestMessage(HttpMethod.Get, GatewayProtocol.HttpUri(Relay, GatewayProtocol.SitesPath));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new RelayRefusedException(res.StatusCode, "The relay doesn't accept this device's token. It may have been removed; ask for a new invite.");
            if (!res.IsSuccessStatusCode) throw new RelayRefusedException(res.StatusCode, $"The relay answered {(int)res.StatusCode} {res.ReasonPhrase}.");
            var sites = JsonSerializer.Deserialize<List<SiteInfo>>(await res.Content.ReadAsStringAsync(ct), GatewayProtocol.Json) ?? [];
            _sites = (DateTime.UtcNow, sites);
            return sites;
        }
        finally { _sitesLock.Release(); }
    }

    /// <summary>
    /// Opens a tunnel to the first of <paramref name="targets"/> (host:port) the site's connector can reach.
    /// Throws <see cref="GatewayException"/> or <see cref="RelayRefusedException"/> with a message for people.
    /// </summary>
    public async Task<GatewayTunnel> OpenAsync(string site, IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        using (GatewayActivity.BeginOpening(site)) return await OpenAsync(site, targets, ct, false);
    }

    /// <summary>Round trip to the relay (listing the sites), for the status box. Also refreshes the site list.</summary>
    public async Task<(TimeSpan Rtt, List<SiteInfo> Sites)> PingAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sites = await GetSitesAsync(refresh: true, ct);
        return (sw.Elapsed, sites);
    }

    private async Task<GatewayTunnel> OpenAsync(string site, IReadOnlyList<string> targets, CancellationToken ct, bool retried)
    {
        if (targets.Count == 0) throw new ArgumentException("No target", nameof(targets));
        var keys = await KeysAsync(site, false, ct);
        IMessagePipe? pipe = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            // A stream on the shared connection when the relay has it (one round trip), else a WebSocket of its own.
            pipe = await MuxAsync(cts.Token) is { } mux ? mux.Open(site) : await ConnectLegacyAsync(site, ct, cts.Token);

            // A connector that was just replaced has a key the cached list doesn't have yet: refresh once.
            byte[]? seen = null;
            SecureChannel channel;
            try { (channel, _) = await SecureChannel.ClientAsync(pipe, k => keys.Contains(Convert.ToBase64String(seen = k)), cts.Token); }
            catch (GatewayException) when (!retried && seen is not null && !keys.Contains(Convert.ToBase64String(seen)))
            {
                pipe.Dispose();
                if (!(await KeysAsync(site, true, ct)).Contains(Convert.ToBase64String(seen))) throw;
                return await OpenAsync(site, targets, ct, retried: true);
            }

            await channel.SendAsync(GatewayProtocol.Encode(new OpenRequest(targets.ToList())), cts.Token);
            var reply = await channel.ReceiveAsync(cts.Token) is { } raw ? GatewayProtocol.Decode<OpenReply>(raw) : null;
            if (reply is null) throw new GatewayException($"The connector for {site} closed the tunnel.");
            if (!reply.Ok) throw new GatewayException($"Through {site}: {reply.Error}");
            return new GatewayTunnel(pipe, channel, reply.Target ?? targets[0], site);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            pipe?.Dispose();
            throw new GatewayException($"The gateway didn't open a tunnel through {site} in time.");
        }
        catch
        {
            pipe?.Dispose();
            throw;
        }
    }

    private async Task<IMessagePipe> ConnectLegacyAsync(string site, CancellationToken ct, CancellationToken timeout)
    {
        var ws = NewSocket();
        try
        {
            await RelayRefusedException.ConnectAsync(ws, GatewayProtocol.WebSocketUri(Relay, $"{GatewayProtocol.ConnectPath}?site={Uri.EscapeDataString(site)}"), timeout);
            return new WebSocketPipe(ws);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException && !ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new GatewayException($"Can't reach the gateway relay {Relay.Host}: {ex.GetBaseException().Message}");
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private ClientWebSocket NewSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        ws.Options.SetRequestHeader(GatewayProtocol.VersionHeader, GatewayProtocol.Version.ToString());
        ws.Options.KeepAliveInterval = GatewayProtocol.KeepAliveInterval;
        ws.Options.KeepAliveTimeout = GatewayProtocol.KeepAliveTimeout;
        return ws;
    }

    // ── The shared connection ─────────────────────────────────────────────────────────────────────────────────
    private readonly object _muxLock = new();
    private readonly CancellationTokenSource _disposed = new();
    private MuxEndpoint? _mux;
    private Task<MuxEndpoint?>? _muxConnecting;
    private DateTime _noMuxUntil;

    /// <summary>
    /// Carry tunnels as streams on one WebSocket to the relay (opened on first use, reopened when it drops). Off,
    /// or with a relay that predates it (checked again every 5 minutes), each tunnel opens a WebSocket of its own.
    /// </summary>
    public bool Multiplex { get; init; } = true;

    /// <summary>
    /// Open tunnels were cut because a connector or the connection to the relay went away (not closed by either
    /// end): sessions should reconnect at once, and will land on another connector.
    /// </summary>
    public event Action? TunnelsLost;

    /// <summary>Whether tunnels go over the shared connection right now (for the status box).</summary>
    public bool Multiplexed => _mux is { IsOpen: true };

    private async Task<MuxEndpoint?> MuxAsync(CancellationToken ct)
    {
        if (!Multiplex || DateTime.UtcNow < _noMuxUntil) return null;
        Task<MuxEndpoint?> t;
        lock (_muxLock)
        {
            if (_mux is { IsOpen: true } m) return m;
            if (_muxConnecting is not { IsCompleted: false }) _muxConnecting = ConnectMuxAsync();
            t = _muxConnecting;
        }
        return await t.WaitAsync(ct);
    }

    private async Task<MuxEndpoint?> ConnectMuxAsync()
    {
        var ws = NewSocket();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            try { await RelayRefusedException.ConnectAsync(ws, GatewayProtocol.WebSocketUri(Relay, GatewayProtocol.MuxPath), cts.Token); }
            catch (RelayRefusedException ex) when (ex.Status != HttpStatusCode.Unauthorized)
            {
                // An older relay (404) or a proxy that won't pass it: one WebSocket per tunnel, as before.
                ws.Dispose();
                _noMuxUntil = DateTime.UtcNow.AddMinutes(5);
                return null;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException && !_disposed.IsCancellationRequested)
            {
                ws.Dispose();
                throw new GatewayException($"Can't reach the gateway relay {Relay.Host}: {ex.GetBaseException().Message}");
            }
            var mux = new MuxEndpoint(ws, isWebSocketClient: true);
            mux.Lost += () => TunnelsLost?.Invoke();
            lock (_muxLock) _mux = mux;
            _ = mux.RunAsync(_disposed.Token).ContinueWith(_ =>
            {
                ws.Dispose();
                lock (_muxLock) if (_mux == mux) _mux = null;
            }, TaskScheduler.Default);
            return mux;
        }
        catch when (ws.State != WebSocketState.Open) { ws.Dispose(); throw; }
    }

    /// <summary>Which target the site's connector reaches first, without keeping the tunnel.</summary>
    public async Task<string> ProbeAsync(string site, IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        using var t = await OpenAsync(site, targets, ct);
        await t.Channel.CloseAsync();
        return t.Target;
    }

    private async Task<HashSet<string>> KeysAsync(string site, bool refresh, CancellationToken ct)
    {
        var sites = await GetSitesAsync(refresh, ct);
        var s = sites.FirstOrDefault(x => string.Equals(x.Name, site, StringComparison.OrdinalIgnoreCase))
                ?? throw new GatewayException($"The gateway has no site called {site} for this device.");
        if (!s.Online) throw new GatewayException($"The connector for {site} isn't online.");
        return s.Keys.ToHashSet();
    }

    public void Dispose()
    {
        _disposed.Cancel();
        lock (_muxLock) _mux?.Dispose();
        _http.Dispose();
    }
}

/// <summary>One open tunnel: the encrypted channel and which target the connector reached.</summary>
public sealed class GatewayTunnel(IMessagePipe pipe, SecureChannel channel, string target, string site) : IDisposable
{
    public SecureChannel Channel { get; } = channel;
    public string Target { get; } = target;
    public string Site { get; } = site;

    public async Task PumpAsync(Stream local, CancellationToken ct)
    {
        using (GatewayActivity.BeginTunnel(Site)) await Pump.RunAsync(local, Channel, ct, count: true);
    }

    public void Dispose() => pipe.Dispose();
}

/// <summary>
/// 127.0.0.1:(random) on this PC that reaches a host through the gateway. Programs that can't speak the gateway
/// (the RDP control, SSH.NET, Npgsql, WebView2) connect here. With <see cref="DirectFirst"/>, each new connection
/// first tries the targets directly (on the same network as them, or on a VPN) and only uses the gateway when
/// none answers; the choice is remembered for a minute so an off-site laptop doesn't wait on every connection.
/// </summary>
public sealed class LocalForwarder : IDisposable
{
    private readonly GatewayClient _client;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private (DateTime At, bool Direct)? _path;

    public LocalForwarder(GatewayClient client, string site, IReadOnlyList<string> targets, bool directFirst = false, int port = 0)
    {
        _client = client;
        Site = site;
        Targets = targets;
        DirectFirst = directFirst;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
        if (directFirst) _ = Task.Run(WatchForSiteAsync);
    }

    public string Site { get; }
    public IReadOnlyList<string> Targets { get; }
    public bool DirectFirst { get; }
    public int Port { get; }
    public TimeSpan DirectTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>
    /// After the direct try failed, how long new connections go straight to the gateway (a database opens a
    /// connection now and then, so a short memory made every one of them wait out the direct try first).
    /// </summary>
    public TimeSpan OffSiteMemory { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>
    /// While connections go through the gateway, how often the direct path is tried in the background, so coming
    /// back on site is noticed within seconds instead of after <see cref="OffSiteMemory"/> (it must answer twice
    /// in a row).
    /// </summary>
    public TimeSpan ProbeEvery { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// The direct path answers again after connections went through the gateway (this PC is back on site): new
    /// connections go directly. Open gateway connections are left alone; <see cref="Drop"/> moves them.
    /// </summary>
    public event Action? BackOnSite;
    /// <summary>The target last reached, and whether it was direct ("10.0.0.5:5432 via lab").</summary>
    public string? LastPath { get; private set; }
    /// <summary>A connection that couldn't be forwarded (the local program sees it close).</summary>
    public event Action<string>? Failed;
    /// <summary>
    /// A manual choice that beats <see cref="DirectFirst"/>: true = always through the gateway, false = directly
    /// only (the gateway is "disconnected"), null = automatic.
    /// </summary>
    public Func<bool?>? Override { get; set; }

    /// <summary>A connection went through: true when directly, false through the gateway.</summary>
    public event Action<bool>? Used;

    /// <summary>
    /// A tunnel that's already open (from checking that the site reaches the host): the next connection uses it
    /// instead of opening another, which saves a full round of relay and connector setup on the first connect.
    /// </summary>
    public void Offer(GatewayTunnel tunnel)
    {
        var box = new ReadyBox(tunnel, DateTime.UtcNow);
        Interlocked.Exchange(ref _readyBox, box)?.Tunnel.Dispose();
        // Unused (the session never connected): close it so the connector lets go of the host.
        _ = Task.Delay(ReadyFor).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref _readyBox, null, box) == box) box.Tunnel.Dispose();
        }, TaskScheduler.Default);
    }

    /// <summary>How long an offered tunnel waits for the first connection.</summary>
    public static TimeSpan ReadyFor { get; } = TimeSpan.FromSeconds(20);

    private sealed record ReadyBox(GatewayTunnel Tunnel, DateTime At);
    private ReadyBox? _readyBox;

    private GatewayTunnel? TakeReady()
    {
        var box = Interlocked.Exchange(ref _readyBox, null);
        if (box is null) return null;
        if (DateTime.UtcNow - box.At < ReadyFor) return box.Tunnel;
        box.Tunnel.Dispose();
        return null;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient local;
            try { local = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            local.NoDelay = true;
            _ = Task.Run(() => ServeAsync(local));
        }
    }

    private sealed class Live(CancellationTokenSource cts, TcpClient local)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public TcpClient Local { get; } = local;
        public bool? Direct { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Live, byte> _live = new();

    /// <summary>
    /// Closes the open connections that went directly (<paramref name="direct"/> true) or through the gateway
    /// (false), e.g. after the switch was flipped: the program reconnects and its new connection takes the new
    /// path. Returns how many were closed.
    /// </summary>
    public int Drop(bool direct)
    {
        var n = 0;
        foreach (var l in _live.Keys)
        {
            if (l.Direct != direct) continue;
            try { l.Cts.Cancel(); } catch { }
            try { l.Local.Dispose(); } catch { }
            n++;
        }
        return n;
    }

    private async Task ServeAsync(TcpClient local)
    {
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var live = new Live(connCts, local);
        _live[live] = 0;
        using (local)
        {
            var ct = connCts.Token;
            try
            {
                var manual = Override?.Invoke();
                if (manual == false)
                {
                    Interlocked.Exchange(ref _readyBox, null)?.Tunnel.Dispose();
                    if (await DialDirectAsync(ct, TimeSpan.FromSeconds(10)) is not var (dtcp, dtarget))
                        throw new GatewayException($"None of {string.Join(", ", Targets)} answered directly (the gateway is switched off).");
                    LastPath = dtarget;
                    live.Direct = true;
                    Used?.Invoke(true);
                    using (dtcp) await BridgeAsync(local.GetStream(), dtcp.GetStream(), ct);
                    return;
                }
                // A tunnel opened just before (the entry's check) goes to the first connection.
                if (TakeReady() is { } ready)
                {
                    using (ready)
                    {
                        LastPath = $"{ready.Target} via {Site}";
                        live.Direct = false;
                        Used?.Invoke(false);
                        await ready.PumpAsync(local.GetStream(), ct);
                    }
                    return;
                }
                var tryDirect = manual != true && DirectFirst && (_path is not { } p || p.Direct || DateTime.UtcNow - p.At > OffSiteMemory);
                if (tryDirect)
                {
                    if (await DialDirectAsync(ct) is var (tcp, target))
                    {
                        _path = (DateTime.UtcNow, true);
                        LastPath = target;
                        live.Direct = true;
                        Used?.Invoke(true);
                        using (tcp) await BridgeAsync(local.GetStream(), tcp.GetStream(), ct);
                        return;
                    }
                }
                using var tunnel = await _client.OpenAsync(Site, Targets, ct);
                if (DirectFirst) _path = (DateTime.UtcNow, false);
                LastPath = $"{tunnel.Target} via {Site}";
                live.Direct = false;
                Used?.Invoke(false);
                await tunnel.PumpAsync(local.GetStream(), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Failed?.Invoke(ex is GatewayException or RelayRefusedException ? ex.Message : ex.GetBaseException().Message);
            }
            catch { }
            finally { _live.TryRemove(live, out _); }
        }
    }

    /// <summary>The network changed: the next connection tries the direct path again (when automatic).</summary>
    public void ForgetPath() => _path = null;

    private async Task WatchForSiteAsync()
    {
        var ct = _cts.Token;
        var answered = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(ProbeEvery, ct); } catch (OperationCanceledException) { return; }
            if (_path is not { Direct: false } || Override?.Invoke() is not null) { answered = 0; continue; }
            try
            {
                if (await DialDirectAsync(ct) is not var (tcp, _)) { answered = 0; continue; }
                tcp.Dispose();
                // Twice in a row before switching back, so a flaky link at the edge of the site's Wi-Fi doesn't
                // move the database back and forth.
                if (++answered < 2) continue;
                answered = 0;
                _path = (DateTime.UtcNow, true);
                BackOnSite?.Invoke();
            }
            catch { answered = 0; }
        }
    }

    private async Task<(TcpClient, string)?> DialDirectAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        foreach (var t in Targets)
        {
            if (GatewayProtocol.ParseTarget(t) is not var (host, port)) continue;
            var tcp = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? DirectTimeout);
            try
            {
                await tcp.ConnectAsync(host, port, cts.Token);
                return (tcp, t);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { tcp.Dispose(); }
        }
        return null;
    }

    private static async Task BridgeAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        static async Task OneWay(NetworkStream from, NetworkStream to, CancellationToken ct)
        {
            try { await from.CopyToAsync(to, ct); } catch { }
            try { to.Socket.Shutdown(SocketShutdown.Send); } catch { }
        }
        await Task.WhenAll(OneWay(a, b, ct), OneWay(b, a, ct));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        Interlocked.Exchange(ref _readyBox, null)?.Tunnel.Dispose();
    }
}
