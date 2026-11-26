using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteMan.Core.Mcp;

/// <summary>
/// RemoteMan listens on a named pipe that only the same Windows user can open. AI clients start
/// <c>RemoteMan.exe --mcp</c>, which passes their stdio messages through it (newline-delimited JSON-RPC both ways).
/// </summary>
public static class McpPipe
{
    public static string DefaultName { get; } = NameFor($"{Environment.UserDomainName}.{Environment.UserName}");

    public static string NameFor(string user) =>
        "RemoteMan.Mcp." + new string(user.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    internal static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Reads a stream line by line and answers each message, possibly several at once (a slow tool doesn't hold up a ping).</summary>
    public static async Task ServeAsync(Stream input, Stream output, McpServer server, CancellationToken ct)
    {
        using var reader = new StreamReader(input, Utf8, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(output, Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var gate = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    var reply = await server.HandleAsync(line, ct);
                    if (reply is null) return;
                    await gate.WaitAsync(ct);
                    try { await writer.WriteLineAsync(reply); }
                    finally { gate.Release(); }
                }, ct));
            }
            await Task.WhenAll(running);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}

/// <summary>The app side: accepts bridge connections while AI access is switched on.</summary>
public sealed class McpListener(Func<McpServer> newServer, string? pipeName = null) : IDisposable
{
    private readonly string _name = pipeName ?? McpPipe.DefaultName;
    private readonly ConcurrentDictionary<int, string> _clients = new();
    private CancellationTokenSource? _cts;
    private int _next;

    /// <summary>Names of the AI clients connected right now.</summary>
    public IReadOnlyList<string> Clients => _clients.Values.ToList();
    public bool IsRunning => _cts is not null;

    /// <summary>A client connected, introduced itself or left (raised on a background thread).</summary>
    public event Action? ClientsChanged;
    /// <summary>The pipe couldn't be opened (raised on a background thread).</summary>
    public event Action<string>? Problem;

    public void Start()
    {
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                failures = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (++failures == 3) Problem?.Invoke(ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, failures * 2)), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => ServeOneAsync(pipe, ct), ct);
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _next);
        var server = newServer();
        _clients[id] = "AI app";
        server.Initialized += name => { _clients[id] = name; ClientsChanged?.Invoke(); };
        ClientsChanged?.Invoke();
        try
        {
            await using (pipe) await McpPipe.ServeAsync(pipe, pipe, server, ct);
        }
        catch (Exception) { }
        finally
        {
            _clients.TryRemove(id, out _);
            ClientsChanged?.Invoke();
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// <c>RemoteMan.exe --mcp</c>: an MCP stdio server for AI clients. Each message goes through to the running RemoteMan;
/// while it isn't running (or AI access is off) the bridge answers itself, so the client still connects and every tool
/// call says how to fix it. Connects again as soon as RemoteMan is back.
/// </summary>
public sealed class McpBridge(string version, string? pipeName = null)
{
    public const string NotRunning =
        "RemoteMan isn't running, or AI access is switched off in it. Start RemoteMan and switch on " +
        "\"Let AI apps use RemoteMan\" under the AI button in the title bar, then try again.";

    private readonly string _name = pipeName ?? McpPipe.DefaultName;
    private readonly ConcurrentDictionary<string, JsonNode?> _pending = new();
    private readonly SemaphoreSlim _outGate = new(1, 1);
    private readonly object _pipeLock = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _toApp;
    private StreamWriter _out = null!;

    /// <summary>How long to wait for RemoteMan's pipe before answering locally.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(400);

    public async Task RunAsync(Stream stdin, Stream stdout, CancellationToken ct = default)
    {
        _out = new StreamWriter(stdout, McpPipe.Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var local = new McpServer(version, (_, _, _) => Task.FromResult(McpToolResult.Error(NotRunning)));
        using var reader = new StreamReader(stdin, McpPipe.Utf8, false, 4096, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                if (await ForwardAsync(line, ct)) continue;
                if (await local.HandleAsync(line, ct) is { } reply) await WriteAsync(reply, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { Disconnect(answerPending: false); }
    }

    /// <summary>Sends the message to RemoteMan. False when it isn't reachable, so the bridge answers itself.</summary>
    private async Task<bool> ForwardAsync(string line, CancellationToken ct)
    {
        var toApp = _toApp;
        if (toApp is null && (!await TryConnectAsync(ct) || (toApp = _toApp) is null)) return false;
        var key = RequestKey(line, out var id);
        if (key is not null) _pending[key] = id;
        try
        {
            await toApp.WriteLineAsync(line.AsMemory(), ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (key is not null) _pending.TryRemove(key, out _);
            Disconnect(answerPending: true);
            return false;
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            return false;
        }
        lock (_pipeLock)
        {
            _pipe = pipe;
            _toApp = new StreamWriter(pipe, McpPipe.Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }
        _ = Task.Run(() => PumpAsync(pipe, ct), ct);
        return true;
    }

    /// <summary>RemoteMan's replies, straight to stdout.</summary>
    private async Task PumpAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, McpPipe.Utf8, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (ResponseKey(line) is { } key) _pending.TryRemove(key, out _);
                await WriteAsync(line, ct);
            }
        }
        catch (Exception) { }
        if (ReferenceEquals(_pipe, pipe)) Disconnect(answerPending: true);
    }

    /// <summary>RemoteMan went away: requests it never answered get an error, so the client isn't left waiting.</summary>
    private void Disconnect(bool answerPending)
    {
        NamedPipeClientStream? pipe;
        lock (_pipeLock)
        {
            pipe = _pipe;
            _pipe = null;
            _toApp = null;
        }
        try { pipe?.Dispose(); } catch (Exception) { }
        if (!answerPending) return;
        foreach (var key in _pending.Keys.ToList())
            if (_pending.TryRemove(key, out var id))
                _ = WriteAsync(McpServer.ErrorResponse(id, "RemoteMan closed before it answered. " + NotRunning), CancellationToken.None);
    }

    private async Task WriteAsync(string line, CancellationToken ct)
    {
        await _outGate.WaitAsync(ct);
        try { await _out.WriteLineAsync(line); }
        finally { _outGate.Release(); }
    }

    /// <summary>A request (method and id) expects an answer: its id, as JSON text.</summary>
    private static string? RequestKey(string line, out JsonNode? id)
    {
        id = null;
        try
        {
            if (JsonNode.Parse(line) is JsonObject o && o.ContainsKey("method") && o["id"] is { } i)
            {
                id = i.DeepClone();
                return i.ToJsonString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? ResponseKey(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject o && !o.ContainsKey("method") && o["id"] is { } i) return i.ToJsonString();
        }
        catch (JsonException) { }
        return null;
    }
}
