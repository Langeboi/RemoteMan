using System.Text.Json.Serialization;

namespace RemoteMan.Core.Model;

public enum EntryKind
{
    Folder = 0,
    Rdp = 1,
    Ssh = 2,
    Vnc = 3,
    Web = 4,
    Telnet = 5,
    /// <summary>Console cable on a COM port.</summary>
    Serial = 6,
    /// <summary>File browser over SSH.</summary>
    Sftp = 7,
    Ftp = 8,
    /// <summary>Console of a Proxmox VM or container (noVNC in the built-in browser).</summary>
    Proxmox = 9,
    Credential = 10,
    /// <summary>A saved command for SSH sessions. Not shown in the tree; lives in the Snippets panel.</summary>
    Snippet = 11,
    /// <summary>Enter-PSSession to a Windows server, in a PowerShell window.</summary>
    PowerShell = 12,
    /// <summary>MikroTik WinBox, started with the address and login.</summary>
    WinBox = 13,
}

/// <summary>
/// One row in the tree. Everything protocol specific lives in the typed settings
/// objects, which are serialized to a single JSON column so the schema never has
/// to change when a field is added.
/// </summary>
public sealed class Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public EntryKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Tags { get; set; }

    /// <summary>Host/port pairs. Index 0 is the primary; the rest are tried in order.</summary>
    public List<HostEndpoint> Endpoints { get; set; } = new();

    /// <summary>Where the login for this entry (or, on a folder, for entries inheriting from it) comes from.</summary>
    public LoginSource Login { get; set; }

    /// <summary>Saved credential entry used when <see cref="Login"/> is <see cref="LoginSource.Saved"/> (Ctrl+click linking).</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>Single sign-on: log in as the current Windows user instead of a stored credential (RDP, web).</summary>
    [JsonIgnore]
    public bool UseWindowsLogin
    {
        get => Login == LoginSource.WindowsLogin;
        set { if (value) Login = LoginSource.WindowsLogin; else if (Login == LoginSource.WindowsLogin) Login = LoginSource.None; }
    }

    /// <summary>Shown under Favorites at the top of the tree.</summary>
    public bool Favorite { get; set; }

    /// <summary>Zabbix host this entry is linked to (set on import or by "Link to Zabbix host"). Wins over automatic matching.</summary>
    public string? ZabbixHostId { get; set; }

    public RdpSettings? Rdp { get; set; }
    /// <summary>Credential entries: the secret itself. Other entries: the login typed into the entry
    /// (<see cref="LoginSource.Embedded"/>).</summary>
    public SshSettings? Ssh { get; set; }
    public WebSettings? Web { get; set; }
    public VncSettings? Vnc { get; set; }
    public CredentialData? Credential { get; set; }
    public SnippetData? Snippet { get; set; }
    public SerialSettings? Serial { get; set; }
    public FtpSettings? Ftp { get; set; }
    public ProxmoxSettings? Proxmox { get; set; }
    public PowerShellSettings? PowerShell { get; set; }

    /// <summary>Any entry: glyph or vendor mark key from Change icon; null = the normal protocol or folder icon.</summary>
    public string? Icon { get; set; }
    /// <summary>Any entry: icon colour as #RRGGBB; null = the normal colour for the kind.</summary>
    public string? IconColor { get; set; }

    // --- sync metadata (owned by the data source) ---
    public DateTimeOffset? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool Deleted { get; set; }

    [JsonIgnore] public bool IsConnectable => Kind is EntryKind.Rdp or EntryKind.Ssh or EntryKind.Vnc or EntryKind.Web
        or EntryKind.Telnet or EntryKind.Serial or EntryKind.Sftp or EntryKind.Ftp or EntryKind.Proxmox or EntryKind.PowerShell or EntryKind.WinBox;

    /// <summary>Opens in the terminal (SSH, Telnet, serial console).</summary>
    [JsonIgnore] public bool IsTerminal => Kind is EntryKind.Ssh or EntryKind.Telnet or EntryKind.Serial;

    /// <summary>Has hosts to connect to (everything connectable except a serial port).</summary>
    [JsonIgnore] public bool HasHosts => IsConnectable && Kind != EntryKind.Serial && Kind != EntryKind.Web;

    public static int DefaultPort(EntryKind kind) => kind switch
    {
        EntryKind.Rdp => 3389,
        EntryKind.Ssh or EntryKind.Sftp => 22,
        EntryKind.Vnc => 5900,
        EntryKind.Web => 443,
        EntryKind.Telnet => 23,
        EntryKind.Ftp => 21,
        EntryKind.Proxmox => 8006,
        EntryKind.PowerShell => 5985,
        EntryKind.WinBox => 8291,
        _ => 0,
    };
}

