using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace RemoteMan.Core.Sharing;

/// <summary>Frame types on a shared-session link.</summary>
public enum ShareFrame : byte
{
    /// <summary>Guest → host, first frame: <see cref="HelloMessage"/>.</summary>
    Hello = 1,
    /// <summary>Host → guest after a good hello: <see cref="WelcomeMessage"/>.</summary>
    Welcome = 2,
    /// <summary>Host → everyone: <see cref="ShareSnapshot"/>.</summary>
    State = 3,
    /// <summary>Host → everyone: terminal output, UTF-8.</summary>
    Output = 4,
    /// <summary>Guest with control → host: keystrokes, UTF-8.</summary>
    Input = 5,
    /// <summary>Host → everyone: terminal size, <see cref="SizeMessage"/>.</summary>
    Size = 6,
    /// <summary>Guest → host: "may I have control?" Host → controller: someone asks (participant id, UTF-8).</summary>
    RequestControl = 7,
    /// <summary>Host or controller → host: give control to this participant id (UTF-8).</summary>
    GiveControl = 8,
    /// <summary>Guest → host: I'm leaving.</summary>
    Leave = 9,
    /// <summary>Host → guest: the session ended or you were refused (reason, UTF-8).</summary>
    Ended = 10,
    /// <summary>Guest → host: open tunnel connection N to the host's RDP sharing port (4-byte id).</summary>
    TunnelOpen = 11,
    /// <summary>Either way: bytes for tunnel connection N (4-byte id + data).</summary>
    TunnelData = 12,
    /// <summary>Either way: tunnel connection N closed (4-byte id).</summary>
    TunnelClose = 13,
    /// <summary>Keep-alive, either way, no payload.</summary>
    Ping = 14,
}

public sealed record HelloMessage(string Ticket, string User, string Machine, bool ReadOnly, int Version = ShareWire.Version);

public sealed record WelcomeMessage(string YourId, ShareKind Kind, string EntryName, int Cols, int Rows, string History,
    string? RdpConnectionString, string? RdpPassword, ShareSnapshot State);

public sealed record SizeMessage(int Cols, int Rows);

/// <summary>
/// Length-prefixed frames: 1 byte type, 4 bytes big-endian length, payload. Runs inside TLS.
/// </summary>
public static class ShareWire
{
    public const int Version = 1;
    public const int MaxFrame = 8 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static byte[] Frame(ShareFrame type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[5 + payload.Length];
        buf[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1), payload.Length);
        payload.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static byte[] Frame(ShareFrame type) => Frame(type, ReadOnlySpan<byte>.Empty);
    public static byte[] Frame(ShareFrame type, string text) => Frame(type, Encoding.UTF8.GetBytes(text));
    public static byte[] FrameJson<T>(ShareFrame type, T value) => Frame(type, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public static byte[] TunnelFrame(ShareFrame type, int id, ReadOnlySpan<byte> data = default)
    {
        var payload = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, id);
        data.CopyTo(payload.AsSpan(4));
        return Frame(type, payload);
    }

    public static (int Id, ReadOnlyMemory<byte> Data) ReadTunnel(byte[] payload) =>
        (BinaryPrimitives.ReadInt32BigEndian(payload), payload.AsMemory(4));

    public static T ReadJson<T>(byte[] payload) => JsonSerializer.Deserialize<T>(payload, Json)
        ?? throw new InvalidDataException("Empty message.");

    public static string Text(byte[] payload) => Encoding.UTF8.GetString(payload);

    /// <summary>Reads one frame; null at a clean end of stream.</summary>
    public static async Task<(ShareFrame Type, byte[] Payload)?> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[5];
        if (!await FillAsync(s, head, ct)) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1));
        if (len < 0 || len > MaxFrame) throw new InvalidDataException($"Frame of {len} bytes.");
        var payload = new byte[len];
        if (len > 0 && !await FillAsync(s, payload, ct)) throw new EndOfStreamException();
        return ((ShareFrame)head[0], payload);
    }

    private static async Task<bool> FillAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }

    /// <summary>Constant-time ticket comparison.</summary>
    public static bool SameTicket(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string NewTicket() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
}
