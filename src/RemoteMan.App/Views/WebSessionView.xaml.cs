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
            if (ChoiceDialog.Show(owner, $"{name} isn't installed", $"Install {name} on this PC and open this page in it?", [$"Install {name}", "Cancel"]) != 0) return;
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
        catch (Exception ex) { ChoiceDialog.Show(owner, "Couldn't open the browser", ex.Message, ["OK"]); }
    }
}
