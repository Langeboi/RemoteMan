using System.Text.Json.Nodes;
using RemoteMan.App.Services;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Mcp;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// The MCP tools: what AI clients (Claude Desktop, Claude Code, VS Code…) can see and do in RemoteMan.
/// Everything runs on the UI thread, like a click would. Secrets never leave: no passwords, keys, usernames of
/// stored logins, or secret custom fields, and credential entries aren't listed at all.
/// </summary>
public sealed partial class MainViewModel
{
    private const int MaxConfigChars = 200_000;

    public McpSettings Mcp => _settings.Mcp;

    /// <summary>Every open connection: tabs in both panes and sessions in their own windows.</summary>
    private IEnumerable<SessionViewModel> OpenConnections =>
        Sessions.Where(s => s.IsConnection).Concat(_windowSessions.Where(s => !Sessions.Contains(s)));

    public async Task<McpToolResult> RunMcpToolAsync(string tool, JsonObject a, CancellationToken ct) => tool switch
    {
        McpCatalog.ListEntries => ListEntriesTool(a),
        McpCatalog.GetEntry => GetEntryTool(a),
        McpCatalog.ListSessions => ListSessionsTool(),
        McpCatalog.ReadTerminal => ReadTerminalTool(a),
        McpCatalog.TypeInTerminal => TypeInTerminalTool(a),
        McpCatalog.OpenConnection => await OpenConnectionToolAsync(a, ct),
        McpCatalog.TeamPresence => TeamPresenceTool(),
        McpCatalog.ZabbixProblems => ZabbixProblemsTool(a),
        McpCatalog.ConfigBackups => ConfigBackupsTool(a),
        McpCatalog.GetDeviceConfig => await DeviceConfigToolAsync(a, ct),
        _ => McpToolResult.Error($"Unknown tool {tool}."),
    };

    // ---------------- entries ----------------

    private IEnumerable<Entry> VisibleEntries => (_store?.Entries ?? [])
        .Where(e => !e.IsHidden && e.Kind != EntryKind.Credential && !IsInCredentialFolder(e));

    /// <summary>Folders that only hold credentials are left out with their contents.</summary>
    private bool IsInCredentialFolder(Entry e)
    {
        for (var p = e.ParentId; p is { } id && _store?.Get(id) is { } f; p = f.ParentId)
            if (f.Kind == EntryKind.Credential) return true;
        return false;
    }

    private string FolderOf(Entry e) => e.ParentId is { } p && _store?.Get(p) is { } f ? FolderPath(f) : "";

    private static string McpKind(EntryKind k) => k.ToString().ToLowerInvariant();

    private Entry FindEntry(string key)
    {
        if (_store is null) throw new McpArgumentException("No data source is open in RemoteMan yet.");
        var visible = VisibleEntries.ToList();
        if (Guid.TryParse(key, out var id)) return visible.FirstOrDefault(e => e.Id == id) ?? throw new McpArgumentException($"No entry with id {key}.");
        var byName = visible.Where(e => e.Name.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new McpArgumentException($"No entry is called \"{key}\". Use list_entries to find it."),
            _ => throw new McpArgumentException($"{byName.Count} entries are called \"{key}\" (in {string.Join(", ", byName.Select(e => FolderOf(e) is { Length: > 0 } f ? f : "the top level"))}). Use the id instead."),
        };
    }

    /// <summary>The hosts as you'd type them: the URL for web entries (any login in it removed), host:port otherwise.</summary>
    private static JsonArray Hosts(Entry e)
    {
        var list = new List<string>();
        if (e.Kind == EntryKind.Web && e.Web?.Url is { Length: > 0 } url && url != "https://")
        {
            var u = WebSettings.NormalizeUrl(url);
            if (Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.UserInfo.Length > 0)
                u = new UriBuilder(parsed) { UserName = "", Password = "" }.Uri.ToString();
            list.Add(u);
        }
        list.AddRange(e.Endpoints.Select(x => x.ToString()));
        if (e.Kind == EntryKind.Serial && e.Serial?.PortName is { Length: > 0 } port) list.Add(port);
        return new JsonArray(list.Distinct().Select(h => (JsonNode)h).ToArray());
    }

