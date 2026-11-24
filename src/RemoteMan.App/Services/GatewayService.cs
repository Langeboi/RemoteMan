using System.Collections.Concurrent;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using RemoteMan.Gateway;

namespace RemoteMan.App.Services;

/// <summary>
/// This PC's connection to the RemoteMan gateway: the relay client and the local forwarders that sessions and the
/// database connect to. Forwarders listen on 127.0.0.1 only and are reused for the same site and hosts, so a second
/// tab to the same machine or the database's connection pool doesn't open new listeners.
/// </summary>
public enum GatewayState { Off, Connecting, Ok, Down }

public sealed class GatewayService
{
    public static GatewayService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, LocalForwarder> _forwarders = new();
    private GatewayClient? _client;

    public bool IsConfigured => _client is not null;
    public string? RelayHost => _client?.Relay.Host;
    public string? Device { get; private set; }
    /// <summary>A forwarded connection failed (shown in the status bar).</summary>
    public event Action<string>? Problem;

    public void Configure(GatewayConfig? cfg)
    {
        _watch?.Cancel();
        _watch = null;
        foreach (var f in _forwarders.Values) f.Dispose();
        _forwarders.Clear();
        _paths.Clear();
        _cfg = null;
        _client?.Dispose();
        _client = null;
        Device = null;
        if (cfg is null || string.IsNullOrEmpty(cfg.Url) || string.IsNullOrEmpty(cfg.Token)) return;
        try
        {
            _cfg = cfg;
            var token = cfg.Token.StartsWith("D1", StringComparison.Ordinal) ? new DpapiSecretProtector().Unprotect(cfg.Token) : cfg.Token;
            _client = new GatewayClient(new Uri(cfg.Url), token);
            Device = cfg.Device;
        }
        catch (Exception) { _client = null; }   // another Windows user's file, or a damaged one: not set up
        State = _client is null ? GatewayState.Off : GatewayState.Connecting;
        Latency = null; Error = null; LiveSites = [];
        Array.Clear(_traffic);
        if (_client is { } c)
        {
            _watch = new CancellationTokenSource();
            _ = WatchAsync(c, _watch.Token);
        }
        Changed?.Invoke();
    }

    // ── The manual switch ──────────────────────────────────────────────────────────────────────────────────────
    private GatewayConfig? _cfg;

    /// <summary>Auto: direct first, the gateway when that fails. Connected: always the gateway. Disconnected: never.</summary>
    public GatewayMode Mode => _cfg?.Mode ?? GatewayMode.Auto;

    /// <summary>The switch was flipped (the settings need saving).</summary>
    public event Action? ModeChanged;

    /// <summary>Connections were closed so they reconnect on the new path (database pools should be cleared).</summary>
    public event Action? Rerouted;

    /// <summary>
    /// A data source name with the local forwarder addresses (127.0.0.1:port) put back to the real servers and
    /// the path last used: "PostgreSQL (10.0.0.5:5432 via school)" rather than "PostgreSQL (127.0.0.1:60518)".
    /// </summary>
    public string Describe(string text)
    {
        if (!text.Contains("127.0.0.1")) return text;
        foreach (var f in _forwarders.Values)
        {
            var shown = f.LastPath ?? f.Targets.FirstOrDefault() ?? "";
            text = text.Replace($"tcp:127.0.0.1,{f.Port}", shown).Replace($"127.0.0.1:{f.Port}", shown);
        }
        // MySQL names the server without its port.
        if (text.Contains("(127.0.0.1)") && _dbForwarder is { } db)
            text = text.Replace("(127.0.0.1)", $"({db.LastPath ?? db.Targets.FirstOrDefault()})");
        return text;
    }

    private LocalForwarder? _dbForwarder;

    public void SetMode(GatewayMode mode)
    {
        if (_cfg is null || _cfg.Mode == mode) return;
        _cfg.Mode = mode;
        _paths.Clear();
        // Open connections on the path that's now switched off are closed, so the database and sessions reconnect
        // on the new one right away instead of staying on the old path until they happen to drop.
        if (mode != GatewayMode.Auto)
        {
            var dropped = 0;
            foreach (var f in _forwarders.Values) dropped += f.Drop(direct: mode == GatewayMode.Connected);
            if (dropped > 0) Rerouted?.Invoke();
        }
        ModeChanged?.Invoke();
        Changed?.Invoke();
    }

