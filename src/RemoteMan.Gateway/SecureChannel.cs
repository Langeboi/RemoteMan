using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace RemoteMan.Gateway;

/// <summary>
/// End-to-end encryption between RemoteMan and a connector, inside the WebSocket the relay splices.
/// The relay (and anything in front of it, such as Cloudflare) only ever sees ciphertext.
/// <para>
/// Handshake, one binary WebSocket message each way:
/// <code>
/// client → connector: 01 | client ephemeral P-256 key (65) | client nonce (32)
/// connector → client: 01 | connector ephemeral key (65) | connector nonce (32) | u16 len | static key (SPKI) | u16 len | signature
/// </code>
/// The connector signs the transcript hash with its static ECDSA key; the client checks that the key is one the
/// relay lists for the site. Both sides then derive two AES-256-GCM keys (one per direction) with HKDF-SHA256 from
/// the ephemeral ECDH secret, salted with the transcript hash. Every later WebSocket message is one sealed frame with
/// a 64-bit counter as its nonce, so frames can't be replayed, reordered or dropped unnoticed.
/// </para>
/// </summary>
public sealed class SecureChannel
{
    public const int MaxPlaintext = 32 * 1024;
    private const byte Version = 1;
    private const int PubLen = 65, NonceLen = 32, TagLen = 16;
    private static readonly byte[] Info = "remoteman-gateway v1"u8.ToArray();

    private readonly WebSocket _ws;
    private readonly AesGcm _send, _recv;
    private ulong _sendCounter, _recvCounter;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _recvBuffer = new byte[MaxPlaintext + TagLen + 64];
    // Reused for every frame sent (under the send lock): no allocation per frame on big transfers.
    private readonly byte[] _sendFrame = new byte[MaxPlaintext + TagLen];
    private readonly byte[] _sendNonce = new byte[12];

    private SecureChannel(WebSocket ws, byte[] sendKey, byte[] recvKey)
    {
        _ws = ws;
        _send = new AesGcm(sendKey, TagLen);
        _recv = new AesGcm(recvKey, TagLen);
    }

