using System.Security.Cryptography.X509Certificates;

namespace RemoteMan.App.Services;

/// <summary>Puts a certificate credential's .pfx into the current user's certificate store (once), so the
/// embedded browser can present it to sites that require a client certificate.</summary>
public static class ClientCertificates
{
    public static string EnsureInUserStore(byte[] pfx, string? password)
    {
        using var cert = new X509Certificate2(pfx, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count == 0) store.Add(cert);
        return cert.Thumbprint;
    }
}
