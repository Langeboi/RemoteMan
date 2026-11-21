using RemoteMan.Core.Storage;

namespace RemoteMan.Core.Model;

/// <summary>
/// Entry templates: make a template from scratch or from an entry, and make new entries from a template.
/// A template is an entry of kind <see cref="EntryKind.Template"/>; what it fills in is an entry payload
/// in <see cref="TemplateData.Prototype"/>.
/// </summary>
public static class EntryTemplates
{
    /// <summary>Stands for the host in a web template's URL, e.g. "https://{host}:8443".</summary>
    public const string HostPlaceholder = "{host}";

    /// <summary>A new, empty template that makes entries of <paramref name="kind"/>.</summary>
    public static Entry Create(string name, EntryKind kind, string? summary = null)
    {
        var t = new Entry { Kind = EntryKind.Template, Name = name, Template = new TemplateData { Kind = kind, Summary = summary } };
        SetPrototype(t, Blank(kind));
        return t;
    }

    /// <summary>The settings a new entry of <paramref name="kind"/> starts with (default port, empty host).</summary>
    public static Entry Blank(EntryKind kind)
    {
        var e = new Entry { Kind = kind };
        switch (kind)
        {
            case EntryKind.Rdp: e.Rdp = new(); break;
            case EntryKind.Ssh or EntryKind.Telnet or EntryKind.Sftp: e.Ssh = new(); break;
            case EntryKind.Serial: e.Ssh = new(); e.Serial = new(); break;
            case EntryKind.Vnc: e.Vnc = new(); break;
            case EntryKind.Web: e.Web = new() { Url = $"https://{HostPlaceholder}" }; break;
            case EntryKind.Ftp: e.Ftp = new(); break;
            case EntryKind.Proxmox: e.Proxmox = new(); break;
            case EntryKind.PowerShell: e.PowerShell = new(); break;
        }
        if (kind != EntryKind.Web && kind != EntryKind.Serial && Entry.DefaultPort(kind) is > 0 and var port)
            e.Endpoints.Add(new HostEndpoint("", port));
        return e;
    }

    /// <summary>The settings the template fills in, as an entry you can open in the entry editor.</summary>
    public static Entry Prototype(Entry template)
    {
        var data = template.Template ?? new TemplateData();
        var e = new Entry { Kind = data.Kind, Name = template.Name };
        if (string.IsNullOrWhiteSpace(data.Prototype)) { var b = Blank(data.Kind); b.Name = template.Name; return b; }
        EntryJson.ApplyPayload(e, data.Prototype);
        return e;
    }

    /// <summary>Stores <paramref name="prototype"/>'s settings in the template. Links to one entry (Zabbix, Oxidized) are left out.</summary>
    public static void SetPrototype(Entry template, Entry prototype)
    {
        template.Template ??= new TemplateData();
        template.Template.Kind = prototype.Kind;
        var p = EntryStore.Clone(prototype);
        p.ZabbixHostId = null;
        p.OxidizedNode = null;
        p.Favorite = false;
        template.Template.Prototype = EntryJson.ToPayload(p);
    }

    /// <summary>A template that makes entries like <paramref name="source"/>: same settings, hosts left empty, same folder.</summary>
    public static Entry FromEntry(Entry source, string name)
    {
        var proto = EntryStore.Clone(source);
        proto.Endpoints = proto.Endpoints.Select(h => h with { Host = "" }).Take(1).ToList();
        if (proto.Web is { } web) web.Url = WithHostPlaceholder(web.Url);
        var t = new Entry
        {
            Kind = EntryKind.Template, Name = name,
            Template = new TemplateData { Kind = source.Kind, FolderId = source.ParentId, Summary = $"Like {source.Name}" },
        };
        SetPrototype(t, proto);
        return t;
    }

    /// <summary>"https://10.0.0.1:8443/login" → "https://{host}:8443/login".</summary>
    public static string WithHostPlaceholder(string url)
    {
        if (!Uri.TryCreate(WebSettings.NormalizeUrl(url), UriKind.Absolute, out var u) || u.Host.Length == 0) return url;
        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        return $"{u.Scheme}://{HostPlaceholder}{port}{u.PathAndQuery.TrimEnd('/')}";
    }

    /// <summary>
    /// A new entry from the template, ready for the entry editor: new id, empty name, in the template's folder
    /// (or <paramref name="fallbackParent"/> when the template has none or it is gone).
    /// </summary>
    public static Entry NewEntry(Entry template, Guid? fallbackParent, Func<Guid, bool>? folderExists = null)
    {
        var p = Prototype(template);
        var e = EntryStore.Clone(p);
        e.Id = Guid.NewGuid();
        e.Name = "";
        e.Revision = 0;
        e.CreatedAt = null; e.CreatedBy = null; e.UpdatedBy = null; e.Deleted = false;
        var folder = template.Template?.FolderId;
        e.ParentId = folder is { } f && (folderExists?.Invoke(f) ?? true) ? f : fallbackParent;
        if (e.Endpoints.Count == 0 && e.Kind != EntryKind.Web && e.Kind != EntryKind.Serial && Entry.DefaultPort(e.Kind) is > 0 and var port)
            e.Endpoints.Add(new HostEndpoint("", port));
        return e;
    }

