using System.Diagnostics;
using System.Net.Sockets;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Networking;

public sealed record EndpointAttempt(HostEndpoint Endpoint, bool Success, TimeSpan Elapsed, string? Error);

public sealed record ResolveResult(HostEndpoint? Endpoint, IReadOnlyList<EndpointAttempt> Attempts)
{
    public bool Success => Endpoint is not null;
    public bool UsedFallback => Success && Attempts.Count > 1;
}

/// <summary>
/// Picks the first reachable endpoint: primary first, then each fallback in order.
/// A TCP connect is a cheap, protocol-neutral reachability test that runs before the
/// (much slower) RDP/SSH handshake, so a dead primary costs at most <c>timeout</c>.
/// </summary>
public sealed class EndpointResolver
{
    private readonly Func<string, int, CancellationToken, Task> _probe;

    public EndpointResolver(Func<string, int, CancellationToken, Task>? probe = null)
    {
        _probe = probe ?? TcpProbeAsync;
    }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<ResolveResult> ResolveAsync(Entry entry, CancellationToken ct = default)
    {
        var attempts = new List<EndpointAttempt>();
        var defaultPort = Entry.DefaultPort(entry.Kind);
        foreach (var ep in entry.Endpoints.Where(e => !string.IsNullOrWhiteSpace(e.Host)))
        {
            var target = ep.Port > 0 ? ep : ep with { Port = defaultPort };
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await _probe(target.Host, target.Port, cts.Token);
                attempts.Add(new(target, true, sw.Elapsed, null));
                return new ResolveResult(target, attempts);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                attempts.Add(new(target, false, sw.Elapsed, "timed out"));
            }
            catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
            {
                attempts.Add(new(target, false, sw.Elapsed, ex.Message));
            }
        }
        return new ResolveResult(null, attempts);
    }

    public static async Task TcpProbeAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
    }

    /// <summary>Parses "host", "host:port", "[v6]:port" and "scheme://host:port/path".</summary>
    public static HostEndpoint Parse(string text)
    {
        text = text.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && text.Contains("://"))
            return new HostEndpoint(uri.Host.Trim('[', ']'), uri.IsDefaultPort ? 0 : uri.Port);
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            var host = text[1..close];
            var rest = text[(close + 1)..];
            return new HostEndpoint(host, rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) ? p6 : 0);
        }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out var port))
            return new HostEndpoint(text[..colon], port);
        return new HostEndpoint(text);
    }
}