    /// <summary>SHA-256 of the connector's static public key (SPKI), as shown to people: 16 groups of hex.</summary>
    public static string Fingerprint(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki))[..32].ToLowerInvariant();

    /// <summary>Client side. <paramref name="acceptKey"/> gets the connector's static key (SPKI) and says whether to trust it.</summary>
    public static async Task<(SecureChannel Channel, byte[] ConnectorKey)> ClientAsync(WebSocket ws, Func<byte[], bool> acceptKey, CancellationToken ct)
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new byte[1 + PubLen + NonceLen];
        hello[0] = Version;
        PublicPoint(eph).CopyTo(hello, 1);
        RandomNumberGenerator.Fill(hello.AsSpan(1 + PubLen, NonceLen));
        await ws.SendAsync(hello, WebSocketMessageType.Binary, true, ct);

        var reply = await ReceiveWholeAsync(ws, 4096, ct) ?? throw new GatewayException("The connector closed the connection during the handshake.");
        if (reply.Length < 1 + PubLen + NonceLen + 4 || reply[0] != Version) throw new GatewayException("The connector answered with an unknown handshake.");
        var peerPub = reply.AsSpan(1, PubLen).ToArray();
        var at = 1 + PubLen + NonceLen;
        var keyLen = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at)); at += 2;
        if (at + keyLen + 2 > reply.Length) throw new GatewayException("Bad handshake from the connector.");
        var spki = reply.AsSpan(at, keyLen).ToArray(); at += keyLen;
        var sigLen = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at)); at += 2;
        if (at + sigLen != reply.Length) throw new GatewayException("Bad handshake from the connector.");
        var sig = reply.AsSpan(at, sigLen).ToArray();

        var transcript = Transcript(hello, reply.AsSpan(1, PubLen + NonceLen), spki);
        using (var verify = ECDsa.Create())
        {
            try { verify.ImportSubjectPublicKeyInfo(spki, out _); }
            catch (CryptographicException) { throw new GatewayException("The connector sent an unreadable key."); }
            if (!verify.VerifyHash(transcript, sig)) throw new GatewayException("The connector's signature doesn't match. Someone may be in the middle.");
        }
        if (!acceptKey(spki)) throw new GatewayException($"The connector's key ({Fingerprint(spki)}) isn't one the relay lists for this site.");

        var (c2n, n2c) = DeriveKeys(eph, peerPub, transcript);
        return (new SecureChannel(ws, c2n, n2c), spki);
    }

    /// <summary>Connector side: answers the client's hello and signs with the connector's static key.</summary>
    public static async Task<SecureChannel> ConnectorAsync(WebSocket ws, ECDsa staticKey, CancellationToken ct)
    {
        var hello = await ReceiveWholeAsync(ws, 1024, ct) ?? throw new GatewayException("The client went away during the handshake.");
        if (hello.Length != 1 + PubLen + NonceLen || hello[0] != Version) throw new GatewayException("Unknown handshake from the client.");
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = new byte[PubLen + NonceLen];
        PublicPoint(eph).CopyTo(mine, 0);
        RandomNumberGenerator.Fill(mine.AsSpan(PubLen));
        var spki = staticKey.ExportSubjectPublicKeyInfo();
        var transcript = Transcript(hello, mine, spki);
        var sig = staticKey.SignHash(transcript);

        var reply = new byte[1 + mine.Length + 2 + spki.Length + 2 + sig.Length];
        reply[0] = Version;
        var at = 1;
        mine.CopyTo(reply, at); at += mine.Length;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(at), (ushort)spki.Length); at += 2;
        spki.CopyTo(reply, at); at += spki.Length;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(at), (ushort)sig.Length); at += 2;
        sig.CopyTo(reply, at);
        await ws.SendAsync(reply, WebSocketMessageType.Binary, true, ct);

        var (c2n, n2c) = DeriveKeys(eph, hello.AsSpan(1, PubLen).ToArray(), transcript);
        return new SecureChannel(ws, n2c, c2n);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        for (var off = 0; off < plaintext.Length || plaintext.Length == 0; off += MaxPlaintext)
        {
            var chunk = plaintext.Slice(off, Math.Min(MaxPlaintext, plaintext.Length - off));
            await _sendLock.WaitAsync(ct);
            try
            {
                var frame = _sendFrame.AsMemory(0, chunk.Length + TagLen);
                BinaryPrimitives.WriteUInt64BigEndian(_sendNonce.AsSpan(4), _sendCounter++);
                _send.Encrypt(_sendNonce, chunk.Span, frame.Span[..chunk.Length], frame.Span[chunk.Length..]);
                await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
            }
            finally { _sendLock.Release(); }
            if (plaintext.Length == 0) break;
        }
    }

    /// <summary>The next frame's plaintext, or null when the other side closed.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var n = await ReceiveSealedAsync(ct);
        if (n < 0) return null;
        var plain = new byte[n - TagLen];
        Open(n, plain);
        return plain;
    }

    /// <summary>
    /// The next frame's plaintext decrypted into <paramref name="into"/> (at least <see cref="MaxPlaintext"/> bytes),
    /// without allocating. Returns its length, or -1 when the other side closed.
    /// </summary>
    public async Task<int> ReceiveAsync(Memory<byte> into, CancellationToken ct)
    {
        var n = await ReceiveSealedAsync(ct);
        if (n < 0) return -1;
        if (into.Length < n - TagLen) throw new ArgumentException("Buffer too small.", nameof(into));
        Open(n, into.Span[..(n - TagLen)]);
        return n - TagLen;
    }

    private async Task<int> ReceiveSealedAsync(CancellationToken ct)
    {
        var n = 0;
        while (true)
        {
            if (n == _recvBuffer.Length) throw new GatewayException("Frame too large.");
            var r = await _ws.ReceiveAsync(_recvBuffer.AsMemory(n), ct);
            if (r.MessageType == WebSocketMessageType.Close) return -1;
            n += r.Count;
            if (r.EndOfMessage) break;
        }
        if (n < TagLen) throw new GatewayException("Frame too short.");
        return n;
    }

    private void Open(int n, Span<byte> plain)
    {
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _recvCounter++);
        try { _recv.Decrypt(nonce, _recvBuffer.AsSpan(0, n - TagLen), _recvBuffer.AsSpan(n - TagLen, TagLen), plain); }
        catch (CryptographicException) { throw new GatewayException("A frame failed its integrity check; the connection was tampered with."); }
    }

    public async Task CloseAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        }
        catch { }
    }

    private static byte[] PublicPoint(ECDiffieHellman k)
    {
        var p = k.ExportParameters(false).Q;
        var b = new byte[PubLen];
        b[0] = 4;
        p.X!.CopyTo(b, 1);
        p.Y!.CopyTo(b, 33);
        return b;
    }

    private static byte[] Transcript(ReadOnlySpan<byte> clientHello, ReadOnlySpan<byte> connectorPart, ReadOnlySpan<byte> spki)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(clientHello);
        h.AppendData(connectorPart);
        h.AppendData(spki);
        return h.GetHashAndReset();
    }

    private static (byte[] C2N, byte[] N2C) DeriveKeys(ECDiffieHellman mine, byte[] peerPoint, byte[] transcript)
    {
        if (peerPoint.Length != PubLen || peerPoint[0] != 4) throw new GatewayException("Bad key in the handshake.");
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = peerPoint[1..33], Y = peerPoint[33..65] },
        });
        var secret = mine.DeriveRawSecretAgreement(peer.PublicKey);
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, transcript, Info);
        CryptographicOperations.ZeroMemory(secret);
        return (okm[..32], okm[32..]);
    }

    internal static async Task<byte[]?> ReceiveWholeAsync(WebSocket ws, int max, CancellationToken ct)
    {
        var buf = new byte[max];
        var n = 0;
        while (true)
        {
            if (n == max) throw new GatewayException("Message too large.");
            var r = await ws.ReceiveAsync(buf.AsMemory(n), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            n += r.Count;
            if (r.EndOfMessage) return buf[..n];
        }
    }
}

/// <summary>A gateway failure with a message meant for people.</summary>
public sealed class GatewayException(string message) : Exception(message);
