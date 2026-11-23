using System.Net.Http.Json;
using System.Text.Json;
using RemoteMan.Gateway;
using RemoteMan.Gateway.Server;

// remoteman-gateway: the RemoteMan gateway's relay and connector, and the relay's admin commands.
// Settings come from the environment (see deploy/gateway/README.md), so the same image serves both roles.

var env = (string name, string? fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
var dataDir = env("DATA", OperatingSystem.IsWindows() ? "data" : "/data")!;
var storePath = Path.Combine(dataDir, "relay.json");
var healthFile = Path.Combine(Path.GetTempPath(), "remoteman-gateway.health");
var port = env("PORT", "8080")!;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage();

try
{
    switch (args[0])
    {
        case "relay": return await RunRelayAsync();
        case "connector": return await RunConnectorAsync();
        case "site": return await SiteAsync(args.Skip(1).ToArray());
        case "device": return Device(args.Skip(1).ToArray());
        case "status": return await StatusAsync();
        case "health": return await HealthAsync();
        case "key":
        {
            using var k = Connector.LoadOrCreateKey(Path.Combine(dataDir, "connector.key"));
            Console.WriteLine($"Connector key fingerprint: {SecureChannel.Fingerprint(k.ExportSubjectPublicKeyInfo())}");
            return 0;
        }
        default: return Usage();
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

async Task<int> RunRelayAsync()
{
    var publicUrl = env("PUBLIC_URL", null);
    var relay = new Relay(new RelayStore(storePath), Log);
    var cert = env("TLS_CERT", null); var key = env("TLS_KEY", null);
    var urls = cert is not null ? $"https://0.0.0.0:{port}" : $"http://0.0.0.0:{port}";
    var app = RelayHost.Build(relay, urls, cert, key, publicUrl);
    Log($"RemoteMan gateway relay {Version()} listening on {urls}{(publicUrl is null ? "" : $", reached as {publicUrl}")}");
    if (publicUrl is null) Log("Set PUBLIC_URL (e.g. https://remoteman.example.com) so `gateway device add` can print ready invites.");
    if (relay.Store.Sites.Count == 0) Log("No sites yet. Add one (it prints the connector's setup line and waits for it): docker exec -it remoteman-relay gateway site add <name>");
    await app.RunAsync();
    return 0;
}

async Task<int> RunConnectorAsync()
{
    var relayUrl = env("RELAY_URL", null);
    var token = env("SITE_TOKEN", null);
    if (relayUrl is null || token is null || !Uri.TryCreate(relayUrl, UriKind.Absolute, out var relayUri))
    {
        Console.Error.WriteLine("Set RELAY_URL (https://your-relay) and SITE_TOKEN (from `gateway site add <name>` on the relay).");
        return 2;
    }
    var allow = new AllowList(env("ALLOW", null));
    using var key = Connector.LoadOrCreateKey(Path.Combine(dataDir, "connector.key"));
    var connector = new Connector(new ConnectorOptions
    {
        Relay = relayUri, SiteToken = token, Key = key, Allow = allow, Log = Log,
        Name = env("NAME", Environment.MachineName)!,
    });
    Log($"RemoteMan gateway connector {Version()}, key {connector.Fingerprint}, may open {allow}");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
    _ = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            try { File.WriteAllText(healthFile, connector.Connected ? "online" : "offline"); } catch { }
            try { await Task.Delay(5000, cts.Token); } catch { }
        }
    });
    await connector.RunAsync(cts.Token);
    return 0;
}

async Task<int> SiteAsync(string[] a)
{
    var store = new RelayStore(storePath);
    switch (a)
    {
        case ["add", var name, .. var opts]:
        {
            var token = store.AddSite(name);
            Console.WriteLine($"Site {name} added.\n");
            PrintConnectorSetup(name, token, opts.Contains("--compose"));
            if (opts.Contains("--no-wait")) return 0;
            return await WaitForConnectorAsync(name, offerDevice: true);
        }
        case ["wait", var name]:
            if (store.Site(name) is null) { Console.WriteLine($"No site called {name}."); return 1; }
            return await WaitForConnectorAsync(name, offerDevice: false);
        case ["token", var name, .. var opts]:
            Console.WriteLine($"New token for {name}; the old one stops working, so connectors still using it go offline.\n");
            PrintConnectorSetup(name, store.NewSiteToken(name), opts.Contains("--compose"));
            return 0;
        case ["remove", var name]:
            Console.WriteLine(store.RemoveSite(name) ? $"Removed {name}." : $"No site called {name}.");
            return 0;
        case ["reset-key", var name]:
            store.ResetKeys(name);
            Console.WriteLine($"Forgot {name}'s connector keys; the next connector to connect is recorded.");
            return 0;
        case ["list"] or []:
            if (store.Sites.Count == 0) Console.WriteLine("No sites. Add one: gateway site add <name>");
            foreach (var s in store.Sites)
                Console.WriteLine($"{s.Name,-20} keys: {(s.Keys.Count == 0 ? "none yet (no connector has connected)" : string.Join(", ", s.Keys.Select(k => SecureChannel.Fingerprint(Convert.FromBase64String(k)))))}");
            return 0;
        default:
            return Usage();
    }
}

