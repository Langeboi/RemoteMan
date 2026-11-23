using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RemoteMan.Core.Integrations.Oxidized;

public sealed class OxidizedApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Read-only client for the Oxidized web API (oxidized-web): the node list, a node's versions in the
/// git output, one version's text, and the latest config. The only write is <see cref="BackUpNowAsync"/>,
/// which asks Oxidized to fetch a node next, like the button in its web page.
/// </summary>
public sealed partial class OxidizedClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly IDisposable? _attached;

    /// <param name="url">Where Oxidized's web page is, e.g. https://oxidized.lab.local or http://10.0.0.5:8888.</param>
    /// <param name="connectVia">Open TCP connections here instead (the local end of an SSH tunnel), keeping the URL's host for TLS.</param>
    /// <param name="attached">Disposed with the client, e.g. the SSH tunnel.</param>
    public OxidizedClient(string url, string? userName = null, string? password = null, bool ignoreCertificateErrors = true,
        Func<EndPoint>? connectVia = null, HttpMessageHandler? handler = null, IDisposable? attached = null)
    {
        _base = BaseUri(url);
        _attached = attached;
        _http = new HttpClient(handler ?? CreateHandler(ignoreCertificateErrors, connectVia)) { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(userName))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteMan");
    }

    /// <summary>The URL with a trailing slash, so relative API paths keep any sub-path (https://host/oxidized/).</summary>
    public static Uri BaseUri(string url)
    {
        url = url.Trim();
        if (url.Length == 0) throw new OxidizedApiException("Enter the Oxidized URL first.");
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new OxidizedApiException($"\"{url}\" isn't a valid Oxidized URL.");
        var path = u.AbsolutePath;
        if (path.EndsWith("/nodes", StringComparison.OrdinalIgnoreCase)) path = path[..^"nodes".Length];
        if (!path.EndsWith('/')) path += "/";
        return new UriBuilder(u) { Path = path, Query = "", Fragment = "" }.Uri;
    }

    /// <summary>Oxidized's own page for a node (opens in the browser).</summary>
    public static string NodePageUrl(string url, OxidizedNode node) =>
        new Uri(BaseUri(url), $"node/version?node_full={Uri.EscapeDataString(node.FullName)}").ToString();

    private static SocketsHttpHandler CreateHandler(bool ignoreCert, Func<EndPoint>? connectVia)
    {
        var h = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (ignoreCert)
            h.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true };
        if (connectVia is not null)
        {
            h.ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(connectVia(), ct);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch { s.Dispose(); throw; }
            };
        }
        return h;
    }

    // ---------------- calls ----------------

    public async Task<IReadOnlyList<OxidizedNode>> GetNodesAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("nodes.json", ct);
        if (json is not JsonArray list) throw new OxidizedApiException("Oxidized answered, but not with a node list. Check the URL points at Oxidized's web page.");
        return list.Where(n => n is JsonObject).Select(n => ParseNode(n!.AsObject())).Where(n => n.Name.Length > 0).ToList();
    }

    /// <summary>Saved versions, newest first. Empty when Oxidized doesn't use the git output.</summary>
    public async Task<IReadOnlyList<OxidizedVersion>> GetVersionsAsync(OxidizedNode node, CancellationToken ct = default)
    {
        JsonNode? json;
        try { json = await GetJsonAsync($"node/version.json?node_full={Uri.EscapeDataString(node.FullName)}", ct); }
        catch (OxidizedApiException ex) when (ex.InnerException is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.InternalServerError })
        {
            return []; // file output: no history
        }
        return ParseVersions(json);
    }

    /// <summary>The config text of one version.</summary>
    public async Task<string> GetVersionTextAsync(OxidizedNode node, OxidizedVersion version, CancellationToken ct = default)
    {
        var q = $"node={Uri.EscapeDataString(node.Name)}&oid={Uri.EscapeDataString(version.Oid)}" +
                $"&date={Uri.EscapeDataString(version.Date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))}&num={version.Number}";
        if (!string.IsNullOrEmpty(node.Group)) q += $"&group={Uri.EscapeDataString(node.Group)}";
        var body = await GetStringAsync($"node/version/view.json?{q}", ct);
        return ParseText(body);
    }

    /// <summary>The config Oxidized has now (works with the file output too).</summary>
    public async Task<string> GetLatestAsync(OxidizedNode node, CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(node.Group)
            ? $"node/fetch/{Uri.EscapeDataString(node.Name)}"
            : $"node/fetch/{Uri.EscapeDataString(node.Group)}/{Uri.EscapeDataString(node.Name)}";
        return ParseText(await GetStringAsync(path, ct));
    }

    /// <summary>Asks Oxidized to back up this node next.</summary>
    public async Task BackUpNowAsync(OxidizedNode node, CancellationToken ct = default)
    {
        // oxidized-web: GET /node/next/:node (then redirects to the node list).
        await GetStringAsync($"node/next/{Uri.EscapeDataString(node.Name)}", ct);
    }

    /// <summary>Asks Oxidized to read its device list again now (oxidized-web's reload), so new devices show up at once.</summary>
    public async Task ReloadAsync(CancellationToken ct = default) => await GetStringAsync("reload.json", ct);

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        var body = await GetStringAsync(path, ct);
        try { return JsonNode.Parse(body); }
        catch (JsonException ex)
        {
            throw new OxidizedApiException(body.TrimStart().StartsWith('<')
                ? "Oxidized answered with a web page instead of data. Check the URL, and that a login page isn't in front of it."
                : $"Oxidized sent something RemoteMan can't read ({ex.Message}).", ex);
        }
    }

    private async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        var uri = new Uri(_base, path);
        HttpResponseMessage res;
        try { res = await _http.GetAsync(uri, ct); }
        catch (HttpRequestException ex) { throw new OxidizedApiException($"Can't reach Oxidized at {_base.Host}: {ex.InnerException?.Message ?? ex.Message}", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new OxidizedApiException($"Oxidized at {_base.Host} didn't answer in time.", ex); }
        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode) return body;
            var inner = new HttpRequestException(body, null, res.StatusCode);
            throw res.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new OxidizedApiException("Oxidized refused the login. Check the username and password in Oxidized settings.", inner),
                HttpStatusCode.NotFound => new OxidizedApiException($"Oxidized doesn't have {uri.AbsolutePath} (is this oxidized-web, and is the node name right?).", inner),
                _ => new OxidizedApiException($"Oxidized answered {(int)res.StatusCode} {res.ReasonPhrase}.", inner),
            };
        }
    }

    // ---------------- parsing (public for tests) ----------------

    public static OxidizedNode ParseNode(JsonObject o)
    {
        var name = Str(o, "name") ?? "";
        var group = Str(o, "group");
        // oxidized-web puts nodes without a group in "default" for display, but full_name is the real key.
        var full = Str(o, "full_name");
        if (full is not null && !full.Contains('/')) group = null;
        else if (full is not null && full.Contains('/')) group = full[..full.LastIndexOf('/')];

        string status = Str(o, "status") ?? "";
        DateTimeOffset? lastRun = ParseTime(Str(o, "time"));
        if (o["last"] is JsonObject last)
        {
            if (status.Length == 0) status = Str(last, "status") ?? "";
            lastRun ??= ParseTime(Str(last, "end")) ?? ParseTime(Str(last, "start"));
        }
        if (status.Length == 0) status = "never";
        return new OxidizedNode(name, string.IsNullOrEmpty(group) ? null : group, Str(o, "ip"), Str(o, "model"), status, lastRun, ParseTime(Str(o, "mtime")));
    }

    public static IReadOnlyList<OxidizedVersion> ParseVersions(JsonNode? json)
    {
        if (json is not JsonArray arr) return [];
        var list = new List<(string Oid, DateTimeOffset Date, string? Author, string? Message)>();
        foreach (var v in arr.OfType<JsonObject>())
        {
            var oid = Str(v, "oid");
            var date = ParseTime(Str(v, "date")) ?? ParseTime(Str(v, "time"));
            if (oid is null || date is null) continue;
            var author = v["author"] is JsonObject a ? Str(a, "name") : Str(v, "author");
            list.Add((oid, date.Value, author, Str(v, "message")?.Trim()));
        }
        // Oxidized lists newest first; number them like its web page does (oldest = 1).
        list = list.OrderByDescending(x => x.Date).ToList();
        return list.Select((x, i) => new OxidizedVersion(x.Oid, x.Date, x.Author, x.Message, list.Count - i)).ToList();
    }

    /// <summary>The view route answers a JSON string, a JSON array of lines, or plain text, depending on the version.</summary>
    public static string ParseText(string body)
    {
        var t = body.TrimStart();
        if (t.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(t) ?? ""; } catch (JsonException) { }
        }
        else if (t.StartsWith('['))
        {
            try
            {
                if (JsonNode.Parse(t) is JsonArray a && a.All(x => x is JsonValue))
                    return string.Join("\n", a.Select(x => x!.GetValue<string>().TrimEnd('\r', '\n')));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        }
        return body;
    }

    private static string? Str(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    /// <summary>Ruby's Time#to_s ("2026-11-21 08:02:10 +0100" or "... UTC"), ISO 8601, or "never"/"unknown".</summary>
    public static DateTimeOffset? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var m = RubyTime().Match(s);
        if (m.Success)
        {
            var local = DateTime.ParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var z = m.Groups[3].Value;
            var offset = z is "" or "UTC" or "Z" ? TimeSpan.Zero
                : new TimeSpan(int.Parse(z.Substring(1, 2), CultureInfo.InvariantCulture), int.Parse(z.Substring(z.Length - 2), CultureInfo.InvariantCulture), 0) * (z[0] == '-' ? -1 : 1);
            return new DateTimeOffset(local, offset);
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d)[ T](\d\d:\d\d:\d\d)(?:\.\d+)?\s*(UTC|Z|[+-]\d\d:?\d\d)?$")]
    private static partial Regex RubyTime();

    public void Dispose()
    {
        _http.Dispose();
        _attached?.Dispose();
    }
}
