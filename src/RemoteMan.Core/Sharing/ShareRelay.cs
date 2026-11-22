using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RemoteMan.Core.Sharing;

/// <summary>
/// The relay for teammates on different networks, in-process. The one that runs on the database
/// server is <c>deploy/relay/remoteman-relay.py</c> and speaks the same line protocol:
/// <code>
/// host  → relay: HOST &lt;share&gt;            relay → host: OK, then CONN &lt;id&gt; per guest (PING now and then)
/// guest → relay: JOIN &lt;share&gt;            relay → guest: OK once spliced, or NOHOST
/// host  → relay: ACCEPT &lt;share&gt; &lt;id&gt;     relay → host: OK, then raw bytes both ways
/// </code>
/// The relay only pipes bytes: TLS runs end to end between guest and host, so it can't read anything.
/// </summary>
public sealed class ShareRelay : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Stream> _hosts = new();
    private readonly ConcurrentDictionary<string, (string Share, TaskCompletionSource<Stream> Host)> _pending = new();

    public ShareRelay(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); } catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(tcp));
        }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        var s = tcp.GetStream();
        var keep = false;
        try
        {
            var line = await ShareNet.ReadLineAsync(s, _cts.Token) ?? "";
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts)
            {
                case ["HOST", var share]:
                    _hosts[share] = s;
                    await ShareNet.WriteLineAsync(s, "OK", _cts.Token);
                    keep = true;
                    // Hold until the host goes away.
                    var one = new byte[64];
                    while (await s.ReadAsync(one, _cts.Token) > 0) { }
                    _hosts.TryRemove(new KeyValuePair<string, Stream>(share, s));
                    keep = false;
                    break;
                case ["JOIN", var share]:
                    if (!_hosts.TryGetValue(share, out var host)) { await ShareNet.WriteLineAsync(s, "NOHOST", _cts.Token); break; }
                    var id = Guid.NewGuid().ToString("N")[..12];
                    var tcs = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending[id] = (share, tcs);
                    await ShareNet.WriteLineAsync(host, $"CONN {id}", _cts.Token);
                    var accepted = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), _cts.Token);
                    await ShareNet.WriteLineAsync(s, "OK", _cts.Token);
                    keep = true;
                    await Task.WhenAny(s.CopyToAsync(accepted, _cts.Token), accepted.CopyToAsync(s, _cts.Token));
                    accepted.Dispose();
                    keep = false;
                    break;
                case ["ACCEPT", var share, var conn]:
                    if (_pending.TryRemove(conn, out var p) && p.Share == share)
                    {
                        await ShareNet.WriteLineAsync(s, "OK", _cts.Token);
                        keep = true;   // the JOIN side owns the splice now
                        p.Host.TrySetResult(s);
                        return;
                    }
                    await ShareNet.WriteLineAsync(s, "NOCONN", _cts.Token);
                    break;
            }
        }
        catch (Exception) { keep = false; }
        finally { if (!keep) tcp.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
