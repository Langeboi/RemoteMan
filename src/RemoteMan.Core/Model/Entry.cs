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

    /// <summary>Oxidized node ("group/name") this entry is linked to by hand. Wins over automatic matching.</summary>
    public string? OxidizedNode { get; set; }

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

    /// <summary>Where a connection opens: a tab, its own window, or full screen (Misc tab).</summary>
    public OpenMode OpenIn { get; set; }
    /// <summary>Colour stripe on the session tab as #RRGGBB; null = none.</summary>
    public string? TabColor { get; set; }
    /// <summary>Wake-on-LAN, waiting for the host, reconnecting and local commands (Advanced > Connection).</summary>
    public ConnectionSettings? Connection { get; set; }
    /// <summary>Your own name/value pairs (iDRAC address, VLAN, licence key). Secret values are protected.</summary>
    public List<CustomField>? Fields { get; set; }
    /// <summary>Settings sections taken from the nearest parent folder instead of this entry.</summary>
    public InheritSections Inherit { get; set; }

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

public enum OpenMode { Tab = 0, Window = 1, FullScreen = 2 }

/// <summary>Groups of settings an entry can take from its folder ("Folder" on an Advanced section).</summary>
[Flags]
public enum InheritSections
{
    None = 0,
    Connection = 1,
    /// <summary>RDP: size, monitors, scaling, colours, speed, smart sizing.</summary>
    Display = 2,
    /// <summary>RDP: sound, microphone, clipboard, printers, drives, Windows keys.</summary>
    Resources = 4,
    /// <summary>RDP: RD Gateway and SSH tunnel.</summary>
    Gateway = 8,
    /// <summary>SSH, Telnet, serial: theme, font, scrollback, encoding, backspace, session log.</summary>
    Terminal = 16,
    /// <summary>SSH: host keys, algorithms, auth order.</summary>
    Authentication = 32,
    /// <summary>Web: certificates, zoom, cookies, basic auth, staying signed in.</summary>
    Browser = 64,
}

public sealed class ConnectionSettings
{
    public bool WakeOnLan { get; set; }
    /// <summary>MAC address of the machine to wake, any common notation.</summary>
    public string? MacAddress { get; set; }
    /// <summary>Where the magic packet goes: a broadcast address (default 255.255.255.255) or a relay host.</summary>
    public string? WakeBroadcast { get; set; }
    /// <summary>Keep probing the hosts until one answers, instead of failing at once.</summary>
    public bool WaitUntilUp { get; set; }
    public int WaitSeconds { get; set; } = 120;
    /// <summary>Reconnect when an established session drops (not when you close it).</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Command run on this PC before connecting (cmd.exe /c).</summary>
    public string? RunBefore { get; set; }
    public bool WaitForRunBefore { get; set; } = true;
    /// <summary>Command run on this PC after the session closes.</summary>
    public string? RunAfter { get; set; }

    public ConnectionSettings Copy() => (ConnectionSettings)MemberwiseClone();
}

public sealed class CustomField
{
    public string Name { get; set; } = "";
    /// <summary>Plain text, or protected with the data source's protector when <see cref="Secret"/>.</summary>
    public string? Value { get; set; }
    public bool Secret { get; set; }
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

    // --- Display ---
    /// <summary>One monitor in a tab, or all/some monitors (opens in Remote Desktop Connection).</summary>
    public RdpMonitors Monitors { get; set; }
    /// <summary>Monitor numbers for <see cref="RdpMonitors.Selected"/>, e.g. "0,1".</summary>
    public string? SelectedMonitors { get; set; }
    /// <summary>Remote scaling in percent; 0 = same as this PC.</summary>
    public int ScalePercent { get; set; }
    public int ColorDepth { get; set; } = 32;
    public RdpSpeed Speed { get; set; }

    // --- Local resources ---
    public RdpAudio Audio { get; set; }
    public bool Microphone { get; set; }
    public bool RedirectPrinters { get; set; }
    /// <summary>Drive letters to redirect, e.g. "C,E"; empty = all drives (when <see cref="RedirectDrives"/>).</summary>
    public string? Drives { get; set; }
    public RdpKeys WindowsKeys { get; set; } = RdpKeys.FullScreenOnly;

    // --- Gateway and tunnel ---
    /// <summary>Own login for the RD Gateway; null = the entry's login.</summary>
    public Guid? GatewayCredentialId { get; set; }
    /// <summary>Connect directly to local addresses (gateway usage "detect").</summary>
    public bool GatewayBypassLocal { get; set; } = true;
    /// <summary>SSH entry to tunnel the connection through (local port forward).</summary>
    public Guid? SshTunnelId { get; set; }
}

