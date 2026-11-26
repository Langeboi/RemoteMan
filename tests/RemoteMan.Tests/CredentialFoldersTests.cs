using RemoteMan.Core.Model;
using Xunit;

namespace RemoteMan.Tests;

/// <summary>Credentials folders sort last among folders, and new credentials are filed into one.</summary>
public class CredentialFoldersTests
{
    private static Entry Folder(string name, Guid? parent = null) => new() { Kind = EntryKind.Folder, Name = name, ParentId = parent };
    private static Entry Cred(string name, Guid? parent = null) => new() { Kind = EntryKind.Credential, Name = name, ParentId = parent, Credential = new() };
    private static Entry Ssh(string name, Guid? parent = null) => new() { Kind = EntryKind.Ssh, Name = name, ParentId = parent, Ssh = new SshSettings() };

    private static List<string> Sorted(List<Entry> all, Guid? parent)
    {
        var byParent = all.ToLookup(e => e.ParentId);
        return byParent[parent].OrderBy(e => CredentialFolders.TreeRank(e, id => byParent[id]))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.Name).ToList();
    }

    [Fact]
    public void CredentialsFolderSortsBelowEverythingElseInItsFolder()
    {
        var intern = Folder("Intern");
        var all = new List<Entry>
        {
            intern,
            Folder("Credentials", intern.Id), Folder("Network", intern.Id), Folder("Zabbix", intern.Id),
            Ssh("box", intern.Id), Cred("admin", intern.Id),
        };
        Assert.Equal(new[] { "Network", "Zabbix", "box", "admin", "Credentials" }, Sorted(all, intern.Id));
    }

    [Fact]
    public void AFolderHoldingOnlyCredentialsCountsAsACredentialsFolder()
    {
        var keys = Folder("Keys");
        var empty = Folder("Empty");
        var all = new List<Entry> { keys, empty, Folder("Zeta"), Cred("root", keys.Id) };
        Assert.Equal(new[] { "Empty", "Zeta", "Keys" }, Sorted(all, null));
    }

    [Fact]
    public void NewCredentialGoesIntoTheExistingCredentialsFolder()
    {
        var lab = Folder("Lab");
        var creds = Folder("credentials", lab.Id);
        var parent = CredentialFolders.PlaceNewCredential(lab.Id, [lab, creds, Folder("Credentials")], out var made);
        Assert.Equal(creds.Id, parent);
        Assert.Null(made);
    }

    [Fact]
    public void NewCredentialMakesACredentialsFolderWhenThereIsNone()
    {
        var lab = Folder("Lab");
        var parent = CredentialFolders.PlaceNewCredential(lab.Id, [lab, Ssh("box", lab.Id)], out var made);
        Assert.NotNull(made);
        Assert.Equal(made!.Id, parent);
        Assert.Equal("Credentials", made.Name);
        Assert.Equal(lab.Id, made.ParentId);

        var top = CredentialFolders.PlaceNewCredential(null, [], out var topMade);
        Assert.Equal(topMade!.Id, top);
        Assert.Null(topMade.ParentId);
    }

    [Fact]
    public void NewCredentialMadeInsideACredentialsFolderStaysThere()
    {
        var keys = Folder("Keys");
        var parent = CredentialFolders.PlaceNewCredential(keys.Id, [keys, Cred("root", keys.Id)], out var made);
        Assert.Equal(keys.Id, parent);
        Assert.Null(made);
    }
}
