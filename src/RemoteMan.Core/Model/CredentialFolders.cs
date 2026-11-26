namespace RemoteMan.Core.Model;

/// <summary>
/// "Credentials" folders: the folder in each folder that holds its credential (KEY) entries.
/// They sort after the other folders in the tree, and new credentials are filed into one.
/// </summary>
public static class CredentialFolders
{
    public const string FolderName = "Credentials";

    /// <summary>True for a folder named "Credentials", or a non-empty folder that holds only credentials.</summary>
    public static bool IsCredentialsFolder(Entry folder, IEnumerable<Entry> children)
    {
        if (folder.Kind != EntryKind.Folder) return false;
        if (string.Equals(folder.Name.Trim(), FolderName, StringComparison.OrdinalIgnoreCase)) return true;
        var any = false;
        foreach (var c in children)
        {
            if (c.Kind != EntryKind.Credential) return false;
            any = true;
        }
        return any;
    }

    /// <summary>Tree order inside one folder: folders, then connections, then loose credentials, and the credentials folder at the very bottom.</summary>
    public static int TreeRank(Entry e, Func<Guid, IEnumerable<Entry>> childrenOf) => e.Kind switch
    {
        EntryKind.Folder => IsCredentialsFolder(e, childrenOf(e.Id)) ? 3 : 0,
        EntryKind.Credential => 2,
        _ => 1,
    };

    /// <summary>
    /// Where a new credential created in <paramref name="parent"/> goes: <paramref name="parent"/> itself when it is
    /// already a credentials folder, else its "Credentials" subfolder. When there is none, <paramref name="newFolder"/>
    /// is a folder to save first (its id is the returned parent).
    /// </summary>
    public static Guid PlaceNewCredential(Guid? parent, IEnumerable<Entry> entries, out Entry? newFolder)
    {
        newFolder = null;
        var live = entries.Where(e => !e.Deleted).ToList();
        if (parent is { } p && live.FirstOrDefault(e => e.Id == p) is { Kind: EntryKind.Folder } here
            && IsCredentialsFolder(here, live.Where(e => e.ParentId == p)))
            return p;
        var existing = live
            .Where(e => e.Kind == EntryKind.Folder && e.ParentId == parent
                        && string.Equals(e.Name.Trim(), FolderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (existing is not null) return existing.Id;
        newFolder = new Entry { Kind = EntryKind.Folder, Name = FolderName, ParentId = parent };
        return newFolder.Id;
    }
}
