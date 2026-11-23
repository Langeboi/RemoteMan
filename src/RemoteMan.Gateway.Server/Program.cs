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
        case "site": return Site(args.Skip(1).ToArray());
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
    if (relay.Store.Sites.Count == 0) Log("No sites yet. Add one with: docker exec remoteman-relay gateway site add <name>");
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

int Site(string[] a)
{
    var store = new RelayStore(storePath);
    switch (a)
    {
        case ["add", var name]:
        {
            var token = store.AddSite(name);
            var relay = env("PUBLIC_URL", "https://<your relay>");
            Console.WriteLine($"""
                Site {name} added. Run the connector inside that network with this compose.yaml (docker compose up -d):

                services:
                  connector:
                    image: ghcr.io/langeboi/remoteman-gateway:test
                    container_name: remoteman-connector
                    command: connector
                    restart: unless-stopped
                    environment:
                      RELAY_URL: {relay}
                      SITE_TOKEN: {token}
                      NAME: {name}-1
                      # ALLOW: 10.0.0.0/8,192.168.0.0/16   # what it may open (default: all private ranges)
                    volumes:
                      - connector-data:/data
                volumes:
                  connector-data:

                The token is shown only now. Lost it? gateway site token {name}
                """);
            return 0;
        }
        case ["token", var name]:
            Console.WriteLine($"New token for {name} (the old one stops working):\n{store.NewSiteToken(name)}");
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
                Device {name} added ({(sites is null ? "all sites" : string.Join(", ", sites))}). In RemoteMan: More > Gateway settings, paste:

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
        gateway site add <name>                       new site; prints the connector's compose.yaml with its token
        gateway site list | remove <name> | token <name> | reset-key <name>
        gateway device add <name> [--sites a,b]       new device; prints the invite to paste into RemoteMan
        gateway device list | remove <name>
        gateway status                                connectors online and tunnels open
        gateway key                                   this connector's key fingerprint
        """);
    return 1;
}
