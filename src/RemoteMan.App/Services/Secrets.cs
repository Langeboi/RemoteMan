using System.IO;
using System.Security.Cryptography;
using System.Text;
using RemoteMan.Core.Ai;
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

    /// <summary>AI provider keys: this PC and this Windows user only, never the shared database.</summary>
    public static string? LoadAiKey(AiProviderKind kind)
    {
        var f = AiKeyFile(kind);
        try { return File.Exists(f) ? new DpapiSecretProtector().Unprotect(File.ReadAllText(f)) : null; }
        catch (CryptographicException) { return null; }
    }

    public static void SaveAiKey(AiProviderKind kind, string? key)
    {
        var f = AiKeyFile(kind);
        if (string.IsNullOrEmpty(key)) { if (File.Exists(f)) File.Delete(f); return; }
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(f, new DpapiSecretProtector().Protect(key));
    }

    private static string AiKeyFile(AiProviderKind kind) => Path.Combine(AppPaths.Root, $"ai-{kind.ToString().ToLowerInvariant()}.key");

    private static string VaultFile(string name) =>
        Path.Combine(AppPaths.Root, "vault-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16] + ".key");

    public static ISecretProtector For(DataSourceConfig cfg)
    {
        if (cfg.Kind == DataSourceKind.Sqlite && !cfg.UseSharedVaultKey) return new DpapiSecretProtector();
        var pass = LoadVaultPassphrase(cfg.Name) ?? throw new InvalidOperationException($"No vault passphrase set for \"{cfg.Name}\".");
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
