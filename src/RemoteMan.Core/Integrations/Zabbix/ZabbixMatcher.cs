using System.Net;
using System.Text.RegularExpressions;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Integrations.Zabbix;

public sealed class ZabbixMatchOptions
{
    public bool ByAddress { get; set; } = true;
    public bool ByName { get; set; } = true;
    /// <summary>Compare "dc01 (Domain controller)" as "dc01".</summary>
    public bool IgnoreBracketSuffix { get; set; } = true;
}

public sealed record ZabbixMatch(ZabbixHost Host, string Reason);

/// <summary>
/// Finds the Zabbix host for an entry, in this order: the saved host id, then the entry's
/// host or IP against Zabbix interface addresses, then the entry's name against the Zabbix
/// host name and visible name. A key shared by several Zabbix hosts never matches, so a
/// NAT address in front of many hosts doesn't link them all to one.
/// </summary>
public sealed partial class ZabbixMatcher
{
    private readonly Dictionary<string, ZabbixHost> _byId;
    private readonly Dictionary<string, List<(ZabbixHost Host, ZabbixInterface If)>> _byAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ZabbixHost>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ZabbixMatchOptions _options;

    public ZabbixMatcher(IEnumerable<ZabbixHost> hosts, ZabbixMatchOptions? options = null)
    {
        _options = options ?? new ZabbixMatchOptions();
        var list = hosts.ToList();
        _byId = list.ToDictionary(h => h.HostId);
        foreach (var h in list.Where(h => h.Enabled))
        {
            foreach (var i in h.Interfaces)
            {
                foreach (var a in new[] { i.Ip, i.Dns }.Where(a => !string.IsNullOrWhiteSpace(a) && a != "0.0.0.0" && a != "127.0.0.1"))
                {
                    var key = NormalizeAddress(a);
                    if (!_byAddress.TryGetValue(key, out var l)) _byAddress[key] = l = new();
                    if (!l.Any(x => x.Host == h)) l.Add((h, i));
                }
            }
            foreach (var n in new[] { h.Host, h.Name }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(NormalizeName).Distinct())
            {
                if (!_byName.TryGetValue(n, out var l)) _byName[n] = l = new();
                if (!l.Contains(h)) l.Add(h);
            }
        }
    }

    public ZabbixHost? GetHost(string hostId) => _byId.GetValueOrDefault(hostId);

    public ZabbixMatch? Match(Entry entry)
    {
        if (!entry.IsConnectable) return null;

        if (!string.IsNullOrEmpty(entry.ZabbixHostId))
            return _byId.TryGetValue(entry.ZabbixHostId, out var linked) ? new ZabbixMatch(linked, $"Saved link: {linked.DisplayName} (hostid {linked.HostId})") : null;

        var addresses = Addresses(entry).ToList();
        if (_options.ByAddress)
        {
            foreach (var a in addresses)
            {
                if (_byAddress.TryGetValue(NormalizeAddress(a), out var hits) && hits.Count == 1)
                    return new ZabbixMatch(hits[0].Host, $"Address {a} ({hits[0].If.TypeName} interface)");
            }
        }

        if (_options.ByName)
        {
            foreach (var n in NameCandidates(entry, addresses))
            {
                if (_byName.TryGetValue(NormalizeName(n), out var hits) && hits.Count == 1)
                    return new ZabbixMatch(hits[0], $"Name \"{n}\"");
            }
        }
        return null;
    }

    /// <summary>Hosts a person is likely to pick in the link picker, best first.</summary>
    public IEnumerable<(ZabbixHost Host, string Why)> Suggestions(Entry entry)
    {
        var seen = new HashSet<string>();
        var addresses = Addresses(entry).ToList();
        foreach (var a in addresses)
            if (_byAddress.TryGetValue(NormalizeAddress(a), out var hits))
                foreach (var (h, _) in hits)
                    if (seen.Add(h.HostId)) yield return (h, $"same address {a}");
        foreach (var n in NameCandidates(entry, addresses))
            if (_byName.TryGetValue(NormalizeName(n), out var hits))
                foreach (var h in hits)
                    if (seen.Add(h.HostId)) yield return (h, $"same name \"{n}\"");
    }

    private static IEnumerable<string> Addresses(Entry entry)
    {
        foreach (var ep in entry.Endpoints)
            if (!string.IsNullOrWhiteSpace(ep.Host)) yield return ep.Host.Trim();
        if (entry.Web?.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
            yield return u.Host.Trim('[', ']');
    }

    private IEnumerable<string> NameCandidates(Entry entry, IReadOnlyList<string> addresses)
    {
        var name = entry.Name.Trim();
        if (name.Length > 0) yield return name;
        if (_options.IgnoreBracketSuffix)
        {
            var stripped = BracketSuffix().Replace(name, "").Trim();
            if (stripped.Length > 0 && stripped != name) yield return stripped;
        }
        // "dc01.lab.local" as an address also names the host "dc01".
        foreach (var a in addresses)
        {
            if (IPAddress.TryParse(a, out _)) continue;
            yield return a;
            var dot = a.IndexOf('.');
            if (dot > 0) yield return a[..dot];
        }
    }

    private static string NormalizeAddress(string a) => a.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
    private static string NormalizeName(string n) => n.Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$")]
    private static partial Regex BracketSuffix();
}
