namespace RemoteMan.Core.Model;

/// <summary>Counts for the Dashboard tab, worked out from the entries in one pass. Pure, so it is cheap to redo on every change.</summary>
public sealed record DashboardStats(
    int Entries,
    int Folders,
    int Credentials,
    int AddedThisWeek,
    int MaxFolderDepth,
    int UsingSavedCredential,
    IReadOnlyList<DashboardStats.KindCount> ByKind,
    IReadOnlyList<DashboardStats.FolderCount> TopFolders,
    DashboardStats.Housekeeping Checks)
{
    public sealed record KindCount(EntryKind Kind, int Count);

    /// <summary>A top-level folder and the connectable entries anywhere under it.</summary>
    public sealed record FolderCount(Guid Id, string Name, int Count);

    /// <summary>Things worth tidying. Each list holds the entry ids, so the dashboard can show them.</summary>
    public sealed record Housekeeping(
        IReadOnlyList<Guid> NoLogin,
        IReadOnlyList<Guid> MissingCredential,
        IReadOnlyList<Guid> WithFallbacks,
        IReadOnlyList<Guid> DuplicateHosts,
        IReadOnlyList<Guid> UnusedCredentials);

    public static DashboardStats Compute(IReadOnlyCollection<Entry> all, DateTimeOffset now, int topFolders = 6)
    {
        var live = all.Where(e => !e.Deleted && e.Kind != EntryKind.Snippet).ToList();
        var byId = live.ToDictionary(e => e.Id);
        var conn = live.Where(e => e.IsConnectable).ToList();
        var folders = live.Where(e => e.Kind == EntryKind.Folder).ToList();
        var creds = live.Where(e => e.Kind == EntryKind.Credential).ToList();

        Entry? Parent(Entry e) => e.ParentId is { } p && byId.TryGetValue(p, out var f) && f.Kind == EntryKind.Folder ? f : null;

        // Depth and top-level folder of each entry (guarded against a parent loop in bad data).
        Entry? TopFolder(Entry e, out int depth)
        {
            depth = 0;
            Entry? top = null;
            var seen = new HashSet<Guid>();
            for (var p = Parent(e); p is not null && seen.Add(p.Id); p = Parent(p)) { top = p; depth++; }
            return top;
        }

        var maxDepth = 0;
        foreach (var f in folders) { TopFolder(f, out var d); maxDepth = Math.Max(maxDepth, d + 1); }

        var byKind = conn.GroupBy(e => e.Kind).Select(g => new KindCount(g.Key, g.Count()))
            .OrderByDescending(k => k.Count).ThenBy(k => k.Kind).ToList();

        var top = conn.Select(e => TopFolder(e, out _)).Where(f => f is not null).GroupBy(f => f!.Id)
            .Select(g => new FolderCount(g.Key, g.First()!.Name, g.Count()))
            .OrderByDescending(f => f.Count).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(topFolders).ToList();

        // The login an entry really uses: Inherited walks up to the nearest folder that sets one.
        Entry? LoginOwner(Entry e)
        {
            var seen = new HashSet<Guid>();
            var cur = e;
            while (cur.Login == LoginSource.Inherited && seen.Add(cur.Id))
            {
                if (Parent(cur) is not { } p) return null;
                cur = p;
            }
            return cur.Login == LoginSource.Inherited ? null : cur;
        }

        var noLogin = new List<Guid>();
        var missing = new List<Guid>();
        var usingSaved = 0;
        var usedCreds = new HashSet<Guid>();
        foreach (var e in conn)
        {
            var owner = LoginOwner(e);
            switch (owner?.Login ?? LoginSource.None)
            {
                case LoginSource.None when e.Kind is not EntryKind.Serial and not EntryKind.Web:
                    noLogin.Add(e.Id);
                    break;
                case LoginSource.Saved:
                    if (owner!.CredentialId is { } cid && byId.TryGetValue(cid, out var c) && c.Kind == EntryKind.Credential) { usingSaved++; usedCreds.Add(cid); }
                    else missing.Add(e.Id);
                    break;
            }
        }
        // Folders and jump hosts can point at credentials too; those count as used.
        foreach (var e in live) if (e.CredentialId is { } cid) usedCreds.Add(cid);

        var fallbacks = conn.Where(e => e.Endpoints.Count(p => !string.IsNullOrWhiteSpace(p.Host)) > 1).Select(e => e.Id).ToList();

        // Same primary host and port on two entries of the same kind (different kinds on one host are normal).
        var dupes = conn.Where(e => e.HasHosts && e.Endpoints.Count > 0 && !string.IsNullOrWhiteSpace(e.Endpoints[0].Host))
            .GroupBy(e => (e.Kind, Host: e.Endpoints[0].Host.Trim().ToLowerInvariant(), Port: e.Endpoints[0].Port > 0 ? e.Endpoints[0].Port : Entry.DefaultPort(e.Kind)))
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(e => e.Id)).ToList();

        var unused = creds.Where(c => !usedCreds.Contains(c.Id)).Select(c => c.Id).ToList();

        var weekAgo = now - TimeSpan.FromDays(7);
        return new DashboardStats(
            conn.Count, folders.Count, creds.Count,
            conn.Count(e => e.CreatedAt is { } t && t >= weekAgo),
            maxDepth, usingSaved, byKind, top,
            new Housekeeping(noLogin, missing, fallbacks, dupes, unused));
    }
}
