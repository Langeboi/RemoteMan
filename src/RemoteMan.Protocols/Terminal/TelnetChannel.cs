using System.Net.Sockets;
using System.Text;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Terminal;

/// <summary>
/// Telnet client (RFC 854) with the few options devices care about: echo, suppress go-ahead,
/// terminal type and window size. Signs in by answering the login and password prompts when
/// the entry has a stored login.
/// </summary>
public sealed class TelnetChannel : ITerminalChannel
{
    private const byte Iac = 255, Dont = 254, Do = 253, Wont = 252, Will = 251, Sb = 250, Se = 240;
    private const byte OptEcho = 1, OptSga = 3, OptTtype = 24, OptNaws = 31;

    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private readonly CancellationTokenSource _cts = new();
    private Encoding _encoding = new UTF8Encoding(false);
    private Decoder _decoder = new UTF8Encoding(false).GetDecoder();

    /// <summary>Character set of the remote side (set before connecting).</summary>
    public Encoding Encoding { get => _encoding; set { _encoding = value; _decoder = value.GetDecoder(); } }
    private uint _cols = 80, _rows = 24;
    private bool _naws;
    private string? _user, _password;
    private readonly StringBuilder _recent = new();

    public event Action<string>? Output;
    public event Action<string>? Info;
    public event Action<Exception?>? Closed;

    public async Task ConnectAsync(HostEndpoint endpoint, ResolvedCredential cred, uint cols, uint rows, CancellationToken ct = default)
    {
        _cols = cols; _rows = rows;
        _user = string.IsNullOrEmpty(cred.UserName) ? null : cred.UserName;
        _password = cred.Password;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _tcp.ConnectAsync(endpoint.Host, endpoint.Port > 0 ? endpoint.Port : 23, timeout.Token);
        _tcp.NoDelay = true;
        _stream = _tcp.GetStream();
        _ = Task.Run(ReadLoop);
    }

    private async Task ReadLoop()
    {
        var buf = new byte[8192];
        var text = new List<byte>();
        Exception? error = null;
        try
        {
            var state = 0; byte verb = 0; var inSb = false; var sb = new List<byte>();
            while (!_cts.IsCancellationRequested)
            {
                var n = await _stream!.ReadAsync(buf, _cts.Token);
                if (n == 0) break;
                text.Clear();
                for (var i = 0; i < n; i++)
                {
                    var b = buf[i];
                    switch (state)
                    {
                        case 0:
                            if (b == Iac) state = 1;
                            else if (inSb) sb.Add(b);
                            else text.Add(b);
                            break;
                        case 1: // after IAC
                            state = 0;
                            if (b == Iac) { if (inSb) sb.Add(b); else text.Add(b); }
                            else if (b is Do or Dont or Will or Wont) { verb = b; state = 2; }
                            else if (b == Sb) { inSb = true; sb.Clear(); }
                            else if (b == Se) { inSb = false; HandleSub(sb); }
                            break;
                        case 2: // option
                            state = 0;
                            Negotiate(verb, b);
                            break;
                    }
                }
                if (text.Count > 0)
                {
                    var chars = new char[_decoder.GetCharCount(text.ToArray(), 0, text.Count)];
                    _decoder.GetChars(text.ToArray(), 0, text.Count, chars, 0);
                    var s = new string(chars);
                    Output?.Invoke(s);
                    AutoLogin(s);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        if (!_cts.IsCancellationRequested) Closed?.Invoke(error);
    }

    private void Negotiate(byte verb, byte opt)
    {
        switch (verb)
        {
            case Will: Write(Iac, opt is OptEcho or OptSga ? Do : Dont, opt); break;
            case Wont: Write(Iac, Dont, opt); break;
            case Do:
                if (opt is OptTtype or OptSga) Write(Iac, Will, opt);
                else if (opt == OptNaws) { Write(Iac, Will, opt); _naws = true; SendSize(); }
                else Write(Iac, Wont, opt);
                break;
            case Dont: Write(Iac, Wont, opt); break;
        }
    }

    private void HandleSub(List<byte> sb)
    {
        // Terminal type request: IAC SB TTYPE SEND IAC SE -> IAC SB TTYPE IS "XTERM" IAC SE
        if (sb.Count >= 2 && sb[0] == OptTtype && sb[1] == 1)
        {
            var reply = new List<byte> { Iac, Sb, OptTtype, 0 };
            reply.AddRange(Encoding.ASCII.GetBytes("XTERM-256COLOR"));
            reply.AddRange([Iac, Se]);
            Write(reply.ToArray());
        }
    }

    /// <summary>Answers the first login and password prompts with the stored login, once each.</summary>
    private void AutoLogin(string s)
    {
        if (_user is null && _password is null) return;
        _recent.Append(s);
        if (_recent.Length > 300) _recent.Remove(0, _recent.Length - 300);
        var tail = _recent.ToString().TrimEnd().ToLowerInvariant();
        if (_user is not null && (tail.EndsWith("login:") || tail.EndsWith("username:") || tail.EndsWith("user name:")))
        {
            Send(_user + "\r");
            _user = null; _recent.Clear();
        }
        else if (_password is not null && tail.EndsWith("password:"))
        {
            Send(_password + "\r");
            _password = null; _user = null; _recent.Clear();
        }
    }

    private void SendSize()
    {
        if (!_naws) return;
        Write(Iac, Sb, OptNaws, (byte)(_cols >> 8), (byte)_cols, (byte)(_rows >> 8), (byte)_rows, Iac, Se);
    }

    public void Send(string data)
    {
        // Enter is CR LF on the wire (what most devices expect); a literal 0xFF byte is doubled.
        var bytes = _encoding.GetBytes(data.Replace("\r", "\r\n"));
        var outBuf = new List<byte>(bytes.Length + 4);
        foreach (var b in bytes) { outBuf.Add(b); if (b == Iac) outBuf.Add(Iac); }
        Write(outBuf.ToArray());
    }

    public void Resize(uint cols, uint rows) { _cols = cols; _rows = rows; SendSize(); }

    private readonly object _writeLock = new();
    private void Write(params byte[] data)
    {
        try { lock (_writeLock) _stream?.Write(data, 0, data.Length); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream?.Dispose();
        _tcp.Dispose();
    }
}