    private bool? Override() => Mode switch { GatewayMode.Connected => true, GatewayMode.Disconnected => false, _ => null };

    // ── Live state for the status pill and box ─────────────────────────────────────────────────────────────────
    private CancellationTokenSource? _watch;
    private readonly double[] _traffic = new double[60];
    private int _trafficAt;
    private long _lastIn, _lastOut;
    private DateTime _nextPing;

    public GatewayState State { get; private set; }
    /// <summary>Round trip to the relay (listing the sites), measured every 15 seconds.</summary>
    public TimeSpan? Latency { get; private set; }
    /// <summary>Why the relay can't be used, when <see cref="State"/> is Down.</summary>
    public string? Error { get; private set; }
    public List<SiteInfo> LiveSites { get; private set; } = [];
    public double InPerSecond { get; private set; }
    public double OutPerSecond { get; private set; }
    /// <summary>Bytes per second, both ways, for the last 60 seconds (oldest first).</summary>
    public double[] Traffic => [.. _traffic[_trafficAt..], .. _traffic[.._trafficAt]];
    /// <summary>The site the box shows: the one last used, else the first online one.</summary>
    public string? ActiveSite =>
        GatewayActivity.LastSite is { } last && LiveSites.Any(s => string.Equals(s.Name, last, StringComparison.OrdinalIgnoreCase)) ? last
        : (LiveSites.FirstOrDefault(s => s.Online) ?? LiveSites.FirstOrDefault())?.Name;
    /// <summary>Raised about once a second while set up, and on every change (from a worker thread).</summary>
    public event Action? Changed;

    /// <summary>
    /// Where this PC is told to use the gateway ("Data source", "3 folders/entries"), for the status box's hint
    /// when nothing has gone through it yet. Set by the main window's view model.
    /// </summary>
    public Func<string?>? UsageSummary { get; set; }

    /// <summary>Measure the relay now (the box was opened).</summary>
    public void RefreshSoon() => _nextPing = DateTime.MinValue;

