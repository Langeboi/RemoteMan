using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteMan.Gateway.Server;

/// <summary>The relay's web server: plain HTTP behind Caddy (or Cloudflare Tunnel), or HTTPS with your own certificate.</summary>
public static class RelayHost
{
    public static WebApplication Build(Relay relay, string urls, string? certPem = null, string? keyPem = null, string? publicUrl = null)
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
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(30) });

        app.MapGet("/", () => Results.Text($"RemoteMan gateway relay{(publicUrl is null ? "" : " for " + publicUrl)}. Nothing to see here; RemoteMan and connectors use /v1.\n"));
        app.MapGet("/healthz", () => Results.Text("ok\n"));

        app.MapGet(GatewayProtocol.StatusPath, (HttpContext c) =>
            c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) && !c.Request.Headers.ContainsKey("X-Forwarded-For")
                ? Results.Json(relay.Status(), GatewayProtocol.Json) : Results.StatusCode(403));

        app.MapGet(GatewayProtocol.SitesPath, (HttpContext c) =>
            relay.Store.DeviceByToken(Bearer(c)) is { } device
                ? Results.Json(relay.SitesFor(device).ToList(), GatewayProtocol.Json)
                : Results.StatusCode(401));

        app.Map(GatewayProtocol.ConnectorPath, async (HttpContext c) =>
        {
            if (relay.Store.SiteByToken(Bearer(c)) is not { } site) { c.Response.StatusCode = 401; return; }
            if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
            var key = c.Request.Headers[GatewayProtocol.KeyHeader].ToString();
            if (!ValidKey(key)) { c.Response.StatusCode = 400; return; }
            var name = c.Request.Headers[GatewayProtocol.NameHeader].ToString() is { Length: > 0 and <= 64 } n ? n : "connector";
            using var ws = await c.WebSockets.AcceptWebSocketAsync();
            await relay.ServeConnectorAsync(site, name, key, ws, c.RequestAborted);
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
            if (await relay.RequestTunnelAsync(site.Name, c.RequestAborted) is not var (connector, done)) { c.Response.StatusCode = 504; return; }
            try
            {
                using var ws = await c.WebSockets.AcceptWebSocketAsync();
                await relay.SpliceAsync(ws, connector, done, c.RequestAborted);
            }
            finally { done.TrySetResult(); }
        });

        return app;
    }

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
