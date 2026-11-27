using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using RemoteMan.Gateway.Server.Hosted;

namespace RemoteMan.Gateway.Server.Admin;

/// <summary>
/// Hosted workspaces in the console (admins: accounts, workspaces, members, backups; members: their own account) and
/// the app's sign-in under /v1/account. Only mapped when the relay has a hosted PostgreSQL server (HOSTED_DB).
/// </summary>
public sealed partial class AdminConsole
{
    private void MapHosted(RouteGroupBuilder api)
    {
        if (HostedSvc is not { } h) return;
        var g = api.MapGroup("/hosted");

        g.MapPost("/accounts", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            var name = Str(b, "name") ?? "";
            var ws = new List<(string, WorkspaceAccess)>();
            if (b.TryGetProperty("workspaces", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var x in list.EnumerateArray())
                    if (Str(x, "id") is { } id) ws.Add((id, ParseAccess(Str(x, "access")) ?? WorkspaceAccess.Edit));
            var a = await h.AddAccountAsync(name, Str(b, "fullName"), Str(b, "password") ?? "", !b.TryGetProperty("mustChange", out var mc) || mc.ValueKind != JsonValueKind.False, ws, s.User, c.RequestAborted);
            return Results.Json(new { name = a.Name }, GatewayProtocol.Json);
        }));
        g.MapPut("/accounts/{name}", (HttpContext c, string name) => AuthedBody(c, async (s, b) =>
        {
            await h.UpdateAccountAsync(name, Str(b, "fullName"), b.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True, s.User, c.RequestAborted);
            return Ok();
        }));
        g.MapPost("/accounts/{name}/password", (HttpContext c, string name) => AuthedBody(c, async (s, b) =>
        {
            h.ResetPassword(name, Str(b, "password") ?? "", s.User);
            await Task.CompletedTask;
            return Ok();
        }));
        g.MapPost("/accounts/{name}/signout", (HttpContext c, string name) => Authed(c, async s =>
        {
            await h.SignOutEverywhereAsync(name, s.User, c.RequestAborted);
            return Ok();
        }));
        g.MapDelete("/accounts/{name}", (HttpContext c, string name) => Authed(c, async s =>
        {
            await h.RemoveAccountAsync(name, s.User, c.RequestAborted);
            foreach (var x in _sessions.Values.Where(x => x.Member && x.User.Equals(name, StringComparison.OrdinalIgnoreCase))) _sessions.TryRemove(x.Id, out _);
            return Ok();
        }));
        // An admin signs out anyone's app; a member only their own.
        g.MapDelete("/apps/{id}", (HttpContext c, string id) => AuthedAny(c, s =>
        {
            if (h.Store.Tokens.FirstOrDefault(t => t.Id == id) is not { } t) return Err(404, "That sign-in isn't there any more.");
            if (s.Member && !t.Account.Equals(s.User, StringComparison.OrdinalIgnoreCase)) return Err(403, "Not yours.");
            h.Store.RemoveToken(id);
            Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "account.signout", null, t.Account, $"{t.Account} signed out on {t.Device ?? "a PC"}", s.User));
            return Ok();
        }));

        g.MapPost("/workspaces", Task<IResult> (HttpContext c) => AuthedBody(c, async (s, b) =>
        {
            var members = new List<WorkspaceMember>();
            if (b.TryGetProperty("members", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var x in list.EnumerateArray())
                    if (Str(x, "account") is { } acc) members.Add(new WorkspaceMember { Account = acc, Access = ParseAccess(Str(x, "access")) ?? WorkspaceAccess.Edit });
            var w = await h.AddWorkspaceAsync(Str(b, "name") ?? "", Str(b, "description"), members, Str(b, "copyFrom"), s.User, c.RequestAborted);
            return Results.Json(new { id = w.Id }, GatewayProtocol.Json);
        }));
        g.MapPut("/workspaces/{id}", (HttpContext c, string id) => AuthedBody(c, async (s, b) =>
        {
            h.RenameWorkspace(id, Str(b, "name") ?? "", Str(b, "description"), s.User);
            await Task.CompletedTask;
            return Ok();
        }));
        g.MapPost("/workspaces/{id}/members", (HttpContext c, string id) => AuthedBody(c, async (s, b) =>
        {
            var changes = new List<(string, WorkspaceAccess?)>();
            if (b.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var x in list.EnumerateArray())
                    if (Str(x, "account") is { } acc) changes.Add((acc, ParseAccess(Str(x, "access"))));
            if (changes.Count == 0) return Err(400, "Nothing to change.");
            await h.SetMembersAsync(id, changes, s.User, c.RequestAborted);
            return Ok();
        }));
        g.MapDelete("/workspaces/{id}", (HttpContext c, string id) => Authed(c, async s =>
        {
            await h.DeleteWorkspaceAsync(id, s.User, c.RequestAborted);
            return Ok();
        }));

        g.MapPost("/workspaces/{id}/backup", (HttpContext c, string id) => Authed(c, async s =>
        {
            if (h.Store.Workspace(id) is not { } w) return Err(404, "No such workspace.");
            var r = await h.BackUpAsync(w, "manual", s.User, c.RequestAborted);
            return Results.Json(new { file = r.File }, GatewayProtocol.Json);
        }));
        g.MapGet("/backups/{ws}/{file}", (HttpContext c, string ws, string file) => Authed(c, s =>
        {
            if (h.Store.Backups.FirstOrDefault(b => b.Workspace == ws && b.File == file) is not { } rec) return Err(404, "That backup isn't there any more.");
            var path = h.BackupPath(rec);
            if (!File.Exists(path)) return Err(404, "That backup's file is missing.");
            var name = h.Store.Workspace(ws)?.Name ?? ws;
            var safe = new string(name.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-').ToLowerInvariant();
            Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "backup.download", null, name, $"Backup of {name} from {rec.Taken.ToLocalTime():g} downloaded", s.User));
            c.Response.Headers.CacheControl = "no-store";
            return Results.File(path, "application/gzip", $"remoteman-{(safe.Length > 0 ? safe : ws)}-{rec.Taken.ToLocalTime():yyyyMMdd-HHmm}.sql.gz");
        }));
        g.MapPost("/backups/{ws}/{file}/restore", (HttpContext c, string ws, string file) => Authed(c, async s =>
        {
            if (h.Store.Backups.FirstOrDefault(b => b.Workspace == ws && b.File == file) is not { } rec) return Err(404, "That backup isn't there any more.");
            var w = await h.RestoreAsCopyAsync(rec, s.User, c.RequestAborted);
            return Results.Json(new { id = w.Id, name = w.Name }, GatewayProtocol.Json);
        }));
        g.MapPost("/backups/run", Task<IResult> (HttpContext c) => Authed(c, async s =>
        {
            if (h.BackupRunning) return Err(409, "A backup is running already.");
            await h.RunNightlyAsync(c.RequestAborted);
            return Ok();
        }));
        g.MapPost("/backups/test", Task<IResult> (HttpContext c) => Authed(c, async s =>
        {
            var t = await h.TestRestoresAsync(null, c.RequestAborted);
            return Results.Json(new { ok = t.Ok, detail = t.Detail }, GatewayProtocol.Json);
        }));
    }

    private static WorkspaceAccess? ParseAccess(string? s) => s?.ToLowerInvariant() switch
    {
        "admin" => WorkspaceAccess.Admin,
        "edit" => WorkspaceAccess.Edit,
        "read" or "read-only" or "readonly" => WorkspaceAccess.Read,
        _ => null,
    };

    private static string AccessName(WorkspaceAccess a) => a switch { WorkspaceAccess.Admin => "admin", WorkspaceAccess.Edit => "edit", _ => "read" };

    /// <summary>What admins see of hosted workspaces, folded into the console's snapshot.</summary>
    private object? HostedSnapshot()
    {
        if (HostedSvc is not { } h) return null;
        var accounts = h.Store.Accounts;
        var tokens = h.Store.Tokens;
        var workspaces = h.Store.Workspaces;
        var (host, port) = h.Db.PublicEndpoint;
        var online = workspaces.ToDictionary(w => w.Id, w => h.Stats(w.Id)?.Online ?? []);
        return new
        {
            serverError = h.ServerError, serverVersion = h.ServerVersion, endpoint = $"{host}:{port}", verified = h.Db.Options.CaPem is not null,
            nextBackup = h.NextBackup, backupRunning = h.BackupRunning,
            accounts = accounts.OrderBy(a => a.ConsoleAdmin ? 0 : 1).ThenBy(a => a.Name).Select(a => new
            {
                name = a.Name, fullName = a.FullName, admin = a.ConsoleAdmin, disabled = a.Disabled, mustChange = a.MustChangePassword,
                totp = a.ConsoleAdmin ? Store.Admin(a.Name)?.TotpSecret is not null : a.TotpSecret is not null,
                created = a.Created, lastLogin = a.ConsoleAdmin ? Store.Admin(a.Name)?.LastLogin : a.LastLogin, login = a.DbLogin,
                online = online.Values.Any(o => o.Any(u => u.Login == a.DbLogin)),
                apps = tokens.Where(t => t.Account.Equals(a.Name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.LastSeen)
                    .Select(t => new { id = t.Id, device = t.Device, created = t.Created, lastSeen = t.LastSeen, lastIp = t.LastIp }),
                workspaces = workspaces.SelectMany(w => w.Members.Where(m => m.Account.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(m => new { id = w.Id, name = w.Name, access = AccessName(m.Access) })),
            }),
            workspaces = workspaces.OrderBy(w => w.Name).Select(w =>
            {
                var st = h.Stats(w.Id);
                return new
                {
                    id = w.Id, name = w.Name, description = w.Description, created = w.Created, createdBy = w.CreatedBy, database = w.Database,
                    entries = st?.Entries, bytes = st?.Bytes, lastChange = st?.LastChange,
                    members = w.Members.Select(m =>
                    {
                        var acc = accounts.FirstOrDefault(a => a.Name.Equals(m.Account, StringComparison.OrdinalIgnoreCase));
                        var login = acc?.DbLogin ?? m.Account;
                        return new
                        {
                            account = m.Account, fullName = acc?.FullName, access = AccessName(m.Access), admin = acc?.ConsoleAdmin == true,
                            machines = st?.Online.Where(o => o.Login == login).Select(o => o.Machine).Distinct().ToList() ?? [],
                            lastChange = st?.LastChangeBy.TryGetValue(login, out var at) == true ? at : (DateTimeOffset?)null,
                            signedIn = tokens.Any(t => t.Account.Equals(m.Account, StringComparison.OrdinalIgnoreCase)),
                            lastLogin = acc?.ConsoleAdmin == true ? Store.Admin(m.Account)?.LastLogin : acc?.LastLogin,
                        };
                    }),
                    online = st?.Online.Select(o => o.Login).Distinct().Count() ?? 0,
                };
            }),
            backups = h.Store.Backups.OrderByDescending(b => b.Taken).Select(b => new
            {
                workspace = b.Workspace, file = b.File, taken = b.Taken, size = b.Size, kind = b.Kind,
                entries = b.Rows.GetValueOrDefault("remoteman_entries"), name = workspaces.FirstOrDefault(w => w.Id == b.Workspace)?.Name,
            }),
            restoreTests = h.Store.RestoreTests.OrderByDescending(t => t.At).Take(5),
        };
    }

    /// <summary>A member's own page: their workspaces, signed-in PCs and website sessions.</summary>
    private object MemberSnapshot(HttpContext c, Session me)
    {
        var h = HostedSvc!;
        var a = h.Store.Account(me.User)!;
        return new
        {
            me = new { name = a.Name, fullName = a.FullName, totp = a.TotpSecret is not null, member = true, mustChange = a.MustChangePassword },
            relay = new { version = _o.Version, url = PublicUrl(c) },
            workspaces = h.Store.WorkspacesOf(a.Name).Select(x => new
            {
                id = x.Workspace.Id, name = x.Workspace.Name, description = x.Workspace.Description, access = AccessName(x.Access),
                online = h.Stats(x.Workspace.Id)?.Online.Select(o => o.Login).Distinct().Count() ?? 0,
                members = x.Workspace.Members.Count,
            }),
            apps = h.Store.Tokens.Where(t => t.Account.Equals(a.Name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.LastSeen)
                .Select(t => new { id = t.Id, device = t.Device, created = t.Created, lastSeen = t.LastSeen, lastIp = t.LastIp }),
            sessions = _sessions.Values.Where(x => x.User == me.User && Fresh(x)).Select(x => new { current = x.Id == me.Id, created = x.Created, lastSeen = x.LastSeen, ip = x.Ip, agent = x.Agent }),
        };
    }

    private async Task<IResult> MemberLoginAsync(HttpContext c, HostedService h, string user, string password, string? code, string ip)
    {
        var r = h.Authenticate(user, password, code);
        if (r.NeedCode) return Results.Json(new { needCode = true }, GatewayProtocol.Json, statusCode: 401);
        if (r.Account is not { } a)
        {
            _throttle.Fail("ip:" + ip);
            _throttle.Fail("user:" + user.ToLowerInvariant());
            Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "account.login-failed", null, Trim(user, 40), $"Failed website sign-in as {Trim(user, 40)} from {ip}"));
            return Err(401, r.Error ?? "Wrong name, password or code.");
        }
        _throttle.Reset("user:" + user.ToLowerInvariant());
        h.Store.NoteLogin(a.Name);
        StartSession(c, a.Name, a.Stamp, true);
        Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "account.login", null, a.Name, $"{a.Name} signed in to the website from {ip}", a.Name));
        await Task.CompletedTask;
        return Ok();
    }

    private IResult MemberPassword(HttpContext c, Session s, JsonElement b)
    {
        var h = HostedSvc!;
        var a = h.Store.Account(s.User) ?? throw new ArgumentException("No such account.");
        h.ChangeOwnPassword(a, Str(b, "current") ?? "", Str(b, "next") ?? "");
        Resign(c, s);
        return Ok();
    }

    // ---- the app's sign-in: /v1/account ----

    private void MapAppAccount(WebApplication app)
    {
        if (HostedSvc is not { } h) return;

        app.MapPost("/v1/account/signin", async (HttpContext c) =>
        {
            var ip = ClientIp(c);
            var b = await Body(c);
            var user = (Str(b, "user") ?? "").Trim();
            if ((_throttle.Locked("ip:" + ip) ?? _throttle.Locked("user:" + user.ToLowerInvariant())) is { } wait)
                return Err(429, $"Too many failed sign-ins. Wait {Relay.Duration(wait)} and try again.");
            var r = h.Authenticate(user, Str(b, "password") ?? "", Str(b, "code"));
            if (r.NeedCode) return Results.Json(new { needCode = true, error = "Type the code from your authenticator app." }, GatewayProtocol.Json, statusCode: 401);
            if (r.Account is not { } a)
            {
                _throttle.Fail("ip:" + ip);
                _throttle.Fail("user:" + user.ToLowerInvariant());
                Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "account.login-failed", null, Trim(user, 40), $"Failed RemoteMan sign-in as {Trim(user, 40)} from {ip}"));
                return Err(401, r.Error ?? "Wrong username, password or code.");
            }
            _throttle.Reset("user:" + user.ToLowerInvariant());
            var (token, rec) = h.Store.AddToken(a.Name, Str(b, "device"), ip);
            h.Store.NoteLogin(a.Name);
            Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, "account.app-login", null, a.Name, $"{a.Name} signed in to RemoteMan on {rec.Device ?? "a PC"} from {ip}", a.Name));
            return Results.Json(new { token, account = a.Name, fullName = a.FullName, mustChangePassword = a.MustChangePassword }, GatewayProtocol.Json);
        });

        app.MapGet("/v1/account/me", (HttpContext c) =>
        {
            if (AppAccount(c, h) is not var (t, a)) return Err(401, "Sign in again.");
            var ws = a.MustChangePassword ? [] : h.Store.WorkspacesOf(a.Name).Select(x => new
            {
                id = x.Workspace.Id, name = x.Workspace.Name, description = x.Workspace.Description, access = AccessName(x.Access),
                connectionString = h.Db.AppConnectionString(x.Workspace, a),
                online = h.Stats(x.Workspace.Id)?.Online.Select(o => o.Login).Distinct().Count() ?? 0,
            }).ToList();
            c.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                account = a.Name, fullName = a.FullName, admin = a.ConsoleAdmin, mustChangePassword = a.MustChangePassword,
                server = PublicUrl(c), caPem = h.Db.Options.CaPem, workspaces = ws,
            }, GatewayProtocol.Json);
        });

        app.MapPost("/v1/account/password", async (HttpContext c) =>
        {
            if (AppAccount(c, h) is not var (_, a)) return Err(401, "Sign in again.");
            var b = await Body(c);
            try { h.ChangeOwnPassword(a, Str(b, "current") ?? "", Str(b, "next") ?? ""); }
            catch (ArgumentException ex) { return Err(400, ex.Message); }
            return Ok();
        });

        app.MapPost("/v1/account/signout", (HttpContext c) =>
        {
            if (AppAccount(c, h) is not var (t, _)) return Ok();
            h.Store.RemoveToken(t.Id);
            return Ok();
        });
    }

    /// <summary>The app's bearer token → its account (signed out when the account is gone or turned off).</summary>
    private static (AppToken, HostedAccount)? AppAccount(HttpContext c, HostedService h)
    {
        var auth = c.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        if (h.Store.TokenFor(token) is not { } t) return null;
        if (h.Store.Account(t.Account) is not { Disabled: false } a) { h.Store.RemoveToken(t.Id); return null; }
        h.Store.TouchToken(t, ClientIp(c));
        return (t, a);
    }
}
