using RemoteMan.App.Updates;

namespace RemoteMan.App.ViewModels;

/// <summary>The What's new tab: opened by the window on the first start after an update, and from More > What's new.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Opens the What's new tab, or switches to it when it's already open.</summary>
    public void OpenWhatsNew()
    {
        if (Sessions.OfType<WhatsNewViewModel>().FirstOrDefault() is { } open) { ActiveSession = open; return; }
        AddSession(new WhatsNewViewModel(WhatsNew.File, WhatsNew.NewKeys, UpdateService.Instance.CurrentVersion, WhatsNew.PreviousVersion));
    }
}
