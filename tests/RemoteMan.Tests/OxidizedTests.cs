using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;
using Xunit;

namespace RemoteMan.Tests;

public class OxidizedTests
{
    private static OxidizedNode Node(string name, string ip, string? group = "network", string status = "success") =>
        new(name, group, ip, "ios", status, DateTimeOffset.UtcNow, null);

    private static Entry Ssh(string name, params string[] hosts)
    {
        var e = new Entry { Kind = EntryKind.Ssh, Name = name, Ssh = new SshSettings() };
        foreach (var h in hosts) e.Endpoints.Add(new HostEndpoint(h, 22));
        return e;
    }

    // ---------------- matching ----------------

    [Fact]
    public void Matches_saved_link_then_address_then_name()
    {
        var m = new OxidizedMatcher([Node("core-sw01", "10.0.0.2"), Node("fw01.lab.local", "10.0.0.1"), Node("other", "10.0.0.9", group: null)]);
        Assert.Equal("core-sw01", m.Match(Ssh("whatever", "10.0.0.2"))!.Node.Name);
        // Name with a bracket suffix, address behind NAT.
        Assert.Equal("core-sw01", m.Match(Ssh("Core-SW01 (Core)", "203.0.113.4"))!.Node.Name);
        // FQDN node answers to the short name.
        Assert.Equal("fw01.lab.local", m.Match(Ssh("fw01", "198.51.100.1"))!.Node.Name);
        var linked = Ssh("core-sw01", "10.0.0.2");
        linked.OxidizedNode = "other";
        Assert.Equal("other", m.Match(linked)!.Node.FullName);
        linked.OxidizedNode = "network/gone";
        Assert.Null(m.Match(linked));
    }

    [Fact]
    public void Shared_address_never_matches()
    {
        var m = new OxidizedMatcher([Node("a", "10.0.0.50"), Node("b", "10.0.0.50")]);
        Assert.Null(m.Match(Ssh("x", "10.0.0.50")));
        Assert.Equal(2, m.Suggestions(Ssh("x", "10.0.0.50")).Count());
    }

    // ---------------- parsing ----------------

