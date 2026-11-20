using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Ssh;

/// <summary>One jump host (ProxyJump) on the way to the target: its hosts in fallback order and its own login.</summary>
public sealed record SshHop(string Name, IReadOnlyList<HostEndpoint> Endpoints, ResolvedCredential Credential, SshSettings Settings);

/// <summary>
/// One interactive SSH shell. SSH.NET covers old and new servers out of the box:
/// key exchange from mlkem768x25519 / sntrup761 (post-quantum) and curve25519 / ECDH down to
/// DH group14-sha1 and group1-sha1; ed25519 / ECDSA / rsa-sha2 / ssh-rsa host keys and OpenSSH
/// certificates; AES-GCM / ChaCha20 / AES-CTR / AES-CBC / 3DES ciphers; password,
/// keyboard-interactive and public key auth (OpenSSH, PKCS#1/#8 PEM and PuTTY .ppk keys).
/// Legacy algorithms are removed from the offer when the entry disables them.
/// </summary>
public sealed class SshSession : IDisposable
{
    private static readonly string[] LegacyKex = ["diffie-hellman-group1-sha1", "diffie-hellman-group-exchange-sha1", "diffie-hellman-group14-sha1"];
    private static readonly string[] LegacyCiphers = ["3des-cbc", "aes128-cbc", "aes192-cbc", "aes256-cbc", "blowfish-cbc", "twofish-cbc", "cast128-cbc", "arcfour", "arcfour128", "arcfour256"];
    private static readonly string[] LegacyMacs = ["hmac-sha1", "hmac-sha1-etm@openssh.com", "hmac-md5", "hmac-md5-96", "hmac-sha1-96"];
    private static readonly string[] LegacyHostKeys = ["ssh-rsa", "ssh-rsa-cert-v01@openssh.com", "ssh-dss"];

    private SshClient? _client;
    private ShellStream? _shell;
    private ConnectionInfo? _info;
    private readonly List<SshClient> _jumps = new();
    private readonly List<ForwardedPortLocal> _tunnels = new();

    public event Action<string>? Output;
    public event Action<string>? Info;
    public event Action<Exception?>? Closed;

    public string? NegotiatedSummary { get; private set; }

    public Task ConnectAsync(HostEndpoint endpoint, ResolvedCredential cred, SshSettings settings, KnownHosts knownHosts,
        uint cols, uint rows, CancellationToken ct = default) =>
        ConnectAsync([endpoint], [], cred, settings, knownHosts, cols, rows, ct);

    /// <summary>
    /// Connects to the first target endpoint that answers. With jump hosts, each hop is reached
    /// through a local forward on the hop before it (ProxyJump), and every hop has its own login,
    /// host key check and fallback hosts.
    /// </summary>
    public async Task ConnectAsync(IReadOnlyList<HostEndpoint> endpoints, IReadOnlyList<SshHop> jumps, ResolvedCredential cred, SshSettings settings,
        KnownHosts knownHosts, uint cols, uint rows, CancellationToken ct = default)
    {
        SshClient? via = null;
        foreach (var hop in jumps)
        {
            Info?.Invoke($"Jump host {hop.Name}…");
            var (jc, jep, _) = await ConnectHopAsync(via, hop.Endpoints, hop.Credential, hop.Settings, knownHosts, ct);
            _jumps.Add(jc);
            Info?.Invoke($"Via {hop.Name} ({jep}).");
            via = jc;
        }

        var (client, ep, info) = await ConnectHopAsync(via, endpoints, cred, settings, knownHosts, ct);
        _client = client;
        _info = info;
        ConnectedEndpoint = ep;
        _client.ErrorOccurred += (_, e) => Closed?.Invoke(e.Exception);
        NegotiatedSummary = $"{_client.ConnectionInfo.CurrentKeyExchangeAlgorithm} / {_client.ConnectionInfo.CurrentServerEncryption} / {_client.ConnectionInfo.CurrentHostKeyAlgorithm}";

        _shell = _client.CreateShellStream(settings.TerminalType ?? "xterm-256color", cols, rows, 0, 0, 64 * 1024);
        _shell.DataReceived += (_, e) => Output?.Invoke(Encoding.UTF8.GetString(e.Data));
        _shell.Closed += (_, _) => Closed?.Invoke(null);

        if (!string.IsNullOrWhiteSpace(settings.StartupCommand)) Send(settings.StartupCommand + "\n");
    }

    /// <summary>The endpoint the shell actually reached (the real host, also when tunnelled).</summary>
    public HostEndpoint? ConnectedEndpoint { get; private set; }

