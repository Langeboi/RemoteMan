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
    [ObservableProperty] private bool _isActive;
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
