using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.Core.Ai;
using RemoteMan.Core.Model;
using RemoteMan.Protocols;

namespace RemoteMan.App.ViewModels;

/// <summary>An open tab. The matching view (SSH/RDP/Web) is chosen by DataTemplate and kept alive while hidden.</summary>
public abstract partial class SessionViewModel : ObservableObject, IDisposable
{
    protected SessionViewModel(Entry entry, HostEndpoint endpoint, ResolvedCredential credential, bool usedFallback)
    {
        Entry = entry;
        Endpoint = endpoint;
        Credential = credential;
        UsedFallback = usedFallback;
        Title = entry.Name;
    }

    public Entry Entry { get; }

    /// <summary>Colour stripe on the tab (entry's Misc > Tab colour); null = none.</summary>
    public System.Windows.Media.Brush? TabBrush => Entry.TabColor is { Length: > 0 } c
        && System.Windows.Media.ColorConverter.ConvertFromString(c) is System.Windows.Media.Color color
        ? new System.Windows.Media.SolidColorBrush(color) : null;

    /// <summary>The entry's custom fields by name, secrets decrypted (login scripts, copy).</summary>
    public IReadOnlyDictionary<string, string> FieldValues { get; set; } = new Dictionary<string, string>();

    /// <summary>Reconnect when the connection drops (Advanced > Connection).</summary>
    public bool AutoReconnect => Entry.Connection?.AutoReconnect ?? true;

    /// <summary>Opens in its own borderless full-screen window (not a tab).</summary>
    public bool FullScreen { get; set; }

    /// <summary>Things that live as long as the tab, e.g. the SSH tunnel an RDP session goes through.</summary>
    public List<IDisposable> Owned { get; } = new();
    public HostEndpoint Endpoint { get; }
    public ResolvedCredential Credential { get; }
    public bool UsedFallback { get; }
    public virtual string KindTag => EntryNode.KindTagFor(Entry.Kind);
    /// <summary>Pinned tabs (the Dashboard) have no close button and stay in the left pane.</summary>
    public virtual bool IsPinned => false;
    /// <summary>False for tabs that aren't a connection (Dashboard, Overview): no presence, not reopened, not counted as open.</summary>
    public virtual bool IsConnection => true;
    /// <summary>The Overview tab: one reused tab that follows the selection, its title in italics.</summary>
    public virtual bool IsPreview => false;

    [ObservableProperty] private string _title;
    /// <summary>True while this tab is the visible one in its pane (both panes can show one in split view).</summary>
    [ObservableProperty] private bool _isActive;
    /// <summary>0 = left pane, 1 = right pane (split view).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Column))] private int _pane;
    /// <summary>Grid column in the working area: moving panes only re-lays out the view, it never reconnects.</summary>
    public int Column => Pane == 0 ? 0 : 2;
    /// <summary>Broadcast typing is on and this tab gets what is typed in the group (amber mark on the tab).</summary>
    [ObservableProperty] private bool _isBroadcasting;
    /// <summary>Shown in the status bar while this tab is active.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>The connection failed or dropped for a reason worth explaining (shown as a notice).</summary>
    public event Action<RemoteMan.Core.Networking.ConnectionProblem>? Failed;
    public void ReportProblem(RemoteMan.Core.Networking.ConnectionProblem? p) { if (p is not null) Failed?.Invoke(p); }

    public event EventHandler? CloseRequested;
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The view tears down its connection when this fires.</summary>
    public event EventHandler? Disposed;
    public void Dispose()
    {
        Disposed?.Invoke(this, EventArgs.Empty);
        foreach (var d in Owned) { try { d.Dispose(); } catch (Exception) { } }
        Owned.Clear();
        Closed?.Invoke(this);
    }

    /// <summary>After the tab is gone (runs the entry's "after closing" command).</summary>
    public event Action<SessionViewModel>? Closed;
}

public sealed class SshSessionViewModel(Entry e, HostEndpoint ep, ResolvedCredential c, bool fb) : SessionViewModel(e, ep, c, fb)
{
    public SshSettings Settings => Entry.Ssh ?? new SshSettings();

    /// <summary>Hosts to try in order. Without a jump host this is just the probed <see cref="SessionViewModel.Endpoint"/>.</summary>
    public IReadOnlyList<HostEndpoint>? Endpoints { get; init; }

    /// <summary>Jump hosts, outermost first (empty = direct).</summary>
    public IReadOnlyList<RemoteMan.Protocols.Ssh.SshHop> Jumps { get; init; } = [];

    /// <summary>Set by the view once the shell is up; snippets use it to type into this session.</summary>
    internal Action<string>? Sender { get; set; }

    public bool IsConnected => Sender is not null;

    public bool Send(string text)
    {
        if (Sender is not { } send) return false;
        send(text);
        return true;
    }
    /// <summary>Recent terminal output as plain text, for the AI assistant's session context.</summary>
    public TerminalTail Tail { get; } = new();

    /// <summary>Types text into the shell (set by the view once connected). Nothing is sent with Enter.</summary>
    public Action<string>? SendToTerminal { get; set; }
}

public sealed class RdpSessionViewModel(Entry e, HostEndpoint ep, ResolvedCredential c, bool fb) : SessionViewModel(e, ep, c, fb)
{
    public RdpSettings Settings => Entry.Rdp ?? new RdpSettings();
    /// <summary>The RD Gateway's own login, when the entry picks one.</summary>
    public ResolvedCredential? GatewayCredential { get; init; }
    /// <summary>"via bastion" when the connection goes through an SSH tunnel.</summary>
    public string? Via { get; init; }
}

/// <summary>SFTP and FTP entries: a file browser tab.</summary>
public sealed class FilesSessionViewModel(Entry e, HostEndpoint ep, ResolvedCredential c, bool fb) : SessionViewModel(e, ep, c, fb)
{
    public SshSettings Ssh => Entry.Ssh ?? new SshSettings();
    public FtpSettings Ftp => Entry.Ftp ?? new FtpSettings();
}

public sealed class WebSessionViewModel(Entry e, HostEndpoint ep, ResolvedCredential c, bool fb) : SessionViewModel(e, ep, c, fb)
{
    /// <summary>Proxmox console tabs have no web settings: no form filling, certificate errors per the entry.</summary>
    public WebSettings Settings => Entry.Web ?? (Entry.Kind == EntryKind.Proxmox
        ? new WebSettings { AutoFill = false, AutoSubmit = false, IgnoreCertificateErrors = Entry.Proxmox?.IgnoreCertificateErrors ?? true }
        : new WebSettings());

    /// <summary>The entry URL, with its host swapped for the endpoint that answered (fallback support).</summary>
    public string Url
    {
        get
        {
            if (Entry.Kind == EntryKind.Proxmox) return RemoteMan.Protocols.Proxmox.ProxmoxConsole.ConsoleUrl(Endpoint, Entry.Proxmox ?? new ProxmoxSettings());
            var url = WebSettings.NormalizeUrl(Settings.Url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
            var b = new UriBuilder(u) { Host = Endpoint.Host };
            if (Endpoint.Port > 0 && Endpoint.Port != (u.Scheme == "https" ? 443 : 80)) b.Port = Endpoint.Port;
            return b.Uri.ToString();
        }
    }
}
