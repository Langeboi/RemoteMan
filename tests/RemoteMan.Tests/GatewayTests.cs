using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using RemoteMan.Gateway;
using RemoteMan.Gateway.Server;
using Xunit;

namespace RemoteMan.Tests;

/// <summary>Relay, connector and client in one process, over real loopback sockets.</summary>
public sealed class GatewayTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rmgw").FullName;
    private WebApplication _app = null!;
    private Relay _relay = null!;
    private Uri _relayUri = null!;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _log = [];
    private TcpListener _echo = null!;
    private string _siteToken = "", _deviceToken = "";
    private Connector _connector = null!;

    public async Task InitializeAsync()
    {
        var store = new RelayStore(Path.Combine(_dir, "relay.json"));
        _siteToken = store.AddSite("lab");
        store.AddSite("school");
        _deviceToken = store.AddDevice("laptop", ["lab"]);
        _relay = new Relay(store, Log);
        _app = RelayHost.Build(_relay, "http://127.0.0.1:0");
        await _app.StartAsync();
        _relayUri = new Uri(_app.Urls.First());

        _echo = new TcpListener(IPAddress.Loopback, 0);
        _echo.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await _echo.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () => { using (c) { try { await c.GetStream().CopyToAsync(c.GetStream()); } catch { } } });
            }
        });

        _connector = StartConnector("127.0.0.1");
        await WaitUntil(() => _relay.IsOnline("lab"));
    }

    private Connector StartConnector(string allow)
    {
        var c = new Connector(new ConnectorOptions
        {
            Relay = _relayUri, SiteToken = _siteToken, Key = ECDsa.Create(ECCurve.NamedCurves.nistP256),
            Allow = new AllowList(allow), Name = "lab-1", Log = Log, ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        _ = c.RunAsync(_cts.Token);
        return c;
    }

    private void Log(string s) { lock (_log) _log.Add(s); }

    private int EchoPort => ((IPEndPoint)_echo.LocalEndpoint).Port;

    private GatewayClient Client(string? token = null) => new(_relayUri, token ?? _deviceToken);

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _echo.Stop();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    private static async Task<byte[]> RoundTrip(int port, byte[] payload)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        var send = Task.Run(async () => { await s.WriteAsync(payload); tcp.Client.Shutdown(SocketShutdown.Send); });
        var got = new MemoryStream();
        await s.CopyToAsync(got).WaitAsync(TimeSpan.FromSeconds(20));
        await send;
        return got.ToArray();
    }

    [Fact]
    public async Task Bytes_go_through_the_relay_and_come_back_unchanged()
    {
        using var client = Client();
        using var fwd = new LocalForwarder(client, "lab", [$"127.0.0.1:{EchoPort}"]);
        var payload = RandomNumberGenerator.GetBytes(300_000);   // several frames
        Assert.Equal(payload, await RoundTrip(fwd.Port, payload));
        Assert.Equal($"127.0.0.1:{EchoPort} via lab", fwd.LastPath);
        // A second connection through the same forwarder.
        Assert.Equal("hello"u8.ToArray(), await RoundTrip(fwd.Port, "hello"u8.ToArray()));
    }

    [Fact]
    public async Task Connector_tries_targets_in_order()
    {
        using var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var deadPort = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();
        using var client = Client();
        var reached = await client.ProbeAsync("lab", [$"127.0.0.1:{deadPort}", $"127.0.0.1:{EchoPort}"]);
        Assert.Equal($"127.0.0.1:{EchoPort}", reached);
    }

    [Fact]
    public async Task Connector_refuses_what_its_allow_list_does_not_cover()
    {
        using var client = Client();
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.ProbeAsync("lab", [$"127.0.0.2:{EchoPort}"]));
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public async Task Unknown_token_is_refused()
    {
        using var client = Client("not-a-token");
        var ex = await Assert.ThrowsAsync<RelayRefusedException>(() => client.GetSitesAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
    }

    [Fact]
    public async Task Device_sees_only_its_sites_and_their_connector_keys()
    {
        using var client = Client();
        var sites = await client.GetSitesAsync();
        var lab = Assert.Single(sites);
        Assert.Equal("lab", lab.Name);
        Assert.True(lab.Online);
        Assert.Equal([_connector.Fingerprint], lab.Fingerprints);
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.ProbeAsync("school", [$"127.0.0.1:{EchoPort}"]));
        Assert.Contains("no site called school", ex.Message);
    }

    [Fact]
    public async Task Site_without_a_connector_is_reported_offline()
    {
        var store = _relay.Store;
        var token = store.AddDevice("all", null);
        using var client = Client(token);
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.ProbeAsync("school", ["10.0.0.1:22"]));
        Assert.Contains("isn't online", ex.Message);
    }

    [Fact]
    public async Task Removed_device_loses_access_at_once()
    {
        var token = _relay.Store.AddDevice("temp", ["lab"]);
        using var client = Client(token);
        Assert.Single(await client.GetSitesAsync());
        Assert.True(new RelayStore(Path.Combine(_dir, "relay.json")).RemoveDevice("temp"));   // like `gateway device remove` from a shell
        await Assert.ThrowsAsync<RelayRefusedException>(() => client.GetSitesAsync(refresh: true));
    }

    [Fact]
    public async Task Direct_first_skips_the_gateway_when_the_host_answers()
    {
        using var client = Client();
        using var fwd = new LocalForwarder(client, "lab", [$"127.0.0.1:{EchoPort}"], directFirst: true);
        Assert.Equal("ping"u8.ToArray(), await RoundTrip(fwd.Port, "ping"u8.ToArray()));
        Assert.Equal($"127.0.0.1:{EchoPort}", fwd.LastPath);
    }

    [Fact]
    public async Task A_second_connector_takes_over_when_the_first_one_stops()
    {
        using var stop = new CancellationTokenSource();
        var second = new Connector(new ConnectorOptions
        {
            Relay = _relayUri, SiteToken = _siteToken, Key = ECDsa.Create(ECCurve.NamedCurves.nistP256),
            Allow = new AllowList("127.0.0.1"), Name = "lab-2", Log = Log,
        });
        _ = second.RunAsync(stop.Token);
        await WaitUntil(() => second.Connected);
        using var client = Client();
        var lab = Assert.Single(await client.GetSitesAsync(refresh: true));
        Assert.Equal(2, lab.Connectors);
        Assert.Equal(2, lab.Keys.Count);
        Assert.Equal($"127.0.0.1:{EchoPort}", await client.ProbeAsync("lab", [$"127.0.0.1:{EchoPort}"]));
    }
}

