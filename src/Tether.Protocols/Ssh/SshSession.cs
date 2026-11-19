using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Tether.Core.Model;

namespace Tether.Protocols.Ssh;

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

    public event Action<string>? Output;
    public event Action<string>? Info;
    public event Action<Exception?>? Closed;

    public string? NegotiatedSummary { get; private set; }

    public async Task ConnectAsync(HostEndpoint endpoint, ResolvedCredential cred, SshSettings settings, KnownHosts knownHosts,
        uint cols, uint rows, CancellationToken ct = default)
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

        _info = info;
        _client = new SshClient(info);
        if (settings.KeepAliveSeconds > 0) _client.KeepAliveInterval = TimeSpan.FromSeconds(settings.KeepAliveSeconds);

        _client.HostKeyReceived += (_, e) =>
        {
            var verdict = knownHosts.Check(endpoint.Host, endpoint.Port, e.HostKeyName, e.FingerPrintSHA256);
            switch (verdict)
            {
                case HostKeyVerdict.New:
                    Info?.Invoke($"Host key {e.HostKeyName} SHA256:{e.FingerPrintSHA256} accepted automatically (first use, saved).");
                    e.CanTrust = settings.AutoAcceptHostKey;
                    break;
                case HostKeyVerdict.Changed:
                    Info?.Invoke($"WARNING: host key for {endpoint} changed, now {e.HostKeyName} SHA256:{e.FingerPrintSHA256}.");
                    e.CanTrust = settings.AutoAcceptHostKey;
                    break;
                default:
                    e.CanTrust = true;
                    break;
            }
        };
        _client.ErrorOccurred += (_, e) => Closed?.Invoke(e.Exception);

        await _client.ConnectAsync(ct);
        NegotiatedSummary = $"{_client.ConnectionInfo.CurrentKeyExchangeAlgorithm} / {_client.ConnectionInfo.CurrentServerEncryption} / {_client.ConnectionInfo.CurrentHostKeyAlgorithm}";

        _shell = _client.CreateShellStream(settings.TerminalType ?? "xterm-256color", cols, rows, 0, 0, 64 * 1024);
        _shell.DataReceived += (_, e) => Output?.Invoke(Encoding.UTF8.GetString(e.Data));
        _shell.Closed += (_, _) => Closed?.Invoke(null);

        if (!string.IsNullOrWhiteSpace(settings.StartupCommand)) Send(settings.StartupCommand + "\n");
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
    }
}
