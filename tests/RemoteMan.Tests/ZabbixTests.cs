using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
using Xunit;

namespace RemoteMan.Tests;

public class ZabbixTests
{
    private static ZabbixHost Host(string id, string host, string ip, string name = "", bool maint = false, int available = 1,
        string[]? groups = null, string[]? templates = null, string dns = "") =>
        new(id, host, name, true, maint, [new ZabbixInterface(ip, dns, true, "10050", 1, available)],
            groups ?? ["Homelab"], templates ?? []);

    private static Entry Ssh(string name, params string[] hosts)
    {
        var e = new Entry { Kind = EntryKind.Ssh, Name = name, Ssh = new SshSettings() };
        foreach (var h in hosts) e.Endpoints.Add(new HostEndpoint(h, 22));
        return e;
    }

    [Fact]
    public void Saved_link_wins_over_address_and_name()
    {
        var m = new ZabbixMatcher([Host("1", "dc01", "10.0.20.5"), Host("2", "other", "10.0.0.9")]);
        var e = Ssh("dc01", "10.0.20.5");
        e.ZabbixHostId = "2";
        Assert.Equal("2", m.Match(e)!.Host.HostId);
        Assert.StartsWith("Saved link", m.Match(e)!.Reason);
    }

    [Fact]
    public void Matches_by_address_then_by_name()
    {
        var m = new ZabbixMatcher([Host("1", "pve-01", "10.0.10.11"), Host("2", "DC01", "192.168.99.5")]);
        Assert.Equal("1", m.Match(Ssh("hypervisor", "10.0.10.11"))!.Host.HostId);
        // NAT: the address differs, the name carries it (case-insensitive, bracket suffix ignored).
        var dc = Ssh("dc01 (Domain controller)", "203.0.113.7");
        Assert.Equal("2", m.Match(dc)!.Host.HostId);
        Assert.Contains("Name", m.Match(dc)!.Reason);
    }

    [Fact]
    public void Short_dns_name_of_endpoint_matches_host_name()
    {
        var m = new ZabbixMatcher([Host("1", "dc01", "10.0.20.5")]);
        Assert.Equal("1", m.Match(Ssh("Domain controller", "dc01.lab.local"))!.Host.HostId);
    }

    [Fact]
    public void Shared_nat_address_does_not_match()
    {
        var m = new ZabbixMatcher([Host("1", "a", "198.51.100.1"), Host("2", "b", "198.51.100.1")]);
        Assert.Null(m.Match(Ssh("something", "198.51.100.1")));
        Assert.Equal(2, m.Suggestions(Ssh("something", "198.51.100.1")).Count());
    }

    [Fact]
    public void Health_takes_worst_problem_and_maintenance()
    {
        var now = DateTimeOffset.UtcNow;
        var hosts = new[] { Host("1", "a", "10.0.0.1"), Host("2", "b", "10.0.0.2"), Host("3", "c", "10.0.0.3", maint: true), Host("4", "d", "10.0.0.4", available: 2), Host("5", "e", "10.0.0.5") };
        var problems = new[]
        {
            new ZabbixProblem("e1", "1", "Disk full", ZabbixSeverity.High, now.AddMinutes(-14), false, null, false),
            new ZabbixProblem("e2", "1", "Service down", ZabbixSeverity.Average, now.AddHours(-2), true, "Mads J", false),
            new ZabbixProblem("e3", "2", "Load high", ZabbixSeverity.Warning, now, false, null, false),
            new ZabbixProblem("e4", "3", "Reboot", ZabbixSeverity.Disaster, now, false, null, true),
            new ZabbixProblem("e5", "5", "Info only", ZabbixSeverity.Information, now, false, null, false),
        };
        var s = new ZabbixSnapshot(hosts, problems, now);
        Assert.Equal(ZabbixHealthState.High, s.HealthFor(Ssh("a", "10.0.0.1")).State);
        Assert.Equal("2 open problems", s.HealthFor(Ssh("a", "10.0.0.1")).Summary);
        Assert.Equal(ZabbixHealthState.Warning, s.HealthFor(Ssh("b", "10.0.0.2")).State);
        Assert.Equal(ZabbixHealthState.Maintenance, s.HealthFor(Ssh("c", "10.0.0.3")).State);
        Assert.Equal(ZabbixHealthState.Unreachable, s.HealthFor(Ssh("d", "10.0.0.4")).State);
        Assert.Equal(ZabbixHealthState.Ok, s.HealthFor(Ssh("e", "10.0.0.5")).State);
        Assert.Equal(ZabbixHealthState.None, s.HealthFor(Ssh("zz", "10.9.9.9")).State);
        Assert.Equal(3, s.ProblemHostCount);
    }

