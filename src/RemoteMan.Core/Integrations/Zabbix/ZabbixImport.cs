using System.Net;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Integrations.Zabbix;

public enum ZabbixAddressChoice { InterfaceAddress = 0, HostName = 1 }
public enum ZabbixFolderChoice { PerHostGroup = 0, SingleFolder = 1 }

public sealed record ZabbixImportCandidate(ZabbixHost Host, EntryKind Kind, string Address, string FolderPath, Entry? Existing);

public sealed record ZabbixImportPlan(IReadOnlyList<Entry> NewFolders, IReadOnlyList<Entry> NewEntries, IReadOnlyList<Entry> Linked);

/// <summary>Turns Zabbix hosts into entries, guessing the protocol from the host's templates.</summary>
public static class ZabbixImport
{
    public const string SingleFolderName = "Imported from Zabbix";

    public static EntryKind GuessKind(ZabbixHost host)
    {
        var t = string.Join(" | ", host.Templates.Append(host.Host).Append(host.Name)).ToLowerInvariant();
        if (t.Contains("windows")) return EntryKind.Rdp;
        if (t.Contains("synology") || t.Contains("qnap") || t.Contains("by http") && !t.Contains("linux") && !t.Contains("proxmox")) return EntryKind.Web;
        return EntryKind.Ssh;
    }

    public static string AddressFor(ZabbixHost host, ZabbixAddressChoice choice)
    {
        if (choice == ZabbixAddressChoice.HostName) return host.Host;
        var i = host.MainInterface;
        if (i is null) return host.Host;
        var a = i.Address;
        return string.IsNullOrWhiteSpace(a) || a == "127.0.0.1" ? host.Host : a;
    }

    /// <summary>The host's first group, as a folder path. Zabbix nests groups with "/".</summary>
    public static string FolderPathFor(ZabbixHost host, ZabbixFolderChoice choice) =>
        choice == ZabbixFolderChoice.SingleFolder ? SingleFolderName
        : host.Groups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? SingleFolderName;

    public static IReadOnlyList<ZabbixImportCandidate> Candidates(ZabbixSnapshot snapshot, IEnumerable<Entry> entries,
        ZabbixAddressChoice address, ZabbixFolderChoice folders)
    {
        var connectables = entries.Where(e => e.IsConnectable && !e.Deleted).ToList();
        var existingByHost = new Dictionary<string, Entry>();
        foreach (var e in connectables)
            if (snapshot.Matcher.Match(e) is { } m) existingByHost.TryAdd(m.Host.HostId, e);
        return snapshot.Hosts
            .Where(h => h.Enabled)
            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(h => new ZabbixImportCandidate(h, GuessKind(h), AddressFor(h, address), FolderPathFor(h, folders), existingByHost.GetValueOrDefault(h.HostId)))
            .ToList();
    }

    /// <summary>
    /// Entries to create for the chosen hosts, folders they need, and existing entries that just
    /// get the Zabbix link saved. Folders are matched by name under <paramref name="rootId"/>.
    /// </summary>
    public static ZabbixImportPlan Plan(IEnumerable<ZabbixImportCandidate> chosen, IEnumerable<Entry> entries, Guid? rootId, string? user = null)
    {
        var all = entries.Where(e => !e.Deleted).ToList();
        var newFolders = new List<Entry>();
        var newEntries = new List<Entry>();
        var linked = new List<Entry>();

        Guid? Folder(string path)
        {
            Guid? parent = rootId;
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var hit = all.Concat(newFolders).FirstOrDefault(e => e.Kind == EntryKind.Folder && e.ParentId == parent
                    && string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
                if (hit is null)
                {
                    hit = new Entry { Kind = EntryKind.Folder, Name = part, ParentId = parent };
                    newFolders.Add(hit);
                }
                parent = hit.Id;
            }
            return parent;
        }

        foreach (var c in chosen)
        {
            if (c.Existing is { } ex)
            {
                if (ex.ZabbixHostId != c.Host.HostId)
                {
                    var copy = Storage.EntryStore.Clone(ex);
                    copy.ZabbixHostId = c.Host.HostId;
                    linked.Add(copy);
                }
                continue;
            }
            var e = new Entry
            {
                Kind = c.Kind,
                Name = c.Host.DisplayName,
                ParentId = Folder(c.FolderPath),
                ZabbixHostId = c.Host.HostId,
                Description = c.Host.Templates.Count > 0 ? "Zabbix: " + string.Join(", ", c.Host.Templates) : null,
                CreatedBy = user,
            };
            switch (c.Kind)
            {
                case EntryKind.Rdp: e.Rdp = new RdpSettings(); e.Endpoints.Add(new HostEndpoint(c.Address)); break;
                case EntryKind.Ssh: e.Ssh = new SshSettings(); e.Endpoints.Add(new HostEndpoint(c.Address)); break;
                case EntryKind.Vnc: e.Vnc = new VncSettings(); e.Endpoints.Add(new HostEndpoint(c.Address)); break;
                case EntryKind.Web:
                    var host = IPAddress.TryParse(c.Address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{c.Address}]" : c.Address;
                    e.Web = new WebSettings { Url = $"https://{host}/" };
                    break;
            }
            newEntries.Add(e);
        }
        return new ZabbixImportPlan(newFolders, newEntries, linked);
    }
}