    private JsonObject Summary(Entry e)
    {
        var o = new JsonObject
        {
            ["id"] = e.Id.ToString(),
            ["name"] = e.Name,
            ["kind"] = McpKind(e.Kind),
            ["folder"] = FolderOf(e),
        };
        if (e.Kind != EntryKind.Folder) o["hosts"] = Hosts(e);
        if (e.Description is { Length: > 0 } d) o["description"] = d;
        if (e.Tags is { Length: > 0 } t) o["tags"] = t;
        if (IsFavorite(e)) o["favorite"] = true;
        if (ZabbixSnapshot?.HealthFor(e) is { IsLinked: true } h) o["zabbix"] = h.Summary;
        return o;
    }

    private McpToolResult ListEntriesTool(JsonObject a)
    {
        if (_store is null) return McpToolResult.Error("No data source is open in RemoteMan yet.");
        IEnumerable<Entry> list = VisibleEntries;
        if (a.Str("kind") is { } kind)
        {
            if (!Enum.TryParse<EntryKind>(kind, true, out var k)) throw new McpArgumentException($"Unknown kind \"{kind}\". Use one of: {string.Join(", ", McpCatalog.Kinds)}.");
            list = list.Where(e => e.Kind == k);
        }
        if (a.Str("folder") is { } folder)
        {
            var f = folder.Trim().Replace('/', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var want = string.Join(" ", f);
            list = list.Where(e =>
            {
                var path = string.Join(" ", FolderOf(e).Replace('/', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
                return path.Equals(want, StringComparison.OrdinalIgnoreCase) || path.StartsWith(want + " ", StringComparison.OrdinalIgnoreCase)
                    || path.Split(' ').Contains(want, StringComparer.OrdinalIgnoreCase);
            });
        }
        if (a.Str("query") is { } q)
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list = list.Where(e =>
            {
                var hay = $"{e.Name} {FolderOf(e)} {e.Description} {e.Tags} {string.Join(' ', Hosts(e).Select(h => (string?)h))}";
                return words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
            });
        }
        var limit = a.Int("limit", 100, 1, 500);
        var all = list.OrderBy(FolderOf, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return McpToolResult.Json(new JsonObject
        {
            ["count"] = all.Count,
            ["shown"] = Math.Min(all.Count, limit),
            ["entries"] = new JsonArray(all.Take(limit).Select(e => (JsonNode)Summary(e)).ToArray()),
        });
    }

    private McpToolResult GetEntryTool(JsonObject a)
    {
        var e = FindEntry(a.Required("entry"));
        var eff = _store!.Effective(e);
        var o = Summary(e);
        if (e.CreatedAt is { } c) o["created"] = $"{c.ToLocalTime():yyyy-MM-dd HH:mm} by {e.CreatedBy}";
        o["updated"] = $"{e.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} by {e.UpdatedBy}";

        if (e.IsConnectable)
        {
            var (source, holder) = _store.ResolveLogin(e);
            o["login"] = source switch
            {
                LoginSource.Saved when holder?.CredentialId is { } cid && _store.Get(cid) is { } cred => $"saved credential \"{cred.Name}\"",
                LoginSource.Saved => "saved credential",
                LoginSource.WindowsLogin => "the Windows user (single sign-on)",
                LoginSource.Embedded => "typed into the entry",
                _ => "none stored (asked when connecting)",
            };
        }
        switch (e.Kind)
        {
            case EntryKind.Rdp when eff.Rdp is { } rdp:
                o["rdp"] = new JsonObject
                {
                    ["admin_session"] = rdp.AdminSession,
                    ["rd_gateway"] = rdp.UseRdGateway ? rdp.GatewayHost : null,
                    ["ssh_tunnel"] = rdp.SshTunnelId is { } t ? _store.Get(t)?.Name : null,
                };
                break;
            case EntryKind.Ssh or EntryKind.Sftp when eff.Ssh?.JumpHostId is { } j:
                o["jump_host"] = _store.Get(j)?.Name;
                break;
        }
        if (eff.Connection is { } conn)
        {
            var co = new JsonObject();
            if (conn.WakeOnLan) co["wake_on_lan"] = conn.MacAddress;
            if (conn.GatewaySite is { Length: > 0 } site) co["gateway_site"] = site + (conn.GatewayAlways ? " (always)" : " (when not reachable directly)");
            if (co.Count > 0) o["connection"] = co;
        }
        if (e.Fields is { Count: > 0 } fields)
            o["fields"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Name, (JsonNode?)(f.Secret ? "(secret, hidden)" : f.Value ?? ""))));

        if (ZabbixSnapshot?.HealthFor(e) is { IsLinked: true } h)
            o["zabbix"] = new JsonObject
            {
                ["host"] = h.Host!.DisplayName,
                ["state"] = h.State.ToString().ToLowerInvariant(),
                ["summary"] = h.Summary,
                ["problems"] = new JsonArray(h.Problems.Select(p => (JsonNode)ProblemJson(p)).ToArray()),
            };
        if (OxidizedSnapshot?.Matcher.Match(e) is { } ox) o["config_backup"] = NodeJson(ox.Node);

        var others = _store.PresenceFor(e.Id).Where(p => p.IsAlive(DateTimeOffset.UtcNow) && !IsMe(p)).ToList();
        if (others.Count > 0)
            o["teammates_here"] = new JsonArray(others.Select(p => (JsonNode)$"{p.User} ({p.State.ToString().ToLowerInvariant()}, on {p.Machine})").ToArray());
        var mine = OpenConnections.Where(s => s.Entry.Id == e.Id).ToList();
        if (mine.Count > 0) o["open_sessions"] = new JsonArray(mine.Select(s => (JsonNode)s.McpId).ToArray());
        return McpToolResult.Json(o);
    }

    private bool IsMe(Presence p) =>
        _store is { } s && p.User.Equals(s.User, StringComparison.OrdinalIgnoreCase) && p.Machine.Equals(s.Machine, StringComparison.OrdinalIgnoreCase);

    // ---------------- sessions ----------------

    private McpToolResult ListSessionsTool()
    {
        var list = OpenConnections.Select(s =>
        {
            var o = new JsonObject
            {
                ["session"] = s.McpId,
                ["title"] = s.Title,
                ["kind"] = McpKind(s.Entry.Kind),
                ["entry_id"] = s.Entry.Id.ToString(),
                ["host"] = s.Via ?? s.Endpoint.ToString(),
                ["showing"] = s.IsActive,
                ["terminal"] = s is SshSessionViewModel,
            };
            if (s is SshSessionViewModel ssh) o["connected"] = ssh.IsConnected;
            if (s.IsReconnecting) o["reconnecting"] = true;
            if (s.UsedFallback) o["on_fallback_host"] = true;
            if (s.Share is not null) o["shared_with_team"] = true;
            return (JsonNode)o;
        }).ToArray();
        return McpToolResult.Json(new JsonObject { ["sessions"] = new JsonArray(list) });
    }

    private SessionViewModel FindSession(string? id)
    {
        if (id is null)
            return ActiveSession is { IsConnection: true } a ? a
                : throw new McpArgumentException("No session is showing in RemoteMan. Pass a session id from list_sessions.");
        return OpenConnections.FirstOrDefault(s => s.McpId.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpArgumentException($"No open session {id}. It may have been closed; see list_sessions.");
    }

    private static SshSessionViewModel Terminal(SessionViewModel s) => s as SshSessionViewModel
        ?? throw new McpArgumentException($"{s.Title} is a {s.KindTag} session, not a terminal. Only SSH, Telnet and serial sessions have text to read or type into.");

    private McpToolResult ReadTerminalTool(JsonObject a)
    {
        if (Mcp.Terminal == McpTerminalAccess.Off)
            return McpToolResult.Error("Reading terminals is switched off in RemoteMan (AI button > Terminals).");
        var s = Terminal(FindSession(a.Str("session")));
        var lines = a.Int("lines", 100, 1, 2000);
        var text = s.Tail.LastLines(lines);
        var head = $"Session {s.McpId}, {s.Title} ({s.KindTag}, {s.Via ?? s.Endpoint.ToString()}){(s.IsConnected ? "" : ", not connected")}. Last {lines} lines:";
        return new McpToolResult($"{head}\n<terminal_output>\n{text}\n</terminal_output>");
    }

    private McpToolResult TypeInTerminalTool(JsonObject a)
    {
        var enter = a.Bool("press_enter");
        switch (Mcp.Terminal)
        {
            case McpTerminalAccess.Off or McpTerminalAccess.Read:
                return McpToolResult.Error("Typing into terminals is switched off in RemoteMan (AI button > Terminals). Show the user the command instead.");
            case McpTerminalAccess.Type when enter:
                return McpToolResult.Error("RemoteMan only lets AI apps type, not run: call again without press_enter and ask the user to press Enter, " +
                    "or ask them to allow running commands (AI button > Terminals > Type and run).");
        }
        var s = Terminal(FindSession(a.Required("session")));
        var text = a.Required("text");
        if (s.InputLocked) return McpToolResult.Error($"{s.Title} is shared and a teammate has control right now.");
        if (enter)
        {
            if (!s.Send(text.TrimEnd('\r', '\n').Replace("\r\n", "\r").Replace('\n', '\r') + "\r"))
                return McpToolResult.Error($"{s.Title} isn't connected.");
            Message = $"An AI app ran a command in {s.Title}";
            return new McpToolResult($"Typed into {s.Title} and pressed Enter. Use read_terminal to see the result.");
        }
        if (s.SendToTerminal is not { } type) return McpToolResult.Error($"{s.Title} isn't connected.");
        type(text);
        Message = $"An AI app typed into {s.Title}. Review it, then press Enter yourself.";
        return new McpToolResult($"Typed into {s.Title} without pressing Enter. The user will review it and press Enter.");
    }

    private async Task<McpToolResult> OpenConnectionToolAsync(JsonObject a, CancellationToken ct)
    {
        if (!Mcp.OpenConnections) return McpToolResult.Error("Opening connections is switched off in RemoteMan (AI button).");
        var e = FindEntry(a.Required("entry"));
        if (!e.IsConnectable) return McpToolResult.Error($"{e.Name} is a {McpKind(e.Kind)}, not something to connect to.");
        var before = OpenConnections.ToHashSet();
        var connect = ConnectEntryAsync(_store!.Get(e.Id) ?? e);
        // A password prompt can keep it waiting on the user: answer anyway after a while.
        var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(60), ct)) == connect;
        if (finished) await connect;
        var opened = OpenConnections.FirstOrDefault(s => !before.Contains(s) && s.Entry.Id == e.Id);
        if (opened is not null)
            return McpToolResult.Json(new JsonObject
            {
                ["opened"] = e.Name,
                ["session"] = opened.McpId,
                ["host"] = opened.Via ?? opened.Endpoint.ToString(),
                ["on_fallback_host"] = opened.UsedFallback,
                ["note"] = opened is SshSessionViewModel ? "The terminal is starting; read_terminal shows it once it's logged in." : "Open in RemoteMan.",
            });
        if (finished && (e.Kind is EntryKind.PowerShell or EntryKind.WinBox || e.Kind == EntryKind.Web && e.Web?.Browser != BrowserKind.Embedded))
            return new McpToolResult($"{e.Name} was started outside RemoteMan (its own window or program).");
        return McpToolResult.Error(finished
            ? $"{e.Name} didn't open. RemoteMan says: {(Message is { Length: > 0 } m ? m : "no host answered.")}"
            : $"{e.Name} is still connecting, or RemoteMan is waiting for the user (a password prompt). Check list_sessions in a moment.");
    }

