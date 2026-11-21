using System.Security.Cryptography;
using System.Text;

namespace RemoteMan.Core.Security;

/// <summary>Time-based one-time codes (RFC 6238), as made by Google Authenticator, Microsoft Authenticator and others.</summary>
public static class Totp
{
    /// <summary>The current code for a base32 secret (spaces and lower case allowed), or an otpauth:// URI.</summary>
    public static string Code(string secret, DateTimeOffset? now = null, int digits = 6, int period = 30)
    {
        var (key, d, p) = Parse(secret, digits, period);
        var counter = (long)Math.Floor((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / (double)p);
        return Hotp(key, counter, d);
    }

    /// <summary>Seconds until the current code changes.</summary>
    public static int SecondsLeft(string secret, DateTimeOffset? now = null)
    {
        var (_, _, p) = Parse(secret, 6, 30);
        var t = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return p - (int)(t % p);
    }

    public static bool IsValid(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;
        try { return Parse(secret, 6, 30).Key.Length > 0; }
        catch (FormatException) { return false; }
    }

    private static string Hotp(byte[] key, long counter, int digits)
    {
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xFF); counter >>= 8; }
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var mod = (int)Math.Pow(10, digits);
        return (bin % mod).ToString().PadLeft(digits, '0');
    }

    private static (byte[] Key, int Digits, int Period) Parse(string secret, int digits, int period)
    {
        var s = secret.Trim();
        if (s.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            string? sec = null;
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                switch (kv[0].ToLowerInvariant())
                {
                    case "secret": sec = v; break;
                    case "digits" when int.TryParse(v, out var dg) && dg is >= 6 and <= 8: digits = dg; break;
                    case "period" when int.TryParse(v, out var pd) && pd > 0: period = pd; break;
                }
            }
            s = sec ?? throw new FormatException("The otpauth link has no secret.");
        }
        return (Base32(s), digits, period);
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = new StringBuilder();
        foreach (var ch in text.ToUpperInvariant())
        {
            if (ch is ' ' or '-' or '=') continue;
            if (alphabet.IndexOf(ch) < 0) throw new FormatException($"'{ch}' isn't valid in a two-factor secret.");
            clean.Append(ch);
        }
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in clean.ToString())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
