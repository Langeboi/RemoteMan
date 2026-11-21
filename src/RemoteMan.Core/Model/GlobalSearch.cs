namespace RemoteMan.Core.Model;

/// <summary>Where a search hit was found. The order is the ranking: a name match beats a description match.</summary>
public enum SearchField { Name = 0, Host = 1, Tag = 2, Field = 3, Description = 4, Folder = 5 }

/// <summary>
/// One result of <see cref="GlobalSearch.Find"/>: the entry, where the text was found (<see cref="Label"/>, e.g. "Field: Serial"),
/// the text around it (<see cref="Context"/>) and the matched part's position in that text, for highlighting.
/// </summary>
public sealed record SearchHit(Entry Entry, SearchField Field, string Label, string Context, int MatchStart, int MatchLength, string Path);

/// <summary>
/// The Ctrl K search: names, hosts and fallback hosts, URLs, tags, custom field values, descriptions and folder names.
/// Passwords and secret custom fields are never searched. Prefixes narrow it: tag:, host:, field:, desc:, in: (folder), kind:.
/// </summary>
public static class GlobalSearch
{
    public static readonly IReadOnlyList<string> Prefixes = ["tag:", "host:", "field:", "desc:", "in:", "kind:"];

    public static List<SearchHit> Find(IReadOnlyCollection<Entry> all, string query, int max = 200)
    {
        var q = (query ?? "").Trim();
        string? prefix = null;
        foreach (var p in Prefixes)
            if (q.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { prefix = p[..^1].ToLowerInvariant(); q = q[p.Length..].Trim(); break; }
        if (q.Length == 0) return new();

        var live = all.Where(e => !e.Deleted && !e.IsHidden).ToList();
        var byId = live.ToDictionary(e => e.Id);
        string PathOf(Entry e)
        {
            var parts = new List<string>();
            var seen = new HashSet<Guid>();
            for (var p = e.ParentId; p is { } id && byId.TryGetValue(id, out var f) && seen.Add(id); p = f.ParentId) parts.Insert(0, f.Name);
            return string.Join("\\", parts);
        }

        var hits = new List<SearchHit>();
        foreach (var e in live)
        {
            if (Match(e, q, prefix, PathOf) is { } hit) hits.Add(hit);
        }
        return hits
            .OrderBy(h => h.Field)
            .ThenBy(h => h.Field == SearchField.Name && h.MatchStart == 0 ? 0 : 1)
            .ThenBy(h => h.Entry.Kind == EntryKind.Folder ? 1 : 0)
            .ThenBy(h => h.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static SearchHit? Match(Entry e, string q, string? prefix, Func<Entry, string> pathOf)
    {
        SearchHit? Hit(SearchField f, string label, string text)
        {
            var i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var (context, start) = Around(text, i, q.Length);
            return new SearchHit(e, f, label, context, start, q.Length, pathOf(e));
        }
        bool Want(string p) => prefix is null || prefix == p;

        if (prefix == "kind")
        {
            var kind = e.Kind == EntryKind.Web ? "web website" : e.Kind.ToString().ToLowerInvariant();
            return kind.Contains(q, StringComparison.OrdinalIgnoreCase) || (q.Equals("credential", StringComparison.OrdinalIgnoreCase) && e.Kind == EntryKind.Credential)
                ? new SearchHit(e, SearchField.Name, "Kind", e.Kind.ToString(), 0, 0, pathOf(e)) : null;
        }
        if (prefix is null && Hit(SearchField.Name, "Name", e.Name) is { } name) return name;
        if (e.Kind == EntryKind.Folder && prefix is not ("in" or null)) return null;

        if (Want("host"))
        {
            for (var i = 0; i < e.Endpoints.Count; i++)
                if (Hit(SearchField.Host, i == 0 && e.Kind != EntryKind.Web ? "Host" : "Fallback", e.Endpoints[i].ToString()) is { } h) return h;
            if (e.Kind == EntryKind.Web && e.Web?.Url is { Length: > 0 } url && Hit(SearchField.Host, "URL", url) is { } u) return u;
        }
        if (Want("tag") && e.Tags is { Length: > 0 } tags && Hit(SearchField.Tag, "Tag", tags) is { } t) return t;
        if (Want("field") && e.Fields is { Count: > 0 } fields)
        {
            foreach (var f in fields)
            {
                if (!f.Secret && f.Value is { Length: > 0 } v && Hit(SearchField.Field, "Field: " + f.Name, $"{f.Name}: {v}") is { } fv) return fv;
                // field:serial finds entries that have a Serial field, whatever its value.
                if (prefix == "field" && Hit(SearchField.Field, "Field: " + f.Name, f.Secret ? f.Name : $"{f.Name}: {f.Value}") is { } fn) return fn;
            }
        }
        if (Want("desc") && e.Description is { Length: > 0 } d && Hit(SearchField.Description, "Description", OneLine(d)) is { } desc) return desc;
        if (Want("in") && pathOf(e) is { Length: > 0 } path && Hit(SearchField.Folder, "Folder", path) is { } folder) return folder;
        if (prefix == "in" && e.Kind == EntryKind.Folder && Hit(SearchField.Folder, "Folder", e.Name) is { } self) return self;
        return null;
    }

    private static string OneLine(string s) => string.Join(' ', s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Up to 40 characters before the match, so a long description still shows the matched words.</summary>
    private static (string Text, int Start) Around(string text, int index, int length)
    {
        const int before = 40;
        if (index <= before) return (text, index);
        var from = text.LastIndexOf(' ', index - 1, before) is var sp and > 0 ? sp + 1 : index - before;
        return ("…" + text[from..], index - from + 1);
    }
}
