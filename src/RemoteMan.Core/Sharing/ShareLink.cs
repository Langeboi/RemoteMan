using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace RemoteMan.Core.Sharing;

/// <summary>
/// One TLS connection in a shared session with a send queue, so a slow peer never holds up the
/// others: frames are queued and written by one loop per link.
/// </summary>
public sealed class ShareLink : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private int _closed;

    public ShareLink(Stream stream)
    {
        _stream = stream;
        _writer = Task.Run(WriteLoopAsync);
    }

    public CancellationToken Closing => _cts.Token;
    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    public void Send(byte[] frame)
    {
        if (!IsClosed) _out.Writer.TryWrite(frame);
    }

    public Task<(ShareFrame Type, byte[] Payload)?> ReadAsync() => ShareWire.ReadAsync(_stream, _cts.Token);

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(_cts.Token))
            {
                await _stream.WriteAsync(frame, _cts.Token);
                // Batch whatever else is already queued before flushing.
                while (_out.Reader.TryRead(out var more)) await _stream.WriteAsync(more, _cts.Token);
                await _stream.FlushAsync(_cts.Token);
            }
        }
        catch (Exception) { Close(); }
    }

    /// <summary>Sends what is queued (briefly), then closes.</summary>
    public async Task CloseAfterAsync(byte[] last)
    {
        Send(last);
        _out.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        Close();
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _out.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await _writer; } catch (Exception) { }
        _cts.Dispose();
    }
}

/// <summary>Helpers for the network side of sharing: certificate, addresses, relay handshake.</summary>
public static class ShareNet
{
    /// <summary>Default port the host listens on (falls back to any free port).</summary>
    public const int DefaultPort = 47110;
    /// <summary>Default port of the relay on the database server.</summary>
    public const int RelayPort = 47120;

    /// <summary>A throwaway self-signed certificate for one share. Guests pin its SHA-256, which travels in the invite.</summary>
    public static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=RemoteMan shared session", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        // Windows' TLS stack can't use an ephemeral key directly; a PFX round trip gives it a usable one.
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    public static string Thumbprint(X509Certificate c) => Convert.ToHexString(SHA256.HashData(c.GetRawCertData()));

    /// <summary>This machine's usable IPv4 addresses: LAN and VPN adapters that are up, real ones first.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var list = new List<(IPAddress Ip, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                // Adapters with a gateway (the LAN) first, then VPN/overlay adapters, which often have none.
                var rank = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)) ? 0 : 1;
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                    if (a.Address.GetAddressBytes() is [169, 254, ..]) continue;   // link-local, never reachable
                    list.Add((a.Address, rank));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
    }

    public static bool TryParseEndpoint(string text, int defaultPort, out string host, out int port)
    {
        host = text.Trim(); port = defaultPort;
        if (host.Length == 0) return false;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var p) && p is > 0 and < 65536)
        {
            port = p; host = host[..colon];
        }
        else if (host.StartsWith('[') && host.Contains("]:") && int.TryParse(host[(host.IndexOf("]:") + 2)..], out var p6))
        {
            port = p6; host = host[1..host.IndexOf(']')];
        }
        return host.Length > 0;
    }

    public static async Task<TcpClient> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tcp = new TcpClient { NoDelay = true };
        try { await tcp.ConnectAsync(host, port, cts.Token); return tcp; }
        catch { tcp.Dispose(); throw; }
    }

    /// <summary>Writes one ASCII line (relay handshake).</summary>
    public static Task WriteLineAsync(Stream s, string line, CancellationToken ct) =>
        s.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), ct).AsTask();

    /// <summary>Reads one ASCII line byte by byte, so nothing after it is consumed (the TLS handshake follows).</summary>
    public static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct, int max = 200)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < max)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
        throw new InvalidDataException("Line too long.");
    }

    public static async Task<SslStream> ClientTlsAsync(Stream raw, string thumbprint, CancellationToken ct)
    {
        var ssl = new SslStream(raw, false, (_, cert, _, _) => cert is not null && string.Equals(Thumbprint(cert), thumbprint, StringComparison.OrdinalIgnoreCase));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "remoteman-share" }, ct);
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }
}
