using RemoteMan.Core.Model;

namespace RemoteMan.Core.Networking;

/// <summary>
/// Where a web entry can be reached: the URL's own host first, then each fallback host in order.
/// <see cref="Invalid"/> counts fallback hosts that were skipped because they are not a host name or IP address.
/// </summary>
public sealed record WebTargets(HostEndpoint? Main, IReadOnlyList<HostEndpoint> Fallbacks, int Invalid)
{
    public IReadOnlyList<HostEndpoint> All => Main is null ? Fallbacks : [Main, .. Fallbacks];
}

/// <summary>Fallback hosts for web entries. Nothing here throws, whatever was typed into the entry.</summary>
public static class WebEndpoints
{
    /// <summary>The URL's host and port (443 or 80 when the URL has none). Null for an empty or placeholder URL.</summary>
    public static HostEndpoint? Main(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains(EntryTemplates.HostPlaceholder, StringComparison.Ordinal)) return null;
        if (!Uri.TryCreate(WebSettings.NormalizeUrl(url), UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        return EndpointResolver.Clean(new HostEndpoint(u.Host, u.Port > 0 ? u.Port : 0));
    }

    /// <summary>The URL first, then the entry's fallback hosts (a fallback without a port uses the URL's port). Duplicates are dropped.</summary>
    public static WebTargets For(Entry e)
    {
        var main = Main(e.Web?.Url);
        var defaultPort = main is { Port: > 0 } m ? m.Port : 443;
        var seen = new HashSet<(string, int)>();
        if (main is not null) seen.Add(Key(main));
        var fallbacks = new List<HostEndpoint>();
        var invalid = 0;
        foreach (var raw in e.Endpoints.Where(x => !string.IsNullOrWhiteSpace(x.Host)))
        {
            if (EndpointResolver.Clean(raw) is not { } ep) { invalid++; continue; }
            if (ep.Port == 0) ep = ep with { Port = defaultPort };
            if (seen.Add(Key(ep))) fallbacks.Add(ep);
        }
        return new WebTargets(main, fallbacks, invalid);
    }

    /// <summary>The URL with its host (and port) swapped for <paramref name="ep"/>. Null when either can't be used.</summary>
    public static string? UrlFor(string? url, HostEndpoint ep)
    {
        if (EndpointResolver.Clean(ep) is not { } c) return null;
        try
        {
            if (!Uri.TryCreate(WebSettings.NormalizeUrl(url), UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
            var b = new UriBuilder(u) { Host = c.Host };
            if (c.Port > 0) b.Port = c.Port;
            return b.Uri.ToString();
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return null; }
    }

    private static (string, int) Key(HostEndpoint ep) => (ep.Host.ToLowerInvariant(), ep.Port);
}
