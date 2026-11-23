using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Gateway;

namespace RemoteMan.App.Views;

/// <summary>More > Gateway settings: paste this PC's invite, see the sites it can use and whether their connectors are online.</summary>
public static class GatewayDialog
{
    public static void Show(Window owner, AppSettings settings)
    {
        var gw = GatewayService.Instance;
        var box = new TextBox
        {
            MinWidth = 460, Margin = new Thickness(0, 6, 0, 0), FontSize = 11,
            ToolTip = "rmgw1.… as printed by `gateway device add <name>` on the relay",
        };
        if (Application.Current.TryFindResource("MonoFont") is FontFamily mono) box.FontFamily = mono;
        var sites = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 520 };
        sites.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = gw.IsConfigured ? "New invite (replaces the current one)" : "Invite", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(box);
        if (gw.IsConfigured)
        {
            panel.Children.Add(sites);
            sites.Text = "Asking the relay which sites this PC can use…";
            panel.Loaded += async (_, _) => sites.Text = await DescribeSitesAsync(gw);
        }

        var text = gw.IsConfigured
            ? $"This PC reaches other networks through the relay {gw.RelayHost}{(gw.Device is { } d ? $" as {d}" : "")}. " +
              "Entries and folders pick a site under Advanced > Connection; a database picks one in Data sources."
            : "The gateway reaches databases and machines in other networks (school, a friend's homelab, behind CG-NAT) " +
              "through your relay, with nothing to open on their side. On the relay run `gateway device add <name>` and paste the invite it prints.";
        string[] buttons = gw.IsConfigured ? ["Save", "Remove from this PC", "Close"] : ["Save", "Cancel"];
        var pick = ChoiceDialog.Show(owner, "Gateway settings", text, buttons, DialogIcon.None, extra: panel);

        if (pick == 0 && box.Text.Trim().Length > 0)
        {
            if (GatewayInvite.TryParse(box.Text) is not { } invite)
            {
                ChoiceDialog.Info(owner, "That isn't an invite", "An invite starts with rmgw1. and is one long line. Copy all of it from the relay's output.");
                return;
            }
            settings.Gateway = GatewayService.FromInvite(invite);
            settings.Save();
            gw.Configure(settings.Gateway);
            var restartNote = settings.Active.GatewaySite is { Length: > 0 }
                ? " The database goes through it from the next start." : "";
            ChoiceDialog.Info(owner, "Gateway set up", $"This PC now uses the relay {new Uri(invite.Url).Host}.{restartNote}");
        }
        else if (pick == 1 && gw.IsConfigured)
        {
            if (ChoiceDialog.Show(owner, "Remove the gateway from this PC?",
                    "Entries that need it won't connect from other networks. The relay keeps the device until you run `gateway device remove` there.",
                    ["Remove", "Cancel"], DialogIcon.Question, danger: true) != 0) return;
            settings.Gateway = null;
            settings.Save();
            gw.Configure(null);
        }
    }

    private static async Task<string> DescribeSitesAsync(GatewayService gw)
    {
        try
        {
            var list = await gw.Client.GetSitesAsync(refresh: true);
            if (list.Count == 0) return "The relay has no sites for this PC yet. Add one there with `gateway site add <name>`.";
            return string.Join("\n", list.Select(s =>
                $"{s.Name}: {(s.Online ? $"online ({s.Connectors} connector{(s.Connectors == 1 ? "" : "s")})" : "offline")}" +
                (s.Keys.Count > 0 ? $" · key {string.Join(", ", s.Fingerprints.Select(f => f[..8]))}" : "")));
        }
        catch (Exception ex) when (ex is RelayRefusedException or GatewayException) { return ex.Message; }
        catch (Exception ex) { return $"Can't reach the relay: {ex.GetBaseException().Message}"; }
    }
}
