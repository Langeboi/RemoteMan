using System.Collections.Concurrent;

namespace RemoteMan.Gateway;

/// <summary>
/// What this process is doing through the gateway right now, for RemoteMan's status box: tunnels being opened
/// and open per site, and bytes each way. Only RemoteMan's side counts (the connector runs in its own process).
/// </summary>
public static class GatewayActivity
{
    private static readonly ConcurrentDictionary<string, int> OpenBySite = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> OpeningBySite = new(StringComparer.OrdinalIgnoreCase);
    private static long _in, _out;

    /// <summary>A tunnel started opening, opened or closed (not raised per byte).</summary>
    public static event Action? Changed;

    public static long BytesIn => Interlocked.Read(ref _in);
    public static long BytesOut => Interlocked.Read(ref _out);
    public static int OpenTunnels => OpenBySite.Values.Sum();
    public static int Opening => OpeningBySite.Values.Sum();
    public static int OpenFor(string site) => OpenBySite.TryGetValue(site, out var n) ? n : 0;
    /// <summary>The site of the most recent tunnel (opening or opened).</summary>
    public static string? LastSite { get; private set; }

    internal static IDisposable BeginOpening(string site) => Track(OpeningBySite, site);
    internal static IDisposable BeginTunnel(string site) => Track(OpenBySite, site);
    internal static void Received(int n) => Interlocked.Add(ref _in, n);
    internal static void Sent(int n) => Interlocked.Add(ref _out, n);

    private static IDisposable Track(ConcurrentDictionary<string, int> map, string site)
    {
        LastSite = site;
        map.AddOrUpdate(site, 1, (_, n) => n + 1);
        Changed?.Invoke();
        return new Release(() =>
        {
            map.AddOrUpdate(site, 0, (_, n) => Math.Max(0, n - 1));
            Changed?.Invoke();
        });
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) release(); }
    }
}
