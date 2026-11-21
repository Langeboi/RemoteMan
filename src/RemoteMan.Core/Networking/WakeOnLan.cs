using System.Net;
using System.Net.Sockets;

namespace RemoteMan.Core.Networking;

/// <summary>Sends the Wake-on-LAN "magic packet": six 0xFF bytes and the MAC address 16 times, over UDP 9 (and 7).</summary>
public static class WakeOnLan
{
    /// <summary>Accepts 3C:52:82:1A:4F:09, 3C-52-82-1A-4F-09, 3C52.821A.4F09 and 3C52821A4F09.</summary>
    public static byte[]? ParseMac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || text.Any(c => !Uri.IsHexDigit(c) && c is not (':' or '-' or '.' or ' '))) return null;
        return Convert.FromHexString(hex);
    }

    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Array.Copy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    public static async Task SendAsync(string mac, string? target = null)
    {
        var bytes = ParseMac(mac) ?? throw new FormatException($"\"{mac}\" isn't a MAC address.");
        var packet = MagicPacket(bytes);
        IPAddress address;
        if (string.IsNullOrWhiteSpace(target)) address = IPAddress.Broadcast;
        else if (!IPAddress.TryParse(target.Trim(), out address!))
            address = (await Dns.GetHostAddressesAsync(target.Trim())).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        // Sent a few times: a single UDP packet can get lost on a busy or sleeping link.
        for (var i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, new IPEndPoint(address, 9));
            await udp.SendAsync(packet, new IPEndPoint(address, 7));
        }
    }
}