    private async Task<(SshClient Client, HostEndpoint Endpoint, ConnectionInfo Info)> ConnectHopAsync(SshClient? via,
        IReadOnlyList<HostEndpoint> endpoints, ResolvedCredential cred, SshSettings settings, KnownHosts knownHosts, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var raw in endpoints.Where(e => !string.IsNullOrWhiteSpace(e.Host)))
        {
            var ep = raw.Port > 0 ? raw : raw with { Port = 22 };
            var dial = ep;
            ForwardedPortLocal? fwd = null;
            if (via is not null)
            {
                // Listen on a free loopback port and carry it over the previous hop (direct-tcpip).
                fwd = new ForwardedPortLocal("127.0.0.1", 0, ep.Host, (uint)ep.Port);
                via.AddForwardedPort(fwd);
                fwd.Start();
                dial = new HostEndpoint("127.0.0.1", (int)fwd.BoundPort);
            }
            var info = BuildConnectionInfo(dial, cred, settings);
            var client = new SshClient(info);
            if (settings.KeepAliveSeconds > 0) client.KeepAliveInterval = TimeSpan.FromSeconds(settings.KeepAliveSeconds);
            // Host keys are checked against the real host name, not the loopback forward.
            client.HostKeyReceived += (_, e) =>
            {
                switch (knownHosts.Check(ep.Host, ep.Port, e.HostKeyName, e.FingerPrintSHA256))
                {
                    case HostKeyVerdict.New:
                        Info?.Invoke($"Host key {e.HostKeyName} SHA256:{e.FingerPrintSHA256} for {ep} accepted automatically (first use, saved).");
                        e.CanTrust = settings.AutoAcceptHostKey;
                        break;
                    case HostKeyVerdict.Changed:
                        Info?.Invoke($"WARNING: host key for {ep} changed, now {e.HostKeyName} SHA256:{e.FingerPrintSHA256}.");
                        e.CanTrust = settings.AutoAcceptHostKey;
                        break;
                    default:
                        e.CanTrust = true;
                        break;
                }
            };
            try
            {
                await client.ConnectAsync(ct);
                if (fwd is not null) _tunnels.Add(fwd);
                return (client, ep, info);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                client.Dispose();
                if (fwd is not null) { fwd.Stop(); via!.RemoveForwardedPort(fwd); fwd.Dispose(); }
                if (endpoints.Count > 1) Info?.Invoke($"{ep}: {ex.Message}");
            }
        }
        throw new SshConnectionException(last is null ? "No host to connect to." : $"No host answered: {last.Message}");
    }

    /// <summary>Login methods and algorithm offer for an entry; shared by the shell, SFTP and port tunnels.</summary>
    public static ConnectionInfo BuildConnectionInfo(HostEndpoint endpoint, ResolvedCredential cred, SshSettings settings)
    {
        var user = cred.UserName ?? Environment.UserName;
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrEmpty(cred.PrivateKey))
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(cred.PrivateKey));
            var key = string.IsNullOrEmpty(cred.Passphrase) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, cred.Passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        if (cred.Password is { } pw)
        {
            methods.Add(new PasswordAuthenticationMethod(user, pw));
            // Many network devices and PAM setups only offer keyboard-interactive.
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = pw; };
            methods.Add(kbd);
        }
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(user));

        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (!settings.AllowLegacyAlgorithms)
        {
            foreach (var k in LegacyKex) info.KeyExchangeAlgorithms.Remove(k);
            foreach (var c in LegacyCiphers) info.Encryptions.Remove(c);
            foreach (var m in LegacyMacs) info.HmacAlgorithms.Remove(m);
            foreach (var h in LegacyHostKeys) info.HostKeyAlgorithms.Remove(h);
        }
        return info;
    }

    /// <summary>
    /// Opens an SFTP channel on a second connection with the same login and algorithms, so file
    /// transfers never stall the terminal. The host key was already verified by the shell.
    /// </summary>
    public async Task<SftpBrowser> OpenSftpAsync(CancellationToken ct = default)
    {
        if (_info is null || _client is not { IsConnected: true }) throw new InvalidOperationException("SSH session is not connected.");
        var sftp = new SftpClient(_info) { OperationTimeout = TimeSpan.FromSeconds(30) };
        sftp.HostKeyReceived += (_, e) => e.CanTrust = true;
        await sftp.ConnectAsync(ct);
        return new SftpBrowser(sftp);
    }

    public void Send(string data)
    {
        if (_shell is null) return;
        _shell.Write(data);
        _shell.Flush();
    }

    public void Resize(uint cols, uint rows) => _shell?.ChangeWindowSize(cols, rows, 0, 0);

    public void Dispose()
    {
        _shell?.Dispose();
        if (_client is { IsConnected: true }) _client.Disconnect();
        _client?.Dispose();
        // Tear down jump hosts last hop first.
        foreach (var t in _tunnels) { try { t.Stop(); } catch (ObjectDisposedException) { } t.Dispose(); }
        for (var i = _jumps.Count - 1; i >= 0; i--)
        {
            if (_jumps[i].IsConnected) _jumps[i].Disconnect();
            _jumps[i].Dispose();
        }
        _tunnels.Clear();
        _jumps.Clear();
    }
}
