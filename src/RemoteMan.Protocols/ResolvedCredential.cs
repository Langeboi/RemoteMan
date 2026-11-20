namespace RemoteMan.Protocols;

/// <summary>Decrypted login material handed to a session at connect time. Never persisted.</summary>
/// <param name="UseWindowsLogin">Single sign-on: authenticate as the current Windows user, no stored secret.</param>
/// <param name="Certificate">.pfx bytes of a client certificate (its password is <paramref name="Password"/>).</param>
/// <param name="AskPassword">The password isn't stored: ask the user before connecting.</param>
public sealed record ResolvedCredential(string? UserName, string? Domain, string? Password, string? PrivateKey, string? Passphrase,
    bool UseWindowsLogin = false, byte[]? Certificate = null, bool AskPassword = false)
{
    public static ResolvedCredential None { get; } = new(null, null, null, null, null);

    public static ResolvedCredential WindowsLogin() =>
        new(Environment.UserName, Environment.UserDomainName, null, null, null, UseWindowsLogin: true);

    public bool IsEmpty => UserName is null && Password is null && PrivateKey is null && Certificate is null && !UseWindowsLogin;
}
