using System.Text;
using RemoteMan.Core.Import;
using RemoteMan.Core.Model;
using RemoteMan.Core.Security;
using RemoteMan.Core.Transfer;
using Xunit;

namespace RemoteMan.Tests;

public class TransferTests
{
    private static readonly AesGcmSecretProtector Vault = new("team-vault-pass");

    private static List<Entry> SampleTree()
    {
        var folder = new Entry { Kind = EntryKind.Folder, Name = "Homelab" };
        var cred = new Entry
        {
            Kind = EntryKind.Credential, Name = "root", ParentId = folder.Id,
            Credential = new CredentialData { UserName = "root", ProtectedPassword = Vault.Protect("s3cret!"), ProtectedPrivateKey = Vault.Protect("-----BEGIN KEY-----") },
        };
        var bastion = new Entry { Kind = EntryKind.Ssh, Name = "bastion", ParentId = folder.Id, Endpoints = { new("10.0.0.5", 22) }, Ssh = new(), CredentialId = cred.Id };
        var pve = new Entry
        {
            Kind = EntryKind.Ssh, Name = "pve-01", ParentId = folder.Id, Endpoints = { new("10.0.10.11", 22), new("pve-01.lab", 22) },
            Ssh = new SshSettings { JumpHostId = bastion.Id }, CredentialId = cred.Id,
        };
        var snippet = BuiltInSnippets.ToEntry(BuiltInSnippets.All[0]);
        return [folder, cred, bastion, pve, snippet];
    }

    [Fact]
    public void ExportRoundTripsWithSecretsAndOpensOnlyWithThePassword()
    {
        var bundle = ExportFile.CreateBundle(SampleTree(), Vault, new ExportOptions(), "alice", "pc1");
        using var ms = new MemoryStream();
        ExportFile.Write(ms, bundle, "correct horse", iterations: 1000);

        var text = Encoding.UTF8.GetString(ms.ToArray());
        Assert.DoesNotContain("s3cret!", text);
        Assert.DoesNotContain("pve-01", text);

        ms.Position = 0;
        Assert.Throws<WrongPasswordException>(() => ExportFile.Read(ms, "wrong"));
        ms.Position = 0;
        var back = ExportFile.Read(ms, "correct horse");
        Assert.Equal(5, back.Entries.Count);
        Assert.Equal("alice", back.CreatedBy);
        Assert.True(back.IncludesSecrets);
        Assert.Equal("s3cret!", back.Entries.Single(e => e.Kind == EntryKind.Credential).Password);
    }

    [Fact]
    public void ExportWithoutSecretsLeavesThemOut()
    {
        var bundle = ExportFile.CreateBundle(SampleTree(), Vault, new ExportOptions { IncludeSecrets = false, IncludeSnippets = false });
        Assert.False(bundle.IncludesSecrets);
        Assert.DoesNotContain(bundle.Entries, e => e.Kind == EntryKind.Snippet);
        var cred = bundle.Entries.Single(e => e.Kind == EntryKind.Credential);
        Assert.Null(cred.Password);
        Assert.DoesNotContain("Protected", cred.Payload);
    }

    [Fact]
    public void RestoreAsCopiesRemapsIdsLinksAndReencryptsForTheTarget()
    {
        var source = SampleTree();
        var bundle = ExportFile.CreateBundle(source, Vault, new ExportOptions());
        var target = new AesGcmSecretProtector("other-team");
        var restoreFolder = new Entry { Kind = EntryKind.Folder, Name = "Restored" };

        var plan = ImportPlanner.Plan(ImportPlanner.FromBundle(bundle).Items, [restoreFolder], target, ImportMode.AddCopies, restoreFolder.Id, skipDuplicates: false);

        Assert.Equal(5, plan.Count);
        Assert.All(plan, e => Assert.DoesNotContain(source, s => s.Id == e.Id));
        var folder = plan.Single(e => e.Kind == EntryKind.Folder);
        Assert.Equal(restoreFolder.Id, folder.ParentId);
        var cred = plan.Single(e => e.Kind == EntryKind.Credential);
        var bastion = plan.Single(e => e.Name == "bastion");
        var pve = plan.Single(e => e.Name == "pve-01");
        Assert.Equal(cred.Id, pve.CredentialId);
        Assert.Equal(bastion.Id, pve.Ssh!.JumpHostId);
        Assert.Equal(folder.Id, pve.ParentId);
        Assert.Equal("s3cret!", target.Unprotect(cred.Credential!.ProtectedPassword!));
        // Parents come before children.
        Assert.True(plan.IndexOf(folder) < plan.IndexOf(pve));
    }

