using System.Security.Cryptography;
using System.Text;

namespace RemoteMan.Core.Security;

/// <summary>
/// Time-based one-time codes (RFC 6238), as made by Google Authenticator, Microsoft Authenticator and others.
/// The stored secret is what the user gave: a base32 key, or a whole <c>otpauth://totp/…</c> link (from a QR code),
/// which also carries the issuer, account, digits, period and hash algorithm.
/// </summary>
public static class Totp
{
    /// <summary>The current code for a base32 secret (spaces and lower case allowed), or an otpauth:// URI.</summary>
    public static string Code(string secret, DateTimeOffset? now = null, int digits = 6, int period = 30)
    {
        var o = Parse(secret, digits, period);
        var counter = (long)Math.Floor((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / (double)o.Period);
        return Hotp(o.Key, counter, o.Digits, o.Algorithm);
    }

    /// <summary>Seconds until the current code changes.</summary>
    public static int SecondsLeft(string secret, DateTimeOffset? now = null)
    {
        var p = Period(secret);
        var t = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return p - (int)(t % p);
    }

    /// <summary>How long each code lasts, in seconds (30 unless the otpauth link says otherwise).</summary>
    public static int Period(string secret) => Parse(secret, 6, 30).Period;

    /// <summary>
    /// The code to put on the clipboard: the current one, or the next one when the current one runs out
    /// within <paramref name="margin"/> seconds, so a pasted code isn't already stale. <paramref name="validFor"/> is its lifetime left.
    /// </summary>
    public static string CodeToCopy(string secret, out int validFor, DateTimeOffset? now = null, int margin = 3)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        validFor = SecondsLeft(secret, t);
        if (validFor > margin) return Code(secret, t);
        var next = t.AddSeconds(validFor);
        validFor += Period(secret);
        return Code(secret, next);
    }

    /// <summary>"GitHub (lange)" from an otpauth link; empty for a bare key.</summary>
    public static string Label(string secret)
    {
        var o = Parse(secret, 6, 30);
        return (o.Issuer, o.Account) switch
        {
            ({ Length: > 0 } i, { Length: > 0 } a) when !string.Equals(i, a, StringComparison.OrdinalIgnoreCase) => $"{i} ({a})",
            ({ Length: > 0 } i, _) => i,
            (_, { Length: > 0 } a) => a,
            _ => "",
        };
    }

    /// <summary>Splits a code in two for reading ("123 456"); copy the ungrouped one.</summary>
    public static string Group(string code) => code.Length < 6 ? code : $"{code[..(code.Length / 2)]} {code[(code.Length / 2)..]}";

    public static bool IsValid(string? secret) => Validate(secret) is null;

    /// <summary>Null when the secret works, otherwise why it doesn't (for the editor).</summary>
    public static string? Validate(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "Empty.";
        try { return Parse(secret, 6, 30).Key.Length > 0 ? null : "The secret is empty."; }
        catch (FormatException ex) { return ex.Message; }
    }

    private static string Hotp(byte[] key, long counter, int digits, HashAlgorithmName algorithm)
    {
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xFF); counter >>= 8; }
        var hash = algorithm == HashAlgorithmName.SHA256 ? HMACSHA256.HashData(key, msg)
            : algorithm == HashAlgorithmName.SHA512 ? HMACSHA512.HashData(key, msg)
            : HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var mod = (int)Math.Pow(10, digits);
        return (bin % mod).ToString().PadLeft(digits, '0');
    }

    private sealed record Options(byte[] Key, int Digits, int Period, HashAlgorithmName Algorithm, string? Issuer, string? Account);

    private static Options Parse(string secret, int digits, int period)
    {
        var s = secret.Trim();
        var algorithm = HashAlgorithmName.SHA1;
        string? issuer = null, account = null;
        if (s.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) throw new FormatException("That otpauth link is malformed.");
            if (uri.Host.Equals("hotp", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Counter-based (HOTP) codes aren't supported, only time-based (TOTP).");
            if (!uri.Host.Equals("totp", StringComparison.OrdinalIgnoreCase)) throw new FormatException("That isn't a TOTP link.");

            var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var colon = label.IndexOf(':');
            if (colon >= 0) { issuer = label[..colon].Trim(); account = label[(colon + 1)..].Trim(); }
            else if (label.Length > 0) account = label.Trim();

            string? sec = null;
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                switch (kv[0].ToLowerInvariant())
                {
                    case "secret": sec = v; break;
                    case "issuer" when v.Trim().Length > 0: issuer = v.Trim(); break;
                    case "digits" when int.TryParse(v, out var dg) && dg is >= 6 and <= 8: digits = dg; break;
                    case "period" when int.TryParse(v, out var pd) && pd is > 0 and <= 3600: period = pd; break;
                    case "algorithm":
                        algorithm = v.ToUpperInvariant().Replace("-", "") switch
                        {
                            "SHA1" or "" => HashAlgorithmName.SHA1,
                            "SHA256" => HashAlgorithmName.SHA256,
                            "SHA512" => HashAlgorithmName.SHA512,
                            _ => throw new FormatException($"The {v} algorithm isn't supported (SHA1, SHA256 or SHA512)."),
                        };
                        break;
                }
            }
            s = sec ?? throw new FormatException("The otpauth link has no secret.");
        }
        else if (s.Contains("://")) throw new FormatException("That link isn't an otpauth:// link.");
        return new Options(Base32(s), digits, period, algorithm,
            string.IsNullOrEmpty(issuer) ? null : issuer, string.IsNullOrEmpty(account) ? null : account);
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
            buffer = ((buffer << 5) | alphabet.IndexOf(ch)) & 0xFFFF;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
