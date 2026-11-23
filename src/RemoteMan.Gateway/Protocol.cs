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
/// RemoteMan → relay  GET /v1/sites              (Bearer device token) the sites this device may use, online state, keys
/// </code>
/// The relay splices the two ends message for message; inside, <see cref="SecureChannel"/> runs end to end, then
/// RemoteMan sends an <see cref="OpenRequest"/>, the connector answers with an <see cref="OpenReply"/>, and from then
/// on the frames carry the TCP stream's bytes.
/// </summary>
public static class GatewayProtocol
{
    public const string ConnectorPath = "/v1/connector";
    public const string AcceptPath = "/v1/accept/";
    public const string ConnectPath = "/v1/connect";
    public const string SitesPath = "/v1/sites";
    public const string StatusPath = "/v1/status";
    public const string KeyHeader = "X-RemoteMan-Connector-Key";
    public const string NameHeader = "X-RemoteMan-Connector-Name";
    public const string VersionHeader = "X-RemoteMan-Gateway";
    public const int Version = 1;

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
    public static async Task RunAsync(Stream tcp, SecureChannel channel, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Task.Run(async () =>
        {
            var buf = new byte[SecureChannel.MaxPlaintext];
            try
            {
                int n;
                while ((n = await tcp.ReadAsync(buf, cts.Token)) > 0)
                    await channel.SendAsync(buf.AsMemory(0, n), cts.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
            await channel.CloseAsync();
        });
        var down = Task.Run(async () =>
        {
            try
            {
                while (await channel.ReceiveAsync(cts.Token) is { } data)
                    if (data.Length > 0) await tcp.WriteAsync(data, cts.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                           or System.Net.WebSockets.WebSocketException or GatewayException) { }
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
