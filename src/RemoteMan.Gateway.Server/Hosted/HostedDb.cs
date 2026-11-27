using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using RemoteMan.Data;

namespace RemoteMan.Gateway.Server.Hosted;

/// <summary>Where the hosted PostgreSQL server is: how the relay reaches it, and how the apps do.</summary>
public sealed class HostedDbOptions
{
    /// <summary>HOSTED_DB: connection string with a superuser login (only the relay has it). The database name is ignored.</summary>
    public required string AdminConnectionString { get; init; }
    /// <summary>HOSTED_DB_PUBLIC: host[:port] the apps connect to. Defaults to the host in <see cref="AdminConnectionString"/>.</summary>
    public string? PublicHost { get; init; }
    /// <summary>HOSTED_DB_CA: the server's certificate (or its CA) as PEM. Apps then check it (VerifyFull); without it they only encrypt.</summary>
    public string? CaPem { get; init; }
    /// <summary>HOSTED_DB_TLS=off: apps connect without TLS. Only for a test server on your own network.</summary>
    public bool NoTls { get; init; }
    /// <summary>Connections one login may hold at once (an app uses two or three).</summary>
    public int ConnectionLimit { get; init; } = 20;
}

public sealed record WorkspaceStats(long Entries, long Bytes, DateTimeOffset? LastChange, IReadOnlyList<OnlineUser> Online, IReadOnlyDictionary<string, DateTimeOffset> LastChangeBy);
public sealed record OnlineUser(string Login, string Machine);

/// <summary>
/// The hosted PostgreSQL server, seen from the relay: makes each workspace's database and roles, keeps each account's
/// login in step with the website, and backs workspaces up and restores them. Runs as a superuser on that server.
/// <para>Per workspace w: database rmw_w (owned by postgres, so nobody else can drop it), role rmw_w_owner (owns the
/// tables; editors are members and act as it in that database), rmw_w_ro (reads, and writes presence), rmw_w_adm
/// (marks admins: they may change the shared settings). Every account is one login role on the server, so the name
/// people see on changes is the account name, and access is per workspace through those group roles.</para>
/// </summary>
public sealed partial class HostedDb(HostedDbOptions options)
{
    public const string Marker = "remoteman-hosted account";

    private readonly NpgsqlConnectionStringBuilder _admin = new(options.AdminConnectionString);

    public HostedDbOptions Options => options;

    /// <summary>Host and port the apps use.</summary>
    public (string Host, int Port) PublicEndpoint
    {
        get
        {
            var p = options.PublicHost?.Trim();
            if (string.IsNullOrEmpty(p)) return ((_admin.Host ?? "localhost").Split(',')[0], _admin.Port);
            var i = p.LastIndexOf(':');
            return i > 0 && !p.EndsWith(']') && int.TryParse(p[(i + 1)..], out var port) ? (p[..i].Trim('[', ']'), port) : (p.Trim('[', ']'), 5432);
        }
    }