    [Fact]
    public void Import_plan_creates_nested_folders_and_links_existing()
    {
        var now = DateTimeOffset.UtcNow;
        var hosts = new[]
        {
            Host("1", "WIN2019", "172.16.5.10", groups: ["School lab"], templates: ["Windows by Zabbix agent"]),
            Host("2", "backup-01", "10.0.10.30", groups: ["Homelab/Linux"], templates: ["Linux by Zabbix agent"]),
            Host("3", "nas-01", "10.0.10.40", groups: ["Homelab/Storage"], templates: ["Synology by SNMP"]),
        };
        var existing = Ssh("WIN2019", "172.16.5.10");
        var homelab = new Entry { Kind = EntryKind.Folder, Name = "homelab" };
        var s = new ZabbixSnapshot(hosts, [], now);
        var cands = ZabbixImport.Candidates(s, [existing, homelab], ZabbixAddressChoice.InterfaceAddress, ZabbixFolderChoice.PerHostGroup);
        Assert.Same(existing, cands.Single(c => c.Host.HostId == "1").Existing);
        Assert.Equal(EntryKind.Rdp, cands.Single(c => c.Host.HostId == "1").Kind);
        Assert.Equal(EntryKind.Web, cands.Single(c => c.Host.HostId == "3").Kind);

        var plan = ZabbixImport.Plan(cands, [existing, homelab], null);
        Assert.Single(plan.Linked);
        Assert.Equal("1", plan.Linked[0].ZabbixHostId);
        Assert.Equal(2, plan.NewEntries.Count);
        // "Homelab" is reused (case-insensitive); Linux and Storage are new under it.
        Assert.Equal(["Linux", "Storage"], plan.NewFolders.Select(f => f.Name).OrderBy(n => n));
        Assert.All(plan.NewFolders, f => Assert.Equal(homelab.Id, f.ParentId));
        var backup = plan.NewEntries.Single(e => e.Name == "backup-01");
        Assert.Equal("10.0.10.30", backup.Endpoints[0].Host);
        Assert.Equal("2", backup.ZabbixHostId);
    }

    [Fact]
    public void Api_endpoint_and_problems_url()
    {
        Assert.Equal("https://zabbix.lab.local/zabbix/api_jsonrpc.php", ZabbixClient.ApiEndpoint("zabbix.lab.local/zabbix/").ToString());
        Assert.Equal("https://z/api_jsonrpc.php", ZabbixClient.ApiEndpoint("https://z/api_jsonrpc.php").ToString());
        Assert.Equal("https://z/zabbix/zabbix.php?action=problem.view&filter_set=1&hostids%5B%5D=10512",
            ZabbixClient.ProblemsUrl("https://z/zabbix", "10512"));
    }

    [Theory]
    [InlineData("7.0.5", true, "hostgroups")]
    [InlineData("6.0.30", false, "groups")]
    public async Task Client_uses_bearer_on_new_versions_and_auth_field_on_old(string version, bool bearer, string groupsKey)
    {
        var handler = new FakeZabbix(version, groupsKey);
        using var c = new ZabbixClient("https://z/zabbix", "TOKEN", handler: handler);
        await c.ConnectAsync();
        var hosts = await c.GetHostsAsync();
        var problems = await c.GetProblemsAsync(ZabbixSeverity.Warning);

        Assert.Equal("DC01", hosts.Single().Host);
        Assert.Equal(["Homelab"], hosts.Single().Groups);
        Assert.True(hosts.Single().Unreachable);
        var p = Assert.Single(problems);
        Assert.Equal("10512", p.HostId);
        Assert.Equal(ZabbixSeverity.High, p.Severity);
        Assert.Equal("Mads J", p.AcknowledgedBy);
        foreach (var r in handler.Requests.Where(r => r.Method != "apiinfo.version"))
        {
            Assert.Equal(bearer, r.Bearer == "TOKEN");
            Assert.Equal(!bearer, r.AuthField == "TOKEN");
        }
        Assert.Contains(handler.Requests, r => r.Method == "host.get" && r.Params?[bearer ? "selectHostGroups" : "selectGroups"] is not null);
    }