public enum RdpMonitors { One = 0, All = 1, Selected = 2 }
/// <summary>Auto = let RDP detect the link (default); the others fix the experience flags.</summary>
public enum RdpSpeed { Auto = 0, Lan = 1, Broadband = 2, Slow = 3 }
public enum RdpAudio { Local = 0, Remote = 1, Off = 2 }
/// <summary>Values match the control's KeyboardHookMode.</summary>
public enum RdpKeys { Local = 0, Remote = 1, FullScreenOnly = 2 }

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

    // --- Terminal (null/0 = the app-wide terminal look) ---
    public string? Theme { get; set; }
    public int FontSize { get; set; }
    public int Scrollback { get; set; }
    /// <summary>Character set of the remote side, e.g. "iso-8859-1" for old switches; null = UTF-8.</summary>
    public string? Encoding { get; set; }
    /// <summary>Backspace sends Ctrl+H (^H) instead of DEL (^?).</summary>
    public bool BackspaceCtrlH { get; set; }

    // --- Session log ---
    public bool LogSession { get; set; }
    /// <summary>Folder for logs; empty = Documents\RemoteMan logs. A file per session: name_yyyy-MM-dd_HHmmss.log.</summary>
    public string? LogFolder { get; set; }

    /// <summary>Port forwards that start with the session.</summary>
    public List<PortForward>? Forwards { get; set; }
    /// <summary>Wait-for / type steps run after login (SSH, Telnet, serial).</summary>
    public List<ScriptStep>? LoginScript { get; set; }

    // --- Authentication ---
    /// <summary>Order to try login methods, e.g. "publickey,keyboard-interactive,password"; null = key first.</summary>
    public string? AuthOrder { get; set; }
    /// <summary>Comma lists: only offer these (in this order). Null = automatic.</summary>
    public string? KexAlgorithms { get; set; }
    public string? Ciphers { get; set; }
    public string? HostKeyAlgorithms { get; set; }
}

public enum ForwardKind { Local = 0, Remote = 1, Dynamic = 2 }

/// <summary>Local: this PC listens and the server connects onward. Remote: the server listens and this PC
/// connects onward. Dynamic: a SOCKS proxy on this PC.</summary>
public sealed class PortForward
{
    public ForwardKind Kind { get; set; }
    public string ListenHost { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; }
    public string? DestHost { get; set; }
    public int DestPort { get; set; }
    public bool Enabled { get; set; } = true;

    public override string ToString() => Kind switch
    {
        ForwardKind.Dynamic => $"SOCKS {ListenHost}:{ListenPort}",
        ForwardKind.Remote => $"server {ListenHost}:{ListenPort} → {DestHost}:{DestPort}",
        _ => $"{ListenHost}:{ListenPort} → {DestHost}:{DestPort}",
    };
}

/// <summary>Wait until the output contains <see cref="WaitFor"/> (empty = don't wait), then type <see cref="Send"/>
/// and Enter. {field:Name} and {secret:Name} put in a custom field of the entry; {totp} the current 2FA code.</summary>
public sealed class ScriptStep
{
    public string? WaitFor { get; set; }
    public string? Send { get; set; }
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

    /// <summary>Page zoom in percent.</summary>
    public int Zoom { get; set; } = 100;
    public WebProfile Profile { get; set; }
    /// <summary>Answer the browser's own login pop-up (HTTP basic auth) with the entry's login.</summary>
    public bool AnswerBasicAuth { get; set; } = true;
    /// <summary>Path or URL to open once the login went through, e.g. /ng/system/dashboard.</summary>
    public string? AfterLoginUrl { get; set; }
    /// <summary>Reload the page every N minutes while the tab is open; 0 = never.</summary>
    public int KeepAliveMinutes { get; set; }
    /// <summary>Fill the credential's two-factor code into the code box after the password.</summary>
    public bool FillTotp { get; set; } = true;
}

/// <summary>Shared: cookies shared by all built-in web tabs. Own: a profile for this entry only (sign in to two
/// systems as different users). Private: nothing kept after the tab closes.</summary>
public enum WebProfile { Shared = 0, Own = 1, Private = 2 }

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
    /// <summary>Two-factor (TOTP) secret, base32 as shown under the QR code; protected like the password.</summary>
    public string? ProtectedTotpSecret { get; set; }
    public string? Notes { get; set; }
}
