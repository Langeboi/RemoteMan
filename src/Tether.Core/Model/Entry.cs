using System.Text.Json.Serialization;

namespace Tether.Core.Model;

public enum EntryKind
{
    Folder = 0,
    Rdp = 1,
    Ssh = 2,
    Vnc = 3,
    Web = 4,
    Credential = 10,
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

    /// <summary>Credential entry used to log in (set by Ctrl+click linking).</summary>
    public Guid? CredentialId { get; set; }

    public RdpSettings? Rdp { get; set; }
    public SshSettings? Ssh { get; set; }
    public WebSettings? Web { get; set; }
    public VncSettings? Vnc { get; set; }
    public CredentialData? Credential { get; set; }

    // --- sync metadata (owned by the data source) ---
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool Deleted { get; set; }

    [JsonIgnore] public bool IsConnectable => Kind is EntryKind.Rdp or EntryKind.Ssh or EntryKind.Vnc or EntryKind.Web;

    public static int DefaultPort(EntryKind kind) => kind switch
    {
        EntryKind.Rdp => 3389,
        EntryKind.Ssh => 22,
        EntryKind.Vnc => 5900,
        EntryKind.Web => 443,
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
}

public enum BrowserKind { Embedded = 0, Edge = 1, Chrome = 2, Firefox = 3, SystemDefault = 4 }

public sealed class WebSettings
{
    public string Url { get; set; } = "https://";
    public BrowserKind Browser { get; set; } = BrowserKind.Embedded;
    public bool IgnoreCertificateErrors { get; set; } = true;
    /// <summary>Try to fill the linked credential into the first login form.</summary>
    public bool AutoFill { get; set; } = true;
    public bool CopyOnSelect { get; set; } = true;
}

public enum VncViewer { TightVnc, TigerVnc, RealVnc, UltraVnc }

public sealed class VncSettings
{
    public VncViewer Viewer { get; set; } = VncViewer.TigerVnc;
    public bool ViewOnly { get; set; }
}

public sealed class CredentialData
{
    public string? UserName { get; set; }
    public string? Domain { get; set; }
    /// <summary>Encrypted with the data source's <c>ISecretProtector</c>; never plaintext at rest.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>OpenSSH/PuTTY private key text, protected the same way.</summary>
    public string? ProtectedPrivateKey { get; set; }
    public string? ProtectedPassphrase { get; set; }
}