    /// <summary>The host the new entry points at: first host, else the URL's host.</summary>
    public static string? HostOf(Entry e)
    {
        if (e.Endpoints.FirstOrDefault(h => h.Host.Length > 0) is { } ep) return ep.Host;
        if (e.Web?.Url is { } url && !url.Contains(HostPlaceholder) && Uri.TryCreate(WebSettings.NormalizeUrl(url), UriKind.Absolute, out var u)) return u.Host;
        return null;
    }

    /// <summary>
    /// The second entry the template asks for (e.g. "fw01 CLI" over SSH), next to <paramref name="made"/> and with its login.
    /// Null when the template has no companion or the new entry has no host.
    /// </summary>
    public static Entry? Companion(Entry template, Entry made)
    {
        if (template.Template is not { CompanionKind: { } kind } data || HostOf(made) is not { } host) return null;
        var c = Blank(kind);
        c.Id = Guid.NewGuid();
        c.ParentId = made.ParentId;
        c.Name = made.Name + (string.IsNullOrEmpty(data.CompanionSuffix) ? $" {kind}" : data.CompanionSuffix);
        var port = data.CompanionPort > 0 ? data.CompanionPort : Entry.DefaultPort(kind);
        c.Endpoints = kind == EntryKind.Web ? new() : [new HostEndpoint(host, port)];
        if (kind == EntryKind.Web) c.Web!.Url = $"https://{host}" + (port is > 0 and not 443 ? $":{port}" : "");
        c.Login = made.Login == LoginSource.Embedded ? LoginSource.Embedded : made.Login;
        c.CredentialId = made.CredentialId;
        c.Credential = made.Login == LoginSource.Embedded && made.Credential is { } cred
            ? new CredentialData { Type = kind is EntryKind.Ssh ? cred.Type : CredentialType.Password, UserName = cred.UserName, Domain = cred.Domain,
                ProtectedPassword = cred.ProtectedPassword, ProtectedPrivateKey = cred.ProtectedPrivateKey, ProtectedPassphrase = cred.ProtectedPassphrase,
                PrivateKeyPath = cred.PrivateKeyPath, AskPassword = cred.AskPassword, ProtectedTotpSecret = cred.ProtectedTotpSecret }
            : null;
        c.Icon = made.Icon;
        c.IconColor = made.IconColor;
        c.Tags = made.Tags;
        if (kind == EntryKind.Ssh && made.Ssh is { } ssh) { c.Ssh!.AllowLegacyAlgorithms = ssh.AllowLegacyAlgorithms; c.Ssh.AutoAcceptHostKey = ssh.AutoAcceptHostKey; }
        return c;
    }

    /// <summary>Starter templates for common lab gear. Offered from the Templates window; you change them to match your setup.</summary>
    public static IReadOnlyList<Entry> Starters()
    {
        Entry T(string name, string summary, Entry proto, EntryKind? companion = null, int companionPort = 0, string? suffix = null)
        {
            var t = new Entry { Kind = EntryKind.Template, Name = name, Template = new TemplateData { Summary = summary, CompanionKind = companion, CompanionPort = companionPort, CompanionSuffix = suffix } };
            SetPrototype(t, proto);
            return t;
        }
        Entry Web(string url, string icon, string tags) { var e = Blank(EntryKind.Web); e.Web!.Url = url; e.Icon = icon; e.Tags = tags; return e; }
        Entry Ssh(string icon, string tags, bool legacy) { var e = Blank(EntryKind.Ssh); e.Ssh!.AllowLegacyAlgorithms = legacy; e.Icon = icon; e.Tags = tags; return e; }

        var forti = Web($"https://{HostPlaceholder}", "fortigate", "fortigate, firewall");
        forti.Fields = [new CustomField { Name = "Model" }, new CustomField { Name = "Serial number" }];
        var mikrotik = Blank(EntryKind.WinBox); mikrotik.Icon = "mikrotik"; mikrotik.Tags = "mikrotik";
        var cisco = Ssh("cisco", "cisco", legacy: true);
        cisco.Fields = [new CustomField { Name = "Rack" }, new CustomField { Name = "Serial number" }];
        var pve = Web($"https://{HostPlaceholder}:8006", "proxmox", "proxmox");
        var win = Blank(EntryKind.Rdp); win.Rdp!.AdminSession = true; win.Icon = "windows"; win.Tags = "windows";
        var linux = Ssh("linux", "linux", legacy: false);
        return
        [
            T("FortiGate", "Web GUI on 443 + SSH for the CLI", forti, EntryKind.Ssh, 22, " CLI"),
            T("MikroTik RouterOS", "WinBox + SSH for the CLI", mikrotik, EntryKind.Ssh, 22, " CLI"),
            T("Cisco IOS switch", "SSH, older key exchange allowed", cisco),
            T("Proxmox VE node", "Web on 8006 + SSH shell", pve, EntryKind.Ssh, 22, " shell"),
            T("Windows Server", "RDP, admin session", win),
            T("Linux server", "SSH", linux),
        ];
    }
}