    [Fact]
    public void MergeKeepsIdsAndSkipDuplicatesReusesExisting()
    {
        var source = SampleTree();
        var items = ImportPlanner.FromBundle(ExportFile.CreateBundle(source, Vault, new ExportOptions())).Items;
        var merged = ImportPlanner.Plan(items, source, Vault, ImportMode.Merge);
        Assert.Equal(source.Select(e => e.Id).OrderBy(x => x), merged.Select(e => e.Id).OrderBy(x => x));

        // Importing the same thing again as copies: everything already exists, nothing is added.
        var again = ImportPlanner.Plan(items, source, Vault, ImportMode.AddCopies, null, skipDuplicates: true);
        Assert.Empty(again);
    }

    [Fact]
    public void ReadsRdpFiles()
    {
        var rdp = "screen mode id:i:2\r\nfull address:s:dc01.lab.local:3390\r\nusername:s:LAB\\Administrator\r\ngatewayhostname:s:gw.lab.local\r\ngatewayusagemethod:i:1\r\nadministrative session:i:1\r\nredirectclipboard:i:1\r\ndrivestoredirect:s:*\r\npassword 51:b:01000000D08C9DDF\r\n";
        var other = "full address:s:10.0.20.32\r\nusername:s:LAB\\Administrator\r\n";
        var r = RdpFileImporter.Import([("dc01.rdp", rdp), ("ws-02.rdp", other)]);

        var dc = r.Items.Single(i => i.Entry.Name == "dc01").Entry;
        Assert.Equal(new HostEndpoint("dc01.lab.local", 3390), dc.Endpoints[0]);
        Assert.Equal("gw.lab.local", dc.Rdp!.GatewayHost);
        Assert.True(dc.Rdp.AdminSession);
        Assert.True(dc.Rdp.RedirectDrives);
        var creds = r.Items.Where(i => i.Entry.Kind == EntryKind.Credential).ToList();
        Assert.Single(creds); // shared by both files
        Assert.Equal("LAB", creds[0].Entry.Credential!.Domain);
        Assert.Equal("Administrator", creds[0].Entry.Credential!.UserName);
        Assert.Equal(creds[0].Entry.Id, dc.CredentialId);
        Assert.Equal(3389, r.Items.Single(i => i.Entry.Name == "ws-02").Entry.Endpoints[0].Port);
        Assert.Contains(r.Warnings, w => w.Contains("saved password"));
    }

    [Fact]
    public void ReadsPuttyRegExport()
    {
        var reg = "Windows Registry Editor Version 5.00\r\n\r\n" +
                  "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\pi%20hole]\r\n" +
                  "\"HostName\"=\"pi@10.0.0.53\"\r\n\"PortNumber\"=dword:00000016\r\n\"Protocol\"=\"ssh\"\r\n" +
                  "\"PublicKeyFile\"=\"C:\\\\keys\\\\pi.ppk\"\r\n\"RemoteCommand\"=\"htop\"\r\n\r\n" +
                  "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\switch]\r\n" +
                  "\"HostName\"=\"172.16.1.2\"\r\n\"PortNumber\"=dword:00000017\r\n\"Protocol\"=\"telnet\"\r\n\r\n" +
                  "[HKEY_CURRENT_USER\\Software\\SimonTatham\\PuTTY\\Sessions\\Default%20Settings]\r\n\"HostName\"=\"\"\r\n";
        var sessions = PuttyImporter.ParseRegFile(reg);
        Assert.Equal(3, sessions.Count);

        var r = PuttyImporter.Import(sessions, path => path == @"C:\keys\pi.ppk" ? "PuTTY-User-Key-File-3: ssh-ed25519" : null);
        var pi = r.Items.Single(i => i.Entry.Kind == EntryKind.Ssh).Entry;
        Assert.Equal("pi hole", pi.Name);
        Assert.Equal(new HostEndpoint("10.0.0.53", 22), pi.Endpoints[0]);
        Assert.Equal("htop", pi.Ssh!.StartupCommand);
        var cred = r.Items.Single(i => i.Entry.Kind == EntryKind.Credential);
        Assert.Equal("pi", cred.Entry.Credential!.UserName);
        Assert.StartsWith("PuTTY-User-Key-File", cred.PrivateKey);
        Assert.Equal(cred.Entry.Id, pi.CredentialId);
        Assert.Contains(r.Warnings, w => w.Contains("telnet"));
    }

