using System.Collections.Concurrent;
using Npgsql;
using RemoteMan.Gateway.Server.Admin;

namespace RemoteMan.Gateway.Server.Hosted;

public sealed class HostedOptions
{
    /// <summary>Where backups go (on the relay's machine, so not on the database server they protect).</summary>
    public required string BackupDir { get; init; }
    /// <summary>BACKUP_AT: local time of the nightly backup, "HH:mm". The container's TZ sets the zone.</summary>
    public TimeSpan BackupAt { get; init; } = new(3, 0, 0);
    public Action<string> Log { get; init; } = Console.WriteLine;
}

/// <summary>Result of a sign-in attempt by name and password (and code, when the account has one).</summary>
public sealed record SignInResult(HostedAccount? Account, bool NeedCode, string? Error);

/// <summary>
/// Hosted workspaces: the accounts people sign in to RemoteMan with, the workspaces (one database each on the hosted
/// PostgreSQL server) and who is in them, the app's sign-in, and nightly backups. The relay's console shows and changes
/// all of it; the hosted server only ever sees databases and logins.
/// </summary>
public sealed class HostedService
{
    private readonly RelayStore _relay;
    private readonly HostedOptions _o;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, WorkspaceStats> _stats = new();

    public HostedService(HostedStore store, HostedDb db, RelayStore relay, HostedOptions options)
    {
        Store = store;
        Db = db;
        _relay = relay;
        _o = options;
        Directory.CreateDirectory(options.BackupDir);
    }

    public HostedStore Store { get; }
    public HostedDb Db { get; }
    /// <summary>The console's activity list (set when the console starts).</summary>
    public ActivityLog Activity { get; set; } = new(null);
    public string? ServerError { get; private set; }
    public string? ServerVersion { get; private set; }
    public DateTimeOffset? NextBackup { get; private set; }
    public bool BackupRunning { get; private set; }

    public WorkspaceStats? Stats(string id) => _stats.TryGetValue(id, out var s) ? s : null;

    private void Audit(string kind, string? subject, string text, string? actor) =>
        Activity.Add(new GatewayEvent(DateTimeOffset.UtcNow, kind, null, subject, text, actor));

    // ---- start-up and background work ----

    public void Start(CancellationToken ct) => _ = Task.Run(() => RunAsync(ct), ct);

