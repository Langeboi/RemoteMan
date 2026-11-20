namespace RemoteMan.Protocols;

/// <summary>Decrypted login material handed to a session at connect time. Never persisted.</summary>
/// <param name="UseWindowsLogin">Single sign-on: authenticate as the current Windows user, no stored secret.</param>
public sealed record ResolvedCredential(string? UserName, string? Domain, string? Password, string? PrivateKey, string? Passphrase,
    bool UseWindowsLogin = false)
{
    public static ResolvedCredential WindowsLogin() =>
        new(Environment.UserName, Environment.UserDomainName, null, null, null, UseWindowsLogin: true);
}
