using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RemoteMan.Gateway.Server.Admin;

public sealed class AdminOptions
{
    /// <summary>ADMIN=off turns the web console off (the CLI and invite links keep working).</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>ADMIN_ALLOW: who may open the console at all (addresses/ranges); null = anyone who can reach the relay.</summary>
    public AllowList? Allow { get; init; }
    public string? PublicUrl { get; init; }
    public string ConnectorImage { get; init; } = "ghcr.io/langeboi/remoteman-gateway:test";
    /// <summary>activity.jsonl in the data volume; null keeps the activity list in memory only.</summary>
    public string? ActivityPath { get; init; }
    public string Version { get; init; } = "dev";
    public Action<string> Log { get; init; } = Console.WriteLine;
}

/// <summary>
/// The relay's web admin console at /admin: sites and their connectors (live), resources, devices and who they belong
/// to, invite links, console admins and the activity list. Sign-in is a password (PBKDF2) plus an optional
/// authenticator code, sessions live in memory behind an HttpOnly, SameSite=Strict cookie, every change needs a
/// custom header (so another site can't post a form at it), and failed sign-ins are slowed down per address and per name.
/// The first admin is made with a one-time setup link the relay prints in its log.
/// </summary>
public sealed class AdminConsole
{
    private const string Cookie = "rmgw_admin";
    private const string CsrfHeader = "X-RemoteMan-Admin";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private sealed class Session
    {
        public required string Id { get; init; }
        public required string User { get; init; }
        public required int Stamp { get; init; }
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public string? Ip { get; init; }
        public string? Agent { get; init; }
        public string? PendingTotp { get; set; }
    }

    /// <summary>How long a one-line install link works (until its connector comes online, at most this long).</summary>
    private static readonly TimeSpan InstallFor = TimeSpan.FromHours(24);

    private readonly Relay _relay;
    private readonly AdminOptions _o;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Throttle _throttle = new();

    public AdminConsole(Relay relay, AdminOptions options)
    {
        _relay = relay;
        _o = options;
        Activity = new ActivityLog(options.ActivityPath);
        relay.Activity += e => { if (e.Kind != "tunnel.open") Activity.Add(e); };
    }

    public ActivityLog Activity { get; }
    private RelayStore Store => _relay.Store;

    /// <summary>
    /// Where the console lives. Not /admin: a generic path is the first thing scanners try. Everything else on the
    /// relay (the root page, /admin, the console when it's off) is a bare 404 that names nothing.
    /// </summary>
    public const string ConsolePath = "/rmrelay";

    public void Map(WebApplication app)
    {
        // Invite links work even with the console off: they only need the code.
        app.MapGet("/enroll", (HttpContext c) => Page(c, "enroll.html"));
        app.MapPost(GatewayProtocol.EnrollPath + "/peek", Task<IResult> (HttpContext c) => PeekInviteAsync(c));
        app.MapPost(GatewayProtocol.EnrollPath, Task<IResult> (HttpContext c) => RedeemInviteAsync(c));
        app.MapGet("/v1/install/{code}", (HttpContext c, string code) => TakeInstall(c, code));
        app.MapGet(ConsolePath + "/assets/{file}", (HttpContext c, string file) => Page(c, file));

        if (!_o.Enabled)
        {
            return;   // nothing under the console path: a plain 404
        }

        // Routing treats /rmrelay and /rmrelay/ alike; the page's relative links need the slash.
        app.MapGet(ConsolePath, (HttpContext c) => c.Request.Path.Value!.EndsWith('/') ? Page(c, "index.html") : Results.Redirect(c.Request.PathBase + ConsolePath + "/"));

        var api = app.MapGroup(ConsolePath + "/api");
        api.MapGet("/state", State);
        api.MapPost("/setup", Task<IResult> (HttpContext c) => SetupAsync(c));
        api.MapPost("/login", Task<IResult> (HttpContext c) => LoginAsync(c));
        api.MapPost("/logout", Task<IResult> (HttpContext c) => Authed(c, s => { _sessions.TryRemove(s.Id, out _); ClearCookie(c); return Ok(); }));

        api.MapGet("/snapshot", Task<IResult> (HttpContext c) => Authed(c, s => Results.Json(Snapshot(c, s), GatewayProtocol.Json)));
        api.MapGet("/activity", Task<IResult> (HttpContext c) => Authed(c, _ =>
        {
            var q = c.Request.Query;
            var n = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 2000) : 300;
            return Results.Json(Activity.Latest(n, Q(q["site"]), Q(q["subject"])), GatewayProtocol.Json);
        }));

