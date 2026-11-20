using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Storage;

/// <summary>One field that differs between two versions of an entry.</summary>
/// <param name="Path">Dotted path in the entry's JSON, e.g. "Rdp.AdminSession" or "Endpoints".</param>
/// <param name="IsSecret">A password, key or certificate: show "changed", never the value.</param>
public sealed record FieldChange(string Path, string Label, string? Before, string? After, bool IsSecret);

/// <summary>A field both people changed to different values.</summary>
public sealed record MergeConflict(string Path, string Label, string? Mine, string? Theirs, bool IsSecret);

/// <summary>
/// The result of combining your edit with a save someone made while you were editing.
/// <see cref="Merged"/> holds every change that didn't collide; for each conflict it holds your value
/// until <see cref="Resolve"/> picks theirs.
/// </summary>
public sealed class MergeResult
{
    internal MergeResult(Entry merged, Dictionary<string, JsonNode?> flat, Entry theirs,
        List<MergeConflict> conflicts, List<FieldChange> keptTheirs, List<FieldChange> keptMine, Dictionary<string, JsonNode?> theirsFlat)
    {
        Merged = merged; _flat = flat; _theirs = theirs; Conflicts = conflicts; KeptTheirs = keptTheirs; KeptMine = keptMine; _theirsFlat = theirsFlat;
    }

    private readonly Dictionary<string, JsonNode?> _flat;
    private readonly Dictionary<string, JsonNode?> _theirsFlat;
    private readonly Entry _theirs;

    public Entry Merged { get; }
    public IReadOnlyList<MergeConflict> Conflicts { get; }
    /// <summary>Their changes to fields you didn't touch.</summary>
    public IReadOnlyList<FieldChange> KeptTheirs { get; }
    /// <summary>Your changes to fields they didn't touch.</summary>
    public IReadOnlyList<FieldChange> KeptMine { get; }

    /// <summary>The entry to save, with theirs taken for the conflict paths in <paramref name="useTheirs"/>.</summary>
    public Entry Resolve(IEnumerable<string> useTheirs)
    {
        var flat = new Dictionary<string, JsonNode?>(_flat);
        foreach (var path in useTheirs)
            flat[path] = _theirsFlat.TryGetValue(path, out var v) ? v?.DeepClone() : null;
        return EntryDiff.Build(flat, _theirs);
    }
}

/// <summary>
/// Field-level comparison of entries: the change history ("who changed what") and three-way merging
/// of concurrent edits. Works on the same JSON the database stores, so a new setting is covered
/// without touching this code.
/// </summary>
public static class EntryDiff
{
    // Kept whole: a host list or a reservation is one value, not a set of fields.
    private static readonly HashSet<string> Atomic = new(StringComparer.Ordinal) { "Endpoints", "Reservation" };