    private McpToolResult TeamPresenceTool()
    {
        if (_store is null) return McpToolResult.Error("No data source is open in RemoteMan yet.");
        if (!IsSharedSource) return new McpToolResult("This is a local data source: nobody else is on it.");
        var now = DateTimeOffset.UtcNow;
        var people = _store.Presence.Where(p => p.IsAlive(now) && !IsMe(p))
            .GroupBy(p => (p.User, p.Machine))
            .Select(g => (JsonNode)new JsonObject
            {
                ["user"] = g.Key.User,
                ["machine"] = g.Key.Machine,
                ["on"] = new JsonArray(g.Where(p => p.EntryId != Guid.Empty).Select(p => (JsonNode)new JsonObject
                {
                    ["entry"] = _store.Get(p.EntryId)?.Name ?? p.EntryId.ToString(),
                    ["entry_id"] = p.EntryId.ToString(),
                    ["state"] = p.State.ToString().ToLowerInvariant(),
                }).ToArray()),
            }).ToArray();
        return McpToolResult.Json(new JsonObject { ["online"] = new JsonArray(people) });
    }

    // ---------------- Zabbix and Oxidized ----------------

    private static JsonObject ProblemJson(ZabbixProblem p) => new()
    {
        ["problem"] = p.Name,
        ["severity"] = p.Severity.ToString().ToLowerInvariant(),
        ["since"] = p.Since.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        ["acknowledged"] = p.Acknowledged ? p.AcknowledgedBy ?? "yes" : null,
    };

