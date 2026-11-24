using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteMan.Core.Integrations.Zabbix;

public sealed class ZabbixApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal read-only Zabbix JSON-RPC client. Authenticates with an API token: sent as a
/// Bearer header on Zabbix 6.4 and newer, and as the "auth" field on 5.4 to 6.2.
/// </summary>
public sealed class ZabbixClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly IDisposable? _attached;
    private Version? _version;
    private int _id;

    /// <param name="url">The Zabbix frontend URL, e.g. https://zabbix.lab.local/zabbix (api_jsonrpc.php is added).</param>
    /// <param name="connectVia">
    /// Open TCP connections to this endpoint instead of the URL's host, keeping the URL's host name for
    /// the Host header and TLS. Used for SSH jump hosts: point it at the local end of a forwarded port.
    /// </param>
    /// <param name="attached">Disposed with the client, e.g. the SSH tunnel <paramref name="connectVia"/> points at.</param>
    public ZabbixClient(string url, string token, bool ignoreCertificateErrors = true, Func<EndPoint>? connectVia = null,
        HttpMessageHandler? handler = null, IDisposable? attached = null)
    {
        _attached = attached;
        _endpoint = ApiEndpoint(url);
        _token = token.Trim();
        _http = new HttpClient(handler ?? CreateHandler(ignoreCertificateErrors, connectVia)) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public Version? Version => _version;

    public static Uri ApiEndpoint(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ZabbixApiException($"\"{url}\" isn't a valid Zabbix URL.");
        if (u.AbsolutePath.EndsWith("api_jsonrpc.php", StringComparison.OrdinalIgnoreCase)) return u;
        var b = new UriBuilder(u) { Path = u.AbsolutePath.TrimEnd('/') + "/api_jsonrpc.php", Query = "" };
        return b.Uri;
    }

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
            h.ConnectCallback = async (ctx, ct) =>
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

    /// <summary>Checks the URL and token. Returns the Zabbix version.</summary>
    public async Task<Version> ConnectAsync(CancellationToken ct = default)
    {
        var v = await CallAsync("apiinfo.version", new JsonArray(), auth: false, ct);
        var text = v.GetValue<string>();
        _version = System.Version.TryParse(text, out var parsed) ? parsed : new Version(7, 0);
        // A cheap authenticated call, so a bad token fails here and not on the first poll.
        await CallAsync("host.get", new JsonObject { ["countOutput"] = true }, auth: true, ct);
        return _version;
    }

    public async Task<IReadOnlyList<ZabbixHost>> GetHostsAsync(CancellationToken ct = default)
    {
        if (_version is null) await ConnectAsync(ct);
        var v62 = _version! >= new Version(6, 2);
        var p = new JsonObject
        {
            ["output"] = new JsonArray("hostid", "host", "name", "status", "maintenance_status"),
            ["selectInterfaces"] = new JsonArray("ip", "dns", "useip", "port", "type", "main", "available"),
            [v62 ? "selectHostGroups" : "selectGroups"] = new JsonArray("name"),
            ["selectParentTemplates"] = new JsonArray("name"),
        };
        var res = await CallAsync("host.get", p, auth: true, ct);
        return res.AsArray().Select(h => ParseHost(h!, v62)).ToList();
    }

    /// <summary>
    /// Open problems from the given severity up, with the host each one belongs to. Problems on disabled
    /// triggers, disabled items or unmonitored hosts are left out, and so are suppressed problems.
    /// </summary>
    public async Task<IReadOnlyList<ZabbixProblem>> GetProblemsAsync(ZabbixSeverity minimum, CancellationToken ct = default)
    {
        if (_version is null) await ConnectAsync(ct);
        var sev = new JsonArray();
        for (var i = (int)minimum; i <= 5; i++) sev.Add(i.ToString());
        var problems = await CallAsync("problem.get", new JsonObject
        {
            ["output"] = new JsonArray("eventid", "objectid", "name", "severity", "clock", "acknowledged", "suppressed"),
            ["source"] = 0,
            ["object"] = 0,
            ["severities"] = sev,
            ["recent"] = false,
            // Only fields every supported version accepts: Zabbix 7 rejects the user name fields here
            // ("value must be one of ..."), so names are looked up with user.get below.
            ["selectAcknowledges"] = new JsonArray("userid", "action", "clock"),
            ["sortfield"] = new JsonArray("eventid"),
            ["sortorder"] = "DESC",
        }, auth: true, ct);
        // Suppressed problems (by a maintenance window, or by hand in Zabbix 6.4+) are hidden from the
        // Zabbix frontend's Problems view by default, so they raise no alarm here either.
        var list = problems.AsArray().Where(p => Str(p!, "suppressed") != "1").ToList();
        if (list.Count == 0) return [];

        // problem.get doesn't say which host a problem is on; its trigger does. It also keeps returning
        // problems whose trigger, item or host has since been disabled, which the Zabbix frontend hides.
        // "monitored" asks only for enabled triggers on enabled items on monitored hosts, so problems
        // from anything disabled find no trigger below and are left out the same way.
        var triggerIds = new JsonArray();
        foreach (var t in list.Select(p => Str(p!, "objectid")).Distinct()) triggerIds.Add(t);
        var triggers = await CallAsync("trigger.get", new JsonObject
        {
            ["output"] = new JsonArray("triggerid"),
            ["triggerids"] = triggerIds,
            ["monitored"] = true,
            ["selectHosts"] = new JsonArray("hostid"),
            ["preservekeys"] = true,
        }, auth: true, ct);
        var hostsByTrigger = new Dictionary<string, List<string>>();
        // "preservekeys" gives an object keyed by trigger id, but an empty result comes back as [] (every problem
        // left is on a disabled trigger, item or host), so read either shape.
        var found = triggers switch
        {
            JsonObject o => o.Select(kv => kv.Value),
            JsonArray a => a.AsEnumerable(),
            _ => [],
        };
        foreach (var t in found.OfType<JsonNode>())
            hostsByTrigger[Str(t, "triggerid")] = t["hosts"]?.AsArray().Select(h => Str(h!, "hostid")).ToList() ?? [];

        // action bit 2 = acknowledged; take the most recent update on each problem that did.
        var lastAck = new Dictionary<JsonNode, JsonNode>();
        foreach (var p in list)
            if (p!["acknowledges"]?.AsArray().Where(a => (Int(a!, "action") & 2) != 0).OrderByDescending(a => Int(a!, "clock")).FirstOrDefault() is { } a)
                lastAck[p] = a;
        var userNames = await GetUserNamesAsync(lastAck.Values.Select(a => Str(a, "userid")), ct);

        var result = new List<ZabbixProblem>();
        foreach (var p in list)
        {
            string? ackBy = null;
            if (lastAck.TryGetValue(p!, out var ack))
            {
                // Older servers may still put names on the acknowledge itself; use them if user.get gave nothing.
                var full = $"{Str(ack, "name")} {Str(ack, "surname")}".Trim();
                ackBy = userNames.TryGetValue(Str(ack, "userid"), out var n) ? n
                    : full.Length > 0 ? full : Str(ack, "username");
            }
            if (!hostsByTrigger.TryGetValue(Str(p, "objectid"), out var hosts)) continue;
            foreach (var hostId in hosts)
            {
                result.Add(new ZabbixProblem(
                    Str(p, "eventid"), hostId, Str(p, "name"),
                    (ZabbixSeverity)Math.Clamp((int)Int(p, "severity"), 0, 5),
                    DateTimeOffset.FromUnixTimeSeconds(Int(p, "clock")),
                    Str(p, "acknowledged") == "1", string.IsNullOrEmpty(ackBy) ? null : ackBy,
                    Str(p, "suppressed") == "1"));
            }
        }
        return result;
    }

    /// <summary>
    /// Display names ("Name Surname", else the login) for the given user ids. Best effort: a token
    /// without rights to see other users gets fewer names back, and any failure gives an empty map.
    /// </summary>
    private async Task<Dictionary<string, string>> GetUserNamesAsync(IEnumerable<string> userIds, CancellationToken ct)
    {
        var names = new Dictionary<string, string>();
        var ids = new JsonArray();
        foreach (var id in userIds.Where(i => i.Length > 0 && i != "0").Distinct()) ids.Add(id);
        if (ids.Count == 0) return names;
        try
        {
            var users = await CallAsync("user.get", new JsonObject
            {
                ["output"] = new JsonArray("userid", "username", "name", "surname"),
                ["userids"] = ids,
            }, auth: true, ct);
            foreach (var u in users.AsArray())
            {
                var full = $"{Str(u!, "name")} {Str(u!, "surname")}".Trim();
                var name = full.Length > 0 ? full : Str(u!, "username");
                if (name.Length > 0) names[Str(u!, "userid")] = name;
            }
        }
        catch (ZabbixApiException) { }
        return names;
    }

    /// <summary>The frontend page that lists a host's problems, or every problem when no host is given.</summary>
    public static string ProblemsUrl(string url, string? hostId = null)
    {
        var api = ApiEndpoint(url);
        var basePath = api.AbsolutePath[..^"api_jsonrpc.php".Length];
        var query = hostId is null ? "action=problem.view" : $"action=problem.view&filter_set=1&hostids%5B%5D={Uri.EscapeDataString(hostId)}";
        var b = new UriBuilder(api) { Path = basePath + "zabbix.php", Query = query };
        return b.Uri.ToString();
    }

    private static ZabbixHost ParseHost(JsonNode h, bool v62)
    {
        var interfaces = h["interfaces"]?.AsArray().Select(i => new ZabbixInterface(
            Str(i!, "ip"), Str(i!, "dns"), Str(i!, "useip") != "0", Str(i!, "port"), (int)Int(i!, "type"), (int)Int(i!, "available"))).ToList() ?? [];
        var groups = h[v62 ? "hostgroups" : "groups"]?.AsArray().Select(g => Str(g!, "name")).ToList() ?? [];
        var templates = h["parentTemplates"]?.AsArray().Select(t => Str(t!, "name")).ToList() ?? [];
        return new ZabbixHost(Str(h, "hostid"), Str(h, "host"), Str(h, "name"),
            Str(h, "status") == "0", Str(h, "maintenance_status") == "1", interfaces, groups, templates);
    }

    private static string Str(JsonNode n, string key) => n[key] switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var v => v.ToJsonString().Trim('"'),
    };

    private static long Int(JsonNode n, string key) => long.TryParse(Str(n, key), out var v) ? v : 0;

    private async Task<JsonNode> CallAsync(string method, JsonNode @params, bool auth, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
            ["id"] = Interlocked.Increment(ref _id),
        };
        var legacyAuth = auth && _version is not null && _version < new Version(6, 4);
        if (legacyAuth) body["auth"] = _token;

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json-rpc"),
        };
        if (auth && !legacyAuth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new ZabbixApiException($"Can't reach Zabbix at {_endpoint.Host}: {ex.Message}", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new ZabbixApiException($"Zabbix at {_endpoint.Host} didn't answer in time.", ex); }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new ZabbixApiException($"Zabbix answered HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Check the URL.");
            JsonNode? doc;
            try { doc = JsonNode.Parse(text); }
            catch (JsonException ex) { throw new ZabbixApiException("Zabbix didn't answer with JSON. Check that the URL points at the Zabbix frontend.", ex); }
            if (doc?["error"] is { } err)
            {
                var data = Str(err, "data");
                var msg = Str(err, "message");
                throw new ZabbixApiException(string.IsNullOrEmpty(data) ? msg : $"{msg} {data}");
            }
            return doc?["result"] ?? throw new ZabbixApiException("Zabbix sent an empty answer.");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _attached?.Dispose();
    }
}
