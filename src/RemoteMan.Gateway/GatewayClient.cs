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
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
            ws.Options.SetRequestHeader(GatewayProtocol.VersionHeader, GatewayProtocol.Version.ToString());
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await RelayRefusedException.ConnectAsync(ws, GatewayProtocol.WebSocketUri(Relay, $"{GatewayProtocol.ConnectPath}?site={Uri.EscapeDataString(site)}"), cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException && !ct.IsCancellationRequested)
            {
                throw new GatewayException($"Can't reach the gateway relay {Relay.Host}: {ex.GetBaseException().Message}");
            }

            // A connector that was just replaced has a key the cached list doesn't have yet: refresh once.
            byte[]? seen = null;
            SecureChannel channel;
            try { (channel, _) = await SecureChannel.ClientAsync(ws, k => keys.Contains(Convert.ToBase64String(seen = k)), cts.Token); }
            catch (GatewayException) when (!retried && seen is not null && !keys.Contains(Convert.ToBase64String(seen)))
            {
                ws.Dispose();
                if (!(await KeysAsync(site, true, ct)).Contains(Convert.ToBase64String(seen))) throw;
                return await OpenAsync(site, targets, ct, retried: true);
            }

            await channel.SendAsync(GatewayProtocol.Encode(new OpenRequest(targets.ToList())), cts.Token);
            var reply = await channel.ReceiveAsync(cts.Token) is { } raw ? GatewayProtocol.Decode<OpenReply>(raw) : null;
            if (reply is null) throw new GatewayException($"The connector for {site} closed the tunnel.");
            if (!reply.Ok) throw new GatewayException($"Through {site}: {reply.Error}");
            return new GatewayTunnel(ws, channel, reply.Target ?? targets[0], site);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new GatewayException($"The gateway didn't open a tunnel through {site} in time.");
        }
        catch
        {
            ws.Dispose();
            throw;
        }
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

    public void Dispose() => _http.Dispose();
}

/// <summary>One open tunnel: the encrypted channel and which target the connector reached.</summary>
public sealed class GatewayTunnel(WebSocket ws, SecureChannel channel, string target, string site) : IDisposable
{
    public SecureChannel Channel { get; } = channel;
    public string Target { get; } = target;
    public string Site { get; } = site;

    public async Task PumpAsync(Stream local, CancellationToken ct)
    {
        using (GatewayActivity.BeginTunnel(Site)) await Pump.RunAsync(local, Channel, ct, count: true);
    }

    public void Dispose() => ws.Dispose();
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
    }

    public string Site { get; }
    public IReadOnlyList<string> Targets { get; }
    public bool DirectFirst { get; }
    public int Port { get; }
    public TimeSpan DirectTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
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

    private async Task ServeAsync(TcpClient local)
    {
        using (local)
        {
            var ct = _cts.Token;
            try
            {
                var manual = Override?.Invoke();
                if (manual == false)
                {
                    Interlocked.Exchange(ref _readyBox, null)?.Tunnel.Dispose();
                    if (await DialDirectAsync(ct, TimeSpan.FromSeconds(10)) is not var (dtcp, dtarget))
                        throw new GatewayException($"None of {string.Join(", ", Targets)} answered directly (the gateway is switched off).");
                    LastPath = dtarget;
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
                        Used?.Invoke(false);
                        await ready.PumpAsync(local.GetStream(), ct);
                    }
                    return;
                }
                var tryDirect = manual != true && DirectFirst && (_path is not { } p || p.Direct || DateTime.UtcNow - p.At > TimeSpan.FromMinutes(1));
                if (tryDirect)
                {
                    if (await DialDirectAsync(ct) is var (tcp, target))
                    {
                        _path = (DateTime.UtcNow, true);
                        LastPath = target;
                        Used?.Invoke(true);
                        using (tcp) await BridgeAsync(local.GetStream(), tcp.GetStream(), ct);
                        return;
                    }
                }
                using var tunnel = await _client.OpenAsync(Site, Targets, ct);
                if (DirectFirst) _path = (DateTime.UtcNow, false);
                LastPath = $"{tunnel.Target} via {Site}";
                Used?.Invoke(false);
                await tunnel.PumpAsync(local.GetStream(), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Failed?.Invoke(ex is GatewayException or RelayRefusedException ? ex.Message : ex.GetBaseException().Message);
            }
            catch { }
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
