using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.App.Services;

namespace RemoteMan.App.Views;

/// <summary>
/// Signing in to a RemoteMan account (Data sources › Sign in, More › RemoteMan account): the workspaces you're a member
/// of then show up as data sources. No invite links: the admin makes the account and tells you the first password.
/// </summary>
public static class AccountDialog
{
    /// <summary>Signed in: the account box (check now, password, sign out). Signed out: the sign-in box. True when something changed.</summary>
    public static async Task<bool> ShowAsync(Window owner, AppSettings settings)
    {
        var acc = HostedAccount.Instance;
        if (!acc.IsSignedIn) return await SignInAsync(owner, settings);

        var ws = settings.DataSources.Where(d => d.Hosted is not null).ToList();
        var text = $"Signed in as {acc.Account}{(acc.FullName is { } f ? $" ({f})" : "")} on {Host(acc.Server!)}.\n\n" +
                   (ws.Count == 0 ? "You aren't in a workspace yet. The admin adds you; it shows up here within a minute."
                       : "Your workspaces (under Data sources):\n" + string.Join("\n", ws.Select(d => $"  •  {d.Name}  ({d.Hosted!.AccessLabel})"))) +
                   (acc.Problem is { } p ? $"\n\n{p}" : acc.LastSync is { } t ? $"\n\nChecked {t.ToLocalTime():HH:mm}." : "");
        switch (ChoiceDialog.Show(owner, "RemoteMan account", text, ["Check now", "Change password", "Sign out", "Close"]))
        {
            case 0:
                var r = await acc.SyncAsync(settings);
                ChoiceDialog.Info(owner, "RemoteMan account", acc.Problem ?? Describe(r) ?? "Nothing new.");
                return r.Changed;
            case 1:
                await ChangePasswordAsync(owner, settings, first: false);
                return false;
            case 2:
                if (!ChoiceDialog.Confirm(owner, "Sign out of RemoteMan on this PC?",
                        "Your workspaces leave the data source list and this PC's copies of them are deleted. Data sources you set up yourself stay. Sign in again any time to get them back.",
                        "Sign out", danger: true)) return false;
                await acc.SignOutAsync(settings);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The sign-in box. Asks for the authenticator code when the account has one, and for a new password at the first sign-in.</summary>
    public static async Task<bool> SignInAsync(Window owner, AppSettings settings)
    {
        var server = Field(HostedAccount.Instance.Server ?? (settings.Gateway?.Url is { Length: > 0 } g ? Host(g) : ""),
            "Your RemoteMan server, e.g. remoteman.example.com (the same one as the gateway, if you use it)");
        var user = Field("", null);
        var pass = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 2, 0, 8) };
        var code = Field("", "The 6-digit code from your authenticator app");
        var codeLabel = Label("Authenticator code");
        codeLabel.Visibility = code.Visibility = Visibility.Collapsed;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");

        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(Label("Server")); panel.Children.Add(server);
        panel.Children.Add(Label("Username")); panel.Children.Add(user);
        panel.Children.Add(Label("Password")); panel.Children.Add(pass);
        panel.Children.Add(codeLabel); panel.Children.Add(code);
        panel.Children.Add(error);
        panel.Loaded += (_, _) => { if (server.Text.Length == 0) server.Focus(); else if (user.Text.Length == 0) user.Focus(); else pass.Focus(); };

        const string intro = "Sign in with the account the admin made for you. Every workspace you're in then shows up under Data sources, " +
                             "next to the ones you set up yourself. Optional: RemoteMan works the same without it.";
        while (true)
        {
            if (ChoiceDialog.Show(owner, "Sign in to RemoteMan", intro, ["Sign in", "Cancel"], extra: panel) != 0) return false;
            // The dialog closed: the panel moves to the next one, with what was typed kept.
            Detach(panel);
            if (server.Text.Trim().Length == 0 || user.Text.Trim().Length == 0 || pass.Password.Length == 0)
            { Show(error, "Fill in the server, your username and your password."); continue; }
            var r = await HostedAccount.Instance.SignInAsync(settings, server.Text, user.Text, pass.Password, code.Visibility == Visibility.Visible ? code.Text : null);
            if (r.NeedCode && code.Visibility != Visibility.Visible)
            {
                codeLabel.Visibility = code.Visibility = Visibility.Visible;
                Show(error, "Your account has an authenticator code. Type it, then Sign in.");
                continue;
            }
            if (!r.Ok) { Show(error, r.Error ?? "That didn't work."); pass.Password = ""; continue; }
            if (r.MustChangePassword && !await ChangePasswordAsync(owner, settings, first: true, firstPassword: pass.Password))
            {
                ChoiceDialog.Info(owner, "Signed in", "You're signed in, but your workspaces show up only after you choose your own password. " +
                                                     "Do it under More › RemoteMan account.");
                return true;
            }
            var ws = settings.DataSources.Where(d => d.Hosted is not null).Select(d => d.Name).ToList();
            ChoiceDialog.Info(owner, "Signed in",
                ws.Count == 0 ? "You aren't in a workspace yet. When the admin adds you, it shows up under Data sources within a minute."
                    : $"{(ws.Count == 1 ? "This workspace is" : $"These {ws.Count} workspaces are")} now under Data sources:\n" +
                      string.Join("\n", ws.Select(n => "  •  " + n)) +
                      "\n\nThe first time you open one, RemoteMan asks for its vault passphrase. Ask whoever runs the workspace for it.");
            return true;
        }
    }

