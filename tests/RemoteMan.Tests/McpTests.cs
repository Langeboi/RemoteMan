using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using RemoteMan.Core.Mcp;
using RemoteMan.Core.Terminal;
using Xunit;

namespace RemoteMan.Tests;

public class McpTests
{
    private static McpServer Echo() => new("1.2.3", (name, args, _) =>
        Task.FromResult(name == McpCatalog.ListSessions ? McpToolResult.Json(new JsonObject { ["got"] = args.Str("x") ?? "none" })
            : McpToolResult.Error("off")));

    private static JsonObject Call(McpServer s, string json) => (JsonObject)JsonNode.Parse(s.HandleAsync(json).GetAwaiter().GetResult()!)!;

    [Fact]
    public void Initialize_negotiates_version_and_names_the_client()
    {
        var s = Echo();
        var r = Call(s, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"claude-code","version":"2"}}}""");
        Assert.Equal("2025-06-18", (string?)r["result"]!["protocolVersion"]);
        Assert.Equal("remoteman", (string?)r["result"]!["serverInfo"]!["name"]);
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        Assert.Equal("claude-code", s.ClientName);

        var old = Call(s, """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        Assert.Equal(McpServer.Versions[0], (string?)old["result"]!["protocolVersion"]);
    }

    [Fact]
    public void Lists_every_tool_with_an_object_schema()
    {
        var r = Call(Echo(), """{"jsonrpc":"2.0","id":"a","method":"tools/list"}""");
        var tools = r["result"]!["tools"]!.AsArray();
        Assert.Equal(McpCatalog.Tools.Count, tools.Count);
        Assert.All(tools, t => Assert.Equal("object", (string?)t!["inputSchema"]!["type"]));
        Assert.Equal("a", (string?)r["id"]);
        Assert.Contains(tools, t => (string?)t!["name"] == "read_terminal");
    }

    [Fact]
    public void Calls_tools_and_reports_errors_the_mcp_way()
    {
        var s = Echo();
        var ok = Call(s, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_sessions","arguments":{"x":"y"}}}""");
        Assert.False((bool)ok["result"]!["isError"]!);
        Assert.Contains("\"got\": \"y\"", (string?)ok["result"]!["content"]![0]!["text"]);

        var off = Call(s, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_terminal"}}""");
        Assert.True((bool)off["result"]!["isError"]!);

        var unknown = Call(s, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"rm_rf"}}""");
        Assert.Equal(-32602, (int)unknown["error"]!["code"]!);
        Assert.Equal(-32601, (int)Call(s, """{"jsonrpc":"2.0","id":6,"method":"nope"}""")["error"]!["code"]!);
        Assert.Equal(-32700, (int)Call(s, "{not json")["error"]!["code"]!);
        Assert.Null(s.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""").GetAwaiter().GetResult());
    }

    [Fact]
    public void Arguments_are_read_leniently_and_clamped()
    {
        var a = (JsonObject)JsonNode.Parse("""{"n":"12","big":9999,"b":"true","s":42,"empty":""}""")!;
        Assert.Equal(12, a.Int("n", 1, 1, 100));
        Assert.Equal(500, a.Int("big", 1, 1, 500));
        Assert.Equal(7, a.Int("missing", 7, 1, 100));
        Assert.True(a.Bool("b"));
        Assert.Equal("42", a.Str("s"));
        Assert.Null(a.Str("empty"));
        Assert.Throws<McpArgumentException>(() => a.Required("missing"));
    }

    [Fact]
    public async Task Bridge_answers_itself_while_remoteman_is_closed_then_goes_through()
    {
        var name = "RemoteMan.Mcp.test." + Guid.NewGuid().ToString("N")[..8];
        var stdin = new Pipe();
        var stdout = new Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bridge = new McpBridge("1.0", name) { ConnectTimeout = TimeSpan.FromMilliseconds(200) };
        var run = bridge.RunAsync(stdin.Reader.AsStream(), stdout.Writer.AsStream(), cts.Token);
        var input = new StreamWriter(stdin.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var output = new StreamReader(stdout.Reader.AsStream(), Encoding.UTF8);

        async Task<JsonObject> Send(string json)
        {
            await input.WriteLineAsync(json);
            return (JsonObject)JsonNode.Parse((await output.ReadLineAsync(cts.Token))!)!;
        }

        // Closed: the client still connects and sees the tools, and a call says what to do.
        var init = await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        Assert.Equal("remoteman", (string?)init["result"]!["serverInfo"]!["name"]);
        var closed = await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_sessions"}}""");
        Assert.True((bool)closed["result"]!["isError"]!);
        Assert.Contains("isn't running", (string?)closed["result"]!["content"]![0]!["text"]);

        // RemoteMan starts: the next call goes through to it.
        using var listener = new McpListener(Echo, name);
        var changed = new TaskCompletionSource();
        listener.ClientsChanged += () => { if (listener.Clients.Count > 0) changed.TrySetResult(); };
        listener.Start();
        JsonObject? open = null;
        for (var i = 0; i < 20 && open?["result"]?["isError"]?.GetValue<bool>() != false; i++)
        {
            open = await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_sessions","arguments":{"x":"live"}}}""");
            if (open["result"]!["isError"]!.GetValue<bool>()) await Task.Delay(100);
        }
        Assert.False((bool)open!["result"]!["isError"]!);
        Assert.Contains("live", (string?)open["result"]!["content"]![0]!["text"]);
        await changed.Task.WaitAsync(cts.Token);
        Assert.Single(listener.Clients);

        stdin.Writer.Complete();
        await run.WaitAsync(cts.Token);
    }

    [Fact]
    public void Tail_strips_escape_codes_across_chunks()
    {
        var t = new TerminalTail();
        t.Append("\x1b[32mroot@pve\x1b[0m:~# ls\r\n");
        t.Append("file1  \x1b[3");      // escape split across chunks
        t.Append("4mdir1\x1b[0m\r\n");
        t.Append("\x1b]0;title\u0007abc\b\bX\r\n\r\n");
        Assert.Equal("root@pve:~# ls\nfile1  dir1\naX", t.LastLines(50));
        Assert.Equal("aX", t.LastLines(1));
    }

    [Fact]
    public void Tail_keeps_only_the_end()
    {
        var t = new TerminalTail(maxChars: 20);
        for (var i = 0; i < 10; i++) t.Append($"line{i}\r\n");
        Assert.EndsWith("line9", t.LastLines(50));
        Assert.DoesNotContain("line0", t.LastLines(50));
    }

}