    private McpToolResult ZabbixProblemsTool(JsonObject a)
    {
        if (ZabbixSnapshot is not { } snap)
            return McpToolResult.Error(ZabbixCfg.IsConfigured ? "Zabbix hasn't answered yet; try again in a moment." : "Zabbix isn't set up in RemoteMan (More > Zabbix).");
        var min = ZabbixSeverity.NotClassified;
        if (a.Str("min_severity") is { } sev && !Enum.TryParse(sev, true, out min)) throw new McpArgumentException($"Unknown severity \"{sev}\".");

        var entriesByHost = VisibleEntries.Where(e => e.IsConnectable)
            .Select(e => (Entry: e, Health: snap.HealthFor(e))).Where(x => x.Health.IsLinked)
            .ToLookup(x => x.Health.Host!.HostId, x => x.Entry);
        IEnumerable<ZabbixHost> hosts = snap.Hosts;
        if (a.Str("entry") is { } key)
        {
            var e = FindEntry(key);
            if (snap.HealthFor(e) is not { IsLinked: true } h) return new McpToolResult($"{e.Name} isn't linked to a Zabbix host.");
            hosts = [h.Host!];
        }
        var hostById = snap.Hosts.ToDictionary(h => h.HostId);
        var ids = hosts.Select(h => h.HostId).ToHashSet();
        var problems = snap.Problems.Where(p => ids.Contains(p.HostId) && !p.Suppressed && p.Severity >= min)
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since)
            .Select(p =>
            {
                var o = ProblemJson(p);
                o["host"] = hostById.TryGetValue(p.HostId, out var h) ? h.DisplayName : p.HostId;
                o["entries"] = new JsonArray(entriesByHost[p.HostId].Select(e => (JsonNode)new JsonObject { ["id"] = e.Id.ToString(), ["name"] = e.Name }).ToArray());
                return (JsonNode)o;
            }).ToArray();
        var unreachable = hosts.Where(h => h.Enabled && h.Unreachable).Select(h => (JsonNode)h.DisplayName).ToArray();
        return McpToolResult.Json(new JsonObject
        {
            ["as_of"] = snap.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            ["problems"] = new JsonArray(problems),
            ["unreachable_hosts"] = new JsonArray(unreachable),
        });
    }

    private static JsonObject NodeJson(OxidizedNode n) => new()
    {
        ["node"] = n.FullName,
        ["ip"] = n.Ip,
        ["model"] = n.Model,
        ["state"] = n.StateAt(DateTimeOffset.Now),
        ["status"] = n.StatusText,
        ["last_backup"] = n.LastRun?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        ["last_changed"] = n.LastChanged?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
    };

    private McpToolResult ConfigBackupsTool(JsonObject a)
    {
        if (OxidizedSnapshot is not { } snap)
            return McpToolResult.Error(OxidizedCfg.IsConfigured ? "Oxidized hasn't answered yet; try again in a moment." : "Oxidized isn't set up in RemoteMan (More > Oxidized).");
        var linked = VisibleEntries.Where(e => e.IsConnectable)
            .Select(e => (Entry: e, Match: snap.Matcher.Match(e))).Where(x => x.Match is not null)
            .ToLookup(x => x.Match!.Node.FullName, x => x.Entry);
        IEnumerable<OxidizedNode> nodes = snap.Nodes;
        if (a.Str("entry") is { } key)
        {
            var e = FindEntry(key);
            if (snap.Matcher.Match(e) is not { } m) return new McpToolResult($"{e.Name} isn't linked to an Oxidized node.");
            nodes = [m.Node];
        }
        if (a.Str("query") is { } q)
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            nodes = nodes.Where(n => words.All(w => $"{n.FullName} {n.Ip} {n.Model}".Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        if (a.Bool("only_problems")) nodes = nodes.Where(n => n.StateAt(DateTimeOffset.Now) != "ok");
        var list = nodes.OrderBy(n => n.FullName, StringComparer.OrdinalIgnoreCase).Take(500).Select(n =>
        {
            var o = NodeJson(n);
            o["entries"] = new JsonArray(linked[n.FullName].Select(e => (JsonNode)new JsonObject { ["id"] = e.Id.ToString(), ["name"] = e.Name }).ToArray());
            return (JsonNode)o;
        }).ToArray();
        return McpToolResult.Json(new JsonObject
        {
            ["as_of"] = snap.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            ["devices"] = new JsonArray(list),
        });
    }

    private async Task<McpToolResult> DeviceConfigToolAsync(JsonObject a, CancellationToken ct)
    {
        if (!Mcp.DeviceConfigs)
            return McpToolResult.Error("Sharing device configs is switched off in RemoteMan, because configs can hold password hashes and keys. " +
                "The user can allow it under the AI button > Device configs.");
        if (OxidizedSnapshot is not { } snap)
            return McpToolResult.Error(OxidizedCfg.IsConfigured ? "Oxidized hasn't answered yet; try again in a moment." : "Oxidized isn't set up in RemoteMan (More > Oxidized).");
        OxidizedNode? node;
        if (a.Str("node") is { } name)
            node = snap.Matcher.Get(name) ?? snap.Nodes.FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpArgumentException($"Oxidized has no node \"{name}\". See config_backups.");
        else if (a.Str("entry") is { } key)
        {
            var e = FindEntry(key);
            node = snap.Matcher.Match(e)?.Node ?? throw new McpArgumentException($"{e.Name} isn't linked to an Oxidized node.");
        }
        else throw new McpArgumentException("Pass \"entry\" or \"node\".");

        using var client = await ConnectOxidizedAsync(ct);
        var text = await client.GetLatestAsync(node, ct);
        var cut = text.Length > MaxConfigChars;
        Message = $"An AI app read the config of {node.FullName}";
        return new McpToolResult(
            $"Latest config of {node.FullName} ({node.Model}), backed up {node.LastRun?.ToLocalTime():yyyy-MM-dd HH:mm}" +
            (cut ? $", first {MaxConfigChars} characters" : "") + ":\n<device_config>\n" + (cut ? text[..MaxConfigChars] : text) + "\n</device_config>");
    }
}