public class GatewayUnitTests
{
    [Fact]
    public void Invite_round_trips_and_rejects_junk()
    {
        var invite = new GatewayInvite("https://remoteman.example.dk", "abc_DEF-123", "laptop");
        Assert.Equal(invite, GatewayInvite.TryParse("  " + invite.Encode() + "\n"));
        Assert.Null(GatewayInvite.TryParse("rmgw1.!!!"));
        Assert.Null(GatewayInvite.TryParse("hello"));
        Assert.Null(GatewayInvite.TryParse(new GatewayInvite("ftp://x", "t").Encode()));
    }

    [Theory]
    [InlineData(null, "10.1.2.3", 22, true)]
    [InlineData(null, "192.168.50.1", 443, true)]
    [InlineData(null, "100.72.0.9", 22, true)]
    [InlineData(null, "8.8.8.8", 53, false)]
    [InlineData(null, "127.0.0.1", 22, false)]
    [InlineData("10.0.5.0/24:22|3389", "10.0.5.9", 3389, true)]
    [InlineData("10.0.5.0/24:22|3389", "10.0.5.9", 80, false)]
    [InlineData("10.0.5.0/24:22|3389", "10.0.6.9", 22, false)]
    [InlineData("192.168.1.20", "192.168.1.20", 5432, true)]
    [InlineData("192.168.1.20", "192.168.1.21", 5432, false)]
    [InlineData("*", "1.1.1.1", 443, true)]
    [InlineData("private, 203.0.113.0/28", "203.0.113.5", 443, true)]
    [InlineData(null, "fd12::1", 22, true)]
    public void Allow_list(string? spec, string ip, int port, bool allowed) =>
        Assert.Equal(allowed, new AllowList(spec).Allows(IPAddress.Parse(ip), port));

    [Fact]
    public void Allow_list_rejects_host_names() =>
        Assert.Throws<FormatException>(() => new AllowList("nas.local"));
}

public class GatewayUrlTests
{
    [Theory]
    [InlineData("https://remoteman.example.dk", "wss://remoteman.example.dk/v1/connect?site=lab")]
    [InlineData("https://remoteman.example.dk:8443", "wss://remoteman.example.dk:8443/v1/connect?site=lab")]
    [InlineData("http://10.0.0.5:47200", "ws://10.0.0.5:47200/v1/connect?site=lab")]
    [InlineData("https://example.dk/remoteman/", "wss://example.dk/remoteman/v1/connect?site=lab")]
    public void Relay_url_keeps_its_port_and_path(string relay, string expected) =>
        Assert.Equal(expected, GatewayProtocol.WebSocketUri(new Uri(relay), "/v1/connect?site=lab").AbsoluteUri);
}
