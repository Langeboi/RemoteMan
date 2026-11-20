using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Model;
using RemoteMan.Protocols.Web;

namespace RemoteMan.App.Views;

/// <summary>Embedded Chromium (WebView2) tab: certificate errors skipped, login auto-filled.</summary>
public partial class WebSessionView : UserControl
{
    private WebSessionViewModel? _vm;
    private bool _started;

    public WebSessionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not WebSessionViewModel vm) return;
        _started = true;
        _vm = vm;
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { vm.Endpoint.Host };
        if (Uri.TryCreate(vm.Settings.Url, UriKind.Absolute, out var entryUri)) hosts.Add(entryUri.Host);
        foreach (var ep in vm.Entry.Endpoints) hosts.Add(ep.Host);
        var sso = vm.Credential.UseWindowsLogin;
        await Web.EnsureCoreWebView2Async(sso ? await WebViewEnvironment.GetSsoAsync(hosts) : await WebViewEnvironment.GetAsync());
        var core = Web.CoreWebView2;

        core.ServerCertificateErrorDetected += (_, a) =>
        {
            if (vm.Settings.IgnoreCertificateErrors) a.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };
        // Certificate credential: offer that client certificate when a site asks for one. WebView2 can
        // only present certificates from the Windows store, so it is added to the user's store once.
        if (vm.Credential.Certificate is { } pfx)
        {
            try
            {
                var thumb = ClientCertificates.EnsureInUserStore(pfx, vm.Credential.Password);
                core.ClientCertificateRequested += (_, a) =>
                {
                    var match = a.MutuallyTrustedCertificates.FirstOrDefault(c =>
                        string.Equals(c.ToX509Certificate2().Thumbprint, thumb, StringComparison.OrdinalIgnoreCase));
                    if (match is null) return;
                    a.SelectedCertificate = match;
                    a.Handled = true;
                };
            }
            catch (Exception ex) { vm.StatusText = $"Client certificate not usable: {ex.Message}"; }
        }

        core.BasicAuthenticationRequested += (_, a) =>
        {
            if (!sso && vm.Credential.UserName is { } u) { a.Response.UserName = u; a.Response.Password = vm.Credential.Password ?? ""; }
        };
        core.SourceChanged += (_, _) => Address.Text = core.Source;
        core.DocumentTitleChanged += (_, _) => vm.StatusText = core.DocumentTitle;
        // Auto-login runs in every page of this tab, limited to the entry's own hosts.
        if (!sso && vm.Settings.AutoFill && vm.Credential.Password is not null)
        {
            var s = vm.Settings;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoFillScript.Build(vm.Credential.UserName, vm.Credential.Password,
                new AutoLoginOptions(hosts, s.AutoSubmit, Blank(s.UserFieldSelector), Blank(s.PasswordFieldSelector), Blank(s.SubmitSelector))));
        }
        core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };

        // Copy on select: the page reports selections, the host writes the Windows clipboard.
        if (vm.Settings.CopyOnSelect)
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CopyOnSelectScript);
            core.WebMessageReceived += (_, a) =>
            {
                try { if (a.TryGetWebMessageAsString() is { Length: > 0 } text) Clipboard.SetDataObject(text, true); }
                catch (Exception) { /* non-string message or clipboard busy */ }
            };
        }
        vm.Disposed += (_, _) => Web.Dispose();

        // A page that doesn't load at all gets a notice saying why (once per tab).
        var reported = false;
        core.NavigationCompleted += (_, a) =>
        {
            if (a.IsSuccess || reported || a.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            reported = true;
            var why = a.WebErrorStatus switch
            {
                CoreWebView2WebErrorStatus.HostNameNotResolved => new RemoteMan.Core.Networking.ConnectionProblem("Name not found", "DNS couldn't find the site.", "Check the address, or your DNS / VPN.", a.WebErrorStatus.ToString()),
                CoreWebView2WebErrorStatus.Timeout or CoreWebView2WebErrorStatus.ServerUnreachable or CoreWebView2WebErrorStatus.CannotConnect
                    => new("Host didn't answer", "The site didn't respond.", "The server may be down, or a firewall or VPN is blocking it.", a.WebErrorStatus.ToString()),
                CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
                    or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors or CoreWebView2WebErrorStatus.CertificateRevoked or CoreWebView2WebErrorStatus.CertificateIsInvalid
                    => new("Certificate problem", "The site's certificate isn't trusted.", "Turn on \"Ignore certificate errors\" on the entry if you trust this site.", a.WebErrorStatus.ToString()),
                _ => new("Page didn't load", $"The page failed to load ({a.WebErrorStatus}).", null, a.WebErrorStatus.ToString()),
            };
            vm.ReportProblem(why);
        };

        // Proxmox console: sign in through the API and hand the ticket to the page as its cookie.
        if (vm.Entry.Kind == RemoteMan.Core.Model.EntryKind.Proxmox)
        {
            try
            {
                vm.StatusText = "Signing in to Proxmox…";
                var ticket = await RemoteMan.Protocols.Proxmox.ProxmoxConsole.GetTicketAsync(vm.Endpoint, vm.Credential, vm.Settings.IgnoreCertificateErrors);
                var cookie = core.CookieManager.CreateCookie("PVEAuthCookie", Uri.EscapeDataString(ticket), vm.Endpoint.Host, "/");
                cookie.IsSecure = true;
                core.CookieManager.AddOrUpdateCookie(cookie);
                vm.StatusText = "";
            }
            catch (Exception ex)
            {
                vm.StatusText = ex.Message;
                vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromException(ex, RemoteMan.Core.Model.EntryKind.Proxmox));
            }
        }

        Address.Text = vm.Url;
        core.Navigate(vm.Url);
    }

    private const string CopyOnSelectScript = """
        document.addEventListener('mouseup', () => {
          setTimeout(() => {
            const a = document.activeElement;
            let t = '';
            if (a && (a.tagName === 'TEXTAREA' || (a.tagName === 'INPUT' && a.type !== 'password')) && a.selectionStart !== a.selectionEnd)
              t = a.value.substring(a.selectionStart, a.selectionEnd);
            else t = String(window.getSelection() || '');
            if (t.trim()) window.chrome.webview.postMessage(t);
          }, 0);
        }, true);
        """;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void OnBack(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    private void OnReload(object sender, RoutedEventArgs e) => Web.Reload();

    private void OnAddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Web.CoreWebView2 is null) return;
        var url = Address.Text.Contains("://") ? Address.Text : "https://" + Address.Text;
        Web.CoreWebView2.Navigate(url);
    }

    private async void OnOpenIn(object sender, SelectionChangedEventArgs e)
    {
        if (OpenIn.SelectedItem is not ComboBoxItem { Tag: string tag } || _vm is null) return;
        OpenIn.SelectedIndex = 0;
        var url = Web.Source?.ToString() ?? _vm.Url;
        var kind = Enum.Parse<BrowserKind>(tag);
        var owner = Window.GetWindow(this);
        try { ExternalBrowser.Open(url, kind, _vm.Settings.IgnoreCertificateErrors); }
        catch (BrowserNotInstalledException)
        {
            var name = BrowserInstaller.DisplayName(kind);
            if (ChoiceDialog.Show(owner, $"{name} isn't installed", $"Install {name} on this PC and open this page in it?", [$"Install {name}", "Cancel"], DialogIcon.Question) != 0) return;
            _vm.StatusText = $"Installing {name}…";
            var outcome = await BrowserInstaller.InstallAsync(kind);
            _vm.StatusText = outcome switch
            {
                BrowserInstaller.Result.Installed => $"{name} installed",
                BrowserInstaller.Result.DownloadPageOpened => $"Opened the {name} download page",
                _ => $"Couldn't install {name}",
            };
            if (outcome == BrowserInstaller.Result.Installed)
                try { ExternalBrowser.Open(url, kind, _vm.Settings.IgnoreCertificateErrors); } catch (Exception ex) { _vm.StatusText = ex.Message; }
        }
        catch (Exception ex) { ChoiceDialog.Error(owner, "Couldn't open the browser", ex.Message); }
    }
}