public sealed record HostEndpoint(string Host, int Port = 0)
{
    public override string ToString() => Port > 0 ? $"{Host}:{Port}" : Host;
}

public sealed class RdpSettings
{
    public string? Domain { get; set; }
    public bool AdminSession { get; set; }
    public bool UseRdGateway { get; set; }
    public string? GatewayHost { get; set; }
    public int DesktopWidth { get; set; }   // 0 = fit to tab
    public int DesktopHeight { get; set; }
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool SmartSizing { get; set; } = true;
    /// <summary>Copy text as soon as it is selected with the mouse (sends Ctrl+C to the remote after a drag).</summary>
    public bool CopyOnSelect { get; set; } = true;
    /// <summary>Connect without prompting on certificate/identity warnings.</summary>
    public bool IgnoreCertificateErrors { get; set; } = true;
}

public sealed class SshSettings
{
    /// <summary>Auto-accept unknown host keys (TOFU) and keep connecting on changed keys (logged).</summary>
    public bool AutoAcceptHostKey { get; set; } = true;
    public bool AllowLegacyAlgorithms { get; set; } = true;
    public string? TerminalType { get; set; } = "xterm-256color";
    public int KeepAliveSeconds { get; set; } = 30;
    public string? StartupCommand { get; set; }
    public bool CopyOnSelect { get; set; } = true;
    /// <summary>Folder downloads go to; empty = the user's Downloads folder.</summary>
    public string? DownloadFolder { get; set; }
    /// <summary>Another SSH entry to connect through (ProxyJump). That entry may have its own jump host.</summary>
    public Guid? JumpHostId { get; set; }
}

public sealed class SerialSettings
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    /// <summary>None, Odd, Even, Mark, Space.</summary>
    public string Parity { get; set; } = "None";
    /// <summary>One, OnePointFive, Two.</summary>
    public string StopBits { get; set; } = "One";
    /// <summary>None, XOnXOff, RequestToSend.</summary>
    public string FlowControl { get; set; } = "None";
}

public sealed class FtpSettings
{
    /// <summary>Explicit TLS (FTPS).</summary>
    public bool UseTls { get; set; }
    public bool IgnoreCertificateErrors { get; set; } = true;
    public string? StartFolder { get; set; }
}

public sealed class ProxmoxSettings
{
    public string Node { get; set; } = "pve";
    public int VmId { get; set; } = 100;
    /// <summary>LXC container instead of a VM.</summary>
    public bool Container { get; set; }
    public bool IgnoreCertificateErrors { get; set; } = true;
}

public sealed class PowerShellSettings
{
    /// <summary>WinRM over HTTPS (port 5986).</summary>
    public bool UseSsl { get; set; }
    public string? ConfigurationName { get; set; }
}

public sealed class SnippetData
{
    /// <summary>Text sent to the terminal. {host}, {user} and {name} are filled in per session.</summary>
    public string Command { get; set; } = "";
    /// <summary>Heading the snippet is listed under in the panel.</summary>
    public string? Group { get; set; }
    /// <summary>
    /// Run it: press Enter after the text. Off by default, so a snippet is only typed and you check it,
    /// fill in what's missing and press Enter yourself. (In a multi-line snippet the earlier lines still run.)
    /// </summary>
    public bool PressEnter { get; set; }

    /// <summary>Ends in a space or "=": the snippet expects a value (an IP, an interface) typed after it.</summary>
    [JsonIgnore] public bool NeedsValue => Command.EndsWith(' ') || Command.EndsWith('=');

    public const string DefaultCategory = "My snippets";

    /// <summary>The part of <see cref="Group"/> before the first "/", e.g. "Debian" in "Debian/Network".</summary>
    [JsonIgnore] public string Category => Group is { Length: > 0 } g ? (g.IndexOf('/') is > 0 and var i ? g[..i] : g).Trim() : DefaultCategory;

