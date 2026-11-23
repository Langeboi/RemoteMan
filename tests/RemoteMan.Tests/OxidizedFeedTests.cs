using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;
using Xunit;

namespace RemoteMan.Tests;

public class OxidizedFeedTests
{
    private static Entry Ssh(string name, string? host, Guid? parent = null, string? icon = null, string? tags = null, int port = 22)
    {
        var e = new Entry { Kind = EntryKind.Ssh, Name = name, ParentId = parent, Icon = icon, Tags = tags, Ssh = new SshSettings() };
        if (host is not null) e.Endpoints.Add(new HostEndpoint(host, port));
        return e;
    }

    private static OxidizedNode Node(string name, string ip, string? group = null) => new(name, group, ip, "ios", "success", DateTimeOffset.UtcNow, null);

    [Fact]
    public void Model_comes_from_field_then_tag_then_icon_then_default()
    {
        var e = Ssh("sw", "10.0.0.2", icon: "aruba");
        Assert.Equal("aoscx", OxidizedFeed.ModelFor(e, "ios"));
        e.Tags = "switch, procurve";
        Assert.Equal("procurve", OxidizedFeed.ModelFor(e, null));
        e.Tags = "FortiGate";
        Assert.Equal("fortios", OxidizedFeed.ModelFor(e, null));
        e.Fields = [new CustomField { Name = "Oxidized model", Value = " dnos " }];
        Assert.Equal("dnos", OxidizedFeed.ModelFor(e, null));
        Assert.Equal("linuxgeneric", OxidizedFeed.ModelFor(Ssh("box", "1.2.3.4"), "linuxgeneric"));
        Assert.Null(OxidizedFeed.ModelFor(Ssh("box", "1.2.3.4"), " "));
    }

    [Fact]
    public void Plan_sends_every_ssh_entry_and_marks_missing_ones()
    {
        var core = new Entry { Kind = EntryKind.Folder, Name = "Core" };
        var sw = Ssh("core-sw01", "10.0.0.2", core.Id);
        var fw = Ssh("fw01", "10.0.0.1", core.Id, port: 2222);
        var skip = Ssh("lab", "10.0.0.7", tags: "no-oxidized");
        var nohost = Ssh("empty", null);
        var rdp = new Entry { Kind = EntryKind.Rdp, Name = "win", Endpoints = [new("10.0.0.50")] };
        var snap = new OxidizedSnapshot([Node("core-sw01", "10.0.0.2", "Core")], new OxidizedMatchOptions(), DateTimeOffset.UtcNow);

        var plan = OxidizedFeed.Plan([core, sw, fw, skip, nohost, rdp], new OxidizedFeedOptions(OxidizedFeedGroup.TopFolder), null, snap);

        Assert.Equal(4, plan.Count);
        Assert.Equal(OxidizedFeedState.InOxidized, plan.Single(i => i.Entry == sw).State);
        var f = plan.Single(i => i.Entry == fw);
        Assert.Equal((OxidizedFeedState.Missing, "Core/fw01", 2222), (f.State, f.FullName, f.Port));
        Assert.Equal(OxidizedFeedState.LeftOut, plan.Single(i => i.Entry == skip).State);
        Assert.Equal(OxidizedFeedState.LeftOut, plan.Single(i => i.Entry == nohost).State);
    }

    [Fact]
    public void Plan_keeps_saved_names_and_makes_names_unique()
    {
        var a = Ssh("Core switch", "10.0.0.2");
        var b = Ssh("fw01", "10.0.0.1");
        var c = Ssh("fw01", "10.0.0.9");
        var links = new Dictionary<string, string> { [OxidizedFeed.IdKey(a.Id)] = "switches/core-sw01" };
        var plan = OxidizedFeed.Plan([a, b, c], new OxidizedFeedOptions(), links, null);
        Assert.Equal("switches/core-sw01", plan.Single(i => i.Entry == a).FullName);
        var names = plan.Where(i => i.Entry != a).Select(i => i.Name).ToList();
        Assert.Contains("fw01", names);
        Assert.Single(names, n => n.StartsWith("fw01-", StringComparison.Ordinal));
    }

    [Fact]
    public void Unticked_entries_are_not_sent_and_new_ones_wait_without_auto_add()
    {
        var a = Ssh("sw1", "10.0.0.2");
        var b = Ssh("sw2", "10.0.0.3");
        var c = Ssh("new", "10.0.0.4");
        var snap = new OxidizedSnapshot([Node("sw1", "10.0.0.2")], new OxidizedMatchOptions(), DateTimeOffset.UtcNow);
        var sync = new OxidizedFeedSync { Excluded = [OxidizedFeed.IdKey(a.Id)], Included = [OxidizedFeed.IdKey(b.Id)] };

        var auto = OxidizedFeed.Plan([a, b, c], new OxidizedFeedOptions(), null, snap, sync.Choice(autoAdd: true));
        Assert.Equal(OxidizedFeedState.Off, auto.Single(i => i.Entry == a).State);
        Assert.NotNull(auto.Single(i => i.Entry == a).Node); // still in Oxidized until it reloads
        Assert.Equal(OxidizedFeedState.Missing, auto.Single(i => i.Entry == c).State);

        var picked = OxidizedFeed.Plan([a, b, c], new OxidizedFeedOptions(), null, snap, sync.Choice(autoAdd: false));
        Assert.Equal(OxidizedFeedState.Missing, picked.Single(i => i.Entry == b).State);
        Assert.Equal(OxidizedFeedState.Off, picked.Single(i => i.Entry == c).State);
        // An unticked entry's device isn't counted as "only in Oxidized".
        Assert.Empty(OxidizedFeed.OnlyInOxidized(snap, auto, []));
    }