    /// <summary>The entry as path → value. Name, folder and kind are included alongside the payload.</summary>
    public static Dictionary<string, JsonNode?> Flatten(Entry e)
    {
        var flat = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["Name"] = JsonValue.Create(e.Name),
            ["ParentId"] = e.ParentId is { } p ? JsonValue.Create(p.ToString()) : null,
            ["Kind"] = JsonValue.Create(e.Kind.ToString()),
        };
        var payload = JsonNode.Parse(EntryJson.ToPayload(e)) as JsonObject ?? new JsonObject();
        Walk(payload, "", flat);
        return flat;
    }

    private static void Walk(JsonObject obj, string prefix, Dictionary<string, JsonNode?> into)
    {
        foreach (var (key, value) in obj)
        {
            var path = prefix.Length == 0 ? key : prefix + "." + key;
            if (value is JsonObject child && !Atomic.Contains(path)) Walk(child, path, into);
            else into[path] = value?.DeepClone();
        }
    }

    /// <summary>Turns a flattened entry back into an entry; sync metadata comes from <paramref name="meta"/>.</summary>
    internal static Entry Build(Dictionary<string, JsonNode?> flat, Entry meta)
    {
        var payload = new JsonObject();
        foreach (var (path, value) in flat)
        {
            if (path is "Name" or "ParentId" or "Kind" || value is null) continue;
            var parts = path.Split('.');
            var o = payload;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (o[parts[i]] is not JsonObject next) { next = new JsonObject(); o[parts[i]] = next; }
                o = next;
            }
            o[parts[^1]] = value.DeepClone();
        }
        var e = new Entry
        {
            Id = meta.Id,
            Name = Str(flat, "Name") ?? meta.Name,
            ParentId = Str(flat, "ParentId") is { } p && Guid.TryParse(p, out var g) ? g : null,
            Kind = Str(flat, "Kind") is { } k && Enum.TryParse<EntryKind>(k, out var kind) ? kind : meta.Kind,
            Revision = meta.Revision, UpdatedAt = meta.UpdatedAt, UpdatedBy = meta.UpdatedBy, Deleted = meta.Deleted,
            CreatedAt = meta.CreatedAt, CreatedBy = meta.CreatedBy,
        };
        EntryJson.ApplyPayload(e, payload.ToJsonString());
        return e;
    }

    private static string? Str(Dictionary<string, JsonNode?> flat, string key) =>
        flat.TryGetValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    private static string? Raw(Dictionary<string, JsonNode?> flat, string key) =>
        flat.TryGetValue(key, out var v) && v is not null ? v.ToJsonString() : null;

    /// <summary>What changed from <paramref name="before"/> (null: the entry was new) to <paramref name="after"/>.</summary>
    /// <param name="nameOf">Turns an entry id (folder, linked credential, jump host) into a name for display.</param>
    public static List<FieldChange> Diff(Entry? before, Entry after, Func<Guid, string?>? nameOf = null)
    {
        var a = before is null ? new Dictionary<string, JsonNode?>() : Flatten(before);
        var b = Flatten(after);
        var list = new List<FieldChange>();
        foreach (var path in a.Keys.Union(b.Keys).OrderBy(Order).ThenBy(p => p, StringComparer.Ordinal))
        {
            var ra = Raw(a, path);
            var rb = Raw(b, path);
            if (ra == rb || (before is null && rb is null)) continue;
            list.Add(new FieldChange(path, Label(path), Display(path, a, nameOf), Display(path, b, nameOf), IsSecret(path)));
        }
        return list;
    }

    /// <summary>
    /// Three-way merge: <paramref name="base"/> is what you started editing, <paramref name="mine"/> your
    /// edit, <paramref name="theirs"/> what is in the database now. A field only one side changed takes
    /// that side's value; a field both changed the same way is fine; a field both changed differently is
    /// a conflict and keeps yours until resolved.
    /// </summary>
    public static MergeResult Merge(Entry @base, Entry mine, Entry theirs, Func<Guid, string?>? nameOf = null)
    {
        var fb = Flatten(@base);
        var fm = Flatten(mine);
        var ft = Flatten(theirs);
        var merged = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var conflicts = new List<MergeConflict>();
        var keptTheirs = new List<FieldChange>();
        var keptMine = new List<FieldChange>();
        foreach (var path in fb.Keys.Union(fm.Keys).Union(ft.Keys).OrderBy(Order).ThenBy(p => p, StringComparer.Ordinal))
        {
            var rb = Raw(fb, path); var rm = Raw(fm, path); var rt = Raw(ft, path);
            if (rm == rt) merged[path] = Clone(fm, path);
            else if (rm == rb)
            {
                merged[path] = Clone(ft, path);
                keptTheirs.Add(new FieldChange(path, Label(path), Display(path, fb, nameOf), Display(path, ft, nameOf), IsSecret(path)));
            }
            else if (rt == rb)
            {
                merged[path] = Clone(fm, path);
                keptMine.Add(new FieldChange(path, Label(path), Display(path, fb, nameOf), Display(path, fm, nameOf), IsSecret(path)));
            }
            else
            {
                merged[path] = Clone(fm, path);
                conflicts.Add(new MergeConflict(path, Label(path), Display(path, fm, nameOf), Display(path, ft, nameOf), IsSecret(path)));
            }
        }
        // Saved on top of their version: their revision is the one the database expects.
        var result = Build(merged, theirs);
        return new MergeResult(result, merged, theirs, conflicts, keptTheirs, keptMine, ft);
    }

    private static JsonNode? Clone(Dictionary<string, JsonNode?> flat, string path) =>
        flat.TryGetValue(path, out var v) ? v?.DeepClone() : null;

    private static int Order(string path) => path switch
    {
        "Name" => 0, "ParentId" => 1, "Endpoints" => 2, "Web.Url" => 3, "Login" => 4, "CredentialId" => 5,
        _ when path.StartsWith("Credential.", StringComparison.Ordinal) => 6,
        "Description" => 7, "Tags" => 8, "Reservation" => 90,
        _ => 50,
    };

    public static bool IsSecret(string path) =>
        path.Split('.')[^1].StartsWith("Protected", StringComparison.Ordinal);

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Name"] = "Name", ["ParentId"] = "Folder", ["Kind"] = "Type", ["Endpoints"] = "Hosts",
        ["Description"] = "Description", ["Tags"] = "Tags", ["Login"] = "Login", ["CredentialId"] = "Linked credential",
        ["Favorite"] = "Favorite", ["ZabbixHostId"] = "Zabbix host", ["Icon"] = "Folder icon", ["IconColor"] = "Folder icon colour",
        ["Reservation"] = "Reservation", ["Web.Url"] = "URL",
        ["Credential.Type"] = "Credential type", ["Credential.UserName"] = "User name", ["Credential.Domain"] = "Domain",
        ["Credential.ProtectedPassword"] = "Password", ["Credential.ProtectedPrivateKey"] = "Private key",
        ["Credential.ProtectedPassphrase"] = "Key passphrase", ["Credential.ProtectedCertificate"] = "Certificate",
        ["Credential.PrivateKeyPath"] = "Key file", ["Credential.AskPassword"] = "Ask for password", ["Credential.Notes"] = "Notes",
        ["Ssh.JumpHostId"] = "SSH: jump host", ["Snippet.Command"] = "Command",
    };

    private static readonly Dictionary<string, string> Sections = new(StringComparer.Ordinal)
    {
        ["Rdp"] = "RDP", ["Ssh"] = "SSH", ["Web"] = "Web", ["Vnc"] = "VNC", ["Serial"] = "Serial", ["Ftp"] = "FTP",
        ["Proxmox"] = "Proxmox", ["PowerShell"] = "PowerShell", ["Snippet"] = "Snippet", ["Credential"] = "Login",
    };

    /// <summary>"Rdp.AdminSession" → "RDP: admin session".</summary>
    public static string Label(string path)
    {
        if (Labels.TryGetValue(path, out var l)) return l;
        var parts = path.Split('.');
        var words = Humanize(parts[^1]);
        if (parts.Length == 1) return char.ToUpperInvariant(words[0]) + words[1..];
        return (Sections.TryGetValue(parts[0], out var s) ? s : parts[0]) + ": " + words;
    }

    private static string Humanize(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>A value as a person reads it: hosts as "a:22, b", ids as names, yes/no for flags.</summary>
    private static string? Display(string path, Dictionary<string, JsonNode?> flat, Func<Guid, string?>? nameOf)
    {
        if (!flat.TryGetValue(path, out var v) || v is null) return null;
        if (IsSecret(path)) return "(hidden)";
        try
        {
            if (path == "Endpoints")
            {
                var eps = v.Deserialize<List<HostEndpoint>>(EntryJson.Options) ?? [];
                return eps.Count == 0 ? null : string.Join(", ", eps.Select(x => x.ToString()));
            }
            if (path == "Reservation")
            {
                var r = v.Deserialize<Reservation>(EntryJson.Options);
                return r is null ? null : $"{r.By} until {r.Until.ToLocalTime():g}" + (string.IsNullOrWhiteSpace(r.Note) ? "" : $" (\"{r.Note}\")");
            }
        }
        catch (JsonException) { }
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var b)) return b ? "on" : "off";
            if (jv.TryGetValue<string>(out var s))
            {
                if ((path.EndsWith("Id", StringComparison.Ordinal) || path == "ParentId") && Guid.TryParse(s, out var g))
                    return nameOf?.Invoke(g) ?? "(deleted entry)";
                return s;
            }
            return jv.ToJsonString();
        }
        return v.ToJsonString();
    }
}
