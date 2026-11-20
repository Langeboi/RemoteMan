using System.Net;
using Renci.SshNet;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Ssh;

/// <summary>
/// A local port forward through an SSH entry: 127.0.0.1:(random) on this machine reaches
/// <c>target</c> as seen from the SSH server. Used to reach a Zabbix API that only the jump host can see.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _port;

    private SshTunnel(SshClient client, ForwardedPortLocal port)
    {
        _client = client;
        _port = port;
    }

    public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, (int)_port.BoundPort);
    public bool IsConnected => _client.IsConnected && _port.IsStarted;

    public static async Task<SshTunnel> OpenAsync(HostEndpoint jumpHost, ResolvedCredential cred, SshSettings settings, KnownHosts knownHosts,
        string targetHost, int targetPort, CancellationToken ct = default)
    {
        var info = SshSession.BuildConnectionInfo(jumpHost, cred, settings);
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(15, settings.KeepAliveSeconds)) };
        client.HostKeyReceived += (_, e) =>
        {
            var verdict = knownHosts.Check(jumpHost.Host, jumpHost.Port, e.HostKeyName, e.FingerPrintSHA256);
            e.CanTrust = verdict == HostKeyVerdict.Match || settings.AutoAcceptHostKey;
        };
        try
        {
            await client.ConnectAsync(ct);
            var port = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(port);
            port.Start();
            return new SshTunnel(client, port);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { if (_port.IsStarted) _port.Stop(); } catch { }
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        _client.Dispose();
    }
}
