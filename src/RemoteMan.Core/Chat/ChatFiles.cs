using System.Security.Cryptography;

namespace RemoteMan.Core.Chat;

/// <summary>
/// A file sent in chat. The bytes live in their own table, encrypted with a key of their own
/// (AES-256-GCM); that key travels only inside the message, which is itself encrypted with the
/// team vault key. So the file table alone can't be read, and a message is all it takes to open one.
/// In a message it is the link <c>[[f:id;size;key|name]]</c>.
/// </summary>
public sealed record ChatFileRef(Guid Id, long Size, byte[] Key, string Name)
{
    /// <summary>Largest file chat takes (it lives in the shared database).</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico" };
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".jar", ".cpl", ".msc", ".dll", ".sys", ".inf", ".url", ".application", ".iso", ".img", ".vhd", ".vhdx",
    };

    public string Extension => Path.GetExtension(Name);
    public bool IsImage => ImageTypes.Contains(Extension);
    public bool IsGif => string.Equals(Extension, ".gif", StringComparison.OrdinalIgnoreCase);
    /// <summary>Runs code when opened: Open asks first.</summary>
    public bool IsProgram => Programs.Contains(Extension);
    public string SizeText => FormatSize(Size);
    public string Kind => IsGif ? "GIF" : IsImage ? "Image" : "File";

    public ChatLink ToLink() => new(ChatLink.File, $"{Id:N};{Size};{Convert.ToBase64String(Key).TrimEnd('=').Replace('+', '-').Replace('/', '_')}", SafeName(Name));

    public static ChatFileRef? From(ChatLink link)
    {
        if (link.Kind != ChatLink.File) return null;
        var p = link.Target.Split(';');
        if (p.Length != 3 || !Guid.TryParse(p[0], out var id) || !long.TryParse(p[1], out var size)) return null;
        try
        {
            var b64 = p[2].Replace('-', '+').Replace('_', '/');
            var key = Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
            return key.Length == 32 ? new ChatFileRef(id, size, key, SafeName(link.Label)) : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>A name that is safe to save under: no folders, no characters Windows refuses.</summary>
    public static string SafeName(string name)
    {
        var n = Path.GetFileName(name.Replace('\\', '/').Split('/').Last()).Trim();
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*', '[', ']'])) n = n.Replace(c, '_');
        n = n.Trim(' ', '.');
        return n.Length == 0 ? "file" : n.Length > 120 ? n[..(120 - Path.GetExtension(n).Length)] + Path.GetExtension(n) : n;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    // ---------------- encryption ----------------

    /// <summary>nonce(12) | tag(16) | ciphertext, with a fresh key.</summary>
    public static (byte[] Blob, byte[] Key) Encrypt(byte[] data)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = new byte[12 + 16 + data.Length];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(blob.AsSpan(0, 12), data, blob.AsSpan(28), blob.AsSpan(12, 16));
        return (blob, key);
    }

    public byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("The file is damaged.");
        var data = new byte[blob.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), data);
        return data;
    }
}

/// <summary>A file as stored: encrypted bytes and who sent it where.</summary>
public sealed record StoredChatFile(Guid Id, Guid RoomId, string Sender, DateTimeOffset SentAt, byte[] Blob);
