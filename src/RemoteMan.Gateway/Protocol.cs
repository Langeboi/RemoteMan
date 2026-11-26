using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteMan.Gateway;

/// <summary>
/// The relay's URLs and the small JSON messages of the tunnel protocol.
/// <code>
/// connector → relay  GET /v1/connector          (WebSocket, Bearer site token)  control: relay sends "open &lt;id&gt;"
/// connector → relay  GET /v1/accept/&lt;id&gt;         (WebSocket, Bearer site token)  the connector's end of one tunnel
/// RemoteMan → relay  GET /v1/connect?site=&lt;name&gt;  (WebSocket, Bearer device token) RemoteMan's end of one tunnel
/// RemoteMan → relay  GET /v1/mux                (WebSocket, Bearer device token) all of this device's tunnels as streams
/// RemoteMan → relay  GET /v1/sites              (Bearer device token) the sites this device may use, online state, keys
/// </code>
/// The relay splices the two ends message for message; inside, <see cref="SecureChannel"/> runs end to end, then
/// RemoteMan sends an <see cref="OpenRequest"/>, the connector answers with an <see cref="OpenReply"/>, and from then
/// on the frames carry the TCP stream's bytes.
/// <para>
/// Multiplexed (newer relays and connectors): RemoteMan keeps one WebSocket to /v1/mux and a connector that sends
/// <see cref="MuxHeader"/> gets its tunnels on its control connection, both as streams (<see cref="MuxOp"/>), so a
/// tunnel costs one round trip instead of two new TLS and WebSocket handshakes. Each side falls back on its own:
/// an older relay answers /v1/mux with 404, and the relay bridges streams to older connectors over /v1/accept.
/// </para>
/// </summary>
public static class GatewayProtocol
{
    public const string ConnectorPath = "/v1/connector";
    public const string AcceptPath = "/v1/accept/";
    public const string ConnectPath = "/v1/connect";
    /// <summary>RemoteMan → relay: one WebSocket that carries all of this device's tunnels as streams.</summary>
    public const string MuxPath = "/v1/mux";
    /// <summary>A connector that sends this ("1") takes tunnels as streams on its control connection.</summary>
    public const string MuxHeader = "X-RemoteMan-Mux";
    public const string SitesPath = "/v1/sites";
    public const string StatusPath = "/v1/status";
    public const string KeyHeader = "X-RemoteMan-Connector-Key";
    public const string NameHeader = "X-RemoteMan-Connector-Name";
    public const string VersionHeader = "X-RemoteMan-Gateway";
    /// <summary>The connector's own ALLOW and its version, shown in the relay's admin console.</summary>
    public const string AllowHeader = "X-RemoteMan-Connector-Allow";
    public const string ConnectorVersionHeader = "X-RemoteMan-Connector-Version";
    /// <summary>RemoteMan → relay POST: redeem an invite link's code for a device token (no auth; the code is the secret).</summary>
    public const string EnrollPath = "/v1/enroll";
    public const int Version = 1;
    /// <summary>
    /// WebSocket pings on every gateway connection: often enough that CG-NAT and home routers keep the path open,
    /// and a dead relay or connector is noticed within about half a minute instead of a minute.
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(15);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Site names: letters, digits, dash, underscore, dot; 1–40 characters.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 40 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static Uri WebSocketUri(Uri relay, string pathAndQuery)
    {
        var b = new UriBuilder(relay) { Scheme = relay.Scheme == "http" ? "ws" : relay.Scheme == "https" ? "wss" : relay.Scheme };
        var basePath = b.Path.TrimEnd('/');
        var q = pathAndQuery.IndexOf('?');
        b.Path = basePath + (q < 0 ? pathAndQuery : pathAndQuery[..q]);
        b.Query = q < 0 ? "" : pathAndQuery[(q + 1)..];
        return b.Uri;
    }

    public static Uri HttpUri(Uri relay, string path)
    {
        var b = new UriBuilder(relay);
        b.Path = b.Path.TrimEnd('/') + path;
        return b.Uri;
    }

    /// <summary>"host:port", "[v6]:port". Null when there's no usable port.</summary>
    public static (string Host, int Port)? ParseTarget(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        string host; string port;
        if (text.StartsWith('['))
        {
            var end = text.IndexOf("]:", StringComparison.Ordinal);
            if (end < 0) return null;
            host = text[1..end]; port = text[(end + 2)..];
        }
        else
        {
            var i = text.LastIndexOf(':');
            if (i <= 0 || text.IndexOf(':') != i) return null;
            host = text[..i]; port = text[(i + 1)..];
        }
        return int.TryParse(port, out var p) && p is > 0 and <= 65535 && host.Length > 0 ? (host, p) : null;
    }

    public static string FormatTarget(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal static T? Decode<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Json);
}

/// <summary>Where RemoteMan wants to go: hosts in fallback order. The connector uses the first that answers.</summary>
public sealed record OpenRequest(List<string> Targets);

/// <summary>Whether the connector reached a target, and which one.</summary>
public sealed record OpenReply(bool Ok, string? Target, string? Error);

