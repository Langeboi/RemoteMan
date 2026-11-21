using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;
using RemoteMan.Protocols;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// A Config backups tab: one device's saved configs from Oxidized. It isn't a connection to the
/// device, so it has no credential; the view talks to Oxidized through <see cref="Connect"/>.
/// </summary>
public sealed partial class ConfigBackupsSessionViewModel : SessionViewModel
{
    public ConfigBackupsSessionViewModel(Entry entry, OxidizedNode node, string oxidizedUrl,
        Func<CancellationToken, Task<OxidizedClient>> connect, Func<Task> refreshNodes)
        : base(entry, entry.Endpoints.FirstOrDefault() ?? new HostEndpoint(node.Ip ?? node.Name), ResolvedCredential.None, false)
    {
        _node = node;
        OxidizedUrl = oxidizedUrl;
        Connect = connect;
        RefreshNodes = refreshNodes;
        Title = $"{entry.Name} · Config";
    }

    /// <summary>The device in Oxidized; replaced with a fresh copy on refresh.</summary>
    [ObservableProperty] private OxidizedNode _node;

    public string OxidizedUrl { get; }
    public Func<CancellationToken, Task<OxidizedClient>> Connect { get; }
    /// <summary>Refreshes the node list in the main window (last backup on the card and tree).</summary>
    public Func<Task> RefreshNodes { get; }
}
