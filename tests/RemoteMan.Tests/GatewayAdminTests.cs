using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RemoteMan.Gateway;
using RemoteMan.Gateway.Server;
using RemoteMan.Gateway.Server.Admin;
using Xunit;

namespace RemoteMan.Tests;

/// <summary>The relay's web admin console: first-admin setup, sign-in, per-connector tokens, site resources, invite links.</summary>
public sealed class GatewayAdminTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rmgwa").FullName;
    private readonly CancellationTokenSource _cts = new();
    private WebApplication _app = null!;
    private Relay _relay = null!;
    private Uri _uri = null!;
    private HttpClient _http = null!;
    private TcpListener _echo = null!;

    public async Task InitializeAsync()
    {
        _relay = new Relay(new RelayStore(Path.Combine(_dir, "relay.json")), _ => { });
        _app = RelayHost.Build(_relay, "http://127.0.0.1:0", null, null, null, new AdminOptions { ActivityPath = Path.Combine(_dir, "activity.jsonl"), Log = _ => { } });
        await _app.StartAsync();
        _uri = new Uri(_app.Urls.First());
        _http = NewClient();
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
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _echo.Stop();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private HttpClient NewClient(bool header = true)
    {
        var h = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = _uri };
        if (header) h.DefaultRequestHeaders.Add("X-RemoteMan-Admin", "1");
        return h;
    }

    private async Task<HttpResponseMessage> Post(HttpClient h, string path, object body) => await h.PostAsJsonAsync("admin/api" + path, body);

    private async Task SignedInAsync()
    {
        var code = _relay.Store.NewSetupCode();
        var r = await Post(_http, "/setup", new { code, user = "lange", password = "correct-horse-1" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private async Task<JsonElement> SnapshotAsync() => await _http.GetFromJsonAsync<JsonElement>("admin/api/snapshot");

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public void Connector_setup_carries_the_site_resources_as_its_allow_list()
    {
        var run = ConnectorSetup.DockerRun("https://r", "T", "c1", "img", "10.0.0.0/8, 192.0.2.0/24");
        Assert.Contains("-e ALLOW=10.0.0.0/8,192.0.2.0/24", run);
        Assert.Contains("ALLOW: 10.0.0.0/8,192.0.2.0/24", ConnectorSetup.Compose("https://r", "T", "c1", "img", "10.0.0.0/8, 192.0.2.0/24"));
        Assert.Contains("-e ALLOW=192.0.2.0/24", ConnectorSetup.Script("https://r", "T", "c1", "img", "192.0.2.0/24"));
        Assert.DoesNotContain("ALLOW=", ConnectorSetup.DockerRun("https://r", "T", "c1", "img"));
    }

    [Fact]
    public async Task Setup_needs_the_one_time_code_and_works_once()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(_http, "/setup", new { code = "guess-guess-guess", user = "x", password = "0123456789" })).StatusCode);
        var code = _relay.Store.NewSetupCode();
        Assert.Equal(HttpStatusCode.OK, (await Post(_http, "/setup", new { code, user = "lange", password = "correct-horse-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(NewClient(), "/setup", new { code, user = "mallory", password = "correct-horse-2" })).StatusCode);
        Assert.Single(_relay.Store.Admins);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("admin/api/snapshot")).StatusCode);
    }

    [Fact]
    public async Task The_api_needs_a_session_and_changes_need_the_console_header()
    {
        await SignedInAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync("admin/api/snapshot")).StatusCode);

        // Same session cookie, but a plain form-style post without the console's header (what another site could send).
        var cookies = new CookieContainer();
        var login = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = _uri };
        login.DefaultRequestHeaders.Add("X-RemoteMan-Admin", "1");
        Assert.Equal(HttpStatusCode.OK, (await login.PostAsJsonAsync("admin/api/login", new { user = "lange", password = "correct-horse-1" })).StatusCode);
        var bare = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = _uri };
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.PostAsJsonAsync("admin/api/sites", new { name = "evil" })).StatusCode);
        Assert.Null(_relay.Store.Site("evil"));
        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("admin/api/snapshot")).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_name_for_a_while()
    {
        await SignedInAsync();
        var other = NewClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(other, "/login", new { user = "lange", password = "nope" })).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await Post(other, "/login", new { user = "lange", password = "correct-horse-1" })).StatusCode);
    }

    [Fact]
    public async Task Two_step_sign_in_asks_for_the_code()
    {
        await SignedInAsync();
        var start = await (await _http.PostAsync("admin/api/me/totp/start", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = start.GetProperty("secret").GetString()!;
        var now = Totp.Code(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Assert.Equal(HttpStatusCode.OK, (await Post(_http, "/me/totp/confirm", new { code = now })).StatusCode);

        var c = NewClient();
        var r = await Post(c, "/login", new { user = "lange", password = "correct-horse-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("needCode").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/login", new { user = "lange", password = "correct-horse-1", code = now })).StatusCode);
    }

    [Fact]
    public async Task A_connector_set_up_in_the_console_comes_online_and_follows_the_site_resources()
    {
        await SignedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await Post(_http, "/sites", new { name = "school" })).StatusCode);
        var setup = await (await Post(_http, "/sites/school/connectors", new { name = "school-1" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = setup.GetProperty("token").GetString()!;
        Assert.Contains(token, setup.GetProperty("dockerRun").GetString());
        Assert.Contains("/v1/install/", setup.GetProperty("install").GetString());

        // The one-line install works until the connector is online (a failed first try can be run again); each
        // fetch carries a fresh token, which replaces the one shown with the setup.
        var install = setup.GetProperty("install").GetString()!;
        var path = new Uri(install.Split(' ')[2]).AbsolutePath;
        var first = await _http.GetStringAsync(path);
        var script = await _http.GetStringAsync(path);
        Assert.DoesNotContain(token, script);
        token = System.Text.RegularExpressions.Regex.Match(script, "SITE_TOKEN=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.NotEqual("", token);
        Assert.DoesNotContain(token, first);

        var connector = new Connector(new ConnectorOptions
        {
            Relay = _uri, SiteToken = token, Key = ECDsa.Create(ECCurve.NamedCurves.nistP256), Allow = new AllowList("127.0.0.0/8"),
            Name = "school-1", Version = "test", Log = _ => { }, ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        _ = connector.RunAsync(_cts.Token);
        await WaitUntil(() => _relay.IsOnline("school"));
        // Online: the install link has done its job.
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync(path)).StatusCode);

        var site = (await SnapshotAsync()).GetProperty("sites")[0];
        var c = site.GetProperty("connectors")[0];
        Assert.True(c.GetProperty("online").GetBoolean());
        Assert.Equal("test", c.GetProperty("version").GetString());
        Assert.Equal("127.0.0.0/8", c.GetProperty("allow").GetString());

        var deviceToken = _relay.Store.AddDevice("laptop", null);
        var port = ((IPEndPoint)_echo.LocalEndpoint).Port;
        using (var client = new GatewayClient(_uri, deviceToken))
            (await client.OpenAsync("school", [$"127.0.0.1:{port}"])).Dispose();

        // Resources set in the console reach the online connector and narrow what it opens.
        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync("admin/api/sites/school", new { resources = $"127.0.0.1:{port + 1}" })).StatusCode);
        await WaitUntil(() => connector.SiteAllow is not null);
        using (var client = new GatewayClient(_uri, deviceToken))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.OpenAsync("school", [$"127.0.0.1:{port}"]));
            Assert.Contains("resources", ex.Message);
        }

        // A bad rule is refused and changes nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PutAsJsonAsync("admin/api/sites/school", new { resources = "nas.lan" })).StatusCode);

        // Revoking the connector's token disconnects it.
        var id = setup.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await _http.DeleteAsync($"admin/api/sites/school/tokens/{id}")).StatusCode);
        await WaitUntil(() => !_relay.IsOnline("school"));
    }

    [Fact]
    public async Task An_invite_link_pasted_into_RemoteMan_makes_a_device()
    {
        await SignedInAsync();
        await Post(_http, "/sites", new { name = "school" });
        var r = await (await Post(_http, "/invites", new { owner = "Anna", sites = new[] { "school" }, days = 1 })).Content.ReadFromJsonAsync<JsonElement>();
        var link = r.GetProperty("link").GetString()!;
        Assert.Contains("/enroll#", link);

        var invite = await GatewayEnrollment.RedeemAsync(link, "ANNA'S PC");
        Assert.Equal("ANNA-S-PC", invite.Device);
        var dev = _relay.Store.Device("ANNA-S-PC")!;
        Assert.Equal("Anna", dev.Owner);
        Assert.Equal(["school"], dev.Sites);
        Assert.NotNull(_relay.Store.DeviceByToken(invite.Token));

        // Used up.
        await Assert.ThrowsAsync<GatewayException>(() => GatewayEnrollment.RedeemAsync(link, "again"));

        // Paused devices are refused; turning them back on lets them in again.
        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync("admin/api/devices/ANNA-S-PC", new { owner = "Anna", sites = new[] { "school" }, disabled = true })).StatusCode);
        Assert.Null(_relay.Store.DeviceByToken(invite.Token));
        await _http.PutAsJsonAsync("admin/api/devices/ANNA-S-PC", new { owner = "Anna", sites = new[] { "school" }, disabled = false });
        Assert.NotNull(_relay.Store.DeviceByToken(invite.Token));
    }

    [Theory]
    [InlineData("https://remoteman.example.dk/enroll#abcdefghijklmnopqrstu", "https://remoteman.example.dk/")]
    [InlineData("https://example.dk/remoteman/enroll#abcdefghijklmnopqrstu", "https://example.dk/remoteman")]
    [InlineData("https://example.dk:8443/enroll/abcdefghijklmnopqrstu", "https://example.dk:8443/")]
    public void Invite_links_parse(string link, string relay)
    {
        var (uri, code) = GatewayEnrollment.TryParseLink(link)!.Value;
        Assert.Equal(relay, uri.ToString());
        Assert.Equal("abcdefghijklmnopqrstu", code);
    }

    [Theory]
    [InlineData("rmgw1.abc")]
    [InlineData("https://example.dk/admin/")]
    [InlineData("https://example.dk/enroll#short")]
    public void Other_text_is_not_an_invite_link(string text) => Assert.Null(GatewayEnrollment.TryParseLink(text));

    [Fact]
    public void Totp_matches_the_rfc_test_vector()
    {
        // RFC 6238 appendix B, SHA-1, T = 59 s: 94287082 → the last 6 digits.
        var key = "12345678901234567890"u8.ToArray();
        Assert.Equal("287082", Totp.Code(key, 59 / 30));
        Assert.Equal(key, Totp.FromBase32(Totp.Base32(key)));
    }

    [Fact]
    public async Task The_console_page_is_served_with_a_strict_policy()
    {
        var r = await _http.GetAsync("admin/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("script-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("admin/assets/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("enroll")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("admin/assets/..%2Frelay.json")).StatusCode);
    }
}
