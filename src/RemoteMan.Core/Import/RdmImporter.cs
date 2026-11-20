using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Transfer;

namespace RemoteMan.Core.Import;

/// <summary>
/// Devolutions Remote Desktop Manager exports: the XML .rdm file and the JSON export. Both describe
/// each entry as a "Connection" with a ConnectionType, a Group path ("Lab\Servers"), a Name and
/// protocol sections. Field names have moved between RDM versions, so lookups are by name,
/// case-insensitive, anywhere inside the connection. Folders come from Group entries and from the
/// Group paths. Passwords come across only when the export holds them in clear text; RDM's
/// "SafePassword" values are encrypted with RDM's own key and are skipped.
/// </summary>
public static class RdmImporter
{
    public static ImportResult Import(string text)
    {
        var root = Load(text);
        var connections = root.DescendantsAndSelf().Where(x => Child(x, "ConnectionType") is not null).ToList();
        if (connections.Count == 0)
        {
            if (root.DescendantsAndSelf().Any(x => x.Name.LocalName.Contains("Encrypt", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("This RDM export is encrypted. In RDM, export again without a master password (File > Export > Export as .rdm, untick \"Encrypt\"), or include passwords in clear text.");
            throw new InvalidDataException("No RDM entries were found in this file.");
        }

        var result = new ImportResult();
        var folders = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var byRdmId = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var credLinks = new List<(Entry Entry, string CredId)>();
        var safeSkipped = 0;
        var unsupported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Entry? Folder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim('\\', '/', ' ');
            if (path.Length == 0) return null;
            if (folders.TryGetValue(path, out var f)) return f;
            var cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            var parent = cut > 0 ? Folder(path[..cut]) : null;
            f = new Entry { Kind = EntryKind.Folder, Name = cut > 0 ? path[(cut + 1)..] : path, ParentId = parent?.Id };
            folders[path] = f;
            result.Items.Add(new ImportItem(f));
            return f;
        }

        foreach (var c in connections)
        {
            var type = Child(c, "ConnectionType")!.Value.Trim();
            var kind = MapKind(type);
            var name = Value(c, "Name") ?? "";
            var group = Value(c, "Group");
            var rdmId = Value(c, "ID");

            if (kind == EntryKind.Folder)
            {
                // A Group entry is the folder its Group path names (or Name, at the top).
                var f = Folder(group ?? name);
                if (f is not null && rdmId is not null) byRdmId[rdmId] = f;
                if (f is not null && Value(c, "Description") is { } fd) f.Description = fd;
                continue;
            }
            if (kind is null)
            {
                unsupported[type] = unsupported.GetValueOrDefault(type) + 1;
                continue;
            }

            var e = new Entry { Kind = kind.Value, Name = name, ParentId = Folder(group)?.Id, Description = Value(c, "Description") };
            var item = new ImportItem(e);
            if (rdmId is not null) byRdmId[rdmId] = e;

            if (kind == EntryKind.Credential)
            {
                var cred = Section(c, "Credentials") ?? c;
                e.Credential = new CredentialData { UserName = Value(cred, "UserName", "Username", "User"), Domain = Value(cred, "Domain") };
                item.Password = ClearPassword(cred, ref safeSkipped);
                item.PrivateKey = Value(cred, "PrivateKeyData");
                item.Passphrase = Value(cred, "PrivateKeyPassPhrase", "PrivateKeyPassphrase");
                if (e.Name.Length == 0) e.Name = e.Credential.UserName ?? "credential";
                result.Items.Add(item);
                continue;
            }

            var section = kind switch
            {
                EntryKind.Ssh => Section(c, "Terminal") ?? Section(c, "Putty"),
                EntryKind.Vnc => Section(c, "VNC"),
                EntryKind.Web => Section(c, "Web", "WebBrowser"),
                _ => Section(c, "RDP"),
            };
            var scopes = section is null ? new[] { c } : new[] { section, c };

            var hostText = First(scopes, "Host", "HostName", "Url", "ComputerName") ?? "";
            var port = int.TryParse(First(scopes, "HostPort", "Port", "RDPPort"), out var p) ? p : 0;
            if (kind == EntryKind.Web)
            {
                var url = First(scopes, "WebBrowserUrl", "URL", "Url", "Host") ?? "";
                e.Web = new WebSettings { Url = url };
            }
            else if (hostText.Length > 0)
            {
                var ep = EndpointResolver.Parse(hostText);
                if (ep.Port == 0) ep = ep with { Port = port > 0 ? port : Entry.DefaultPort(kind.Value) };
                e.Endpoints.Add(ep);
            }
            switch (kind)
            {
                case EntryKind.Rdp:
                    e.Rdp = new RdpSettings
                    {
                        AdminSession = Bool(scopes, "AdminMode", "Console", "AdministrativeSession"),
                        GatewayHost = First(scopes, "GatewayHostname", "GatewayHost"),
                    };
                    e.Rdp.UseRdGateway = !string.IsNullOrWhiteSpace(e.Rdp.GatewayHost);
                    break;
                case EntryKind.Ssh: e.Ssh = new SshSettings(); break;
                case EntryKind.Vnc: e.Vnc = new VncSettings(); break;
            }

            // Linked credential entry, or a username/password stored on the entry itself.
            if (First(scopes, "CredentialConnectionID", "CredentialConnectionId") is { Length: > 0 } credId && !IsEmptyGuid(credId))
                credLinks.Add((e, credId));
            else
            {
                var user = First(scopes, "UserName", "Username");
                var pw = ClearPassword(section ?? c, ref safeSkipped) ?? (section is not null ? ClearPassword(c, ref safeSkipped) : null);
                var keyPath = First(scopes, "PrivateKeyPath");
                if (!string.IsNullOrWhiteSpace(user) || pw is not null)
                {
                    var cred = new Entry
                    {
                        Kind = EntryKind.Credential, ParentId = e.ParentId,
                        Name = $"{e.Name} login",
                        Credential = new CredentialData { UserName = user, Domain = First(scopes, "Domain") },
                    };
                    result.Items.Add(new ImportItem(cred) { Password = pw });
                    e.CredentialId = cred.Id; e.Login = LoginSource.Saved;
                }
                if (!string.IsNullOrWhiteSpace(keyPath)) result.Warnings.Add($"{e.Name}: uses key file {keyPath}; add it to the login after import");
            }
            result.Items.Add(item);
        }

        foreach (var (entry, credId) in credLinks)
        {
            if (byRdmId.TryGetValue(credId, out var cred) && cred.Kind == EntryKind.Credential) { entry.CredentialId = cred.Id; entry.Login = LoginSource.Saved; }
            else result.Warnings.Add($"{entry.Name}: linked RDM credential was not in the export");
        }
        if (safeSkipped > 0)
            result.Warnings.Add($"{safeSkipped} password{(safeSkipped == 1 ? " is" : "s are")} encrypted with RDM's own key and were not imported. Export from RDM with passwords in clear text to bring them over.");
        foreach (var (type, n) in unsupported)
            result.Warnings.Add($"{n} \"{type}\" entr{(n == 1 ? "y" : "ies")} skipped (not a supported type)");

        result.SortAsTree();
        return result;
    }

    private static EntryKind? MapKind(string type)
    {
        var t = type.ToLowerInvariant();
        if (t is "group" or "folder" or "virtualgroup") return EntryKind.Folder;
        if (t.Contains("credential")) return EntryKind.Credential;
        if (t.StartsWith("rdp") || t == "remotedesktop") return EntryKind.Rdp;
        if (t.Contains("ssh") || t == "putty" || t == "terminal") return EntryKind.Ssh;
        if (t.Contains("vnc")) return EntryKind.Vnc;
        if (t.Contains("web")) return EntryKind.Web;
        return null;
    }

    private static XElement Load(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (trimmed.StartsWith('<'))
        {
            try { return XDocument.Parse(trimmed).Root ?? new XElement("empty"); }
            catch (XmlException ex) { throw new InvalidDataException($"The .rdm file could not be read: {ex.Message}"); }
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return FromJson("root", doc.RootElement);
        }
        catch (JsonException) { throw new InvalidDataException("This is not an RDM .rdm (XML) or .json export."); }
    }

    /// <summary>JSON to XML-shaped tree, so one set of lookups covers both export formats.</summary>
    private static XElement FromJson(string name, JsonElement el)
    {
        var x = new XElement(XmlConvert.EncodeLocalName(name));
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        foreach (var item in p.Value.EnumerateArray()) x.Add(FromJson(p.Name, item));
                    else x.Add(FromJson(p.Name, p.Value));
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) x.Add(FromJson("item", item));
                break;
            case JsonValueKind.String: x.Value = el.GetString() ?? ""; break;
            case JsonValueKind.Null: break;
            default: x.Value = el.GetRawText(); break;
        }
        return x;
    }

    private static XElement? Child(XElement x, string name) =>
        x.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static XElement? Section(XElement x, params string[] names) =>
        names.Select(n => Child(x, n)).FirstOrDefault(s => s is not null && s.HasElements);

    /// <summary>The first non-empty direct child value among the names.</summary>
    private static string? Value(XElement x, params string[] names)
    {
        foreach (var n in names)
            if (Child(x, n) is { HasElements: false } c && c.Value.Trim() is { Length: > 0 } v) return v;
        return null;
    }

    private static string? First(IEnumerable<XElement> scopes, params string[] names) =>
        scopes.Select(s => Value(s, names)).FirstOrDefault(v => v is not null);

    private static bool Bool(IEnumerable<XElement> scopes, params string[] names) =>
        First(scopes, names) is { } v && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

    private static string? ClearPassword(XElement x, ref int safeSkipped)
    {
        if (Value(x, "Password", "PasswordClear", "ClearPassword") is { } pw) return pw;
        if (Value(x, "SafePassword", "EncryptedPassword") is not null) safeSkipped++;
        return null;
    }

    private static bool IsEmptyGuid(string s) => Guid.TryParse(s, out var g) && g == Guid.Empty;
}