// The copy-paste that sets up a connector: one docker run line (or a compose.yaml with --compose).
void PrintConnectorSetup(string name, string token, bool compose)
{
    var relay = env("PUBLIC_URL", null);
    var image = env("CONNECTOR_IMAGE", "ghcr.io/langeboi/remoteman-gateway:test")!;
    var relayText = relay ?? "https://<your relay>";
    if (relay is null) Console.WriteLine("(PUBLIC_URL isn't set on the relay, so replace https://<your relay> below with its address.)\n");
    if (compose)
    {
        Console.WriteLine($"""
            On a machine inside {name}'s network, save this as compose.yaml and run docker compose up -d:

            services:
              connector:
                image: {image}
                container_name: remoteman-connector
                command: connector
                restart: unless-stopped
                network_mode: host   # the machine's own DNS and routes, so internal names and subnets resolve
                environment:
                  RELAY_URL: {relayText}
                  SITE_TOKEN: {token}
                  NAME: {name}-1
                  # ALLOW: 10.0.0.0/8,192.168.0.0/16   # what it may open (default: all private ranges)
                volumes:
                  - connector-data:/data
            volumes:
              connector-data:

            """);
    }
    else
    {
        Console.WriteLine($"""
            On a machine inside {name}'s network (any Linux box or VM with Docker; nothing to open in its firewall), paste:

            docker run -d --name remoteman-connector --restart unless-stopped --network host -e RELAY_URL={relayText} -e SITE_TOKEN={token} -e NAME={name}-1 -v remoteman-connector:/data {image} connector

            Add -e ALLOW=10.0.0.0/8,192.168.1.0/24 to limit what it may open (default: all private ranges).
            For a second connector (failover), run the same line on another machine with NAME={name}-2.
            Prefer a compose.yaml? gateway site token {name} --compose  (makes a new token)

            """);
    }
    Console.WriteLine($"The token is shown only now. Lost it? gateway site token {name}");
}

// Watches the relay (in this container) until the site has a connector, then offers to set up a RemoteMan PC.
async Task<int> WaitForConnectorAsync(string name, bool offerDevice)
{
    using var http = LocalHttp(TimeSpan.FromSeconds(5));
    var url = $"{LocalScheme()}://127.0.0.1:{port}{GatewayProtocol.StatusPath}";
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.WriteLine();
    Console.Write($"Waiting for {name}'s connector to come online (Ctrl+C to stop waiting; the connector can come later) ");
    var frames = "|/-\\";
    var at = DateTime.UtcNow;
    var i = 0;
    while (!cts.IsCancellationRequested)
    {
        JsonElement json;
        try { json = await http.GetFromJsonAsync<JsonElement>(url, cts.Token); }
        catch (OperationCanceledException) { break; }
        catch (HttpRequestException)
        {
            Console.WriteLine($"\nThe relay isn't running in this container, so I can't watch for the connector. Run this where the relay runs: gateway site wait {name}");
            return 1;
        }
        var site = json.GetProperty("sites").EnumerateArray().FirstOrDefault(s => string.Equals(s.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        if (site.ValueKind == JsonValueKind.Object && site.GetProperty("connectors").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } c)
        {
            var from = c.TryGetProperty("from", out var f) && f.GetString() is { } fs ? $" from {fs}" : "";
            Console.WriteLine($"\r\n✓ {name} is online: connector {c.GetProperty("name").GetString()}{from}, key {c.GetProperty("key").GetString()?[..16]}");
            if (offerDevice) OfferDevice(name);
            else Console.WriteLine($"RemoteMan reaches {name} now. New PC? gateway device add <pc-name>");
            return 0;
        }
        if (!Console.IsOutputRedirected) Console.Write($"\b{frames[i++ % frames.Length]}");
        if (DateTime.UtcNow - at > TimeSpan.FromMinutes(2) && i % 60 == 0)
            Console.Write($"\r\nStill nothing. Check the connector's log: docker logs remoteman-connector  ");
        try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
    }
    Console.WriteLine($"\nStopped waiting. The connector can still connect; check with: gateway site wait {name}");
    return 0;
}

void OfferDevice(string site)
{
    var store = new RelayStore(storePath);
    if (store.Devices.Count > 0)
    {
        Console.WriteLine($"PCs that already have an invite for all sites reach {site} now. Another PC? gateway device add <pc-name>");
        return;
    }
    if (Console.IsInputRedirected)
    {
        Console.WriteLine("\nNext, give RemoteMan an invite: gateway device add <pc-name>");
        return;
    }
    Console.Write("\nNext, RemoteMan. Name the PC to invite (e.g. laptop), or press Enter to skip: ");
    var pc = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(pc)) { Console.WriteLine("Later: gateway device add <pc-name>"); return; }
    Device(["add", pc]);
}

