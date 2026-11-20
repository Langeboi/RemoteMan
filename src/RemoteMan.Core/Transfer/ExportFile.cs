using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMan.Core.Model;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;

namespace RemoteMan.Core.Transfer;

/// <summary>One entry inside an export, with its secrets in plain text (the whole file is encrypted).</summary>
public sealed class ExportedEntry
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public EntryKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The entry's settings as <see cref="EntryJson"/> stores them, with protected secrets removed.</summary>
    public string Payload { get; set; } = "{}";
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string? Passphrase { get; set; }
    /// <summary>Base64 .pfx bytes of a certificate credential.</summary>
    public string? Certificate { get; set; }
}

/// <summary>The decrypted content of an .rmx file.</summary>
public sealed class ExportBundle
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string? CreatedBy { get; set; }
    public string? Machine { get; set; }
    public bool IncludesSecrets { get; set; }
    public List<ExportedEntry> Entries { get; set; } = new();

    public int Count(EntryKind kind) => Entries.Count(e => e.Kind == kind);
}

public sealed class ExportOptions
{
    public bool IncludeSecrets { get; set; } = true;
    public bool IncludeSnippets { get; set; } = true;
    /// <summary>Export only this folder and everything under it; null = everything.</summary>
    public Guid? RootFolderId { get; set; }
}

/// <summary>Thrown when an .rmx file cannot be opened with the given password.</summary>
public sealed class WrongPasswordException() : Exception("Wrong password, or the file is damaged.");

/// <summary>
/// RemoteMan export and backup file (.rmx): a small JSON envelope around an AES-256-GCM encrypted,
/// gzip-compressed <see cref="ExportBundle"/>. The key is derived from a password with
/// PBKDF2-SHA256, so the file opens on any PC, into any data source, with the password alone.
/// </summary>
public static class ExportFile
{
    public const string Extension = ".rmx";
    public const string Magic = "remoteman-export";
    public const int DefaultIterations = 600_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Envelope
    {
        public string Format { get; set; } = Magic;
        public int Version { get; set; } = 1;
        public string Kdf { get; set; } = "pbkdf2-sha256";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        /// <summary>Ciphertext followed by the 16-byte GCM tag.</summary>
        public string Data { get; set; } = "";
    }

    /// <summary>Builds a bundle from live entries, decrypting their secrets with the source's protector.</summary>
    public static ExportBundle CreateBundle(IEnumerable<Entry> entries, ISecretProtector? protector, ExportOptions options, string? user = null, string? machine = null)
    {
        var all = entries.Where(e => !e.Deleted).ToList();
        if (options.RootFolderId is { } root) all = Subtree(all, root);
        if (!options.IncludeSnippets) all = all.Where(e => e.Kind != EntryKind.Snippet).ToList();

        var bundle = new ExportBundle { CreatedBy = user, Machine = machine, IncludesSecrets = options.IncludeSecrets && protector is not null };
        foreach (var e in all)
        {
            var copy = EntryStore.Clone(e);
            var x = new ExportedEntry { Id = e.Id, ParentId = e.ParentId, Kind = e.Kind, Name = e.Name };
            if (copy.Credential is { } c)
            {
                if (bundle.IncludesSecrets)
                {
                    x.Password = TryUnprotect(protector!, c.ProtectedPassword);
                    x.PrivateKey = TryUnprotect(protector!, c.ProtectedPrivateKey);
                    x.Passphrase = TryUnprotect(protector!, c.ProtectedPassphrase);
                    x.Certificate = TryUnprotect(protector!, c.ProtectedCertificate);
                }
                c.ProtectedPassword = c.ProtectedPrivateKey = c.ProtectedPassphrase = c.ProtectedCertificate = null;
            }
            x.Payload = EntryJson.ToPayload(copy);
            bundle.Entries.Add(x);
        }
        // An exported subtree has no parent above its root.
        var ids = bundle.Entries.Select(e => e.Id).ToHashSet();
        foreach (var x in bundle.Entries) if (x.ParentId is { } p && !ids.Contains(p)) x.ParentId = null;
        return bundle;
    }

    private static string? TryUnprotect(ISecretProtector p, string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return p.Unprotect(s); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; } // a secret from another vault key: leave it out
    }

    private static List<Entry> Subtree(List<Entry> all, Guid root)
    {
        var byParent = all.ToLookup(e => e.ParentId);
        var result = new List<Entry>();
        var start = all.FirstOrDefault(e => e.Id == root);
        if (start is null) return result;
        var queue = new Queue<Entry>([start]);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            result.Add(e);
            foreach (var child in byParent[e.Id]) queue.Enqueue(child);
        }
        // Credentials linked from inside the folder come along, so the export is usable on its own.
        var have = result.Select(e => e.Id).ToHashSet();
        foreach (var credId in result.Select(e => e.CredentialId).Concat(result.Select(e => e.Ssh?.JumpHostId)).OfType<Guid>().Distinct().ToList())
            if (!have.Contains(credId) && all.FirstOrDefault(e => e.Id == credId) is { } extra) { result.Add(extra); have.Add(credId); }
        return result;
    }

    public static void Write(Stream output, ExportBundle bundle, string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A password is required.", nameof(password));
        var plain = Compress(JsonSerializer.SerializeToUtf8Bytes(bundle, Json));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(password, salt, iterations);
        var cipher = new byte[plain.Length + 16];
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(nonce, plain, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), Encoding.ASCII.GetBytes(Magic));
        CryptographicOperations.ZeroMemory(key);

        var env = new Envelope
        {
            Iterations = iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Data = Convert.ToBase64String(cipher),
        };
        JsonSerializer.Serialize(output, env, new JsonSerializerOptions { WriteIndented = true });
    }

    public static async Task WriteFileAsync(string path, ExportBundle bundle, string password)
    {
        // Write next to the target and swap, so a crash never leaves half a backup.
        var tmp = path + ".tmp";
        await Task.Run(() => { using var f = File.Create(tmp); Write(f, bundle, password); });
        File.Move(tmp, path, overwrite: true);
    }

    public static ExportBundle Read(Stream input, string password)
    {
        Envelope env;
        try { env = JsonSerializer.Deserialize<Envelope>(input) ?? throw new InvalidDataException(); }
        catch (JsonException) { throw new InvalidDataException("Not a RemoteMan export file."); }
        if (env.Format != Magic) throw new InvalidDataException("Not a RemoteMan export file.");
        if (env.Version > 1) throw new InvalidDataException("This file was made by a newer RemoteMan. Update RemoteMan to open it.");

        var salt = Convert.FromBase64String(env.Salt);
        var nonce = Convert.FromBase64String(env.Nonce);
        var data = Convert.FromBase64String(env.Data);
        if (data.Length < 16 || env.Iterations < 1) throw new InvalidDataException("The file is damaged.");
        var key = DeriveKey(password, salt, env.Iterations);
        var plain = new byte[data.Length - 16];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, Encoding.ASCII.GetBytes(Magic));
        }
        catch (CryptographicException) { throw new WrongPasswordException(); }
        finally { CryptographicOperations.ZeroMemory(key); }

        return JsonSerializer.Deserialize<ExportBundle>(Decompress(plain), Json) ?? new ExportBundle();
    }

    public static ExportBundle ReadFile(string path, string password)
    {
        using var f = File.OpenRead(path);
        return Read(f, password);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal)) gz.Write(data);
        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}
