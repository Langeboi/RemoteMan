using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteMan.Gateway.Server.Admin;

namespace RemoteMan.Gateway.Server;

/// <summary>The relay's web server: plain HTTP behind Caddy (or Cloudflare Tunnel), or HTTPS with your own certificate.</summary>
public static class RelayHost
{
    public static WebApplication Build(Relay relay, string urls, string? certPem = null, string? keyPem = null, string? publicUrl = null, AdminOptions? admin = null) =>
        Build(relay, urls, certPem, keyPem, publicUrl, admin, out _);

    public static WebApplication Build(Relay relay, string urls, string? certPem, string? keyPem, string? publicUrl, AdminOptions? admin, out AdminConsole console)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(urls);
        // Tunnels live for hours; waiting for them on shutdown only delays `docker compose up -d`.
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));
        if (certPem is not null && keyPem is not null)
        {
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPem, keyPem);
            builder.WebHost.UseKestrelHttpsConfiguration();
            builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = cert));
        }
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = GatewayProtocol.KeepAliveInterval, KeepAliveTimeout = GatewayProtocol.KeepAliveTimeout });

        console = new AdminConsole(relay, admin ?? new AdminOptions { PublicUrl = publicUrl });
        // The root says nothing about what this is or where the console lives: a bare 404 like any unknown path.
        app.MapGet("/", () => Results.NotFound());
        console.Map(app);
        app.MapGet("/healthz", () => Results.Text("ok\n"));

        app.MapGet(GatewayProtocol.StatusPath, (HttpContext c) =>
            c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) && !c.Request.Headers.ContainsKey("X-Forwarded-For")
                ? Results.Json(relay.Status(), GatewayProtocol.Json) : Results.StatusCode(403));

        app.MapGet(GatewayProtocol.SitesPath, (HttpContext c) =>
        {
            if (relay.Store.DeviceByToken(Bearer(c)) is not { } device) return Results.StatusCode(401);
            relay.Store.TouchDevice(device.Name, AdminConsole.ClientIp(c));
            return Results.Json(relay.SitesFor(device).ToList(), GatewayProtocol.Json);
        });

        app.Map(GatewayProtocol.ConnectorPath, async (HttpContext c) =>
        {
            if (relay.Store.SiteAndTokenByToken(Bearer(c)) is not var (site, token)) { c.Response.StatusCode = 401; return; }
            if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
            var key = c.Request.Headers[GatewayProtocol.KeyHeader].ToString();
            if (!ValidKey(key)) { c.Response.StatusCode = 400; return; }
            var name = c.Request.Headers[GatewayProtocol.NameHeader].ToString() is { Length: > 0 and <= 64 } n ? n : "connector";
            using var ws = await c.WebSockets.AcceptWebSocketAsync();
            // Behind a reverse proxy the connector's address is the first X-Forwarded-For entry; it's only shown, never trusted.
            var from = c.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim() is { Length: > 0 and <= 64 } fwd ? fwd : c.Connection.RemoteIpAddress?.ToString();
            var version = Header(c, GatewayProtocol.ConnectorVersionHeader, 64);
            var allow = Header(c, GatewayProtocol.AllowHeader, 2000);
            await relay.ServeConnectorAsync(site, name, key, ws, c.RequestAborted, from, version, allow, token?.Id);
        });

        app.Map(GatewayProtocol.AcceptPath + "{id}", async (HttpContext c, string id) =>
        {
            if (relay.Store.SiteByToken(Bearer(c)) is not { } site) { c.Response.StatusCode = 401; return; }
            if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
            using var ws = await c.WebSockets.AcceptWebSocketAsync();
            await relay.ServeAcceptAsync(site, id, ws);
        });

        app.Map(GatewayProtocol.ConnectPath, async (HttpContext c) =>
        {
            if (relay.Store.DeviceByToken(Bearer(c)) is not { } device) { c.Response.StatusCode = 401; return; }
            if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
            var siteName = c.Request.Query["site"].ToString();
            if (relay.Store.Site(siteName) is not { } site) { c.Response.StatusCode = 404; return; }
            if (!device.MayUse(site.Name)) { c.Response.StatusCode = 403; return; }
            if (!relay.IsOnline(site.Name)) { c.Response.StatusCode = 503; return; }
            // Pair with the connector first, so a site that doesn't answer is an HTTP error RemoteMan can show.
            if (await relay.RequestTunnelFromAsync(site.Name, c.RequestAborted) is not var (connector, done, connectorName)) { c.Response.StatusCode = 504; return; }
            try
            {
                var ip = AdminConsole.ClientIp(c);
                relay.Store.TouchDevice(device.Name, ip);
                using var ws = await c.WebSockets.AcceptWebSocketAsync();
                await relay.SpliceAsync(ws, connector, done, c.RequestAborted, new TunnelInfo { Device = device.Name, Site = site.Name, Connector = connectorName, From = ip });
            }
            finally { done.TrySetResult(); }
        });

        return app;
    }

    private static string? Header(HttpContext c, string name, int max) =>
        c.Request.Headers[name].ToString() is { Length: > 0 } v && v.Length <= max ? v : null;

    private static string? Bearer(HttpContext c)
    {
        var h = c.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    private static bool ValidKey(string b64)
    {
        try
        {
            using var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b64), out _);
            return k.KeySize == 256;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }
}
