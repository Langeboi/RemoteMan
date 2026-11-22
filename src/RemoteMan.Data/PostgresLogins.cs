using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;

namespace RemoteMan.Data;

public enum DbLoginAccess { Shared, Admin, Edit, ReadOnly }

/// <summary>One database login as the Users tab shows it.</summary>
public sealed record DbLogin(string Name, DbLoginAccess Access, bool CanLogin, int Connections, DateTimeOffset? LastChange);

/// <summary>Who the data source is signed in as, and whether the server has the login functions.</summary>
public sealed record DbLoginSelf(string Login, DbLoginAccess? Access, bool IsAdmin, int Version);

/// <summary>A server of the pair: the one we're talking to (primary) and the standbys streaming from it.</summary>
public sealed record DbServerState(string Address, string Role, string State, long LagBytes)
{
    public bool InSync => Role == "primary" || State == "streaming" && LagBytes < 16 * 1024 * 1024;
}

/// <summary>A login change the server refused, with the server's own explanation as the message.</summary>
public sealed class DbLoginException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Manages a PostgreSQL data source's personal logins through the remoteman_admin functions that
/// deploy/postgres/remoteman-db.sh installs (setup, or "remoteman-db update" on an existing pair).
/// The functions run as postgres and only touch RemoteMan logins, so an admin needs no superuser rights.
/// Logins live in the database, so on a primary + standby pair the standby gets every change by replication;
/// the connection string targets the primary, so calls always land on the server that accepts them.
/// </summary>
public sealed partial class PostgresLogins(string connectionString)
{
    /// <summary>The remoteman_admin version this code understands.</summary>
    public const int RequiredVersion = 1;

    public string ConnectionString { get; } = connectionString;

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>A random password of letters and digits (no characters that need quoting in a connection string).</summary>
    public static string GeneratePassword(int length = 20)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(chars, length);
    }

    /// <summary>The connection string a teammate pastes into RemoteMan: this data source's servers with their login.</summary>
    public static string ConnectionStringFor(string baseConnectionString, string login, string password)
    {
        var b = new NpgsqlConnectionStringBuilder(baseConnectionString) { Username = login, Password = password };
        b.Remove("Integrated Security");
        b.Remove("Kerberos Service Name");
        b.Remove("Include Realm");
        b.Remove("Passfile");
        return b.ConnectionString;
    }

    public static DbLoginAccess? ParseAccess(string? access) => access switch
    {
        "shared" => DbLoginAccess.Shared,
        "admin" => DbLoginAccess.Admin,
        "edit" => DbLoginAccess.Edit,
        "read-only" => DbLoginAccess.ReadOnly,
        _ => null,
    };

    public static string AccessName(DbLoginAccess access) => access switch
    {
        DbLoginAccess.Admin => "admin",
        DbLoginAccess.Edit => "edit",
        DbLoginAccess.ReadOnly => "read-only",
        _ => throw new ArgumentException("The shared login's access can't be set.", nameof(access)),
    };

    /// <summary>Null when the server doesn't have the login functions yet (needs "remoteman-db update").</summary>
    public async Task<DbLoginSelf?> GetSelfAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = await Query("SELECT login, access, is_admin, version FROM remoteman_admin.me()", null,
                r => new DbLoginSelf(r.GetString(0), ParseAccess(r.IsDBNull(1) ? null : r.GetString(1)), r.GetBoolean(2), r.GetInt32(3)), ct);
            return rows.FirstOrDefault();
        }
        catch (DbLoginException ex) when (ex.InnerException is PostgresException
                                          {
                                              SqlState: PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.UndefinedFunction
                                          })
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DbLogin>> ListAsync(CancellationToken ct = default)
    {
        var rows = await Query("SELECT login, access, can_login, connections FROM remoteman_admin.list()", null,
            r => (Name: r.GetString(0), Access: ParseAccess(r.GetString(1)) ?? DbLoginAccess.ReadOnly, CanLogin: r.GetBoolean(2), Connections: r.GetInt32(3)), ct);

        // Last change per login, from the entries table (missing on a database RemoteMan hasn't used yet).
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            foreach (var (who, at) in await Query(
                         "SELECT updated_by, max(updated_at) FROM remoteman_entries WHERE updated_by IS NOT NULL GROUP BY updated_by", null,
                         r => (r.GetString(0), r.GetInt64(1)), ct))
                last[who] = DateTimeOffset.FromUnixTimeMilliseconds(at);
        }
        catch (DbLoginException) { }

        return rows.Select(x => new DbLogin(x.Name, x.Access, x.CanLogin, x.Connections,
            last.TryGetValue(x.Name, out var at) ? at : null)).ToList();
    }

    public Task<IReadOnlyList<DbServerState>> ServersAsync(CancellationToken ct = default) =>
        Query("SELECT address, role, state, lag_bytes FROM remoteman_admin.servers()", null,
            r => new DbServerState(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ct);

    public Task AddAsync(string name, string password, DbLoginAccess access, CancellationToken ct = default) =>
        Exec("SELECT remoteman_admin.add(@n, @p, @a)", [("n", name), ("p", password), ("a", AccessName(access))], ct);

    public Task SetAccessAsync(string name, DbLoginAccess access, CancellationToken ct = default) =>
        Exec("SELECT remoteman_admin.set_access(@n, @a)", [("n", name), ("a", AccessName(access))], ct);

    /// <summary>Also turns a disabled login back on. Anyone may set their own password; admins anyone's.</summary>
    public Task SetPasswordAsync(string name, string password, CancellationToken ct = default) =>
        Exec("SELECT remoteman_admin.set_password(@n, @p)", [("n", name), ("p", password)], ct);

    public Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default) =>
        Exec("SELECT remoteman_admin.set_enabled(@n, @e)", [("n", name), ("e", enabled)], ct);

    /// <summary>True when the login is gone; false when it owns something and was disabled instead.</summary>
    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default) =>
        (await Query("SELECT remoteman_admin.remove(@n)", [("n", name)], r => r.GetBoolean(0), ct)).FirstOrDefault();

    private async Task Exec(string sql, (string, object)[] args, CancellationToken ct) =>
        await Query(sql, args, _ => 0, ct);

    private async Task<IReadOnlyList<T>> Query<T>(string sql, (string Name, object Value)[]? args, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        try
        {
            // No pooling: a password change must not leave an open pooled connection behind, and these calls are rare.
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false };
            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (n, v) in args ?? []) cmd.Parameters.AddWithValue(n, v);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<T>();
            while (await r.ReadAsync(ct)) list.Add(map(r));
            return list;
        }
        catch (PostgresException ex)
        {
            throw new DbLoginException(ex.MessageText, ex);
        }
        catch (NpgsqlException ex)
        {
            throw new DbLoginException(ex.GetBaseException().Message, ex);
        }
    }
}
