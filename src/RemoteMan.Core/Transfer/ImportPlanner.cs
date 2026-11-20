using RemoteMan.Core.Model;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;

namespace RemoteMan.Core.Transfer;

/// <summary>An entry read from an import or export file, with its secrets still in plain text.</summary>
public sealed class ImportItem
{
    public ImportItem(Entry entry) => Entry = entry;
    public Entry Entry { get; }
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string? Passphrase { get; set; }
    public string? Certificate { get; set; }
    /// <summary>Unticked in the preview: left out of the import.</summary>
    public bool Include { get; set; } = true;
    public int Depth { get; set; }
}

/// <summary>What an importer found, plus anything it had to skip.</summary>
public sealed class ImportResult
{
    public List<ImportItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Sets <see cref="ImportItem.Depth"/> and orders folders before their contents, as a tree.</summary>
    public void SortAsTree()
    {
        var ids = Items.Select(i => i.Entry.Id).ToHashSet();
        var byParent = Items.ToLookup(i => i.Entry.ParentId is { } p && ids.Contains(p) ? p : (Guid?)null);
        var sorted = new List<ImportItem>();
        void Walk(Guid? parent, int depth)
        {
            foreach (var i in byParent[parent].OrderBy(i => i.Entry.Kind == EntryKind.Folder ? 0 : i.Entry.Kind == EntryKind.Credential ? 2 : 1)
                         .ThenBy(i => i.Entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                i.Depth = depth;
                sorted.Add(i);
                if (i.Entry.Kind == EntryKind.Folder) Walk(i.Entry.Id, depth + 1);
            }
        }
        Walk(null, 0);
        Items.Clear();
        Items.AddRange(sorted);
    }
}

public enum ImportMode
{
    /// <summary>New ids for everything; nothing existing is changed. Same-named folders are reused.</summary>
    AddCopies,
    /// <summary>Restore: entries keep their ids; ones that still exist are overwritten, deleted ones come back.</summary>
    Merge,
}

/// <summary>Turns import items into entries ready to save, with secrets protected for the target data source.</summary>
public static class ImportPlanner
{
    public static ImportResult FromBundle(ExportBundle bundle)
    {
        var r = new ImportResult();
        foreach (var x in bundle.Entries)
        {
            var e = new Entry { Id = x.Id, ParentId = x.ParentId, Kind = x.Kind, Name = x.Name };
            EntryJson.ApplyPayload(e, x.Payload);
            r.Items.Add(new ImportItem(e) { Password = x.Password, PrivateKey = x.PrivateKey, Passphrase = x.Passphrase, Certificate = x.Certificate });
        }
        r.SortAsTree();
        return r;
    }

    /// <param name="existing">Entries already in the data source.</param>
    /// <param name="targetFolderId">Top-level items go here (AddCopies only); null = top level.</param>
    /// <param name="skipDuplicates">AddCopies: skip connections with the same kind, name and primary host already in the same folder.</param>
    /// <returns>Entries to save, parents before children.</returns>
    public static List<Entry> Plan(IEnumerable<ImportItem> items, IEnumerable<Entry> existing, ISecretProtector protector,
        ImportMode mode, Guid? targetFolderId = null, bool skipDuplicates = true)
    {
        var chosen = items.Where(i => i.Include).ToList();
        var live = existing.Where(e => !e.Deleted).ToList();
        var chosenIds = chosen.Select(i => i.Entry.Id).ToHashSet();
        var map = new Dictionary<Guid, Guid>();      // import id -> id in the data source
        var output = new List<Entry>();

        // Parents first, so folder reuse and duplicate checks see the final parent id.
        var ordered = TopoSort(chosen);

        foreach (var item in ordered)
        {
            var e = EntryStore.Clone(item.Entry);
            e.Deleted = false;
            e.Revision = 0;
            e.CreatedAt = null; e.CreatedBy = null; e.UpdatedBy = null;

            if (mode == ImportMode.Merge)
            {
                map[item.Entry.Id] = e.Id;
                if (e.ParentId is { } p && !chosenIds.Contains(p) && live.All(x => x.Id != p)) e.ParentId = targetFolderId;
            }
            else
            {
                // Parents were planned first (or matched an existing folder); an unticked parent means "put it in the target".
                e.ParentId = e.ParentId is { } p && map.TryGetValue(p, out var mp) ? mp : targetFolderId;

                var dup = skipDuplicates ? FindDuplicate(e, live, output) : null;
                if (dup is not null)
                {
                    map[item.Entry.Id] = dup.Id;
                    continue;
                }
                e.Id = Guid.NewGuid();
                map[item.Entry.Id] = e.Id;
            }

            if (e.Credential is { } c)
            {
                c.ProtectedPassword = Protect(protector, item.Password);
                c.ProtectedPrivateKey = Protect(protector, item.PrivateKey);
                c.ProtectedPassphrase = Protect(protector, item.Passphrase);
                c.ProtectedCertificate = Protect(protector, item.Certificate);
            }
            output.Add(e);
        }

        // Re-point links (credential, jump host) at the ids they ended up with.
        var known = live.Select(x => x.Id).Concat(output.Select(x => x.Id)).ToHashSet();
        foreach (var e in output)
        {
            e.CredentialId = Remap(e.CredentialId, map, known);
            if (e.Login == LoginSource.Saved && e.CredentialId is null) e.Login = LoginSource.None;
            if (e.Ssh is { } s) s.JumpHostId = Remap(s.JumpHostId, map, known);
        }
        return output;
    }

    private static Guid? Remap(Guid? id, Dictionary<Guid, Guid> map, HashSet<Guid> known) =>
        id is not { } g ? null : map.TryGetValue(g, out var m) ? m : known.Contains(g) ? g : null;

    private static string? Protect(ISecretProtector p, string? s) => string.IsNullOrEmpty(s) ? null : p.Protect(s);

    private static Entry? FindDuplicate(Entry e, List<Entry> live, List<Entry> pending)
    {
        static string Key(Entry x) =>
            $"{x.Kind}|{x.ParentId}|{x.Name.Trim().ToLowerInvariant()}|{x.Endpoints.FirstOrDefault()?.Host.ToLowerInvariant()}|{x.Web?.Url?.ToLowerInvariant()}|{x.Credential?.UserName?.ToLowerInvariant()}";
        var key = Key(e);
        return live.Concat(pending).FirstOrDefault(x => x.Kind == e.Kind && Key(x) == key);
    }

    private static List<ImportItem> TopoSort(List<ImportItem> items)
    {
        var byId = items.ToDictionary(i => i.Entry.Id);
        var done = new HashSet<Guid>();
        var result = new List<ImportItem>();
        void Visit(ImportItem i, int guard)
        {
            if (done.Contains(i.Entry.Id) || guard > 64) return;
            if (i.Entry.ParentId is { } p && byId.TryGetValue(p, out var parent)) Visit(parent, guard + 1);
            if (done.Add(i.Entry.Id)) result.Add(i);
        }
        foreach (var i in items) Visit(i, 0);
        return result;
    }
}
