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
        foreach (var f in _forwarders.Values) f.Dispose();
        _forwarders.Clear();
        _client?.Dispose();
        _client = null;
        Device = null;
        if (cfg is null || string.IsNullOrEmpty(cfg.Url) || string.IsNullOrEmpty(cfg.Token)) return;
        try
        {
            var token = cfg.Token.StartsWith("D1", StringComparison.Ordinal) ? new DpapiSecretProtector().Unprotect(cfg.Token) : cfg.Token;
            _client = new GatewayClient(new Uri(cfg.Url), token);
            Device = cfg.Device;
        }
        catch (Exception) { _client = null; }   // another Windows user's file, or a damaged one: not set up
    }

    /// <summary>Saves an invite into the settings (token under DPAPI) and starts using it.</summary>
    public static GatewayConfig FromInvite(GatewayInvite invite) =>
        new() { Url = invite.Url, Token = new DpapiSecretProtector().Protect(invite.Token), Device = invite.Device };

    public GatewayClient Client => _client ?? throw new GatewayException("The gateway isn't set up on this PC. Paste your invite under More > Gateway.");

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
            return f;
        });
    }

    /// <summary>
    /// For a data source that names a gateway site: each database server becomes a local forwarder that tries the
    /// server directly first (unless "always") and otherwise goes through the site. Null = connect directly.
    /// </summary>
    public DataSourceFactory.Route? RouteFor(DataSourceConfig cfg)
    {
        if (cfg.GatewaySite is not { Length: > 0 } site || cfg.Kind == DataSourceKind.Sqlite || _client is null) return null;
        return (host, port) => ("127.0.0.1", Forwarder(site, [GatewayProtocol.FormatTarget(host, port)], !cfg.GatewayAlways).Port);
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
