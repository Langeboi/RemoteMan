using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class EntrySettingsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"remoteman-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    [Theory]
    // RFC 6238 test vectors (SHA-1, 8 digits) with the RFC's ASCII key "12345678901234567890" in base32.
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    public void TotpMatchesRfcVectors(long unix, string expected)
    {
        var secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Equal(expected, Totp.Code(secret, DateTimeOffset.FromUnixTimeSeconds(unix), digits: 8));
    }

    [Fact]
    public void TotpReadsSpacedSecretsAndOtpauthLinks()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(59);
        var plain = Totp.Code("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", at);
        Assert.Equal(plain, Totp.Code("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", at));
        Assert.Equal(plain, Totp.Code("otpauth://totp/lab:me?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=lab", at));
        Assert.Equal(6, plain.Length);
        Assert.False(Totp.IsValid("not base32!"));
        Assert.True(Totp.IsValid("JBSWY3DPEHPK3PXP"));
    }

    [Theory]
    [InlineData("3C:52:82:1A:4F:09")]
    [InlineData("3c-52-82-1a-4f-09")]
    [InlineData("3C52.821A.4F09")]
    [InlineData("3C52821A4F09")]
    public void WakeOnLanParsesMacNotations(string mac)
    {
        var bytes = WakeOnLan.ParseMac(mac);
        Assert.Equal(new byte[] { 0x3C, 0x52, 0x82, 0x1A, 0x4F, 0x09 }, bytes);
        var packet = WakeOnLan.MagicPacket(bytes!);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(bytes, packet[96..102]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3C:52:82:1A:4F")]
    [InlineData("zz:52:82:1A:4F:09")]
    public void WakeOnLanRejectsBadMacs(string mac) => Assert.Null(WakeOnLan.ParseMac(mac));

    [Fact]
    public async Task EntryTakesTickedSectionsFromItsFolder()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var top = await store.SaveAsync(new Entry
        {
            Kind = EntryKind.Folder, Name = "School",
            Rdp = new RdpSettings { Audio = RdpAudio.Off, RedirectDrives = false, ColorDepth = 16, Speed = RdpSpeed.Slow },
            Connection = new ConnectionSettings { WaitUntilUp = true, WaitSeconds = 60 },
        });
        // A sub folder that passes on what it inherits.
        var lab = await store.SaveAsync(new Entry { Kind = EntryKind.Folder, Name = "AD lab", ParentId = top.Id, Inherit = InheritSections.Display | InheritSections.Resources });
        var vm = await store.SaveAsync(new Entry
        {
            Kind = EntryKind.Rdp, Name = "dc01", ParentId = lab.Id,
            Inherit = InheritSections.Display | InheritSections.Resources | InheritSections.Connection,
            Rdp = new RdpSettings { Audio = RdpAudio.Local, ColorDepth = 32, GatewayHost = "own-gw", UseRdGateway = true },
            Endpoints = { new HostEndpoint("10.0.10.5", 3389) },
        });

        var eff = store.Effective(store.Get(vm.Id)!);
        Assert.Equal(RdpAudio.Off, eff.Rdp!.Audio);            // resources from "School", through "AD lab"
        Assert.Equal(16, eff.Rdp.ColorDepth);                   // display from "School"
        Assert.Equal(RdpSpeed.Slow, eff.Rdp.Speed);
        Assert.Equal("own-gw", eff.Rdp.GatewayHost);            // gateway not ticked: its own
        Assert.Null(eff.Connection);                            // "AD lab" sets no connection section: its own (empty) wins
        Assert.Equal(RdpAudio.Local, store.Get(vm.Id)!.Rdp!.Audio); // the stored entry is untouched
    }

    [Fact]
    public async Task NewSettingsRoundTripThroughTheDatabase()
    {
        await using var store = new EntryStore(new SqliteDataSource(_db), "alice", "pc1");
        await store.OpenAsync();
        var saved = await store.SaveAsync(new Entry
        {
            Kind = EntryKind.Ssh, Name = "sw01", OpenIn = OpenMode.FullScreen, TabColor = "#E5605B",
            Inherit = InheritSections.Terminal,
            Connection = new ConnectionSettings { WakeOnLan = true, MacAddress = "3C:52:82:1A:4F:09" },
            Fields = [new CustomField { Name = "VLAN", Value = "10" }],
            Ssh = new SshSettings
            {
                Forwards = [new PortForward { Kind = ForwardKind.Dynamic, ListenPort = 1080 }],
                LoginScript = [new ScriptStep { WaitFor = ">", Send = "enable" }],
                Encoding = "iso-8859-1",
            },
        });
        await using var other = new EntryStore(new SqliteDataSource(_db), "bob", "pc2");
        await other.OpenAsync();
        var e = other.Get(saved.Id)!;
        Assert.Equal(OpenMode.FullScreen, e.OpenIn);
        Assert.Equal("#E5605B", e.TabColor);
        Assert.Equal(InheritSections.Terminal, e.Inherit);
        Assert.Equal("3C:52:82:1A:4F:09", e.Connection!.MacAddress);
        Assert.Equal("VLAN", Assert.Single(e.Fields!).Name);
        Assert.Equal(1080, Assert.Single(e.Ssh!.Forwards!).ListenPort);
        Assert.Equal("enable", Assert.Single(e.Ssh.LoginScript!).Send);
        Assert.Equal("iso-8859-1", e.Ssh.Encoding);
    }
}

public class LoginScriptTests
{
    [Fact]
    public void TypesEachReplyAfterItsPromptAppears()
    {
        var sent = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["enable"] = "s3cret" };
        var r = new RemoteMan.Core.Terminal.LoginScriptRunner(
        [
            new ScriptStep { WaitFor = ">", Send = "enable" },
            new ScriptStep { WaitFor = "Password:", Send = "{secret:enable}" },
            new ScriptStep { WaitFor = "#", Send = "terminal length 0" },
        ], t => RemoteMan.Core.Terminal.LoginScriptRunner.Expand(t, fields, () => null, "sw01", "admin", "core-sw"), sent.Add);
        r.Start();
        Assert.Empty(sent);
        r.Feed("\x1b[32mUser Access Verification\x1b[0m\r\nsw01");
        Assert.Empty(sent);
        r.Feed(">");
        Assert.Equal(["enable\r"], sent);
        // The echo of "enable" and the old prompt don't trigger the next step.
        r.Feed("enable\r\nPass");
        Assert.Single(sent);
        r.Feed("word: ");
        Assert.Equal("s3cret\r", sent[1]);
        r.Feed("\r\nsw01#");
        Assert.Equal("terminal length 0\r", sent[2]);
        Assert.True(r.Done);
    }

    [Fact]
    public void StepWithoutWaitIsTypedAtOnce()
    {
        var sent = new List<string>();
        var r = new RemoteMan.Core.Terminal.LoginScriptRunner([new ScriptStep { Send = "sudo -i" }], t => t, sent.Add, "\n");
        r.Start();
        Assert.Equal(["sudo -i\n"], sent);
    }

    [Fact]
    public void LogTextDropsEscapesAndAppliesBackspace()
    {
        Assert.Equal("ls -a\nok", RemoteMan.Core.Terminal.TerminalText.ForLog("\x1b[01;34mls -lx\b\ba\x1b[0m\r\nok"));
    }
}