    private NpgsqlConnection Open(string database)
    {
        var b = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = database, ApplicationName = "remoteman-relay" };
        return new NpgsqlConnection(b.ConnectionString);
    }

    private async Task<NpgsqlConnection> OpenAsync(string database, CancellationToken ct)
    {
        var c = Open(database);
        await c.OpenAsync(ct);
        return c;
    }

    private static async Task Exec(NpgsqlConnection c, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> Scalar<T>(NpgsqlConnection c, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? default : (T)r;
    }

    /// <summary>Double-quoted identifier. Names come from our own checks, this is a second line of defence.</summary>
    private static string Id(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Lit(string text) => "'" + text.Replace("'", "''") + "'";

    [GeneratedRegex("^rmw_[0-9a-f]{8}$")]
    private static partial Regex WorkspaceDb();

    private static void CheckDb(string db) { if (!WorkspaceDb().IsMatch(db)) throw new ArgumentException($"Not a workspace database: {db}"); }

    private static (string Owner, string Ro, string Adm) Roles(string db) => (db + "_owner", db + "_ro", db + "_adm");

    // ---- server ----

    /// <summary>Checks the login works and is a superuser; closes the doors PostgreSQL leaves open by default.</summary>
    public async Task<string> PrepareServerAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(_admin.Database is { Length: > 0 } d ? d : "postgres", ct);
        if (!await Scalar<bool>(c, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user", ct))
            throw new InvalidOperationException("HOSTED_DB must log in as a superuser (it creates databases and logins).");
        // Every account can log in somewhere: the maintenance databases must not be one of those places.
        await Exec(c, "REVOKE CONNECT, TEMPORARY ON DATABASE postgres FROM PUBLIC", ct);
        await Exec(c, "REVOKE CONNECT, TEMPORARY ON DATABASE template1 FROM PUBLIC", ct);
        return await Scalar<string>(c, "SHOW server_version", ct) ?? "?";
    }

    // ---- accounts ----

    /// <summary>Makes or updates the account's login: password, on or off, connection limit.</summary>
    public async Task SyncLoginAsync(HostedAccount a, CancellationToken ct = default)
    {
        await using var c = await OpenAsync("postgres", ct);
        var exists = await Scalar<bool>(c, "SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = @n)", ct, ("n", a.DbLogin));
        if (exists && await Scalar<string>(c, "SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = @n", ct, ("n", a.DbLogin)) != Marker)
            throw new InvalidOperationException($"The database server already has a role called {a.DbLogin} that RemoteMan didn't make. Pick another username.");
        var login = a.Disabled ? "NOLOGIN" : "LOGIN";
        var attrs = $"{login} NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT {options.ConnectionLimit} PASSWORD {Lit(a.DbPassword)}";
        await Exec(c, exists ? $"ALTER ROLE {Id(a.DbLogin)} {attrs}" : $"CREATE ROLE {Id(a.DbLogin)} {attrs}", ct);
        await Exec(c, $"COMMENT ON ROLE {Id(a.DbLogin)} IS {Lit(Marker)}", ct);
        if (a.Disabled) await KickAsync(c, a.DbLogin, null, ct);
    }

    public async Task DropLoginAsync(string login, CancellationToken ct = default)
    {
        await using var c = await OpenAsync("postgres", ct);
        if (await Scalar<string>(c, "SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = @n", ct, ("n", login)) != Marker) return;
        await Exec(c, $"ALTER ROLE {Id(login)} NOLOGIN", ct);
        await KickAsync(c, login, null, ct);
        // Memberships go with the role; it owns nothing (editors create tables as the workspace's owner role).
        await Exec(c, $"DROP ROLE IF EXISTS {Id(login)}", ct);
    }

    /// <summary>Closes the login's open connections (to one database, or all).</summary>
    private static Task KickAsync(NpgsqlConnection c, string login, string? db, CancellationToken ct) => db is null
        ? Exec(c, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = @n", ct, ("n", login))
        : Exec(c, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = @n AND datname = @d", ct, ("n", login), ("d", db));

    // ---- workspaces ----

    /// <summary>Database, roles, tables and the small admin functions the app asks "am I an admin?" with.</summary>
    public async Task CreateWorkspaceAsync(HostedWorkspace w, CancellationToken ct = default)
    {
        var db = w.Database;
        CheckDb(db);
        var (owner, ro, adm) = Roles(db);
        await using (var c = await OpenAsync("postgres", ct))
        {
            foreach (var r in new[] { owner, ro, adm })
                if (!await Scalar<bool>(c, "SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = @n)", ct, ("n", r)))
                    await Exec(c, $"CREATE ROLE {Id(r)} NOLOGIN", ct);
            if (!await Scalar<bool>(c, "SELECT EXISTS (SELECT FROM pg_database WHERE datname = @n)", ct, ("n", db)))
                await Exec(c, $"CREATE DATABASE {Id(db)} OWNER postgres ENCODING 'UTF8' TEMPLATE template0", ct);
            await Exec(c, $"REVOKE ALL ON DATABASE {Id(db)} FROM PUBLIC", ct);
            await Exec(c, $"GRANT CONNECT, TEMPORARY ON DATABASE {Id(db)} TO {Id(owner)}", ct);
            await Exec(c, $"GRANT CONNECT ON DATABASE {Id(db)} TO {Id(ro)}", ct);
            await Exec(c, $"COMMENT ON DATABASE {Id(db)} IS {Lit("RemoteMan workspace " + w.Name)}", ct);
        }
        await using var x = await OpenAsync(db, ct);
        await Exec(x, $"""
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            ALTER SCHEMA public OWNER TO {Id(owner)};
            """, ct);
        // The tables as the app makes them, owned by the owner role.
        await Exec(x, $"SET ROLE {Id(owner)}", ct);
        foreach (var sql in PostgresSchema.All) await Exec(x, sql, ct);
        await Exec(x, "RESET ROLE", ct);
        await Exec(x, $"""
            GRANT USAGE ON SCHEMA public TO {Id(ro)};
            GRANT SELECT ON ALL TABLES IN SCHEMA public TO {Id(ro)};
            ALTER DEFAULT PRIVILEGES FOR ROLE {Id(owner)} IN SCHEMA public GRANT SELECT ON TABLES TO {Id(ro)};
            GRANT SELECT, INSERT, UPDATE, DELETE ON remoteman_presence TO {Id(ro)};
            """, ct);
        await Exec(x, AdminFunctions(owner, ro, adm), ct);
    }

    /// <summary>
    /// The same remoteman_admin.me() the self-hosted servers have (so the app knows who is an admin), and the guard
    /// that lets only admins change the shared settings. Logins are managed on the website, so there is no add/remove here.
    /// </summary>
    private static string AdminFunctions(string owner, string ro, string adm) => $$"""
        CREATE SCHEMA IF NOT EXISTS remoteman_admin AUTHORIZATION postgres;
        REVOKE ALL ON SCHEMA remoteman_admin FROM PUBLIC;
        GRANT USAGE ON SCHEMA remoteman_admin TO {{Id(owner)}}, {{Id(ro)}};

        CREATE OR REPLACE FUNCTION remoteman_admin.access_of(p_name text) RETURNS text
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $f$
            SELECT CASE
                WHEN NOT EXISTS (SELECT FROM pg_roles WHERE rolname = p_name) THEN NULL
                WHEN pg_has_role(p_name, {{Lit(adm)}}, 'MEMBER') THEN 'admin'
                WHEN pg_has_role(p_name, {{Lit(owner)}}, 'MEMBER') THEN 'edit'
                WHEN pg_has_role(p_name, {{Lit(ro)}}, 'MEMBER') THEN 'read-only'
                ELSE NULL END
        $f$;

        CREATE OR REPLACE FUNCTION remoteman_admin.is_admin() RETURNS boolean
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $f$
            SELECT COALESCE(remoteman_admin.access_of(session_user) = 'admin', false)
        $f$;

        CREATE OR REPLACE FUNCTION remoteman_admin.me(OUT login text, OUT access text, OUT is_admin boolean, OUT version int)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $f$
            SELECT session_user::text, remoteman_admin.access_of(session_user), remoteman_admin.is_admin(), 1
        $f$;

        CREATE OR REPLACE FUNCTION remoteman_admin.hosted() RETURNS boolean
        LANGUAGE sql IMMUTABLE AS $f$ SELECT true $f$;

        ALTER TABLE public.remoteman_settings OWNER TO postgres;
        GRANT SELECT, INSERT, UPDATE, DELETE ON public.remoteman_settings TO {{Id(owner)}};
        GRANT SELECT ON public.remoteman_settings TO {{Id(ro)}};

        CREATE OR REPLACE FUNCTION remoteman_admin.guard_settings() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $f$
        BEGIN
            IF NOT remoteman_admin.is_admin() AND NOT (SELECT rolsuper FROM pg_roles WHERE rolname = session_user) THEN
                RAISE EXCEPTION 'Only workspace admins can change shared settings.' USING ERRCODE = 'insufficient_privilege';
            END IF;
            RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
        END $f$;
        DROP TRIGGER IF EXISTS remoteman_settings_admins_only ON public.remoteman_settings;
        CREATE TRIGGER remoteman_settings_admins_only BEFORE INSERT OR UPDATE OR DELETE ON public.remoteman_settings
            FOR EACH ROW EXECUTE FUNCTION remoteman_admin.guard_settings();

        DO $d$
        DECLARE f text;
        BEGIN
            FOR f IN SELECT p.oid::regprocedure::text FROM pg_proc p WHERE p.pronamespace = 'remoteman_admin'::regnamespace LOOP
                EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
            END LOOP;
        END $d$;
        GRANT EXECUTE ON FUNCTION remoteman_admin.me(), remoteman_admin.hosted() TO {{Id(owner)}}, {{Id(ro)}};
        """;

    /// <summary>
    /// Gives every member the access the website says and takes it from everyone else: editors and admins act as the
    /// owner role in this database, readers get the read-only role. People who lose access are disconnected from it.
    /// </summary>
    public async Task SyncMembersAsync(HostedWorkspace w, IEnumerable<HostedAccount> accounts, CancellationToken ct = default)
    {
        var db = w.Database;
        CheckDb(db);
        var (owner, ro, adm) = Roles(db);
        await using var c = await OpenAsync("postgres", ct);
        foreach (var a in accounts)
        {
            if (!await Scalar<bool>(c, "SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = @n)", ct, ("n", a.DbLogin))) continue;
            var m = w.Members.FirstOrDefault(x => x.Account.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
            var want = (Owner: m is { Access: WorkspaceAccess.Edit or WorkspaceAccess.Admin }, Ro: m is { Access: WorkspaceAccess.Read }, Adm: m is { Access: WorkspaceAccess.Admin });
            var had = (Owner: await Member(c, a.DbLogin, owner, ct), Ro: await Member(c, a.DbLogin, ro, ct), Adm: await Member(c, a.DbLogin, adm, ct));
            if (want == had) continue;
            await SetMember(c, a.DbLogin, owner, want.Owner, had.Owner, ct);
            await SetMember(c, a.DbLogin, ro, want.Ro, had.Ro, ct);
            await SetMember(c, a.DbLogin, adm, want.Adm, had.Adm, ct);
            // Editors create and change tables as the owner role, so what they make belongs to the workspace.
            await Exec(c, want.Owner ? $"ALTER ROLE {Id(a.DbLogin)} IN DATABASE {Id(db)} SET role = {Lit(owner)}" : $"ALTER ROLE {Id(a.DbLogin)} IN DATABASE {Id(db)} RESET role", ct);
            // Less access than before: open connections still have the old rights, so they reconnect.
            if (had.Owner && !want.Owner || had.Ro && !want.Ro || had.Adm && !want.Adm) await KickAsync(c, a.DbLogin, db, ct);
        }
    }

    private static Task<bool> Member(NpgsqlConnection c, string login, string role, CancellationToken ct) =>
        Scalar<bool>(c, "SELECT EXISTS (SELECT FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid JOIN pg_roles u ON u.oid = m.member WHERE r.rolname = @r AND u.rolname = @u)", ct, ("r", role), ("u", login));

    private static async Task SetMember(NpgsqlConnection c, string login, string role, bool want, bool had, CancellationToken ct)
    {
        if (want && !had) await Exec(c, $"GRANT {Id(role)} TO {Id(login)}", ct);
        if (!want && had) await Exec(c, $"REVOKE {Id(role)} FROM {Id(login)}", ct);
    }

    public async Task DropWorkspaceAsync(HostedWorkspace w, CancellationToken ct = default)
    {
        var db = w.Database;
        CheckDb(db);
        await using var c = await OpenAsync("postgres", ct);
        if (await Scalar<bool>(c, "SELECT EXISTS (SELECT FROM pg_database WHERE datname = @n)", ct, ("n", db)))
            await Exec(c, $"DROP DATABASE {Id(db)} WITH (FORCE)", ct);
        foreach (var r in new[] { Roles(db).Adm, Roles(db).Ro, Roles(db).Owner })
            await Exec(c, $"DROP ROLE IF EXISTS {Id(r)}", ct);
    }

    public async Task<bool> ExistsAsync(HostedWorkspace w, CancellationToken ct = default)
    {
        await using var c = await OpenAsync("postgres", ct);
        return await Scalar<bool>(c, "SELECT EXISTS (SELECT FROM pg_database WHERE datname = @n)", ct, ("n", w.Database));
    }

    /// <summary>Entries, size, last change and who is in RemoteMan right now (a heartbeat in the last 30 s).</summary>
    public async Task<WorkspaceStats> StatsAsync(HostedWorkspace w, CancellationToken ct = default)
    {
        CheckDb(w.Database);
        await using var c = await OpenAsync(w.Database, ct);
        var entries = await Scalar<long>(c, "SELECT count(*) FROM remoteman_entries WHERE deleted = 0", ct);
        var size = await Scalar<long>(c, "SELECT pg_database_size(current_database())", ct);
        var now = await Scalar<long>(c, "SELECT CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 1000 AS BIGINT)", ct);
        var online = new List<OnlineUser>();
        await using (var cmd = new NpgsqlCommand("SELECT user_name, machine FROM remoteman_presence WHERE last_seen > @t ORDER BY user_name", c))
        {
            cmd.Parameters.AddWithValue("t", now - 30_000);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) online.Add(new OnlineUser(r.GetString(0), r.GetString(1)));
        }
        var by = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? last = null;
        await using (var cmd = new NpgsqlCommand("SELECT updated_by, max(updated_at) FROM remoteman_entries WHERE updated_by IS NOT NULL GROUP BY updated_by", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1));
                by[r.GetString(0)] = at;
                if (last is null || at > last) last = at;
            }
        return new WorkspaceStats(entries, size, last, online, by);
    }

    // ---- backups ----

    /// <summary>
    /// Writes the workspace as a gzipped SQL script: the tables, then each table's rows as COPY blocks (what pg_dump
    /// writes too). <c>gunzip -c file | psql -d remoteman</c> loads it into a new, empty database on a self-hosted
    /// server; <see cref="RestoreAsync"/> reads only the COPY blocks back, for the tables it knows.
    /// </summary>
    public async Task<Dictionary<string, long>> BackupAsync(HostedWorkspace w, string path, CancellationToken ct = default)
    {
        CheckDb(w.Database);
        var rows = new Dictionary<string, long>();
        await using var c = await OpenAsync(w.Database, ct);
        // One snapshot for every table, so the file is consistent even while people work.
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Exec(c, "SET TRANSACTION READ ONLY", ct);
        var tmp = path + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using (var file = File.Create(tmp))
        await using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
        await using (var o = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            await o.WriteLineAsync("-- RemoteMan workspace backup");
            await o.WriteLineAsync($"-- Workspace: {OneLine(w.Name)} ({w.Id})");
            await o.WriteLineAsync($"-- Taken: {DateTimeOffset.UtcNow:O}");
            await o.WriteLineAsync("-- Load into a new, empty database: gunzip -c this-file.sql.gz | psql -d remoteman");
            await o.WriteLineAsync("-- Saved passwords stay encrypted with the workspace's vault passphrase.");
            await o.WriteLineAsync("BEGIN;");
            foreach (var sql in PostgresSchema.All) await o.WriteLineAsync(sql.Trim() + ";");
            foreach (var table in PostgresSchema.BackupTables)
            {
                var cols = await ColumnsAsync(c, table, ct);
                if (cols.Count == 0) continue;
                var list = string.Join(", ", cols.Select(Id));
                await o.WriteLineAsync($"DELETE FROM {table};");
                await o.WriteLineAsync($"COPY {table} ({list}) FROM stdin;");
                long n = 0;
                using (var reader = await c.BeginTextExportAsync($"COPY {table} ({list}) TO STDOUT", ct))
                {
                    while (await reader.ReadLineAsync(ct) is { } line) { await o.WriteLineAsync(line); n++; }
                }
                await o.WriteLineAsync("\\.");
                rows[table] = n;
            }
            await o.WriteLineAsync("COMMIT;");
            await o.WriteLineAsync("-- Rows: " + JsonSerializer.Serialize(rows));
        }
        await tx.CommitAsync(ct);
        File.Move(tmp, path, overwrite: true);
        return rows;
    }

    private static async Task<List<string>> ColumnsAsync(NpgsqlConnection c, string table, CancellationToken ct)
    {
        var list = new List<string>();
        await using var cmd = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position", c);
        cmd.Parameters.AddWithValue("t", table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    [GeneratedRegex(@"^COPY (remoteman_[a-z_]+) \(([a-z_"", ]+)\) FROM stdin;$")]
    private static partial Regex CopyLine();

    /// <summary>
    /// Loads a backup into a workspace made for it (its tables replaced). Only COPY blocks for RemoteMan's own tables and
    /// columns are read; anything else in the file is ignored, so a file can't run its own SQL on this server.
    /// </summary>
    public async Task<Dictionary<string, long>> RestoreAsync(HostedWorkspace w, string path, CancellationToken ct = default)
    {
        CheckDb(w.Database);
        var rows = new Dictionary<string, long>();
        await using var c = await OpenAsync(w.Database, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using var file = File.OpenRead(path);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var input = new StreamReader(gz, Encoding.UTF8);
        while (await input.ReadLineAsync(ct) is { } line)
        {
            var m = CopyLine().Match(line);
            if (!m.Success) continue;
            var table = m.Groups[1].Value;
            if (!PostgresSchema.BackupTables.Contains(table)) throw new InvalidDataException($"The backup has a table this server doesn't know: {table}.");
            var have = await ColumnsAsync(c, table, ct);
            var cols = m.Groups[2].Value.Split(',').Select(x => x.Trim().Trim('"')).ToList();
            if (cols.Any(x => !have.Contains(x))) throw new InvalidDataException($"The backup's {table} has columns this server doesn't know.");
            await Exec(c, $"DELETE FROM {table}", ct);
            long n = 0;
            await using (var writer = await c.BeginTextImportAsync($"COPY {table} ({string.Join(", ", cols.Select(Id))}) FROM STDIN", ct))
            {
                while (await input.ReadLineAsync(ct) is { } row && row != "\\.")
                {
                    await writer.WriteAsync(row + "\n");
                    n++;
                }
            }
            rows[table] = n;
        }
        if (rows.Count == 0) throw new InvalidDataException("That file has no RemoteMan data in it.");
        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>Restores a backup into a scratch database and counts its rows against the backup's own count.</summary>
    public async Task<(bool Ok, string Detail)> TestRestoreAsync(BackupRecord b, string path, CancellationToken ct = default)
    {
        var scratch = new HostedWorkspace { Id = "0e570000", Name = "restore test" };
        try
        {
            await DropWorkspaceAsync(scratch, ct);
            await CreateWorkspaceAsync(scratch, ct);
            var got = await RestoreAsync(scratch, path, ct);
            var bad = b.Rows.Where(kv => !got.TryGetValue(kv.Key, out var n) || n != kv.Value).Select(kv => $"{kv.Key}: {kv.Value} in the backup, {(got.TryGetValue(kv.Key, out var n2) ? n2 : 0)} restored").ToList();
            return bad.Count == 0 ? (true, $"{b.Rows.GetValueOrDefault("remoteman_entries")} entries restored") : (false, string.Join("; ", bad));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidDataException or IOException)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { await DropWorkspaceAsync(scratch, CancellationToken.None); } catch (NpgsqlException) { }
        }
    }

    /// <summary>The connection string an app uses for a workspace.</summary>
    public string AppConnectionString(HostedWorkspace w, HostedAccount a)
    {
        var (host, port) = PublicEndpoint;
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = w.Database, Username = a.DbLogin, Password = a.DbPassword,
            SslMode = options.NoTls ? SslMode.Disable : options.CaPem is null ? SslMode.Require : SslMode.VerifyFull,
            Timeout = 10, KeepAlive = 30,
        };
        return b.ConnectionString;
    }

    private static string OneLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ');
}
