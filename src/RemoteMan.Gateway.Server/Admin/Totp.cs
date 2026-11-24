using System.Security.Cryptography;
using System.Text;

namespace RemoteMan.Gateway.Server.Admin;

/// <summary>RFC 6238 time-based one-time codes (6 digits, 30 s, SHA-1), what every authenticator app speaks.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32(RandomNumberGenerator.GetBytes(20));

    public static string Uri(string secret, string account, string issuer) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&digits=6&period=30";

    /// <summary>Accepts the current code and one step either side (clock drift).</summary>
    public static bool Verify(string secret, string? code, DateTimeOffset? now = null)
    {
        code = code?.Replace(" ", "").Trim();
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit)) return false;
        var key = FromBase32(secret);
        var step = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var ok = false;
        for (var d = -1; d <= 1; d++)
            ok |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, step + d)), Encoding.ASCII.GetBytes(code));
        return ok;
    }

    public static string Code(byte[] key, long step)
    {
        var msg = new byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(step & 0xFF); step >>= 8; }
        var h = HMACSHA1.HashData(key, msg);
        var o = h[^1] & 0x0F;
        var bin = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) continue;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