    [Fact]
    public void Devices_only_in_oxidized_are_listed_unless_kept()
    {
        var snap = new OxidizedSnapshot([Node("sw1", "10.0.0.2"), Node("old-router", "10.9.9.9"), Node("gone", "10.9.9.8")], new OxidizedMatchOptions(), DateTimeOffset.UtcNow);
        var plan = OxidizedFeed.Plan([Ssh("sw1", "10.0.0.2")], new OxidizedFeedOptions(), null, snap);
        var only = OxidizedFeed.OnlyInOxidized(snap, plan, [new OxidizedKeptNode { Name = "gone" }]);
        Assert.Equal(new[] { "old-router" }, only.Select(n => n.Name));
    }

    [Fact]
    public void Sealed_login_opens_with_the_server_key_and_only_for_its_entry()
    {
        using var rsa = RSA.Create(3072);
        var pub = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var json = OxidizedFeed.LoginJson("admin", "s3cret", null);
        var blob = OxidizedFeed.Seal(pub, "entry-1", json);

        // What rmserver/oxidized.py does.
        string Open(string sealedBlob, string entryId)
        {
            var raw = Convert.FromBase64String(sealedBlob);
            Assert.Equal("OX1"u8.ToArray(), raw[..3]);
            var n = (raw[3] << 8) | raw[4];
            var key = rsa.Decrypt(raw[5..(5 + n)], RSAEncryptionPadding.OaepSHA256);
            var rest = raw[(5 + n)..];
            var ct = rest[12..^16];
            var plain = new byte[ct.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(rest[..12], ct, rest[^16..], plain, Encoding.UTF8.GetBytes(entryId));
            return Encoding.UTF8.GetString(plain);
        }

        var opened = JsonSerializer.Deserialize<Dictionary<string, string>>(Open(blob, "entry-1"))!;
        Assert.Equal("admin", opened["u"]);
        Assert.Equal("s3cret", opened["p"]);
        Assert.False(opened.ContainsKey("e"));
        Assert.ThrowsAny<CryptographicException>(() => Open(blob, "entry-2"));
    }

    [Fact]
    public void Fingerprint_changes_with_the_login_only()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var a = OxidizedFeed.Fingerprint(key, "PUB", "e1", OxidizedFeed.LoginJson("u", "p", null));
        Assert.Equal(a, OxidizedFeed.Fingerprint(key, "PUB", "e1", OxidizedFeed.LoginJson("u", "p", null)));
        Assert.NotEqual(a, OxidizedFeed.Fingerprint(key, "PUB", "e1", OxidizedFeed.LoginJson("u", "p2", null)));
        Assert.NotEqual(a, OxidizedFeed.Fingerprint(key, "PUB2", "e1", OxidizedFeed.LoginJson("u", "p", null)));
    }

    [Fact]
    public void Sync_round_trips_with_the_names_the_server_reads()
    {
        var sync = new OxidizedFeedSync { Key = "k", Links = { ["abc"] = "g/n" }, Logins = { ["abc"] = new() { F = "f", S = "s" } }, Keep = [new() { Name = "x", Ip = "1.1.1.1" }] };
        var json = JsonSerializer.Serialize(sync, OxidizedFeed.Json);
        Assert.Contains("\"Links\":{\"abc\":\"g/n\"}", json);
        Assert.Contains("\"Logins\":{\"abc\":{\"F\":\"f\",\"S\":\"s\"}}", json);
        Assert.Contains("\"Keep\":[{\"Name\":\"x\",\"Ip\":\"1.1.1.1\"}]", json);
    }

    [Fact]
    public void Model_comes_from_a_word_in_the_name_before_the_default()
    {
        Assert.Equal("routeros", OxidizedFeed.ModelFor(Ssh("HH-ROS-LEAF1", "10.0.0.1"), "fortios"));
        Assert.Equal("routeros", OxidizedFeed.ModelFor(Ssh("rd chr 02", "10.0.0.2"), null));
        Assert.Equal("fortios", OxidizedFeed.ModelFor(Ssh("VIB-FGT-01", "10.0.0.3"), null));
        Assert.Equal("linuxgeneric", OxidizedFeed.ModelFor(Ssh("PVE-ROSKILDE", "10.0.0.4"), null));
        Assert.Equal("ios", OxidizedFeed.ModelFor(Ssh("ROSKILDE-SW1", "10.0.0.5"), "ios")); // part of a word doesn't count
        Assert.Equal("aoscx", OxidizedFeed.ModelFor(Ssh("HH-ROS-LEAF1", "10.0.0.1", icon: "aruba"), null)); // the icon wins
    }

    [Fact]
    public void Model_picked_in_the_list_wins_and_reaches_the_plan()
    {
        var e = Ssh("HH-ROS-LEAF1", "10.0.0.1", icon: "aruba");
        Assert.Equal("procurve", OxidizedFeed.ModelFor(e, "ios", " procurve "));
        Assert.Equal("aoscx", OxidizedFeed.ModelFor(e, "ios", " "));
        var models = new Dictionary<string, string> { [OxidizedFeed.IdKey(e.Id)] = "ftos" };
        var plan = OxidizedFeed.Plan([e], new OxidizedFeedOptions(), null, null, null, models);
        Assert.Equal("ftos", Assert.Single(plan).Model);

        var sync = new OxidizedFeedSync { Models = { ["abc"] = "routeros" } };
        Assert.Contains("\"Models\":{\"abc\":\"routeros\"}", JsonSerializer.Serialize(sync, OxidizedFeed.Json));
    }
}
