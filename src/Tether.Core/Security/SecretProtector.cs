using System.Security.Cryptography;
using System.Text;

namespace Tether.Core.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

/// <summary>
/// AES-256-GCM. Used for shared data sources: the key is derived from a team vault
/// passphrase with PBKDF2-SHA256, so every user who knows the passphrase can decrypt.
/// Format: base64( "T1" | salt(16) | nonce(12) | tag(16) | ciphertext ).
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int Iterations = 210_000;
    private static readonly byte[] Magic = "T1"u8.ToArray();
    private readonly string _passphrase;

    public AesGcmSecretProtector(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Passphrase required.", nameof(passphrase));
        _passphrase = passphrase;
    }

    // Cache derived keys per salt; PBKDF2 is intentionally slow.
    private readonly Dictionary<string, byte[]> _keys = new();

    private byte[] KeyFor(byte[] salt)
    {
        var k = Convert.ToBase64String(salt);
        lock (_keys)
        {
            if (!_keys.TryGetValue(k, out var key))
                _keys[k] = key = Rfc2898DeriveBytes.Pbkdf2(_passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return key;
        }
    }

    private byte[]? _salt;

    public string Protect(string plaintext)
    {
        _salt ??= RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(KeyFor(_salt), 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. Magic, .. _salt, .. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedText)
    {
        var raw = Convert.FromBase64String(protectedText);
        if (raw.Length < 46 || raw[0] != Magic[0] || raw[1] != Magic[1]) throw new CryptographicException("Not a Tether secret.");
        var salt = raw.AsSpan(2, 16).ToArray();
        var nonce = raw.AsSpan(18, 12);
        var tag = raw.AsSpan(30, 16);
        var cipher = raw.AsSpan(46);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(KeyFor(salt), 16)) aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
