using CommunityToolkit.Mvvm.ComponentModel;
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
    public HostEndpoint Endpoint { get; }
    public ResolvedCredential Credential { get; }
    public bool UsedFallback { get; }
    public string KindTag => EntryNode.KindTagFor(Entry.Kind);

    [ObservableProperty] private string _title;
    /// <summary>True while this tab is the visible one in its pane (both panes can show one in split view).</summary>
    [ObservableProperty] private bool _isActive;
    /// <summary>0 = left pane, 1 = right pane (split view).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Column))] private int _pane;
    /// <summary>Grid column in the working area: moving panes only re-lays out the view, it never reconnects.</summary>
    public int Column => Pane == 0 ? 0 : 2;
    /// <summary>Shown in the status bar while this tab is active.</summary>
    [ObservableProperty] private string _statusText = "";

    public event EventHandler? CloseRequested;
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The view tears down its connection when this fires.</summary>
    public event EventHandler? Disposed;
    public void Dispose() => Disposed?.Invoke(this, EventArgs.Empty);
}

public sealed class SshSessionViewModel(Entry e, HostEndpoint ep, ResolvedCredential c, bool fb) : SessionViewModel(e, ep, c, fb)
{
    public SshSettings Settings => Entry.Ssh ?? new SshSettings();
}

public sealed class RdpSessionViewModel(Entry e, HostEndpoint ep, ResolvedCredential c, bool fb) : SessionViewModel(e, ep, c, fb)
{
    public RdpSettings Settings => Entry.Rdp ?? new RdpSettings();
}

public sealed class WebSessionViewModel(Entry e, HostEndpoint ep, ResolvedCredential c, bool fb) : SessionViewModel(e, ep, c, fb)
{
    public WebSettings Settings => Entry.Web ?? new WebSettings();

    /// <summary>The entry URL, with its host swapped for the endpoint that answered (fallback support).</summary>
    public string Url
    {
        get
        {
            var url = WebSettings.NormalizeUrl(Settings.Url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
            var b = new UriBuilder(u) { Host = Endpoint.Host };
            if (Endpoint.Port > 0 && Endpoint.Port != (u.Scheme == "https" ? 443 : 80)) b.Port = Endpoint.Port;
            return b.Uri.ToString();
        }
    }
}
