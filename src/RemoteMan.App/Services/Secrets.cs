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

    /// <summary>What this PC files a data source's local things under (vault passphrase, offline copy): its name, or for a
    /// hosted workspace the server and workspace id, so a rename on the server keeps them.</summary>
    public static string LocalKey(DataSourceConfig cfg) => cfg.Hosted is { } h ? $"hosted:{h.Server.TrimEnd('/')}|{h.WorkspaceId}" : cfg.Name;

    public static string? LoadVaultPassphrase(DataSourceConfig cfg) => LoadVaultPassphrase(LocalKey(cfg));
    public static void SaveVaultPassphrase(DataSourceConfig cfg, string passphrase) => SaveVaultPassphrase(LocalKey(cfg), passphrase);

    /// <summary>Forgets a data source's vault passphrase on this PC (a hosted workspace you were taken out of).</summary>
    public static void DeleteVaultPassphrase(DataSourceConfig cfg)
    {
        try { File.Delete(VaultFile(LocalKey(cfg))); } catch (IOException) { }
    }

    private static string VaultFile(string name) =>
        Path.Combine(AppPaths.Root, "vault-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16] + ".key");

    public static ISecretProtector For(DataSourceConfig cfg)
    {
        if (cfg.Kind == DataSourceKind.Sqlite && !cfg.UseSharedVaultKey) return new DpapiSecretProtector();
        var pass = LoadVaultPassphrase(cfg) ?? throw new InvalidOperationException($"No vault passphrase set for \"{cfg.Name}\".");
        return new AesGcmSecretProtector(pass);
    }

    /// <summary>Custom field values by name, secret ones decrypted (for login scripts and copying).</summary>
    public static Dictionary<string, string> FieldValues(Entry entry, ISecretProtector protector)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in entry.Fields ?? [])
        {
            if (string.IsNullOrWhiteSpace(f.Name) || f.Value is null) continue;
            try { d[f.Name.Trim()] = f.Secret ? protector.Unprotect(f.Value) : f.Value; }
            catch (Exception) { /* secret from another vault key: left out */ }
        }
        return d;
    }

    /// <summary>Decrypts the login for an entry, following "inherited" up the folders and
    /// "saved" to the credential entry (credential entries resolve to themselves).</summary>
    public static ResolvedCredential Resolve(Entry entry, EntryStore store, ISecretProtector protector)
    {
        var (source, holder) = store.ResolveLogin(entry);
        if (source == LoginSource.WindowsLogin) return ResolvedCredential.WindowsLogin();
        if (holder?.Credential is not { } c) return ResolvedCredential.None;
        string? Un(string? s) => string.IsNullOrEmpty(s) ? null : protector.Unprotect(s);

        var key = Un(c.ProtectedPrivateKey);
        if (key is null && c.Type == CredentialType.SshKey && !string.IsNullOrWhiteSpace(c.PrivateKeyPath))
        {
            var path = Environment.ExpandEnvironmentVariables(c.PrivateKeyPath);
            if (File.Exists(path)) key = File.ReadAllText(path);
        }
        var cert = c.Type == CredentialType.Certificate && Un(c.ProtectedCertificate) is { } b64 ? Convert.FromBase64String(b64) : null;
        return new ResolvedCredential(c.UserName, c.Domain, Un(c.ProtectedPassword), key, Un(c.ProtectedPassphrase),
            Certificate: cert, AskPassword: c.AskPassword && c.Type == CredentialType.Password, TotpSecret: Un(c.ProtectedTotpSecret));
    }
}
