namespace Tether.Protocols;

/// <summary>Decrypted login material handed to a session at connect time. Never persisted.</summary>
public sealed record ResolvedCredential(string? UserName, string? Domain, string? Password, string? PrivateKey, string? Passphrase);