int Device(string[] a)
{
    var store = new RelayStore(storePath);
    switch (a)
    {
        case ["add", var name, ..]:
        {
            var sites = a.Length >= 4 && a[2] == "--sites" ? a[3].Split(',') : null;
            var token = store.AddDevice(name, sites);
            var url = env("PUBLIC_URL", null);
            if (url is null)
            {
                Console.WriteLine($"Device {name} added. Token: {token}\nSet PUBLIC_URL on the relay to get a one-line invite instead.");
                return 0;
            }
            Console.WriteLine($"""
                Device {name} added ({(sites is null ? "all sites" : string.Join(", ", sites))}). In RemoteMan: More > Gateway settings, paste the invite and Save:

                {new GatewayInvite(url, token, name).Encode()}

                Use it on that one PC only. It's shown only now; `gateway device remove {name}` revokes it.
                """);
            return 0;
        }
        case ["remove", var name]:
            Console.WriteLine(store.RemoveDevice(name) ? $"Removed {name}; it can't use the gateway any more." : $"No device called {name}.");
            return 0;
        case ["list"] or []:
            if (store.Devices.Count == 0) Console.WriteLine("No devices. Add one: gateway device add <name> [--sites a,b]");
            foreach (var d in store.Devices)
                Console.WriteLine($"{d.Name,-20} {string.Join(",", d.Sites),-24} added {d.Created:yyyy-MM-dd}");
            return 0;
        default:
            return Usage();
    }
}

async Task<int> StatusAsync()
{
    using var http = LocalHttp(TimeSpan.FromSeconds(5));
    var scheme = LocalScheme();
    JsonElement json;
    try { json = await http.GetFromJsonAsync<JsonElement>($"{scheme}://127.0.0.1:{port}{GatewayProtocol.StatusPath}"); }
    catch (HttpRequestException)
    {
        Console.Error.WriteLine("The relay isn't running in this container (status only works where `relay` runs).");
        return 1;
    }
    Console.WriteLine($"Tunnels open: {json.GetProperty("tunnels")}, since start: {json.GetProperty("tunnelsTotal")}, {json.GetProperty("bytes").GetInt64() / 1048576.0:0.0} MB relayed");
    foreach (var s in json.GetProperty("sites").EnumerateArray())
    {
        var cs = s.GetProperty("connectors").EnumerateArray().ToList();
        Console.WriteLine($"{s.GetProperty("name").GetString(),-20} {(cs.Count == 0 ? "offline" : string.Join(", ", cs.Select(c => $"{c.GetProperty("name").GetString()} ({c.GetProperty("active")} open)")))}");
    }
    return 0;
}

async Task<int> HealthAsync()
{
    // A connector (RELAY_URL set) is healthy while it's connected to the relay; a relay while it answers.
    if (env("RELAY_URL", null) is not null) return File.Exists(healthFile) && File.ReadAllText(healthFile).Trim() == "online" ? 0 : 1;
    using var http = LocalHttp(TimeSpan.FromSeconds(3));
    try { return (await http.GetAsync($"{LocalScheme()}://127.0.0.1:{port}/healthz")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

string LocalScheme() => env("TLS_CERT", null) is null ? "http" : "https";

// Status and health talk to the relay in the same container; with TLS on, its certificate names the public host, not 127.0.0.1.
static HttpClient LocalHttp(TimeSpan timeout) => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = timeout };

static string Version() =>
    Environment.GetEnvironmentVariable("REMOTEMAN_GATEWAY_VERSION") is { Length: > 0 } v && v != "dev" ? v
    : (System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "dev");

static void Log(string message) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");

static int Usage()
{
    Console.WriteLine("""
        remoteman-gateway relay                       run the relay (PORT, DATA, PUBLIC_URL, TLS_CERT/TLS_KEY)
        remoteman-gateway connector                   run a connector (RELAY_URL, SITE_TOKEN, ALLOW, NAME, DATA)
        gateway site add <name> [--compose|--no-wait] new site: prints the connector's docker run line, waits for it
                                                      to come online, then offers an invite for a RemoteMan PC
        gateway site wait <name>                      wait until the site's connector is online
        gateway site list | remove <name> | token <name> [--compose] | reset-key <name>
        gateway device add <name> [--sites a,b]       new device; prints the invite to paste into RemoteMan
        gateway device list | remove <name>
        gateway status                                connectors online and tunnels open
        gateway key                                   this connector's key fingerprint
        """);
    return 1;
}