    private async Task WatchAsync(GatewayClient client, CancellationToken ct)
    {
        _lastIn = GatewayActivity.BytesIn; _lastOut = GatewayActivity.BytesOut;
        _nextPing = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= _nextPing)
            {
                try
                {
                    var (rtt, sites) = await client.PingAsync(ct);
                    if (ct.IsCancellationRequested) return;
                    Latency = rtt; LiveSites = sites; Error = null; State = GatewayState.Ok;
                    _nextPing = DateTime.UtcNow.AddSeconds(15);
                }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Latency = null; State = GatewayState.Down;
                    Error = ex is RelayRefusedException or GatewayException ? ex.Message : $"Can't reach {client.Relay.Host}: {ex.GetBaseException().Message}";
                    _nextPing = DateTime.UtcNow.AddSeconds(5);
                }
            }
            long i = GatewayActivity.BytesIn, o = GatewayActivity.BytesOut;
            InPerSecond = i - _lastIn; OutPerSecond = o - _lastOut;
            _lastIn = i; _lastOut = o;
            _traffic[_trafficAt] = InPerSecond + OutPerSecond;
            _trafficAt = (_trafficAt + 1) % _traffic.Length;
            Changed?.Invoke();
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Saves an invite into the settings (token under DPAPI) and starts using it.</summary>
    public static GatewayConfig FromInvite(GatewayInvite invite) =>
        new() { Url = invite.Url, Token = new DpapiSecretProtector().Protect(invite.Token), Device = invite.Device };

    public GatewayClient Client => _client ?? throw new GatewayException("The gateway isn't set up on this PC. Paste your invite under More > Gateway settings.");

    /// <summary>The site names this device may use (empty when not set up or the relay can't be reached).</summary>
    public async Task<List<SiteInfo>> SitesAsync(bool refresh = false)
    {
        if (_client is null) return [];
        try { return await _client.GetSitesAsync(refresh); }
        catch (Exception) { return []; }
    }

    public LocalForwarder Forwarder(string site, IReadOnlyList<string> targets, bool directFirst)
    {
        var key = $"{site}|{string.Join(",", targets)}|{directFirst}";
        return _forwarders.GetOrAdd(key, _ =>
        {
            var f = new LocalForwarder(Client, site, targets, directFirst);
            f.Failed += m => Problem?.Invoke(m);
            f.Used += direct => RecordPath(site, direct);
            f.Override = Override;
            return f;
        });
    }

    private readonly ConcurrentDictionary<string, (DateTime At, bool Direct)> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A connection to something at <paramref name="site"/> just went directly (true) or through the gateway.</summary>
    public void RecordPath(string site, bool direct) => _paths[site] = (DateTime.UtcNow, direct);

    /// <summary>
    /// True when the last connection to <paramref name="site"/> (the database's forwarder retries directly every
    /// minute, so it keeps this fresh) needed the gateway in the last 10 minutes: this PC is off-site, so entries
    /// skip the direct try and its timeout. Back on site, the next direct success flips it.
    /// </summary>
    public bool PreferGateway(string site) => Mode == GatewayMode.Connected ||
        _paths.TryGetValue(site, out var p) && !p.Direct && DateTime.UtcNow - p.At < TimeSpan.FromMinutes(10);

    /// <summary>
    /// For a data source that names a gateway site: each database server becomes a local forwarder that tries the
    /// server directly first (unless "always") and otherwise goes through the site. Null = connect directly.
    /// </summary>
    public DataSourceFactory.Route? RouteFor(DataSourceConfig cfg, string? site = null)
    {
        site ??= cfg.GatewaySite;
        if (site is not { Length: > 0 } || cfg.Kind == DataSourceKind.Sqlite || _client is null) return null;
        var always = cfg.GatewaySite is { Length: > 0 } && cfg.GatewayAlways;
        return (host, port) =>
        {
            var f = Forwarder(site, [GatewayProtocol.FormatTarget(host, port)], !always);
            _dbForwarder ??= f;
            return ("127.0.0.1", f.Port);
        };
    }

    /// <summary>
    /// The site everything uses when nothing names one: the data source's own, else the relay's only site for this
    /// PC. With one site there's nothing to choose, so a gateway set up on this PC just works: every connection
    /// tries directly first and falls back to the site, and once off-site is noticed the direct try is skipped.
    /// </summary>
    public string? DefaultSite(DataSourceConfig cfg)
    {
        if (_client is null) return null;
        if (cfg.GatewaySite is { } own) return own.Length > 0 ? own : null;
        return LiveSites.Count == 1 ? LiveSites[0].Name : _startupSite;
    }

    private string? _startupSite;

    /// <summary>Asks the relay for the sites once at startup (a few seconds at most), so the database can use the only one.</summary>
    public async Task<string?> DefaultSiteAtStartupAsync(DataSourceConfig cfg)
    {
        if (_client is null) return null;
        if (cfg.GatewaySite is { } own) return own.Length > 0 ? own : null;
        try
        {
            var sites = await SitesAsync().WaitAsync(TimeSpan.FromSeconds(4));
            return _startupSite = sites.Count == 1 ? sites[0].Name : null;
        }
        catch (TimeoutException) { return null; }
    }

    /// <summary>host:port for each of an entry's hosts, in fallback order, with the kind's default port filled in.</summary>
    public static List<string> Targets(Entry e, IEnumerable<HostEndpoint> endpoints, string? url = null)
    {
        var fallback = Entry.DefaultPort(e.Kind);
        if (e.Kind == EntryKind.Web && Uri.TryCreate(url, UriKind.Absolute, out var u)) fallback = u.Port > 0 ? u.Port : u.Scheme == "http" ? 80 : 443;
        return endpoints.Where(x => !string.IsNullOrWhiteSpace(x.Host))
            .Select(x => GatewayProtocol.FormatTarget(x.Host.Trim(), x.Port > 0 ? x.Port : fallback)).Distinct().ToList();
    }
}