        // Sites and connectors
        api.MapPost("/sites", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            var name = Str(b, "name") ?? "";
            if (Str(b, "resources") is { } r0) AllowList.Normalize(r0);   // a bad rule fails before the site exists
            Store.AddSite(name, Str(b, "description"));
            // The token from AddSite is the legacy "site" token; the console hands out per-connector tokens instead.
            Store.RevokeConnectorToken(name, "site");
            if (Str(b, "resources") is { } res) Store.UpdateSite(name, Str(b, "description"), res);
            Audit(s, "site.add", name, null, $"Site {name} added");
            await Task.CompletedTask;
            return Ok();
        }));
        api.MapPut("/sites/{name}", (HttpContext c, string name) => AuthedBody(c, async (s, b) =>
        {
            var before = Store.Site(name)?.Resources;
            try { Store.UpdateSite(name, Str(b, "description"), Str(b, "resources")); }
            catch (FormatException ex) { return Err(400, ex.Message); }
            var after = Store.Site(name)?.Resources;
            if (before != after)
            {
                await _relay.PushResourcesAsync(name);
                Audit(s, "site.resources", name, null, after is null ? $"{name}: resources cleared (the connectors' own ALLOW decides)" : $"{name}: resources set to {after}");
            }
            return Ok();
        }));
        api.MapDelete("/sites/{name}", (HttpContext c, string name) => Authed(c, async s =>
        {
            if (!Store.RemoveSite(name)) return Err(404, $"No site called {name}.");
            await _relay.DisconnectAsync(name);
            Audit(s, "site.remove", name, null, $"Site {name} removed");
            return Ok();
        }));
        api.MapPost("/sites/{name}/connectors", (HttpContext c, string name) => AuthedBody(c, async (s, b) =>
        {
            if (Store.Site(name) is not { } site) return Err(404, $"No site called {name}.");
            var cname = Str(b, "name") ?? NextConnectorName(site);
            var (token, id) = Store.AddConnectorToken(site.Name, cname);
            var relayUrl = PublicUrl(c);
            var installCode = Store.AddInstall(site.Name, id, cname, InstallFor);
            Audit(s, "connector.token", site.Name, cname, $"Connector token {cname} made for {site.Name}");
            await Task.CompletedTask;
            return Results.Json(new
            {
                id, name = cname, token, relayUrl, publicUrlSet = _o.PublicUrl is not null,
                // The connector's own ALLOW starts out as the site's resources, so what the console allows is what it
                // opens (without resources it keeps its default: the private ranges).
                dockerRun = ConnectorSetup.DockerRun(relayUrl, token, cname, _o.ConnectorImage, site.Resources),
                compose = ConnectorSetup.Compose(relayUrl, token, cname, _o.ConnectorImage, site.Resources),
                // No -f: when the link no longer works, the relay answers with a script that says why, and sh prints it.
                install = $"curl -sSL {relayUrl}/v1/install/{installCode} | sudo sh",
            }, GatewayProtocol.Json);
        }));
        api.MapDelete("/sites/{name}/tokens/{id}", (HttpContext c, string name, string id) => Authed(c, async s =>
        {
            if (!Store.RevokeConnectorToken(name, id)) return Err(404, "No such token.");
            await _relay.DisconnectAsync(name, id);
            Audit(s, "connector.revoke", name, id, $"A connector token for {name} was revoked; connectors using it were disconnected");
            return Ok();
        }));
        api.MapPost("/sites/{name}/reset-keys", (HttpContext c, string name) => Authed(c, async s =>
        {
            Store.ResetKeys(name);
            await _relay.DisconnectAsync(name);   // they reconnect and their keys are recorded afresh
            Audit(s, "site.reset-keys", name, null, $"{name}: connector keys forgotten; connectors reconnect and are recorded again");
            return Ok();
        }));
        api.MapPost("/sites/{name}/forget", (HttpContext c, string name) => AuthedBody(c, async (s, b) =>
        {
            await Task.CompletedTask;
            return Store.ForgetConnector(name, Str(b, "name") ?? "", Str(b, "key") ?? "") ? Ok() : Err(404, "No such connector.");
        }));

        // Devices and invites
        api.MapPost("/devices", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            var name = Str(b, "name") ?? "";
            var token = Store.AddDevice(name, Sites(b), Str(b, "owner"));
            Audit(s, "device.add", null, name, $"Device {name} added");
            await Task.CompletedTask;
            return Results.Json(new { invite = new GatewayInvite(PublicUrl(c), token, name).Encode() }, GatewayProtocol.Json);
        }));
        api.MapPut("/devices/{name}", (HttpContext c, string name) => AuthedBody(c, async (s, b) =>
        {
            var disabled = b.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
            var was = Store.Device(name)?.Disabled;
            Store.UpdateDevice(name, Sites(b), Str(b, "owner"), disabled);
            if (disabled) _relay.DropDevice(name);
            Audit(s, "device.update", null, name, was != disabled ? $"Device {name} {(disabled ? "paused" : "turned back on")}" : $"Device {name} changed");
            await Task.CompletedTask;
            return Ok();
        }));
        api.MapDelete("/devices/{name}", (HttpContext c, string name) => Authed(c, s =>
        {
            if (!Store.RemoveDevice(name)) return Err(404, $"No device called {name}.");
            _relay.DropDevice(name);
            Audit(s, "device.remove", null, name, $"Device {name} removed");
            return Ok();
        }));
        api.MapPost("/invites", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            var days = b.TryGetProperty("days", out var dv) && dv.TryGetDouble(out var dd) ? Math.Clamp(dd, 0.04, 30) : 7;
            var uses = b.TryGetProperty("uses", out var uv) && uv.TryGetInt32(out var ui) ? ui : 1;
            var (code, inv) = Store.AddInvite(Str(b, "owner"), Str(b, "device"), Sites(b), TimeSpan.FromDays(days), uses, s.User);
            Audit(s, "invite.add", null, inv.Device ?? inv.Owner, $"Invite link made{(inv.Owner is null ? "" : $" for {inv.Owner}")}{(inv.Device is null ? "" : $" ({inv.Device})")}, valid {Relay.Duration(inv.Expires - inv.Created)}");
            await Task.CompletedTask;
            return Results.Json(new { id = inv.Id, link = $"{PublicUrl(c)}/enroll#{code}" }, GatewayProtocol.Json);
        }));
        api.MapDelete("/invites/{id}", (HttpContext c, string id) => Authed(c, s =>
        {
            if (!Store.RevokeInvite(id)) return Err(404, "No such invite.");
            Audit(s, "invite.revoke", null, id, "Invite link revoked");
            return Ok();
        }));

        // Console admins and your own account
        api.MapPost("/admins", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            var name = Str(b, "name") ?? "";
            Store.AddAdmin(name, Str(b, "password") ?? "");
            Audit(s, "admin.add", null, name, $"Admin {name} added");
            await Task.CompletedTask;
            return Ok();
        }));
        api.MapDelete("/admins/{name}", (HttpContext c, string name) => Authed(c, s =>
        {
            if (!Store.RemoveAdmin(name)) return Err(404, $"No admin called {name}.");
            foreach (var x in _sessions.Values.Where(x => x.User.Equals(name, StringComparison.OrdinalIgnoreCase))) _sessions.TryRemove(x.Id, out _);
            Audit(s, "admin.remove", null, name, $"Admin {name} removed");
            return Ok();
        }));
        api.MapPost("/me/password", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            if (Store.Admin(s.User) is not { } a || !Passwords.Verify(Str(b, "current") ?? "", a.PasswordHash)) return Err(400, "The current password isn't right.");
            Store.SetPassword(s.User, Str(b, "next") ?? "");
            Resign(c, s);
            Audit(s, "admin.password", null, s.User, $"{s.User} changed their password; their other sessions were signed out");
            await Task.CompletedTask;
            return Ok();
        }));
        api.MapPost("/me/totp/start", Task<IResult> (HttpContext c) => Authed(c, s =>
        {
            s.PendingTotp = Totp.NewSecret();
            var issuer = "RemoteMan " + (Uri.TryCreate(PublicUrl(c), UriKind.Absolute, out var u) ? u.Host : "gateway");
            return Results.Json(new { secret = s.PendingTotp, uri = Totp.Uri(s.PendingTotp, s.User, issuer) }, GatewayProtocol.Json);
        }));
        api.MapPost("/me/totp/confirm", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            if (s.PendingTotp is not { } secret) return Err(400, "Start again.");
            if (!Totp.Verify(secret, Str(b, "code"))) return Err(400, "That code doesn't match. Check the time on your phone and try the next code.");
            Store.SetTotp(s.User, secret);
            s.PendingTotp = null;
            Resign(c, s);
            Audit(s, "admin.totp", null, s.User, $"{s.User} turned on two-step sign-in");
            await Task.CompletedTask;
            return Ok();
        }));
        api.MapPost("/me/totp/off", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            if (Store.Admin(s.User) is not { } a || !Passwords.Verify(Str(b, "password") ?? "", a.PasswordHash)) return Err(400, "The password isn't right.");
            Store.SetTotp(s.User, null);
            Resign(c, s);
            Audit(s, "admin.totp", null, s.User, $"{s.User} turned off two-step sign-in");
            await Task.CompletedTask;
            return Ok();
        }));
        api.MapPost("/me/signout-others", Task<IResult> (HttpContext c) => Authed(c, s =>
        {
            foreach (var x in _sessions.Values.Where(x => x.User == s.User && x.Id != s.Id)) _sessions.TryRemove(x.Id, out _);
            return Ok();
        }));
    }

    // ---- sign-in ----

    private IResult State(HttpContext c)
    {
        if (!ConsoleAllowed(c)) return Err(403, "The console isn't open to this address (ADMIN_ALLOW).");
        var s = Current(c);
        return Results.Json(new
        {
            setup = Store.Admins.Count == 0,
            user = s?.User,
            relay = PublicUrl(c),
            version = _o.Version,
        }, GatewayProtocol.Json);
    }

    private async Task<IResult> SetupAsync(HttpContext c)
    {
        if (!ConsoleAllowed(c) || !SameSiteRequest(c)) return Err(403, "Not allowed.");
        var ip = ClientIp(c);
        if (_throttle.Locked("ip:" + ip) is { } wait) return Err(429, $"Too many tries. Wait {Relay.Duration(wait)}.");
        var b = await Body(c);
        var code = Str(b, "code");
        if (!Store.SetupCodeValid(code))
        {
            _throttle.Fail("ip:" + ip);
            return Err(400, "That setup link has expired or was used. On the relay run: gateway admin setup");
        }
        try { Store.AddAdminWithSetupCode(code!, Str(b, "user") ?? "", Str(b, "password") ?? ""); }
        catch (ArgumentException ex) { return Err(400, ex.Message); }
        catch (UnauthorizedAccessException ex) { return Err(400, ex.Message); }
        var name = Store.Admin(Str(b, "user")!)!.Name;
        Store.NoteLogin(name);
        Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "admin.add", null, name, $"Admin {name} created with the setup link from {ip}", name));
        StartSession(c, Store.Admin(name)!);
        return Ok();
    }

    private async Task<IResult> LoginAsync(HttpContext c)
    {
        if (!ConsoleAllowed(c) || !SameSiteRequest(c)) return Err(403, "Not allowed.");
        var b = await Body(c);
        var user = (Str(b, "user") ?? "").Trim();
        var ip = ClientIp(c);
        if ((_throttle.Locked("ip:" + ip) ?? _throttle.Locked("user:" + user.ToLowerInvariant())) is { } wait)
            return Err(429, $"Too many failed sign-ins. Wait {Relay.Duration(wait)} and try again.");

        var admin = Store.Admin(user);
        var ok = Passwords.Verify(Str(b, "password") ?? "", admin?.PasswordHash ?? Passwords.Dummy) && admin is not null;
        if (ok && admin!.TotpSecret is { } secret)
        {
            var code = Str(b, "code");
            if (string.IsNullOrEmpty(code)) return Results.Json(new { needCode = true }, GatewayProtocol.Json, statusCode: 401);
            ok = Totp.Verify(secret, code);
        }
        if (!ok)
        {
            _throttle.Fail("ip:" + ip);
            _throttle.Fail("user:" + user.ToLowerInvariant());
            Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "admin.login-failed", null, user.Length > 40 ? user[..40] : user, $"Failed console sign-in as {Trim(user, 40)} from {ip}"));
            return Err(401, "Wrong name, password or code.");
        }
        _throttle.Reset("user:" + user.ToLowerInvariant());
        Store.NoteLogin(admin!.Name);
        StartSession(c, admin);
        Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "admin.login", null, admin.Name, $"{admin.Name} signed in to the console from {ip}", admin.Name));
        return Ok();
    }

    private void StartSession(HttpContext c, AdminRecord admin)
    {
        var token = RelayStore.NewToken();
        var id = RelayStore.Hash(token);
        _sessions[id] = new Session
        {
            Id = id, User = admin.Name, Stamp = admin.Stamp, Ip = ClientIp(c),
            Agent = c.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? Trim(ua, 160) : null,
        };
        c.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = IsHttps(c), Path = c.Request.PathBase + ConsolePath, MaxAge = MaxAge,
        });
        // Drop sessions that have run out, now and then.
        foreach (var x in _sessions.Values.Where(x => !Fresh(x))) _sessions.TryRemove(x.Id, out _);
    }

    /// <summary>After a password or 2FA change: this session continues under the admin's new stamp.</summary>
    private void Resign(HttpContext c, Session s)
    {
        foreach (var x in _sessions.Values.Where(x => x.User == s.User)) _sessions.TryRemove(x.Id, out _);
        if (Store.Admin(s.User) is { } a) StartSession(c, a);
    }

    private void ClearCookie(HttpContext c) => c.Response.Cookies.Delete(Cookie, new CookieOptions { Path = c.Request.PathBase + ConsolePath });

    private Session? Current(HttpContext c)
    {
        if (c.Request.Cookies[Cookie] is not { Length: > 20 } token) return null;
        if (!_sessions.TryGetValue(RelayStore.Hash(token), out var s)) return null;
        if (!Fresh(s) || Store.Admin(s.User) is not { } a || a.Stamp != s.Stamp)
        {
            _sessions.TryRemove(s.Id, out _);
            return null;
        }
        s.LastSeen = DateTimeOffset.UtcNow;
        return s;
    }

    private static bool Fresh(Session s) => DateTimeOffset.UtcNow - s.LastSeen < IdleTimeout && DateTimeOffset.UtcNow - s.Created < MaxAge;

    private async Task<IResult> Authed(HttpContext c, Func<Session, Task<IResult>> handler)
    {
        if (!ConsoleAllowed(c)) return Err(403, "The console isn't open to this address (ADMIN_ALLOW).");
        if (Current(c) is not { } s) return Err(401, "Sign in again.");
        if (!HttpMethods.IsGet(c.Request.Method) && !SameSiteRequest(c)) return Err(403, "Missing the console's request header.");
        try { return await handler(s); }
        catch (ArgumentException ex) { return Err(400, ex.Message); }
        catch (FormatException ex) { return Err(400, ex.Message); }
    }

    private Task<IResult> Authed(HttpContext c, Func<Session, IResult> handler) => Authed(c, s => Task.FromResult(handler(s)));

    private Task<IResult> AuthedBody(HttpContext c, Func<Session, JsonElement, Task<IResult>> handler) =>
        Authed(c, async s => await handler(s, await Body(c)));

    /// <summary>Changes must carry the console's header: a form or image on another site can't add one, and fetch() from another origin would need CORS, which the relay never grants.</summary>
    private static bool SameSiteRequest(HttpContext c) => c.Request.Headers[CsrfHeader] == "1";

    private bool ConsoleAllowed(HttpContext c) =>
        _o.Allow is null || (IPAddress.TryParse(ClientIp(c), out var ip) && _o.Allow.Allows(ip, 443));

    private void Audit(Session s, string kind, string? site, string? subject, string text) =>
        Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, kind, site, subject, text, s.User));

    // ---- invite links ----

    private async Task<IResult> PeekInviteAsync(HttpContext c)
    {
        var ip = ClientIp(c);
        if (_throttle.Locked("enroll:" + ip) is { } wait) return Err(429, $"Too many tries. Wait {Relay.Duration(wait)}.");
        var b = await Body(c);
        if (Store.InviteByCode(Str(b, "code")) is not { } inv)
        {
            _throttle.Fail("enroll:" + ip);
            return Err(410, "This invite link has expired or was already used. Ask for a new one.");
        }
        return Results.Json(new { owner = inv.Owner, device = inv.Device, sites = inv.Sites, expires = inv.Expires, relay = PublicUrl(c) }, GatewayProtocol.Json);
    }

    private async Task<IResult> RedeemInviteAsync(HttpContext c)
    {
        var ip = ClientIp(c);
        if (_throttle.Locked("enroll:" + ip) is { } wait) return Err(429, $"Too many tries. Wait {Relay.Duration(wait)}.");
        var b = await Body(c);
        var code = Str(b, "code") ?? "";
        if (Store.RedeemInvite(code, Str(b, "name")) is not var (device, token, inv))
        {
            _throttle.Fail("enroll:" + ip);
            return Err(410, "This invite link has expired or was already used. Ask for a new one.");
        }
        Store.TouchDevice(device, ip);
        Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "device.enroll", null, device,
            $"Device {device} joined with an invite link{(inv.Owner is null ? "" : $" for {inv.Owner}")} from {ip}", inv.CreatedBy));
        return Results.Json(new { device, invite = new GatewayInvite(PublicUrl(c), token, device).Encode() }, GatewayProtocol.Json);
    }

    private IResult TakeInstall(HttpContext c, string code)
    {
        // A link keeps working until its connector has come online (so a first try that failed half-way, say on
        // Docker, can simply be run again), its token is revoked, or a day has passed. Each fetch gives the
        // connector a fresh token, so an older copy of the script stops working.
        var online = _relay.Snapshot().Connectors.Select(x => (x.Site.ToLowerInvariant(), x.TokenId)).ToHashSet();
        Store.EndInstalls(i => online.Contains((i.Site.ToLowerInvariant(), i.TokenId)));
        if (Store.TakeInstall(code) is not var (token, inst))
            return Results.Text("echo 'This install link no longer works: its connector is already online, it is over a day old, or its token was revoked. Make a new one in the admin console (Sites > Add connector).' >&2; exit 1\n",
                "text/x-shellscript", statusCode: 410);
        Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "connector.install", inst.Site, inst.Name, $"Connector install script for {inst.Name} fetched from {ClientIp(c)}"));
        c.Response.Headers.CacheControl = "no-store";
        return Results.Text(ConnectorSetup.Script(PublicUrl(c), token, inst.Name, _o.ConnectorImage, Store.Site(inst.Site)?.Resources), "text/x-shellscript");
    }

    // ---- what the console shows ----

    private object Snapshot(HttpContext c, Session me)
    {
        var live = _relay.Snapshot();
        var devices = Store.Devices;
        var sites = Store.Sites.Select(s =>
        {
            var online = live.Connectors.Where(x => x.Site.Equals(s.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var known = s.Connectors.Select(k =>
            {
                var on = online.FirstOrDefault(o => o.Name.Equals(k.Name, StringComparison.OrdinalIgnoreCase) && o.Key == k.Key);
                return new
                {
                    name = k.Name, fingerprint = SecureChannel.Fingerprint(Convert.FromBase64String(k.Key)), key = k.Key,
                    online = on is not null, since = on?.Since, active = on?.Active ?? 0,
                    from = on?.From ?? k.From, version = on?.Version ?? k.Version, allow = on?.Allow ?? k.Allow,
                    firstSeen = (DateTimeOffset?)k.FirstSeen, lastSeen = (DateTimeOffset?)k.LastSeen, tokenId = on?.TokenId,
                };
            }).ToList();
            // Online but not on file (the store couldn't be written): still show it.
            known.AddRange(online.Where(o => !s.Connectors.Any(k => k.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase) && k.Key == o.Key)).Select(o => new
            {
                name = o.Name, fingerprint = o.Fingerprint, key = o.Key, online = true, since = (DateTimeOffset?)o.Since, active = o.Active,
                from = o.From, version = o.Version, allow = o.Allow, firstSeen = (DateTimeOffset?)null, lastSeen = (DateTimeOffset?)null, tokenId = o.TokenId,
            }));
            var tokens = s.ConnectorTokens.Select(t => new { id = t.Id, name = t.Name, created = (DateTimeOffset?)t.Created, lastUsed = t.LastUsed }).ToList();
            if (s.TokenHash.Length > 0) tokens.Insert(0, new { id = "site", name = "Site token (gateway site add)", created = (DateTimeOffset?)s.Created, lastUsed = (DateTimeOffset?)null });
            return new
            {
                name = s.Name, description = s.Description, resources = s.Resources, resourceRules = s.Resources is null ? null : new AllowList(s.Resources).Rules,
                created = s.Created, online = online.Count > 0, connectors = known.OrderByDescending(k => k.online).ThenByDescending(k => k.lastSeen).ToList(),
                tokens, keys = s.Keys.Select(k => SecureChannel.Fingerprint(Convert.FromBase64String(k))).ToList(),
                devices = devices.Count(d => !d.Disabled && d.MayUse(s.Name)),
                tunnels = live.Live.Count(t => string.Equals(t.Site, s.Name, StringComparison.OrdinalIgnoreCase)),
            };
        }).ToList();
        return new
        {
            me = new { name = me.User, totp = Store.Admin(me.User)?.TotpSecret is not null },
            relay = new
            {
                version = _o.Version, url = PublicUrl(c), publicUrlSet = _o.PublicUrl is not null, started = live.Started,
                tunnels = live.Tunnels, tunnelsTotal = live.TunnelsTotal, bytes = live.Bytes, image = _o.ConnectorImage,
                consoleLimited = _o.Allow is not null,
            },
            sites,
            devices = devices.Select(d => new
            {
                name = d.Name, owner = d.Owner, sites = d.Sites, created = d.Created, disabled = d.Disabled, lastSeen = d.LastSeen, lastIp = d.LastIp,
                tunnels = live.Live.Count(t => string.Equals(t.Device, d.Name, StringComparison.OrdinalIgnoreCase)),
            }),
            invites = Store.Invites.Select(i => new { id = i.Id, owner = i.Owner, device = i.Device, sites = i.Sites, created = i.Created, expires = i.Expires, usesLeft = i.UsesLeft, createdBy = i.CreatedBy, usedBy = i.UsedBy }),
            admins = Store.Admins.Select(a => new { name = a.Name, created = a.Created, lastLogin = a.LastLogin, totp = a.TotpSecret is not null }),
            sessions = _sessions.Values.Where(x => x.User == me.User && Fresh(x)).Select(x => new { current = x.Id == me.Id, created = x.Created, lastSeen = x.LastSeen, ip = x.Ip, agent = x.Agent }),
            live = live.Live.Select(t => new { device = t.Device, site = t.Site, connector = t.Connector, from = t.From, started = t.Started, bytes = t.Bytes }),
            activity = Activity.Latest(12),
        };
    }

    private static string NextConnectorName(SiteRecord site)
    {
        var taken = site.ConnectorTokens.Select(t => t.Name).Concat(site.Connectors.Select(c => c.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = site.Name.Length > 36 ? site.Name[..36] : site.Name;
        for (var n = 1; ; n++) if (!taken.Contains($"{baseName}-{n}")) return $"{baseName}-{n}";
    }

    // ---- helpers ----

    /// <summary>PUBLIC_URL, or what the browser used to reach the console (through the proxy's forwarded headers).</summary>
    private string PublicUrl(HttpContext c)
    {
        if (_o.PublicUrl is { } p) return p.TrimEnd('/');
        var proto = c.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim() is { Length: > 0 } fp ? fp : c.Request.Scheme;
        var host = c.Request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim() is { Length: > 0 } fh ? fh : c.Request.Host.ToString();
        var prefix = c.Request.Headers["X-Forwarded-Prefix"].ToString().TrimEnd('/');
        return $"{proto}://{host}{prefix}{c.Request.PathBase}".TrimEnd('/');
    }

    private static bool IsHttps(HttpContext c) =>
        c.Request.IsHttps || c.Request.Headers["X-Forwarded-Proto"].ToString().StartsWith("https", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The caller's address. From a proxy on a private or loopback address, the last X-Forwarded-For entry (what the
    /// proxy itself saw; anything before it the client could have written). From anywhere else, the connection's address.
    /// </summary>
    public static string ClientIp(HttpContext c)
    {
        var remote = c.Connection.RemoteIpAddress;
        if (remote is not null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var xff = c.Request.Headers["X-Forwarded-For"].ToString();
        if (remote is not null && IsPrivate(remote) && xff.Length > 0 && xff.Split(',')[^1].Trim() is { Length: > 0 and <= 64 } last && IPAddress.TryParse(last, out _))
            return last;
        return remote?.ToString() ?? "?";
    }

    private static bool IsPrivate(IPAddress ip) => IPAddress.IsLoopback(ip) || new AllowList("private").Allows(ip, 1);

    private static async Task<JsonElement> Body(HttpContext c)
    {
        if (c.Request.ContentLength is > 64 * 1024) throw new ArgumentException("Too big.");
        try { return await JsonSerializer.DeserializeAsync<JsonElement>(c.Request.Body); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string? Str(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null;

    private static List<string>? Sites(JsonElement b) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty("sites", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : null;

    private static string? Q(Microsoft.Extensions.Primitives.StringValues v) => v.ToString() is { Length: > 0 } s ? s : null;
    private static string Trim(string s, int n) => s.Length > n ? s[..n] : s;
    private static IResult Ok() => Results.Json(new { ok = true });
    private static IResult Err(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    // ---- the console's files, embedded in the program ----

    private static readonly Assembly Self = typeof(AdminConsole).Assembly;

    private static IResult Page(HttpContext c, string file)
    {
        if (file.Contains("..") || file.Contains('/')) return Results.NotFound();
        var stream = Self.GetManifestResourceStream("web/" + file);
        if (stream is null) return Results.NotFound();
        var type = Path.GetExtension(file) switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml", _ => "application/octet-stream",
        };
        var h = c.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
        h.CacheControl = file.EndsWith(".html") ? "no-store" : "public, max-age=300";
        return Results.Stream(stream, type);
    }
}

/// <summary>Slows down guessing: five failures in 15 minutes lock a key (an address, a name) for 30 s, doubling up to 15 minutes.</summary>
internal sealed class Throttle
{
    private sealed class Entry { public int Fails; public DateTimeOffset First; public DateTimeOffset Until; }
    private readonly ConcurrentDictionary<string, Entry> _map = new();

    public TimeSpan? Locked(string key)
    {
        if (!_map.TryGetValue(key, out var e)) return null;
        lock (e) return e.Until > DateTimeOffset.UtcNow ? e.Until - DateTimeOffset.UtcNow : null;
    }

    public void Fail(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var e = _map.GetOrAdd(key, _ => new Entry { First = now });
        lock (e)
        {
            if (now - e.First > TimeSpan.FromMinutes(15) && e.Until < now) { e.Fails = 0; e.First = now; }
            e.Fails++;
            if (e.Fails >= 5) e.Until = now + TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, e.Fails - 5)));
        }
        if (_map.Count > 10_000) foreach (var kv in _map.Where(kv => kv.Value.Until < now && now - kv.Value.First > TimeSpan.FromMinutes(15))) _map.TryRemove(kv.Key, out _);
    }

    public void Reset(string key) => _map.TryRemove(key, out _);
}
