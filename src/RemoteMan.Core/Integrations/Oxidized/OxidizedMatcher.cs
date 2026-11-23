using System.Net;
using System.Text.RegularExpressions;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Integrations.Oxidized;

public sealed class OxidizedMatchOptions
{
    public bool ByAddress { get; set; } = true;
    public bool ByName { get; set; } = true;
    /// <summary>Compare "core-sw01 (Core)" as "core-sw01".</summary>
    public bool IgnoreBracketSuffix { get; set; } = true;
}

/// <summary>
/// Finds the Oxidized node for an entry: the saved link first (<see cref="Entry.OxidizedNode"/>), then
/// the entry's host or IP against the node IP, then the entry's name (and host name) against the node
/// name. A key shared by several nodes never matches.
/// </summary>
public sealed partial class OxidizedMatcher
{
    private readonly Dictionary<string, OxidizedNode> _byFull = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<OxidizedNode>> _byAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<OxidizedNode>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly OxidizedMatchOptions _options;

    public OxidizedMatcher(IEnumerable<OxidizedNode> nodes, OxidizedMatchOptions? options = null)
    {
        _options = options ?? new OxidizedMatchOptions();
        foreach (var n in nodes)
        {
            _byFull[n.FullName] = n;
            if (!string.IsNullOrWhiteSpace(n.Ip)) Add(_byAddress, NormalizeAddress(n.Ip), n);
            Add(_byName, NormalizeName(n.Name), n);
            // A node named by its FQDN also answers to the short name.
            var dot = n.Name.IndexOf('.');
            if (dot > 0 && !IPAddress.TryParse(n.Name, out _)) Add(_byName, NormalizeName(n.Name[..dot]), n);
        }
    }

    private static void Add(Dictionary<string, List<OxidizedNode>> d, string key, OxidizedNode n)
    {
        if (key.Length == 0) return;
        if (!d.TryGetValue(key, out var l)) d[key] = l = new();
        if (!l.Contains(n)) l.Add(n);
    }

    public OxidizedNode? Get(string fullName) => _byFull.GetValueOrDefault(fullName);

    public OxidizedMatch? Match(Entry entry)
    {
        if (!entry.IsConnectable) return null;
        if (!string.IsNullOrEmpty(entry.OxidizedNode))
            return _byFull.TryGetValue(entry.OxidizedNode, out var linked) ? new OxidizedMatch(linked, $"Saved link: {linked.FullName}") : null;

        var addresses = Addresses(entry).ToList();
        if (_options.ByAddress)
            foreach (var a in addresses)
                if (_byAddress.TryGetValue(NormalizeAddress(a), out var hits) && hits.Count == 1)
                    return new OxidizedMatch(hits[0], $"Address {a}");
        if (_options.ByName)
            foreach (var n in NameCandidates(entry, addresses))
                if (_byName.TryGetValue(NormalizeName(n), out var hits) && hits.Count == 1)
                    return new OxidizedMatch(hits[0], $"Name \"{n}\"");
        return null;
    }

    /// <summary>Nodes a person is likely to pick in the link picker, best first.</summary>
    public IEnumerable<(OxidizedNode Node, string Why)> Suggestions(Entry entry)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = Addresses(entry).ToList();
        foreach (var a in addresses)
            if (_byAddress.TryGetValue(NormalizeAddress(a), out var hits))
                foreach (var n in hits)
                    if (seen.Add(n.FullName)) yield return (n, $"same address {a}");
        foreach (var name in NameCandidates(entry, addresses))
            if (_byName.TryGetValue(NormalizeName(name), out var hits))
                foreach (var n in hits)
                    if (seen.Add(n.FullName)) yield return (n, $"same name \"{name}\"");
    }

    private static IEnumerable<string> Addresses(Entry entry)
    {
        foreach (var ep in entry.Endpoints)
            if (!string.IsNullOrWhiteSpace(ep.Host)) yield return ep.Host.Trim();
        if (entry.Web?.Url is { } url && Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
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
        foreach (var a in addresses)
        {
            if (IPAddress.TryParse(a, out _)) continue;
            yield return a;
            var dot = a.IndexOf('.');
            if (dot > 0) yield return a[..dot];
        }
    }

    private static string NormalizeAddress(string a) => a.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
    private static string NormalizeName(string n) => n.Trim().TrimEnd('.').ToLowerInvariant();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$")]
    private static partial Regex BracketSuffix();
}

/// <summary>The node list at one moment, with the matcher built from it.</summary>
public sealed class OxidizedSnapshot(IReadOnlyList<OxidizedNode> nodes, OxidizedMatchOptions options, DateTimeOffset fetchedAt)
{
    public IReadOnlyList<OxidizedNode> Nodes { get; } = nodes;
    public OxidizedMatcher Matcher { get; } = new(nodes, options);
    public DateTimeOffset FetchedAt { get; } = fetchedAt;

    public int FailingCount => Nodes.Count(n => n.IsFailing);

    /// <summary>How many devices are in each state (ok, stale, fail, never) at <paramref name="now"/>.</summary>
    public IReadOnlyDictionary<string, int> CountByState(DateTimeOffset now)
    {
        var counts = new Dictionary<string, int> { ["ok"] = 0, ["stale"] = 0, ["fail"] = 0, ["never"] = 0 };
        foreach (var n in Nodes) counts[n.StateAt(now)]++;
        return counts;
    }

    /// <summary>"2 h ago", "yesterday", "3 days ago".</summary>
    public static string Age(DateTimeOffset when, DateTimeOffset now)
    {
        var d = now - when;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} h ago";
        if (d < TimeSpan.FromHours(48)) return "yesterday";
        return $"{(int)d.TotalDays} days ago";
    }
}