    /// <summary>The part after the first "/", e.g. "Network"; "General" when there is none.</summary>
    [JsonIgnore] public string SubGroup => Group is { } g && g.IndexOf('/') is > 0 and var i && g[(i + 1)..].Trim() is { Length: > 0 } rest ? rest : "General";

    public string Expand(string host, string? user, string entryName) =>
        Command.Replace("{host}", host).Replace("{user}", user ?? "").Replace("{name}", entryName);
}

/// <summary>Embedded is the built-in browser (WebView2, the Edge engine), the only one that can run inside a tab.
/// The others open a separate window. Firefox is no longer offered; it stays here so old entries still load
/// and opens in the built-in browser.</summary>
public enum BrowserKind { Embedded = 0, Edge = 1, Chrome = 2, Firefox = 3, SystemDefault = 4, Brave = 5 }

public sealed class WebSettings
{
    /// <summary>"youtube.com" → "https://youtube.com/". Browsers and the shell need a scheme.</summary>
    public static string NormalizeUrl(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || u is "https://" or "http://") return u;
        if (!u.Contains("://", StringComparison.Ordinal)) u = "https://" + u;
        return Uri.TryCreate(u, UriKind.Absolute, out var parsed) ? parsed.ToString() : u;
    }

    public string Url { get; set; } = "https://";
    private BrowserKind _browser = BrowserKind.Embedded;
    public BrowserKind Browser
    {
        get => _browser;
        set => _browser = value == BrowserKind.Firefox ? BrowserKind.Embedded : value;
    }
    public bool IgnoreCertificateErrors { get; set; } = true;
    /// <summary>Try to fill the linked credential into the first login form.</summary>
    public bool AutoFill { get; set; } = true;
    /// <summary>After filling, press the login button (once per page load, so a failed login doesn't loop).</summary>
    public bool AutoSubmit { get; set; } = true;
    /// <summary>Optional CSS selectors for pages the automatic detection gets wrong.</summary>
    public string? UserFieldSelector { get; set; }
    public string? PasswordFieldSelector { get; set; }
    public string? SubmitSelector { get; set; }
    public bool CopyOnSelect { get; set; } = true;
}

public enum VncViewer { TightVnc, TigerVnc, RealVnc, UltraVnc }

public sealed class VncSettings
{
    public VncViewer Viewer { get; set; } = VncViewer.TigerVnc;
    public bool ViewOnly { get; set; }
}

public enum LoginSource
{
    /// <summary>No stored login: Windows or the server asks.</summary>
    None = 0,
    /// <summary>Use the nearest parent folder's login.</summary>
    Inherited = 1,
    /// <summary>A saved credential entry (<see cref="Entry.CredentialId"/>).</summary>
    Saved = 2,
    /// <summary>The current Windows user (SSO).</summary>
    WindowsLogin = 3,
    /// <summary>Typed into the entry itself (<see cref="Entry.Credential"/>).</summary>
    Embedded = 4,
}

public enum CredentialType
{
    /// <summary>User name, domain and password.</summary>
    Password = 0,
    /// <summary>User name and an SSH private key (stored, or a file on this PC) with optional passphrase.</summary>
    SshKey = 1,
    /// <summary>A .pfx/.p12 client certificate with its password.</summary>
    Certificate = 2,
    /// <summary>A token or API key to copy and paste.</summary>
    Secret = 3,
}

public sealed class CredentialData
{
    public CredentialType Type { get; set; }
    public string? UserName { get; set; }
    public string? Domain { get; set; }
    /// <summary>Encrypted with the data source's <c>ISecretProtector</c>; never plaintext at rest.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>OpenSSH/PuTTY private key text, protected the same way.</summary>
    public string? ProtectedPrivateKey { get; set; }
    public string? ProtectedPassphrase { get; set; }
    /// <summary>SSH key kept on disk instead of in the entry (only the path is stored).</summary>
    public string? PrivateKeyPath { get; set; }
    /// <summary>Base64 .pfx bytes, protected like the password (which holds the .pfx password).</summary>
    public string? ProtectedCertificate { get; set; }
    /// <summary>Don't store a password; ask for it on connect.</summary>
    public bool AskPassword { get; set; }
    public string? Notes { get; set; }
}
