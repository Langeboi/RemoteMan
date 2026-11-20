using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace RemoteMan.Protocols.Ssh;

/// <summary>Small helpers for the credential editor: new key pairs and the public half of a stored key.</summary>
public static class SshKeys
{
    /// <summary>A new RSA 3072 key (PEM, which every SSH server and SSH.NET accept) and its OpenSSH public key line.</summary>
    public static (string PrivateKeyPem, string PublicKeyLine) GenerateRsa(string comment)
    {
        using var rsa = RSA.Create(3072);
        var p = rsa.ExportParameters(false);
        var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteMpint(blob, p.Exponent!);
        WriteMpint(blob, p.Modulus!);
        return (rsa.ExportRSAPrivateKeyPem() + "\n", $"ssh-rsa {Convert.ToBase64String(blob.ToArray())} {comment}");
    }

    /// <summary>"ssh-ed25519 AAAA… comment" for any key SSH.NET can read (OpenSSH, PEM, PuTTY).</summary>
    public static string PublicKeyLine(string privateKey, string? passphrase, string comment)
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
        var key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(s) : new PrivateKeyFile(s, passphrase);
        var alg = key.HostKeyAlgorithms.First();
        // Data is the public key blob in SSH wire format; its leading string names the key type.
        var data = alg.Data;
        var typeLen = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        var type = Encoding.ASCII.GetString(data, 4, typeLen);
        return $"{type} {Convert.ToBase64String(data)} {comment}";
    }

    private static void WriteString(Stream s, byte[] b)
    {
        s.Write([(byte)(b.Length >> 24), (byte)(b.Length >> 16), (byte)(b.Length >> 8), (byte)b.Length]);
        s.Write(b);
    }

    private static void WriteMpint(Stream s, byte[] unsignedBigEndian)
    {
        var b = unsignedBigEndian.SkipWhile(x => x == 0).ToArray();
        if (b.Length > 0 && (b[0] & 0x80) != 0) b = [0, .. b];
        WriteString(s, b);
    }
}
