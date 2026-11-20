using System.Text;
using RemoteMan.Core.Ai;
using Xunit;

namespace RemoteMan.Tests;

public class AiTests
{
    private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Sse_yields_each_event_and_joins_multiline_data()
    {
        var input = ": comment\ndata: {\"a\":1}\n\ndata:one\ndata: two\n\nevent: x\ndata: last";
        var got = new List<string>();
        await foreach (var d in Sse.ReadDataAsync(Text(input))) got.Add(d);
        Assert.Equal(new[] { "{\"a\":1}", "one\ntwo", "last" }, got);
    }

    [Fact]
    public void OpenAi_delta_reads_content_and_done()
    {
        Assert.Equal("Hi", OpenAiCompatibleClient.ParseDelta("{\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}"));
        Assert.Null(OpenAiCompatibleClient.ParseDelta("{\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}"));
        Assert.Null(OpenAiCompatibleClient.ParseDelta("[DONE]"));
        Assert.Throws<AiProviderException>(() => OpenAiCompatibleClient.ParseDelta("{\"error\":{\"message\":\"bad key\"}}"));
    }

    [Fact]
    public void OpenAi_body_puts_system_first_and_maps_roles()
    {
        var body = OpenAiCompatibleClient.BuildBody("m", "sys", [new(ChatRole.User, "q"), new(ChatRole.Assistant, "a")]);
        var msgs = body["messages"]!.AsArray();
        Assert.Equal("system", (string?)msgs[0]!["role"]);
        Assert.Equal("user", (string?)msgs[1]!["role"]);
        Assert.Equal("assistant", (string?)msgs[2]!["role"]);
        Assert.True((bool)body["stream"]!);
    }

    [Fact]
    public void Gemini_body_uses_model_role_and_system_instruction()
    {
        var body = GeminiClient.BuildBody("sys", [new(ChatRole.User, "q"), new(ChatRole.Assistant, "a")]);
        Assert.Equal("sys", (string?)body["systemInstruction"]!["parts"]![0]!["text"]);
        Assert.Equal("model", (string?)body["contents"]![1]!["role"]);
        Assert.Equal("ab", GeminiClient.ParseDelta("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"a\"},{\"text\":\"b\"}]}}]}"));
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

    [Fact]
    public void Reply_splits_fenced_code_including_unclosed()
    {
        var s = ReplySegment.Split("Check the log:\n```bash\njournalctl -u pveproxy\n```\nThen restart.");
        Assert.Equal(3, s.Count);
        Assert.True(s[1].IsCode);
        Assert.Equal("bash", s[1].Language);
        Assert.Equal("journalctl -u pveproxy", s[1].Text);

        var streaming = ReplySegment.Split("Run:\n```sh\nlvs");
        Assert.True(streaming[^1].IsCode);
        Assert.Equal("lvs", streaming[^1].Text);
    }

    [Fact]
    public void Context_wraps_output_and_keeps_message_last()
    {
        var msg = AiClientFactory.WithContext("why?", new SessionContext("pve-01", "SSH", "10.0.10.11:22", "error"));
        Assert.StartsWith("<session name=\"pve-01\"", msg);
        Assert.Contains("<output>\nerror\n</output>", msg);
        Assert.EndsWith("why?", msg);
        Assert.Equal("why?", AiClientFactory.WithContext("why?", null));
    }
}
