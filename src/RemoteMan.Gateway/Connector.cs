using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteMan.Gateway;

public sealed class ConnectorOptions
{
    /// <summary>The relay, e.g. https://remoteman.example.com</summary>
    public required Uri Relay { get; init; }
    /// <summary>The site token from <c>gateway site add</c> on the relay.</summary>
    public required string SiteToken { get; init; }
    /// <summary>This connector's static key; its public half is what RemoteMan checks.</summary>
    public required ECDsa Key { get; init; }
    /// <summary>Shown on the relay (several connectors can serve one site).</summary>
    public string Name { get; init; } = Environment.MachineName;
    public AllowList Allow { get; init; } = new(null);
    /// <summary>Sent to the relay so its admin console can show it (the image's version).</summary>
    public string? Version { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public Action<string> Log { get; init; } = Console.WriteLine;
}

/// <summary>
/// Runs inside the remote network. It only ever connects out (to the relay, on 443), so it works behind CG-NAT
/// and needs no port forwarding: it keeps one control WebSocket open, and for each tunnel the relay asks for it
/// opens a second WebSocket, does the end-to-end handshake and connects to the target on the local network.
/// </summary>
public sealed class Connector
{
    private readonly ConnectorOptions _o;
    private readonly string _keyHeader;
    private int _active;
    private AllowList? _siteAllow;

    public Connector(ConnectorOptions options)
    {
        _o = options;
        _keyHeader = Convert.ToBase64String(options.Key.ExportSubjectPublicKeyInfo());
    }

    public string Fingerprint => SecureChannel.Fingerprint(_o.Key.ExportSubjectPublicKeyInfo());
    public int ActiveTunnels => Volatile.Read(ref _active);
    public bool Connected { get; private set; }
    /// <summary>The site's resources as the relay's admin console set them (null: none, only <see cref="ConnectorOptions.Allow"/> counts).</summary>
    public AllowList? SiteAllow => Volatile.Read(ref _siteAllow);

    /// <summary>Fires each time the control connection to the relay is up (for tests and the health check).</summary>
    public event Action? Online;

    public static ECDsa LoadOrCreateKey(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return key;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var ws = NewSocket();
                ws.Options.SetRequestHeader(GatewayProtocol.KeyHeader, _keyHeader);
                ws.Options.SetRequestHeader(GatewayProtocol.NameHeader, _o.Name);
                ws.Options.SetRequestHeader(GatewayProtocol.AllowHeader, _o.Allow.ToString());
                if (_o.Version is { Length: > 0 and <= 64 } v) ws.Options.SetRequestHeader(GatewayProtocol.ConnectorVersionHeader, v);
                await RelayRefusedException.ConnectAsync(ws, GatewayProtocol.WebSocketUri(_o.Relay, GatewayProtocol.ConnectorPath), ct);
                Connected = true;
                _o.Log($"Connected to the relay {_o.Relay.Host} as {_o.Name} (key {Fingerprint})");
                Online?.Invoke();
                delay = TimeSpan.FromSeconds(1);
                await ControlLoopAsync(ws, ct);
                _o.Log("The relay closed the connection; reconnecting");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (RelayRefusedException ex) when (ex.Status is HttpStatusCode.Unauthorized)
            {
                _o.Log("The relay doesn't know this site token. Check SITE_TOKEN (gateway site list on the relay).");
                delay = TimeSpan.FromSeconds(60);
            }
            catch (RelayRefusedException ex) when (ex.Status is HttpStatusCode.Conflict)
            {
                _o.Log("The relay has a different key on file for this site. If you replaced the connector on purpose, run `gateway site reset-key <site>` on the relay.");
                delay = TimeSpan.FromSeconds(60);
            }
            catch (Exception ex)
            {
                _o.Log($"Can't reach the relay {_o.Relay}: {ex.GetBaseException().Message}");
            }
            finally { Connected = false; }
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) delay = TimeSpan.FromSeconds(1);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            // Back off to at most 5 s while the relay is unreachable (it was 30 s): a site comes back within
            // seconds of the relay or the line recovering. Refusals above wait a minute; retrying won't fix those.
            if (delay < TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(Math.Min(5, delay.TotalSeconds * 2));
        }
    }