    [Fact]
    public void ReadsRdmXmlExport()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ArrayOfConnection>
              <Connection><ConnectionType>Group</ConnectionType><Group>Homelab</Group><Name>Homelab</Name><ID>g1</ID></Connection>
              <Connection>
                <ConnectionType>Credential</ConnectionType><Group>Homelab</Group><Name>Domain admin</Name><ID>c1</ID>
                <Credentials><UserName>Administrator</UserName><Domain>LAB</Domain><SafePassword>xyz==</SafePassword></Credentials>
              </Connection>
              <Connection>
                <ConnectionType>RDPConfigured</ConnectionType><Group>Homelab\Servers</Group><Name>dc01</Name><ID>e1</ID>
                <Url>dc01.lab.local</Url><CredentialConnectionID>c1</CredentialConnectionID>
                <RDP><Port>3390</Port></RDP>
              </Connection>
              <Connection>
                <ConnectionType>SSHShell</ConnectionType><Group>Homelab</Group><Name>pve-01</Name><ID>e2</ID>
                <Terminal><Host>10.0.10.11</Host><HostPort>2222</HostPort><Username>root</Username><Password>plain-pw</Password></Terminal>
              </Connection>
              <Connection><ConnectionType>WebBrowser</ConnectionType><Name>Proxmox</Name><WebBrowserUrl>https://10.0.10.11:8006</WebBrowserUrl></Connection>
              <Connection><ConnectionType>DataReport</ConnectionType><Name>x</Name></Connection>
            </ArrayOfConnection>
            """;
        var r = RdmImporter.Import(xml);
        var byName = r.Items.ToDictionary(i => i.Entry.Name);

        var homelab = byName["Homelab"].Entry;
        var servers = byName["Servers"].Entry;
        Assert.Equal(homelab.Id, servers.ParentId);

        var dc = byName["dc01"].Entry;
        Assert.Equal(EntryKind.Rdp, dc.Kind);
        Assert.Equal(servers.Id, dc.ParentId);
        Assert.Equal(new HostEndpoint("dc01.lab.local", 3390), dc.Endpoints[0]);
        Assert.Equal(byName["Domain admin"].Entry.Id, dc.CredentialId);
        Assert.Equal("LAB", byName["Domain admin"].Entry.Credential!.Domain);

        var pve = byName["pve-01"].Entry;
        Assert.Equal(new HostEndpoint("10.0.10.11", 2222), pve.Endpoints[0]);
        Assert.Equal("plain-pw", byName["pve-01 login"].Password);
        Assert.Equal("https://10.0.10.11:8006", byName["Proxmox"].Entry.Web!.Url);

        Assert.Contains(r.Warnings, w => w.Contains("encrypted with RDM"));
        Assert.Contains(r.Warnings, w => w.Contains("DataReport"));
    }

    [Fact]
    public void ReadsRdmJsonExport()
    {
        var json = """
            {"Connections":[
              {"ConnectionType":"SSHShell","Name":"router","Group":"Lab","Terminal":{"Host":"10.0.0.1","Username":"admin"}},
              {"ConnectionType":"VNC","Name":"kali","VNC":{"Host":"10.0.10.40","Port":"5901"}}
            ]}
            """;
        var r = RdmImporter.Import(json);
        Assert.Equal(new HostEndpoint("10.0.0.1", 22), r.Items.Single(i => i.Entry.Name == "router").Entry.Endpoints[0]);
        Assert.Equal(new HostEndpoint("10.0.10.40", 5901), r.Items.Single(i => i.Entry.Name == "kali").Entry.Endpoints[0]);
        Assert.Contains(r.Items, i => i.Entry.Kind == EntryKind.Folder && i.Entry.Name == "Lab");
    }

    [Fact]
    public void SnippetsExpandPerSession()
    {
        var s = new SnippetData { Command = "ssh {user}@{host} # {name}" };
        Assert.Equal("ssh root@10.0.0.1 # pve", s.Expand("10.0.0.1", "root", "pve"));
        Assert.Equal(BuiltInSnippets.All.Count, BuiltInSnippets.All.Select(d => d.Group + "|" + d.Name).Distinct().Count());
        Assert.Equal(["Debian", "FortiOS 7.6", "MikroTik 7.25"], BuiltInSnippets.All.Select(d => new SnippetData { Group = d.Group }.Category).Distinct().ToArray());

        Assert.Equal(("Debian", "Network"), (new SnippetData { Group = "Debian/Network" }.Category, new SnippetData { Group = "Debian/Network" }.SubGroup));
        Assert.Equal(("Lab 3", "General"), (new SnippetData { Group = "Lab 3" }.Category, new SnippetData { Group = "Lab 3" }.SubGroup));
        Assert.Equal(SnippetData.DefaultCategory, new SnippetData().Category);
        // Nothing runs on its own; the ones that want a value say so.
        Assert.All(BuiltInSnippets.All, d => Assert.False(d.PressEnter));
        Assert.False(new SnippetData().PressEnter);
        Assert.True(new SnippetData { Command = "/tool torch interface=" }.NeedsValue);
        Assert.True(new SnippetData { Command = "execute ping " }.NeedsValue);
        Assert.False(new SnippetData { Command = "get system status" }.NeedsValue);
        // Computed parts are not stored.
        Assert.DoesNotContain("SubGroup", RemoteMan.Core.Storage.EntryJson.ToPayload(BuiltInSnippets.ToEntry(BuiltInSnippets.All[0])));
    }
}
