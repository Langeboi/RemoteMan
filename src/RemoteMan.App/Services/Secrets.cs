using System.IO;
using System.Security.Cryptography;
using System.Text;
using RemoteMan.Core.Model;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Protocols;

namespace RemoteMan.App.Services;

/// <summary>Local data source: secrets bound to this Windows user with DPAPI.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "RemoteMan.v1"u8.ToArray();
    public string Protect(string plaintext) =>
        "D1" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
    public string Unprotect(string protectedText) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText[2..]), Entropy, DataProtectionScope.CurrentUser));
}

public static class Secrets
{
    /// <summary>The team vault passphrase of a shared source, cached locally under DPAPI.</summary>
    public static string? LoadVaultPassphrase(string dataSourceName)
    {
        var f = VaultFile(dataSourceName);
        return File.Exists(f) ? new DpapiSecretProtector().Unprotect(File.ReadAllText(f)) : null;
    }

    public static void SaveVaultPassphrase(string dataSourceName, string passphrase)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(VaultFile(dataSourceName), new DpapiSecretProtector().Protect(passphrase));
    }

    private static string VaultFile(string name) =>
        Path.Combine(AppPaths.Root, "vault-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16] + ".key");

    public static ISecretProtector For(DataSourceConfig cfg)
    {
        if (cfg.Kind == DataSourceKind.Sqlite && !cfg.UseSharedVaultKey) return new DpapiSecretProtector();
        var pass = LoadVaultPassphrase(cfg.Name) ?? throw new InvalidOperationException($"No vault passphrase set for \"{cfg.Name}\".");
        return new AesGcmSecretProtector(pass);
    }

    /// <summary>Decrypts the credential linked to an entry (or the entry's own, for credential entries).</summary>
    public static ResolvedCredential Resolve(Entry entry, EntryStore store, ISecretProtector protector)
    {
        if (entry.UseWindowsLogin) return ResolvedCredential.WindowsLogin();
        var credEntry = entry.Kind == EntryKind.Credential ? entry : entry.CredentialId is { } id ? store.Get(id) : null;
        var c = credEntry?.Credential;
        if (c is null) return new ResolvedCredential(null, null, null, null, null);
        string? Un(string? s) => string.IsNullOrEmpty(s) ? null : protector.Unprotect(s);
        return new ResolvedCredential(c.UserName, c.Domain, Un(c.ProtectedPassword), Un(c.ProtectedPrivateKey), Un(c.ProtectedPassphrase));
    }
}