    private ClientWebSocket NewSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + _o.SiteToken);
        ws.Options.SetRequestHeader(GatewayProtocol.VersionHeader, GatewayProtocol.Version.ToString());
        // CG-NAT and home routers forget idle mappings quickly; regular pings keep the path open and notice a
        // dead relay within the timeout.
        ws.Options.KeepAliveInterval = GatewayProtocol.KeepAliveInterval;
        ws.Options.KeepAliveTimeout = GatewayProtocol.KeepAliveTimeout;
        ws.Options.CollectHttpResponseDetails = true;
        return ws;
    }

    private async Task ControlLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        var msg = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, ct); }
            catch (WebSocketException) { return; }
            if (r.MessageType == WebSocketMessageType.Close) return;
            if (msg.Length + r.Count <= 64 * 1024) msg.Write(buf, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var line = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
            msg.SetLength(0);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            if (line.StartsWith("open ", StringComparison.Ordinal))
            {
                var id = line[5..].Trim();
                _ = Task.Run(() => ServeTunnelAsync(id, ct), ct);
            }
            else if (line.StartsWith("allow", StringComparison.Ordinal))
            {
                var spec = line[5..].Trim();
                try
                {
                    var next = spec.Length == 0 ? null : new AllowList(spec);
                    var before = Volatile.Read(ref _siteAllow)?.ToString();
                    Volatile.Write(ref _siteAllow, next);
                    if (before != next?.ToString())
                        _o.Log(next is null ? "The site has no resources set on the relay; ALLOW alone decides" : $"Site resources from the relay: {next} (also limited by ALLOW={_o.Allow})");
                }
                catch (FormatException ex) { _o.Log($"Ignoring the site's resources from the relay: {ex.Message}"); }
            }
        }
    }

    private async Task ServeTunnelAsync(string id, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        try
        {
            using var ws = NewSocket();
            await RelayRefusedException.ConnectAsync(ws, GatewayProtocol.WebSocketUri(_o.Relay, GatewayProtocol.AcceptPath + Uri.EscapeDataString(id)), ct);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(15));
            var channel = await SecureChannel.ConnectorAsync(ws, _o.Key, hs.Token);
            var req = await channel.ReceiveAsync(hs.Token) is { } raw ? GatewayProtocol.Decode<OpenRequest>(raw) : null;
            if (req?.Targets is not { Count: > 0 }) { await channel.CloseAsync(); return; }

            var (tcp, target, errors) = await DialAsync(req.Targets, ct);
            if (tcp is null)
            {
                await channel.SendAsync(GatewayProtocol.Encode(new OpenReply(false, null, string.Join("; ", errors))), ct);
                await channel.CloseAsync();
                _o.Log($"Tunnel {id}: {string.Join("; ", errors)}");
                return;
            }
            using (tcp)
            {
                await channel.SendAsync(GatewayProtocol.Encode(new OpenReply(true, target, null)), ct);
                _o.Log($"Tunnel {id} → {target}");
                await Pump.RunAsync(tcp.GetStream(), channel, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _o.Log($"Tunnel {id}: {ex.GetBaseException().Message}");
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    /// <summary>Tries each target in order, each of its addresses that the allow list permits.</summary>
    private async Task<(TcpClient? Tcp, string? Target, List<string> Errors)> DialAsync(List<string> targets, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var t in targets.Take(16))
        {
            if (GatewayProtocol.ParseTarget(t) is not var (host, port)) { errors.Add($"{t}: not host:port"); continue; }
            IPAddress[] addresses;
            try
            {
                addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException) { errors.Add($"{t}: no such host on the connector's network"); continue; }
            var allowed = addresses.Where(a => _o.Allow.Allows(a, port)).ToList();
            if (allowed.Count == 0) { errors.Add($"{t}: not allowed by the connector (ALLOW={_o.Allow})"); continue; }
            var site = SiteAllow;
            if (site is not null) allowed = allowed.Where(a => site.Allows(a, port)).ToList();
            if (allowed.Count == 0) { errors.Add($"{t}: not in the site's resources on the relay ({site})"); continue; }
            foreach (var a in allowed)
            {
                var tcp = new TcpClient(a.AddressFamily) { NoDelay = true };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_o.ConnectTimeout);
                try
                {
                    await tcp.ConnectAsync(a, port, cts.Token);
                    return (tcp, t, errors);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { errors.Add($"{t}: timed out"); }
                catch (SocketException ex) { errors.Add($"{t}: {ex.SocketErrorCode}"); }
                tcp.Dispose();
            }
        }
        return (null, null, errors);
    }
}

/// <summary>The relay answered the WebSocket request with an HTTP error instead of upgrading.</summary>
public sealed class RelayRefusedException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public static async Task ConnectAsync(ClientWebSocket ws, Uri uri, CancellationToken ct)
    {
        ws.Options.CollectHttpResponseDetails = true;
        try { await ws.ConnectAsync(uri, ct); }
        catch (WebSocketException) when (ws.HttpStatusCode is not 0 and not HttpStatusCode.SwitchingProtocols)
        {
            var status = ws.HttpStatusCode;
            throw new RelayRefusedException(status, status switch
            {
                HttpStatusCode.Unauthorized => "The relay doesn't accept this token. It may have been removed; ask for a new invite.",
                HttpStatusCode.Forbidden => "This device isn't allowed to use that site.",
                HttpStatusCode.NotFound => "The relay doesn't know that site.",
                HttpStatusCode.ServiceUnavailable => "The site's connector isn't online.",
                HttpStatusCode.Conflict => "The relay has a different key on file for this site.",
                HttpStatusCode.GatewayTimeout => "The site's connector didn't answer in time.",
                _ => $"The relay answered {(int)status} {status}.",
            });
        }
    }
}
