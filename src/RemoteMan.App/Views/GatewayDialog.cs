using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Gateway;

namespace RemoteMan.App.Views;

/// <summary>More > Gateway settings: paste this PC's invite, see the sites it can use and whether their connectors are online.</summary>
public static class GatewayDialog
{
    /// <summary>Returns true when the gateway was set up, replaced or removed (its connections were closed).</summary>
    public static bool Show(Window owner, AppSettings settings)
    {
        var gw = GatewayService.Instance;
        var box = new TextBox
        {
            MinWidth = 460, Margin = new Thickness(0, 6, 0, 0), FontSize = 11,
            ToolTip = "The invite link from the relay's admin console (https://…/enroll#…), or an rmgw1.… invite",
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
              "through your relay, with nothing to open on their side. Paste the invite link from the relay's admin console (or an rmgw1. invite).";
        string[] buttons = gw.IsConfigured ? ["Save", "Remove from this PC", "Close"] : ["Save", "Cancel"];
        var pick = ChoiceDialog.Show(owner, "Gateway settings", text, buttons, DialogIcon.None, extra: panel);

        if (pick == 0 && box.Text.Trim().Length > 0)
        {
            var invite = GatewayInvite.TryParse(box.Text);
            if (invite is null && GatewayEnrollment.TryParseLink(box.Text) is not null)
            {
                // An invite link: the relay makes this PC's device and answers with its token.
                try
                {
                    var link = box.Text.Trim();
                    invite = Task.Run(() => GatewayEnrollment.RedeemAsync(link, Environment.MachineName)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    ChoiceDialog.Info(owner, "The invite link didn't work", ex is GatewayException ? ex.Message : $"Can't reach the relay: {ex.GetBaseException().Message}");
                    return false;
                }
            }
            if (invite is null)
            {
                ChoiceDialog.Info(owner, "That isn't an invite", "Paste the invite link you were sent (https://…/enroll#…), or an invite that starts with rmgw1. Copy all of it.");
                return false;
            }
            settings.Gateway = GatewayService.FromInvite(invite);
            settings.Save();
            gw.Configure(settings.Gateway);
            ChoiceDialog.Info(owner, "Gateway set up", $"This PC now uses the relay {new Uri(invite.Url).Host}.");
            return true;
        }
        else if (pick == 1 && gw.IsConfigured)
        {
            if (ChoiceDialog.Show(owner, "Remove the gateway from this PC?",
                    "Entries that need it won't connect from other networks. The relay keeps the device until you run `gateway device remove` there.",
                    ["Remove", "Cancel"], DialogIcon.Question, danger: true) != 0) return false;
            settings.Gateway = null;
            settings.Save();
            gw.Configure(null);
            return true;
        }
        return false;
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
