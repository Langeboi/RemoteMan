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
    private bool _filled;

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
        await Web.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
        var core = Web.CoreWebView2;

        core.ServerCertificateErrorDetected += (_, a) =>
        {
            if (vm.Settings.IgnoreCertificateErrors) a.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };
        core.BasicAuthenticationRequested += (_, a) =>
        {
            if (vm.Credential.UserName is { } u) { a.Response.UserName = u; a.Response.Password = vm.Credential.Password ?? ""; }
        };
        core.SourceChanged += (_, _) => Address.Text = core.Source;
        core.DocumentTitleChanged += (_, _) => vm.StatusText = core.DocumentTitle;
        core.NavigationCompleted += async (_, _) =>
        {
            if (_filled || !vm.Settings.AutoFill || vm.Credential.Password is null) return;
            _filled = true;
            await core.ExecuteScriptAsync(AutoFillScript.Build(vm.Credential.UserName, vm.Credential.Password));
        };
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

    private void OnBack(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    private void OnReload(object sender, RoutedEventArgs e) => Web.Reload();

    private void OnAddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Web.CoreWebView2 is null) return;
        var url = Address.Text.Contains("://") ? Address.Text : "https://" + Address.Text;
        Web.CoreWebView2.Navigate(url);
    }

    private void OnOpenIn(object sender, SelectionChangedEventArgs e)
    {
        if (OpenIn.SelectedItem is not ComboBoxItem { Tag: string tag } || _vm is null) return;
        try { ExternalBrowser.Open(Web.Source?.ToString() ?? _vm.Url, Enum.Parse<BrowserKind>(tag), _vm.Settings.IgnoreCertificateErrors); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "RemoteMan"); }
        OpenIn.SelectedIndex = 0;
    }
}