/// <summary>A site as <c>/v1/sites</c> lists it for a device.</summary>
public sealed record SiteInfo(string Name, bool Online, int Connectors, List<string> Keys)
{
    /// <summary>Fingerprints of <see cref="Keys"/>, for showing.</summary>
    public IEnumerable<string> Fingerprints => Keys.Select(k => SecureChannel.Fingerprint(Convert.FromBase64String(k)));
}

/// <summary>
/// What a person pastes into RemoteMan: the relay's address and this device's token in one line,
/// <c>rmgw1.&lt;base64url JSON&gt;</c>, printed by <c>gateway device add</c> on the relay.
/// </summary>
public sealed record GatewayInvite(string Url, string Token, string? Device = null)
{
    private const string Prefix = "rmgw1.";

    public string Encode() => Prefix + Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { u = Url, t = Token, d = Device }));

    public static GatewayInvite? TryParse(string? text)
    {
        text = text?.Trim();
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var raw = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            raw = raw.PadRight(raw.Length + (4 - raw.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(raw));
            var u = doc.RootElement.GetProperty("u").GetString();
            var t = doc.RootElement.GetProperty("t").GetString();
            var d = doc.RootElement.TryGetProperty("d", out var dv) ? dv.GetString() : null;
            if (string.IsNullOrEmpty(t) || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
            return new GatewayInvite(u!, t!, d);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    internal static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class Pump
{
    /// <summary>Copies a TCP stream and a secure channel into each other until either side ends.</summary>
    public static async Task RunAsync(Stream tcp, SecureChannel channel, CancellationToken ct, bool count = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Task.Run(async () =>
        {
            var buf = new byte[SecureChannel.MaxPlaintext];
            try
            {
                int n;
                while ((n = await tcp.ReadAsync(buf, cts.Token)) > 0)
                {
                    await channel.SendAsync(buf.AsMemory(0, n), cts.Token);
                    if (count) GatewayActivity.Sent(n);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                           or System.Net.WebSockets.WebSocketException or GatewayException or RelayRefusedException) { }
            await channel.CloseAsync();
        });
        var down = Task.Run(async () =>
        {
            var buf = new byte[SecureChannel.MaxPlaintext];
            try
            {
                int n;
                while ((n = await channel.ReceiveAsync(buf, cts.Token)) >= 0)
                {
                    if (n == 0) continue;
                    await tcp.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    if (count) GatewayActivity.Received(n);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                           or System.Net.WebSockets.WebSocketException or GatewayException or RelayRefusedException) { }
            // The far end is done sending: half-close so the local program sees EOF but can still answer.
            if (tcp is NetworkStream ns) { try { ns.Socket.Shutdown(SocketShutdown.Send); } catch { } }
        });
        try { await Task.WhenAll(up, down); }
        finally { cts.Cancel(); }
    }
}

internal static class Text
{
    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}

/// <summary>
/// An invite link from the relay's admin console (<c>https://relay/enroll#&lt;code&gt;</c>). RemoteMan takes it in place of an
/// <c>rmgw1.</c> invite: it posts the code to the relay, which creates this PC's device and answers with its token.
/// </summary>
public static class GatewayEnrollment
{
    /// <summary>The relay's base address and the code, when <paramref name="text"/> is an invite link.</summary>
    public static (Uri Relay, string Code)? TryParseLink(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        string code;
        string basePath;
        var i = path.LastIndexOf("/enroll", StringComparison.Ordinal);
        if (i < 0) return null;
        if (path.EndsWith("/enroll", StringComparison.Ordinal)) { code = uri.Fragment.TrimStart('#'); basePath = path[..i]; }
        else if (path[(i + 7)..] is ['/', .. var rest] && !rest.Contains('/')) { code = rest; basePath = path[..i]; }
        else return null;
        code = Uri.UnescapeDataString(code).Trim();
        if (code.Length is < 16 or > 128 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        var b = new UriBuilder(uri) { Path = basePath.Length == 0 ? "/" : basePath, Query = "", Fragment = "" };
        return (b.Uri, code);
    }

    /// <summary>Redeems an invite link for this PC. Throws <see cref="GatewayException"/> with a message to show when the relay says no.</summary>
    public static async Task<GatewayInvite> RedeemAsync(string link, string pcName, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (TryParseLink(link) is not var (relay, code)) throw new GatewayException("That isn't an invite link.");
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Add(GatewayProtocol.VersionHeader, GatewayProtocol.Version.ToString());
        using var res = await http.PostAsync(GatewayProtocol.HttpUri(relay, GatewayProtocol.EnrollPath),
            new StringContent(JsonSerializer.Serialize(new { code, name = pcName }, GatewayProtocol.Json), Encoding.UTF8, "application/json"), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var msg = (int)res.StatusCode switch
            {
                404 or 410 => "That invite link has expired or was already used. Ask for a new one.",
                429 => "Too many tries. Wait a minute and try again.",
                _ => $"The relay answered {(int)res.StatusCode}.",
            };
            throw new GatewayException(msg);
        }
        using var doc = JsonDocument.Parse(body);
        var invite = GatewayInvite.TryParse(doc.RootElement.GetProperty("invite").GetString());
        return invite ?? throw new GatewayException("The relay's answer didn't hold an invite.");
    }
}
