using RemoteMan.Core.Chat;
using Xunit;

namespace RemoteMan.Tests;

public class ChatFormatTests
{
    private static string Kinds(string body) => string.Join(",", ChatFormat.Split(body).Select(s => s.Kind == ChatSegmentKind.Code ? "code" : "text"));

    [Fact]
    public void FencedBlocksKeepTheirLanguageAndText()
    {
        var s = ChatFormat.Split("Try this:\n```bash\nsudo systemctl restart gitea\n\n  journalctl -u gitea\n```\nthen tell me");
        Assert.Equal(3, s.Count);
        Assert.Equal("bash", s[1].Language);
        Assert.Equal("sudo systemctl restart gitea\n\n  journalctl -u gitea", s[1].Text);
        Assert.Equal("then tell me", s[2].Text);
    }

    [Fact]
    public void AnUnclosedFenceRunsToTheEnd() =>
        Assert.Equal("text,code", Kinds("look\n```\na = 1\nb = 2"));

    [Fact]
    public void OneLineTripleBackticksAreInline()
    {
        Assert.Equal("text", Kinds("run ```ls -la``` there"));
        Assert.Equal([("run ", false), ("ls -la", true), (" there", false)], ChatFormat.Inline("run ```ls -la``` there"));
        Assert.Equal([("use ", false), ("show ip int br", true)], ChatFormat.Inline("use `show ip int br`"));
    }

    [Theory]
    [InlineData("interface GigabitEthernet0/1\n description uplink\n switchport mode trunk\n switchport trunk allowed vlan 10,20")]
    [InlineData("2026-11-23 10:01:02 ERROR db connect failed\n2026-11-23 10:01:03 WARN retrying\n2026-11-23 10:01:05 ERROR giving up")]
    [InlineData("services:\n  gitea:\n    image: gitea/gitea:1.22\n    restart: always")]
    [InlineData("{\n  \"host\": \"10.0.0.1\",\n  \"port\": 22\n}")]
    [InlineData("mla@docker01:~$ docker ps\nCONTAINER ID   IMAGE\n3f2a   traefik:v3.2\n9b1c   gitea/gitea:1.22")]
    public void PastedCodeConfigAndLogsBecomeCode(string paste) => Assert.Equal("code", Kinds(paste));

    [Fact]
    public void ProseStaysText()
    {
        Assert.Equal("text", Kinds("Hi Jonas,\nCan you check fw01 when you have time?\nThe Gitea DB is down again.\nThanks!"));
        Assert.Equal("text", Kinds("No problem.\nShow me tomorrow.\nNote: the backup ran at 03:00 as usual."));
        Assert.Equal("text", Kinds("see https://wiki.lab/fw for the rules"));
    }

    [Fact]
    public void ProseAroundAPasteStaysText()
    {
        var s = ChatFormat.Split("Got this from the switch:\nvlan 20\n name servers\nvlan 30\n name clients\nLooks wrong to me.");
        Assert.Equal(["text", "code", "text"], s.Select(x => x.Kind == ChatSegmentKind.Code ? "code" : "text"));
        Assert.Equal(4, s[1].LineCount);
    }

    [Fact]
    public void TwoCodeLinesAreNotABlock() => Assert.Equal("text", Kinds("try a = 1\nb = 2"));

    [Fact]
    public void PreviewNamesCode() =>
        Assert.Equal("Here: [code, 2 lines]", ChatFormat.Preview("Here:\n```\na\nb\n```"));
}