    [Fact]
    public void Parses_nodes_json_from_oxidized_web()
    {
        var json = JsonNode.Parse("""
            [{"name":"core-sw01","full_name":"network/core-sw01","ip":"10.0.0.2","group":"network","model":"IOS",
              "last":{"start":"2026-11-21 07:02:01 UTC","end":"2026-11-21 07:02:10 UTC","status":"success","time":9.1},
              "vars":{},"mtime":"2026-11-21 07:02:10 +0000","status":"success","time":"2026-11-21 07:02:10 UTC"},
             {"name":"ap-hall","full_name":"ap-hall","ip":"10.0.0.21","group":"default","model":"airos","last":null,"mtime":"unknown","status":"never","time":"never"},
             {"name":"lab-rtr1","full_name":"lab/lab-rtr1","ip":"10.0.0.9","group":"lab","model":"routeros",
              "last":{"start":"2026-11-21 08:00:00 +0100","end":"2026-11-21 08:00:30 +0100","status":"no_connection","time":30}}]
            """)!.AsArray();
        var nodes = json.Select(n => OxidizedClient.ParseNode(n!.AsObject())).ToList();
        Assert.Equal("network/core-sw01", nodes[0].FullName);
        Assert.True(nodes[0].IsOk);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 7, 2, 10, TimeSpan.Zero), nodes[0].LastRun);
        Assert.Null(nodes[1].Group);
        Assert.Equal("ap-hall", nodes[1].FullName);
        Assert.True(nodes[1].NeverRan);
        Assert.False(nodes[1].IsFailing);
        Assert.True(nodes[2].IsFailing);
        Assert.Equal("no connection", nodes[2].StatusText);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 7, 0, 30, TimeSpan.Zero), nodes[2].LastRun!.Value.ToUniversalTime());
    }

    [Fact]
    public void Parses_versions_newest_first_and_numbers_them()
    {
        var v = OxidizedClient.ParseVersions(JsonNode.Parse("""
            [{"oid":"bbb","date":"2026-11-21 08:02:10 +0100","author":{"name":"oxidized","email":"o@x","time":"2026-11-21 08:02:10 +0100"},"message":"update core-sw01"},
             {"oid":"aaa","date":"2026-11-20 16:52:00 +0100","author":{"name":"oxidized"},"message":"update core-sw01"}]
            """));
        Assert.Equal(2, v.Count);
        Assert.Equal("bbb", v[0].Oid);
        Assert.Equal(2, v[0].Number);
        Assert.Equal(1, v[1].Number);
        Assert.Equal("oxidized", v[0].Author);
    }

    [Theory]
    [InlineData("\"hostname x\\nend\\n\"", "hostname x\nend\n")]
    [InlineData("[\"hostname x\\n\",\"end\\n\"]", "hostname x\nend")]
    [InlineData("hostname x\nend\n", "hostname x\nend\n")]
    public void Version_text_comes_in_several_shapes(string body, string expected) =>
        Assert.Equal(expected, OxidizedClient.ParseText(body));

    [Fact]
    public void Base_uri_keeps_sub_path()
    {
        Assert.Equal("https://h/oxidized/", OxidizedClient.BaseUri("https://h/oxidized").ToString());
        Assert.Equal("http://10.0.0.5:8888/", OxidizedClient.BaseUri("10.0.0.5:8888").ToString());
        Assert.Equal("https://h/", OxidizedClient.BaseUri("https://h/nodes").ToString());
    }

    // ---------------- HTTP ----------------

    private sealed class FakeOxidized : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public string? Auth;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Auth = request.Headers.Authorization?.ToString();
            var path = request.RequestUri.AbsolutePath;
            string body = path switch
            {
                "/ox/nodes.json" => """[{"name":"sw","full_name":"lab/sw","ip":"10.0.0.2","group":"lab","model":"ios","status":"success","time":"2026-11-21 07:02:10 UTC"}]""",
                "/ox/node/version.json" => """[{"oid":"b2","date":"2026-11-21 08:02:10 +0100"},{"oid":"a1","date":"2026-11-20 08:02:10 +0100"}]""",
                "/ox/node/version/view.json" => request.RequestUri.Query.Contains("oid=b2") ? "\"hostname sw\\nvlan 40\\n\"" : "\"hostname sw\\n\"",
                "/ox/node/fetch/lab/sw" => "hostname sw\nvlan 40\n",
                _ => "",
            };
            var res = new HttpResponseMessage(body.Length == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task Reads_nodes_versions_and_text_over_http()
    {
        var fake = new FakeOxidized();
        using var c = new OxidizedClient("https://oxidized.lab/ox", "remoteman", "pw", handler: fake);
        var nodes = await c.GetNodesAsync();
        var sw = Assert.Single(nodes);
        Assert.StartsWith("Basic ", fake.Auth);
        var versions = await c.GetVersionsAsync(sw);
        Assert.Contains("node_full=lab%2Fsw", fake.Requests.Last());
        Assert.Equal(["b2", "a1"], versions.Select(v => v.Oid));
        var newest = await c.GetVersionTextAsync(sw, versions[0]);
        Assert.Contains("group=lab", fake.Requests.Last());
        Assert.Contains("node=sw", fake.Requests.Last());
        Assert.Equal("hostname sw\nvlan 40\n", newest);
        Assert.Equal("hostname sw\nvlan 40\n", await c.GetLatestAsync(sw));
        var diff = LineDiff.Compare(LineDiff.Split(await c.GetVersionTextAsync(sw, versions[1])), LineDiff.Split(newest));
        Assert.Equal((1, 0), LineDiff.Count(diff));
    }

    [Fact]
    public async Task No_git_history_gives_empty_versions()
    {
        var fake = new FakeOxidized();
        using var c = new OxidizedClient("https://oxidized.lab/other", handler: fake);
        Assert.Empty(await c.GetVersionsAsync(Node("sw", "10.0.0.2")));
    }

    // ---------------- diff ----------------

    [Fact]
    public void Diff_finds_added_removed_and_changed_lines()
    {
        var a = LineDiff.Split("hostname sw\nvlan 10\n name A\n!\ninterface Gi1\n description lab\n switchport access vlan 20\n!\nend");
        var b = LineDiff.Split("hostname sw\nvlan 10\n name A\n!\nvlan 40\n name IOT\n!\ninterface Gi1\n description iot\n switchport access vlan 40\n!\nend");
        var d = LineDiff.Compare(a, b);
        Assert.Equal((5, 2), LineDiff.Count(d));
        // Rebuilding each side from the diff gives the inputs back.
        Assert.Equal(a, d.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text));
        Assert.Equal(b, d.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text));
        Assert.All(d.Where(l => l.Kind == DiffKind.Added), l => Assert.Equal(b[l.NewLine - 1], l.Text));
        Assert.All(d.Where(l => l.Kind == DiffKind.Removed), l => Assert.Equal(a[l.OldLine - 1], l.Text));
        // Removals come before additions in a changed block.
        var i = d.ToList().FindIndex(l => l.Text == " description lab");
        Assert.Equal(DiffKind.Removed, d[i].Kind);
        Assert.Equal(" switchport access vlan 20", d[i + 1].Text);
    }

    [Fact]
    public void Diff_of_random_edits_round_trips()
    {
        var rnd = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => "l" + rnd.Next(0, 8)).ToArray();
            var b = a.ToList();
            for (var k = rnd.Next(0, 6); k > 0; k--)
            {
                if (b.Count > 0 && rnd.Next(2) == 0) b.RemoveAt(rnd.Next(b.Count));
                else b.Insert(rnd.Next(b.Count + 1), "n" + rnd.Next(0, 8));
            }
            var d = LineDiff.Compare(a, b);
            Assert.Equal(a, d.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text));
            Assert.Equal(b, d.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text));
        }
    }

    [Fact]
    public void Saved_link_survives_the_payload_round_trip()
    {
        var e = Ssh("sw", "10.0.0.2");
        e.OxidizedNode = "lab/sw";
        Assert.Equal("lab/sw", RemoteMan.Core.Storage.EntryStore.Clone(e).OxidizedNode);
    }

    // ---------------- backup state ----------------

    [Fact]
    public void Backup_state_tells_ok_stale_failing_and_never_apart()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        OxidizedNode N(string status, double hoursAgo) => new("n", null, "10.0.0.1", "ios", status, now.AddHours(-hoursAgo), null);
        Assert.Equal("ok", N("success", 2).StateAt(now));
        Assert.Equal("stale", N("success", 30).StateAt(now));
        Assert.Equal("fail", N("no_connection", 1).StateAt(now));
        Assert.Equal("fail", N("fail", 100).StateAt(now));
        Assert.Equal("never", N("never", 0).StateAt(now));
        Assert.Equal("never", new OxidizedNode("n", null, null, null, "", null, null).StateAt(now));

        var snap = new OxidizedSnapshot([N("success", 1), N("success", 2), N("success", 48), N("fail", 1)], new OxidizedMatchOptions(), now);
        var c = snap.CountByState(now);
        Assert.Equal((2, 1, 1, 0), (c["ok"], c["stale"], c["fail"], c["never"]));
    }
}
