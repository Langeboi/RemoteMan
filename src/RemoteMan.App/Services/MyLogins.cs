using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteMan.App.Services;

/// <summary>Your own login for a shared entry. Lives only on this PC, for this Windows user.</summary>
public sealed class MyLogin
{
    public bool Enabled { get; set; } = true;
    public string? UserName { get; set; }
    public string? Domain { get; set; }
    /// <summary>DPAPI-protected (this Windows user only).</summary>
    public string? ProtectedPassword { get; set; }
}

/// <summary>
/// "Connect with my own login instead": per-user logins for shared entries, kept in a file under
/// %APPDATA%\RemoteMan (one per data source), encrypted with DPAPI. Never written to the database,
/// so nobody else sees them and the shared entry stays as it is for everyone else.
/// </summary>
public static class MyLogins
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static Dictionary<Guid, MyLogin> _logins = new();
    private static string? _file;

    /// <summary>Switches to the logins kept for <paramref name="dataSourceName"/>.</summary>
    public static void Use(string dataSourceName)
    {
        _file = Path.Combine(AppPaths.Root, "my-logins-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataSourceName)))[..16] + ".json");
        try { _logins = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<Guid, MyLogin>>(File.ReadAllText(_file), Json) ?? new() : new(); }
        catch (Exception) { _logins = new(); }
    }

    public static MyLogin? Get(Guid entryId) => _logins.TryGetValue(entryId, out var l) ? l : null;

    /// <summary>The login to use instead of the entry's own, when one is set and switched on.</summary>
    public static MyLogin? Active(Guid entryId) => Get(entryId) is { Enabled: true } l && !string.IsNullOrEmpty(l.UserName) ? l : null;

    /// <summary>Saves (or with null removes) your login for an entry.</summary>
    public static void Set(Guid entryId, MyLogin? login)
    {
        if (login is null) _logins.Remove(entryId); else _logins[entryId] = login;
        if (_file is null) return;
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(_file, JsonSerializer.Serialize(_logins, Json));
    }

    public static string? Password(MyLogin l)
    {
        if (string.IsNullOrEmpty(l.ProtectedPassword)) return null;
        try { return new DpapiSecretProtector().Unprotect(l.ProtectedPassword); }
        catch (CryptographicException) { return null; }
    }

    public static string Protect(string password) => new DpapiSecretProtector().Protect(password);
}