    private async Task RunAsync(CancellationToken ct)
    {
        var lastStats = DateTimeOffset.MinValue;
        var prepared = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!prepared)
                {
                    ServerVersion = await Db.PrepareServerAsync(ct);
                    await ReconcileAsync(ct);
                    prepared = true;
                    ServerError = null;
                    _o.Log($"Hosted workspaces: PostgreSQL {ServerVersion} at {Db.PublicEndpoint.Host}:{Db.PublicEndpoint.Port}, {Store.Workspaces.Count} workspace(s)");
                }
                // A console admin added or removed since: their account follows.
                var admins = _relay.Admins.Select(a => a.Name.ToLowerInvariant()).ToHashSet();
                var accounts = Store.Accounts.Where(a => a.ConsoleAdmin).Select(a => a.Name.ToLowerInvariant()).ToHashSet();
                if (!admins.SetEquals(accounts)) await ReconcileAsync(ct);
                if (DateTimeOffset.UtcNow - lastStats > TimeSpan.FromSeconds(15))
                {
                    await RefreshStatsAsync(ct);
                    lastStats = DateTimeOffset.UtcNow;
                }
                await MaybeBackUpAsync(ct);
                ServerError = null;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or TimeoutException && !ct.IsCancellationRequested)
            {
                if (ServerError != ex.Message) _o.Log($"Hosted workspaces: {ex.Message}");
                ServerError = ex.Message;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Brings the server in line with hosted.json: every login, every workspace and its members.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var admin in _relay.Admins) Store.EnsureAdminAccount(admin.Name);
            // Admins removed from the relay lose their account here too.
            foreach (var a in Store.Accounts.Where(a => a.ConsoleAdmin && _relay.Admin(a.Name) is null).ToList())
            {
                Store.RemoveAccount(a.Name);
                await Db.DropLoginAsync(a.DbLogin, ct);
            }
            var accounts = Store.Accounts;
            foreach (var a in accounts) await Db.SyncLoginAsync(a, ct);
            foreach (var w in Store.Workspaces)
            {
                await Db.CreateWorkspaceAsync(w, ct);   // idempotent: also upgrades older workspaces' functions
                await Db.SyncMembersAsync(w, accounts, ct);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshStatsAsync(CancellationToken ct)
    {
        foreach (var w in Store.Workspaces)
        {
            try { _stats[w.Id] = await Db.StatsAsync(w, ct); }
            catch (PostgresException) { _stats.TryRemove(w.Id, out _); }
        }
        foreach (var id in _stats.Keys.Where(id => Store.Workspace(id) is null).ToList()) _stats.TryRemove(id, out _);
    }

    // ---- sign-in (app and website alike) ----

    /// <summary>Checks a name and password against console admins and accounts. A disabled account can't sign in.</summary>
    public SignInResult Authenticate(string user, string password, string? code)
    {
        user = user.Trim();
        if (_relay.Admin(user) is { } admin)
        {
            if (!Passwords.Verify(password, admin.PasswordHash)) return new(null, false, null);
            if (admin.TotpSecret is { } s)
            {
                if (string.IsNullOrEmpty(code)) return new(null, true, null);
                if (!Totp.Verify(s, code)) return new(null, false, null);
            }
            return new(Store.EnsureAdminAccount(admin.Name), false, null);
        }
        var a = Store.Account(user);
        var ok = Passwords.Verify(password, a is { PasswordHash.Length: > 0 } ? a.PasswordHash : Passwords.Dummy) && a is { ConsoleAdmin: false };
        if (!ok) return new(null, false, null);
        if (a!.TotpSecret is { } secret)
        {
            if (string.IsNullOrEmpty(code)) return new(null, true, null);
            if (!Totp.Verify(secret, code)) return new(null, false, null);
        }
        if (a.Disabled) return new(null, false, "This account is turned off. Ask the admin.");
        return new(a, false, null);
    }

    /// <summary>The account's own password change (the first one included, which clears "must change").</summary>
    public void ChangeOwnPassword(HostedAccount a, string current, string next)
    {
        if (a.ConsoleAdmin)
        {
            var admin = _relay.Admin(a.Name) ?? throw new ArgumentException("No such admin.");
            if (!Passwords.Verify(current, admin.PasswordHash)) throw new ArgumentException("The current password isn't right.");
            _relay.SetPassword(a.Name, next);
        }
        else
        {
            if (!Passwords.Verify(current, a.PasswordHash)) throw new ArgumentException("The current password isn't right.");
            if (current == next) throw new ArgumentException("Pick a password that's different from the first one.");
            Store.SetPassword(a.Name, next, mustChange: false);
        }
        Audit("account.password", a.Name, $"{a.Name} changed their password", a.Name);
    }

    // ---- accounts ----

    public async Task<HostedAccount> AddAccountAsync(string name, string? fullName, string password, bool mustChange, IEnumerable<(string Workspace, WorkspaceAccess Access)> workspaces, string actor, CancellationToken ct = default)
    {
        if (_relay.Admin(name) is not null) throw new ArgumentException($"{name} is already a console admin.");
        var a = Store.AddAccount(name, fullName, password, mustChange);
        await _gate.WaitAsync(ct);
        try
        {
            await Db.SyncLoginAsync(a, ct);
            foreach (var (wid, access) in workspaces)
            {
                if (Store.Workspace(wid) is null) continue;
                Store.UpdateWorkspace(wid, w => w.Members.Add(new WorkspaceMember { Account = a.Name, Access = access }));
                await Db.SyncMembersAsync(Store.Workspace(wid)!, [a], ct);
            }
        }
        catch
        {
            Store.RemoveAccount(a.Name);
            throw;
        }
        finally { _gate.Release(); }
        Audit("account.add", a.Name, $"Account {a.Name} made", actor);
        return a;
    }

    public async Task UpdateAccountAsync(string name, string? fullName, bool disabled, string actor, CancellationToken ct = default)
    {
        var before = Store.Account(name) ?? throw new ArgumentException($"No account called {name}.");
        Store.UpdateAccount(name, a => { a.FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim(); if (!a.ConsoleAdmin) a.Disabled = disabled; });
        var after = Store.Account(name)!;
        if (after.Disabled) Store.RemoveTokensOf(name);
        await WithGate(() => Db.SyncLoginAsync(after, ct), ct);
        if (before.Disabled != after.Disabled) Audit("account.update", name, after.Disabled ? $"Account {name} turned off; their apps were signed out" : $"Account {name} turned back on", actor);
    }

    /// <summary>A new first password from the admin. The person picks their own at the next sign-in; their apps are signed out.</summary>
    public void ResetPassword(string name, string password, string actor)
    {
        Store.SetPassword(name, password, mustChange: true);
        Store.UpdateAccount(name, a => a.TotpSecret = null);
        Store.RemoveTokensOf(name);
        Audit("account.password", name, $"New first password for {name}; two-step sign-in was turned off and their apps signed out", actor);
    }

    public async Task RemoveAccountAsync(string name, string actor, CancellationToken ct = default)
    {
        var a = Store.Account(name) ?? throw new ArgumentException($"No account called {name}.");
        if (a.ConsoleAdmin) throw new ArgumentException($"{a.Name} is a console admin: remove them under Settings.");
        Store.RemoveAccount(a.Name);
        await WithGate(() => Db.DropLoginAsync(a.DbLogin, ct), ct);
        Audit("account.remove", a.Name, $"Account {a.Name} removed", actor);
    }

    /// <summary>Signs out every app of the account and gives it a new database password, so a copied one stops working too.</summary>
    public async Task SignOutEverywhereAsync(string name, string actor, CancellationToken ct = default)
    {
        Store.RemoveTokensOf(name);
        Store.UpdateAccount(name, a => a.DbPassword = HostedStore.NewDbPassword());
        await WithGate(() => Db.SyncLoginAsync(Store.Account(name)!, ct), ct);
        Audit("account.signout", name, $"{name} was signed out of RemoteMan on every PC", actor);
    }

    // ---- workspaces ----

    public async Task<HostedWorkspace> AddWorkspaceAsync(string name, string? description, IEnumerable<WorkspaceMember> members, string? copyFrom, string actor, CancellationToken ct = default)
    {
        var list = members.ToList();
        if (_relay.Admin(actor) is { } admin && Store.Account(admin.Name) is not { ConsoleAdmin: true })
            await WithGate(() => Db.SyncLoginAsync(Store.EnsureAdminAccount(admin.Name), ct), ct);
        // Whoever makes it is in it, as an admin.
        if (Store.Account(actor) is { } me && !list.Any(m => m.Account.Equals(me.Name, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, new WorkspaceMember { Account = me.Name, Access = WorkspaceAccess.Admin });
        string? source = null;
        if (copyFrom is not null)
        {
            var from = Store.Workspace(copyFrom) ?? throw new ArgumentException("The workspace to copy isn't there any more.");
            source = (await BackUpAsync(from, "manual", actor, ct)).File;
        }
        var w = Store.AddWorkspace(name, description, actor, list);
        try
        {
            await WithGate(async () =>
            {
                await Db.CreateWorkspaceAsync(w, ct);
                if (source is not null) await Db.RestoreAsync(w, BackupPath(Store.Backups.First(b => b.File == source)), ct);
                await Db.SyncMembersAsync(w, Store.Accounts, ct);
            }, ct);
        }
        catch
        {
            Store.RemoveWorkspace(w.Id);
            try { await Db.DropWorkspaceAsync(w, CancellationToken.None); } catch (NpgsqlException) { }
            throw;
        }
        Audit("workspace.add", w.Name, $"Workspace {w.Name} made{(copyFrom is null ? "" : $" as a copy of {Store.Workspace(copyFrom)?.Name}")} with {list.Count} member(s)", actor);
        return w;
    }

    public void RenameWorkspace(string id, string name, string? description, string actor)
    {
        var old = Store.Workspace(id)?.Name;
        Store.RenameWorkspace(id, name, description);
        if (old != name) Audit("workspace.rename", name, $"Workspace {old} renamed to {name}", actor);
    }

    /// <summary>Adds, changes or removes members (null access = remove).</summary>
    public async Task SetMembersAsync(string id, IEnumerable<(string Account, WorkspaceAccess? Access)> changes, string actor, CancellationToken ct = default)
    {
        var w = Store.Workspace(id) ?? throw new ArgumentException("No such workspace.");
        var list = changes.ToList();
        foreach (var (acc, _) in list) if (Store.Account(acc) is null) throw new ArgumentException($"No account called {acc}.");
        Store.UpdateWorkspace(id, x =>
        {
            foreach (var (acc, access) in list)
            {
                x.Members.RemoveAll(m => m.Account.Equals(acc, StringComparison.OrdinalIgnoreCase));
                if (access is { } a) x.Members.Add(new WorkspaceMember { Account = Store.Account(acc)!.Name, Access = a });
            }
            if (!x.Members.Any(m => m.Access == WorkspaceAccess.Admin) && x.Members.Count > 0)
                throw new ArgumentException("Keep at least one admin in the workspace.");
        });
        w = Store.Workspace(id)!;
        var touched = list.Select(c => Store.Account(c.Account)!).ToList();
        await WithGate(() => Db.SyncMembersAsync(w, touched, ct), ct);
        foreach (var (acc, access) in list)
            Audit("workspace.member", w.Name, access is null ? $"{acc} removed from {w.Name}" : $"{acc} in {w.Name}: {Label(access.Value)}", actor);
    }

    public static string Label(WorkspaceAccess a) => a switch { WorkspaceAccess.Admin => "Admin", WorkspaceAccess.Edit => "Can edit", _ => "Read only" };

    /// <summary>Deletes the workspace after a last backup, kept 30 days in case it was a mistake.</summary>
    public async Task DeleteWorkspaceAsync(string id, string actor, CancellationToken ct = default)
    {
        var w = Store.Workspace(id) ?? throw new ArgumentException("No such workspace.");
        await BackUpAsync(w, "before-delete", actor, ct);
        await WithGate(() => Db.DropWorkspaceAsync(w, ct), ct);
        Store.RemoveWorkspace(id);
        _stats.TryRemove(id, out _);
        Audit("workspace.remove", w.Name, $"Workspace {w.Name} deleted (its last backup is kept 30 days)", actor);
    }

    // ---- backups ----

    public string BackupPath(BackupRecord b) => Path.Combine(_o.BackupDir, b.Workspace, b.File);

    public async Task<BackupRecord> BackUpAsync(HostedWorkspace w, string kind, string? actor, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rec = new BackupRecord { Workspace = w.Id, Taken = now, Kind = kind, File = $"{now.ToLocalTime():yyyyMMdd-HHmmss}-{kind}.sql.gz" };
        var path = BackupPath(rec);
        rec.Rows = await Db.BackupAsync(w, path, ct);
        rec.Size = new FileInfo(path).Length;
        Store.AddBackup(rec);
        if (kind != "nightly") Audit("backup." + kind, w.Name, $"Backup of {w.Name} taken ({rec.Rows.GetValueOrDefault("remoteman_entries")} entries)", actor);
        return rec;
    }

    /// <summary>Restores a backup as a new workspace next to the old one, with the same members.</summary>
    public async Task<HostedWorkspace> RestoreAsCopyAsync(BackupRecord b, string actor, CancellationToken ct = default)
    {
        var src = Store.Workspace(b.Workspace);
        var baseName = $"{src?.Name ?? "Restored"} ({b.Taken.ToLocalTime():d MMM HH:mm})";
        var name = baseName;
        for (var i = 2; Store.Workspaces.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{baseName} {i}";
        var members = src?.Members.Select(m => new WorkspaceMember { Account = m.Account, Access = m.Access }).ToList() ?? [];
        var w = Store.AddWorkspace(name, src?.Description, actor, members);
        try
        {
            await WithGate(async () =>
            {
                await Db.CreateWorkspaceAsync(w, ct);
                await Db.RestoreAsync(w, BackupPath(b), ct);
                await Db.SyncMembersAsync(w, Store.Accounts, ct);
            }, ct);
        }
        catch
        {
            Store.RemoveWorkspace(w.Id);
            try { await Db.DropWorkspaceAsync(w, CancellationToken.None); } catch (NpgsqlException) { }
            throw;
        }
        Audit("workspace.restore", w.Name, $"Backup of {src?.Name ?? b.Workspace} from {b.Taken.ToLocalTime():g} restored as {w.Name}", actor);
        return w;
    }

    private DateOnly? _lastNightly;

    private async Task MaybeBackUpAsync(CancellationToken ct)
    {
        var local = DateTime.Now;
        var today = DateOnly.FromDateTime(local);
        _lastNightly ??= Store.Backups.Where(b => b.Kind == "nightly").Select(b => (DateOnly?)DateOnly.FromDateTime(b.Taken.LocalDateTime)).Max();
        var due = local.TimeOfDay >= _o.BackupAt && _lastNightly != today;
        var next = due || local.TimeOfDay < _o.BackupAt ? today : today.AddDays(1);
        NextBackup = new DateTimeOffset(next.ToDateTime(TimeOnly.FromTimeSpan(_o.BackupAt)));
        if (!due) return;
        await RunNightlyAsync(ct);
        _lastNightly = today;
        NextBackup = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.FromTimeSpan(_o.BackupAt)));
    }

    /// <summary>Every workspace, then old backups thinned out, then (on Sundays) a restore test of each newest backup.</summary>
    public async Task RunNightlyAsync(CancellationToken ct = default)
    {
        BackupRunning = true;
        try
        {
            var failed = new List<string>();
            var made = new List<BackupRecord>();
            foreach (var w in Store.Workspaces)
            {
                try { made.Add(await BackUpAsync(w, "nightly", null, ct)); }
                catch (Exception ex) when (ex is NpgsqlException or IOException) { failed.Add($"{w.Name}: {ex.Message}"); }
            }
            Prune();
            Audit(failed.Count == 0 ? "backup.nightly" : "backup.failed", null,
                failed.Count == 0 ? $"Nightly backup of {made.Count} workspace(s)" : $"Nightly backup failed for {string.Join("; ", failed)}", null);
            if (DateTime.Now.DayOfWeek == DayOfWeek.Sunday) await TestRestoresAsync(made, ct);
        }
        finally { BackupRunning = false; }
    }

    public async Task<RestoreTest> TestRestoresAsync(IReadOnlyList<BackupRecord>? backups = null, CancellationToken ct = default)
    {
        backups ??= Store.Backups.GroupBy(b => b.Workspace).Where(g => Store.Workspace(g.Key) is not null).Select(g => g.MaxBy(b => b.Taken)!).ToList();
        var problems = new List<string>();
        var n = 0;
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var b in backups)
            {
                var (ok, detail) = await Db.TestRestoreAsync(b, BackupPath(b), ct);
                n++;
                if (!ok) problems.Add($"{Store.Workspace(b.Workspace)?.Name ?? b.Workspace}: {detail}");
            }
        }
        finally { _gate.Release(); }
        var t = new RestoreTest
        {
            At = DateTimeOffset.UtcNow, Ok = problems.Count == 0,
            Detail = problems.Count == 0 ? $"The newest backup of {n} workspace(s) restored into a scratch database and every table's rows matched." : string.Join("; ", problems),
        };
        Store.AddRestoreTest(t);
        Audit(t.Ok ? "backup.test" : "backup.test-failed", null, t.Ok ? $"Restore test passed ({n} workspace(s))" : "Restore test failed: " + t.Detail, null);
        return t;
    }

    /// <summary>Keeps the last 7 nightly backups, Sunday ones for 4 weeks, the 1st of the month for 3 months, others 30 days.</summary>
    public void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        var drop = new List<BackupRecord>();
        foreach (var g in Store.Backups.GroupBy(b => b.Workspace))
        {
            var nightly = g.Where(b => b.Kind == "nightly").OrderByDescending(b => b.Taken).ToList();
            for (var i = 0; i < nightly.Count; i++)
            {
                var b = nightly[i];
                var d = b.Taken.LocalDateTime;
                var age = now - b.Taken;
                var keep = i < 7 || d.DayOfWeek == DayOfWeek.Sunday && age < TimeSpan.FromDays(29) || d.Day == 1 && age < TimeSpan.FromDays(93);
                if (!keep) drop.Add(b);
            }
            drop.AddRange(g.Where(b => b.Kind != "nightly" && now - b.Taken > TimeSpan.FromDays(30)));
        }
        foreach (var b in drop) try { File.Delete(BackupPath(b)); } catch (IOException) { }
        if (drop.Count > 0) Store.RemoveBackups(b => drop.Any(x => x.Workspace == b.Workspace && x.File == b.File));
    }

    private async Task WithGate(Func<Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await work(); }
        finally { _gate.Release(); }
    }
}