    [Fact]
    public async Task Problems_still_load_when_the_token_cannot_read_users()
    {
        var handler = new FakeZabbix("7.4.0", "hostgroups") { NoUserRights = true };
        using var c = new ZabbixClient("https://z", "TOKEN", handler: handler);
        var p = Assert.Single(await c.GetProblemsAsync(ZabbixSeverity.Warning));
        Assert.True(p.Acknowledged);
        Assert.Null(p.AcknowledgedBy);
    }

    [Fact]
    public async Task Problems_on_disabled_triggers_are_left_out()
    {
        var handler = new FakeZabbix("7.0.5", "hostgroups");
        using var c = new ZabbixClient("https://z", "TOKEN", handler: handler);
        var p = Assert.Single(await c.GetProblemsAsync(ZabbixSeverity.Warning));
        Assert.Equal("901", p.EventId);
        Assert.Contains(handler.Requests, r => r.Method == "trigger.get" && r.Params?["monitored"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task No_monitored_triggers_means_no_problems_not_an_error()
    {
        var handler = new FakeZabbix("8.0.0", "hostgroups") { NoMonitoredTriggers = true };
        using var c = new ZabbixClient("https://z", "TOKEN", handler: handler);
        Assert.Empty(await c.GetProblemsAsync(ZabbixSeverity.Warning));
    }

    [Fact]
    public async Task Suppressed_problems_are_left_out()
    {
        // 903 is suppressed (maintenance or manual suppression) on an enabled trigger.
        var handler = new FakeZabbix("7.0.5", "hostgroups");
        using var c = new ZabbixClient("https://z", "TOKEN", handler: handler);
        var problems = await c.GetProblemsAsync(ZabbixSeverity.Warning);
        Assert.DoesNotContain(problems, p => p.EventId == "903");
    }

    [Fact]
    public void Suppressed_problems_raise_no_alarm()
    {
        var now = DateTimeOffset.UtcNow;
        var hosts = new[] { Host("1", "a", "10.0.0.1"), Host("2", "b", "10.0.0.2") };
        var problems = new[]
        {
            new ZabbixProblem("e1", "1", "Disk full", ZabbixSeverity.Disaster, now, false, null, true),
            new ZabbixProblem("e2", "2", "Load high", ZabbixSeverity.Warning, now, false, null, false),
            new ZabbixProblem("e3", "2", "Reboot", ZabbixSeverity.High, now, false, null, true),
        };
        var s = new ZabbixSnapshot(hosts, problems, now);
        Assert.Equal(ZabbixHealthState.Ok, s.HealthFor(Ssh("a", "10.0.0.1")).State);
        Assert.Empty(s.HealthFor(Ssh("a", "10.0.0.1")).Problems);
        Assert.Equal(ZabbixHealthState.Warning, s.HealthFor(Ssh("b", "10.0.0.2")).State);
        Assert.Equal("1 open problem", s.HealthFor(Ssh("b", "10.0.0.2")).Summary);
        Assert.Equal(["e2"], s.Problems.Select(p => p.EventId));
        Assert.Equal(1, s.ProblemHostCount);
    }

    [Fact]
    public async Task Client_reports_api_errors_readably()
    {
        var handler = new FakeZabbix("7.0.0", "hostgroups") { FailAuth = true };
        using var c = new ZabbixClient("https://z", "BAD", handler: handler);
        var ex = await Assert.ThrowsAsync<ZabbixApiException>(() => c.ConnectAsync());
        Assert.Contains("Not authorized", ex.Message);
    }

    private sealed class FakeZabbix(string version, string groupsKey) : HttpMessageHandler
    {
        // What Zabbix 7.x accepts in problem.get's selectAcknowledges.
        private static readonly HashSet<string> AckFields = ["acknowledgeid", "userid", "clock", "message", "action", "old_severity",
            "new_severity", "suppress_until", "taskid", "maintenanceid", "details", "cep_ruleid"];
        public bool FailAuth { get; init; }
        public bool NoUserRights { get; init; }
        public bool NoMonitoredTriggers { get; init; }
        public List<(string Method, string? Bearer, string? AuthField, JsonNode? Params)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var method = body["method"]!.GetValue<string>();
            Requests.Add((method, request.Headers.Authorization?.Parameter, body["auth"]?.GetValue<string>(), body["params"]));
            JsonNode result;
            try { result = Result(method, body); }
            catch (InvalidOperationException ex)
            {
                var e = new JsonObject { ["jsonrpc"] = "2.0", ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Invalid params.", ["data"] = ex.Message }, ["id"] = 1 };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(e.ToJsonString(), Encoding.UTF8, "application/json") };
            }
            var resp = FailAuth && method != "apiinfo.version"
                ? new JsonObject { ["jsonrpc"] = "2.0", ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Invalid params.", ["data"] = "Not authorized." }, ["id"] = 1 }
                : new JsonObject { ["jsonrpc"] = "2.0", ["result"] = result, ["id"] = 1 };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resp.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        private JsonNode Result(string method, JsonNode body)
        {
            return method switch
            {
                "apiinfo.version" => JsonValue.Create(version)!,
                "host.get" when body["params"]!["countOutput"] is not null => JsonValue.Create("1")!,
                "host.get" => JsonNode.Parse($$"""[{"hostid":"10512","host":"DC01","name":"DC01","status":"0","maintenance_status":"0","interfaces":[{"ip":"10.0.20.5","dns":"","useip":"1","port":"10050","type":"1","main":"1","available":"2"}],"{{groupsKey}}":[{"groupid":"5","name":"Homelab"}],"parentTemplates":[{"templateid":"1","name":"Windows by Zabbix agent"}]}]""")!,
                "problem.get" when !body["params"]!["selectAcknowledges"]!.AsArray().All(f => AckFields.Contains(f!.GetValue<string>())) =>
                    throw new InvalidOperationException("Invalid parameter \"/selectAcknowledges\": unknown field"),
                "problem.get" => JsonNode.Parse("""[{"eventid":"901","objectid":"77","name":"Disk full","severity":"4","clock":"1795150000","acknowledged":"1","suppressed":"0","acknowledges":[{"userid":"3","action":"6","clock":"1795150100"}]},{"eventid":"902","objectid":"78","name":"Interface down","severity":"5","clock":"1795150200","acknowledged":"0","suppressed":"0","acknowledges":[]},{"eventid":"903","objectid":"77","name":"CPU high","severity":"4","clock":"1795150300","acknowledged":"0","suppressed":"1","acknowledges":[]}]""")!,
                "user.get" when NoUserRights => throw new InvalidOperationException("No permissions"),
                "user.get" => JsonNode.Parse("""[{"userid":"3","username":"mads","name":"Mads","surname":"J"}]""")!,
                // Trigger 78 is disabled: Zabbix only returns it when "monitored" isn't asked for.
                // Zabbix sends an empty result as [] even with preservekeys.
                "trigger.get" when NoMonitoredTriggers => JsonNode.Parse("[]")!,
                "trigger.get" when body["params"]!["monitored"] is null => JsonNode.Parse("""{"77":{"triggerid":"77","hosts":[{"hostid":"10512"}]},"78":{"triggerid":"78","hosts":[{"hostid":"10512"}]}}""")!,
                "trigger.get" => JsonNode.Parse("""{"77":{"triggerid":"77","hosts":[{"hostid":"10512"}]}}""")!,
                _ => JsonValue.Create("?")!,
            };
        }
    }
}
