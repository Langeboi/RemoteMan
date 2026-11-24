using System.Net;
using System.Net.Sockets;

namespace RemoteMan.Gateway;

/// <summary>
/// Which addresses and ports a connector may open. Rules are separated by commas or spaces:
/// <c>10.0.0.0/8</c>, <c>192.168.1.20</c>, <c>10.0.5.0/24:22,3389</c> (only those ports), <c>*</c> (anything).
/// The default is the private ranges (RFC 1918, CG-NAT 100.64/10, IPv6 ULA and link-local, loopback is excluded)
/// so a leaked device token can't use the connector to reach the internet.
/// </summary>
public sealed class AllowList
{
    public const string PrivateRanges = "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,169.254.0.0/16,fc00::/7,fe80::/10";

    private readonly List<Rule> _rules = [];
    private readonly bool _any;

    public AllowList(string? spec)
    {
        spec = string.IsNullOrWhiteSpace(spec) ? PrivateRanges : spec;
        foreach (var raw in spec.Split([',', ' ', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") { _any = true; continue; }
            if (raw.Equals("private", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in PrivateRanges.Split(',')) _rules.Add(Rule.Parse(p, null));
                continue;
            }
            // A port list attaches to the rule before it: "10.0.5.0/24:22" then ",3389" would be ambiguous, so ports use "|".
            var (net, ports) = SplitPorts(raw);
            _rules.Add(Rule.Parse(net, ports));
        }
    }

    public override string ToString() => _any ? "*" : string.Join(", ", _rules);

    /// <summary>The spec in canonical form ("10.0.0.0/8, 192.168.1.0/24:22|3389"). Throws <see cref="FormatException"/> on a bad rule.</summary>
    public static string Normalize(string spec) => new AllowList(spec).ToString();

    /// <summary>The rules one by one, for showing.</summary>
    public IReadOnlyList<string> Rules => _any ? ["*"] : _rules.Select(r => r.ToString()).ToList();

    public bool Allows(IPAddress address, int port)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (_any) return true;
        return _rules.Any(r => r.Matches(address, port));
    }

    /// <summary>"10.0.5.0/24:22|3389" → ("10.0.5.0/24", [22, 3389]). IPv6 with ports: "[fd00::/8]:22".</summary>
    private static (string Net, int[]? Ports) SplitPorts(string raw)
    {
        if (raw.StartsWith('['))
        {
            var end = raw.IndexOf(']');
            if (end < 0) throw new FormatException($"Bad rule {raw}");
            var rest = raw[(end + 1)..];
            return (raw[1..end], rest.StartsWith(':') ? ParsePorts(rest[1..], raw) : null);
        }
        var colon = raw.IndexOf(':');
        if (colon > 0 && raw.IndexOf(':', colon + 1) < 0) return (raw[..colon], ParsePorts(raw[(colon + 1)..], raw));
        return (raw, null);
    }

    private static int[] ParsePorts(string text, string rule) =>
        text.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var n) && n is > 0 and <= 65535 ? n : throw new FormatException($"Bad port in {rule}")).ToArray();

    private sealed record Rule(byte[] Network, int PrefixBits, AddressFamily Family, int[]? Ports, string Text)
    {
        public static Rule Parse(string text, int[]? ports)
        {
            var slash = text.IndexOf('/');
            var addrText = slash < 0 ? text : text[..slash];
            if (!IPAddress.TryParse(addrText, out var ip)) throw new FormatException($"\"{text}\" isn't an address or range (hostnames aren't allowed in ALLOW).");
            var bytes = ip.GetAddressBytes();
            var bits = slash < 0 ? bytes.Length * 8 : int.TryParse(text[(slash + 1)..], out var pb) ? pb : -1;
            if (bits < 0 || bits > bytes.Length * 8) throw new FormatException($"Bad prefix in {text}");
            return new Rule(bytes, bits, ip.AddressFamily, ports, ports is null ? text : $"{text}:{string.Join('|', ports)}");
        }

        public bool Matches(IPAddress ip, int port)
        {
            if (ip.AddressFamily != Family) return false;
            if (Ports is not null && !Ports.Contains(port)) return false;
            var b = ip.GetAddressBytes();
            var full = PrefixBits / 8; var rem = PrefixBits % 8;
            for (var i = 0; i < full; i++) if (b[i] != Network[i]) return false;
            if (rem == 0) return true;
            var mask = (byte)(0xFF << (8 - rem));
            return (b[full] & mask) == (Network[full] & mask);
        }

        public override string ToString() => Text;
    }
}