    /// <summary>Changing the password; at the first sign-in, choosing your own instead of the admin's.</summary>
    public static async Task<bool> ChangePasswordAsync(Window owner, AppSettings settings, bool first, string? firstPassword = null)
    {
        var current = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 2, 0, 8) };
        var next = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 2, 0, 8) };
        var again = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 2, 0, 8) };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (firstPassword is null) { panel.Children.Add(Label(first ? "First password (from the admin)" : "Current password")); panel.Children.Add(current); }
        panel.Children.Add(Label("New password (at least 10 characters)")); panel.Children.Add(next);
        panel.Children.Add(Label("New password again")); panel.Children.Add(again);
        panel.Children.Add(error);
        panel.Loaded += (_, _) => (firstPassword is null ? current : next).Focus();
        var text = first ? "You signed in with a first password from the admin. Choose one only you know: it's also your password on the RemoteMan website."
            : "Your RemoteMan password, for this app and the website.";
        while (true)
        {
            if (ChoiceDialog.Show(owner, first ? "Choose your own password" : "Change password", text, ["Save", first ? "Later" : "Cancel"], extra: panel) != 0) return false;
            Detach(panel);
            if (next.Password != again.Password) { Show(error, "The two new passwords aren't the same."); continue; }
            var problem = await HostedAccount.Instance.ChangePasswordAsync(settings, firstPassword ?? current.Password, next.Password);
            if (problem is not null) { Show(error, problem); continue; }
            if (!first) ChoiceDialog.Info(owner, "Password changed", "Use the new password next time you sign in, here and on the website.");
            return true;
        }
    }

    /// <summary>"Added X; removed Y" for a check that changed the list, or null.</summary>
    public static string? Describe(HostedAccount.SyncResult r)
    {
        var parts = new List<string>();
        if (r.SignedOut) return "You were signed out of RemoteMan (the password was reset, or the PC was signed out on the website). Sign in again under Data sources.";
        if (r.Added.Count > 0) parts.Add($"New workspace{(r.Added.Count == 1 ? "" : "s")}: {string.Join(", ", r.Added)}");
        if (r.Renamed.Count > 0) parts.Add(string.Join(", ", r.Renamed.Select(x => $"{x.From} is now {x.To}")));
        if (r.Removed.Count > 0) parts.Add($"You're no longer in {string.Join(", ", r.Removed)}");
        return parts.Count == 0 ? null : string.Join(". ", parts) + ".";
    }

    private static string Host(string url) { try { return new Uri(url.Contains("://") ? url : "https://" + url).Host; } catch (UriFormatException) { return url; } }

    private static TextBox Field(string value, string? tip)
    {
        var t = new TextBox { Text = value, MinWidth = 320, Margin = new Thickness(0, 2, 0, 8) };
        if (tip is not null) t.ToolTip = tip;
        return t;
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static void Show(TextBlock error, string text) { error.Text = text; error.Visibility = Visibility.Visible; }

    private static void Detach(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case ContentControl c: c.Content = null; break;
            case Decorator d: d.Child = null; break;
        }
    }
}
