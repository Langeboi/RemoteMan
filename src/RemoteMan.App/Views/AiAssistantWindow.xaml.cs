using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Ai;

namespace RemoteMan.App.Views;

/// <summary>
/// The pop-up chat box. It is a small owned window (not an overlay) so it can sit on top of RDP
/// and WebView2 tabs, which WPF cannot draw over. Hiding it is "standby": nothing runs or is sent.
/// </summary>
public partial class AiAssistantWindow : Window
{
    private readonly AiAssistantViewModel _vm;
    private FrameworkElement? _anchor;
    private bool _stickToBottom = true;
    private WebView2? _accountView;

    public AiAssistantWindow(AiAssistantViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.ReadKeyInput = () => KeyBox.Password;
        vm.PropertyChanged += OnVmChanged;
        vm.SignedOut += OnSignedOut;
        PreviewKeyDown += (_, e) =>
        {
            var toggle = e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
            if (toggle || (e.Key == Key.Escape && !_vm.ProviderMenuOpen)) { HideBox(); e.Handled = true; }
        };
        SourceInitialized += (_, _) => ApplyCorners();
    }

    /// <summary>Shows the box just under <paramref name="anchor"/> (the AI button), right-aligned to it.</summary>
    public void ShowUnder(FrameworkElement anchor)
    {
        _anchor = anchor;
        _vm.RefreshSession();
        Reposition();
        if (!IsVisible) Show();
        Activate();
        if (_vm.ShowConversation) Composer.Focus();
        else if (_vm.ShowAccountView) _ = EnsureAccountViewAsync();
    }

    public void Reposition()
    {
        if (_anchor is null || PresentationSource.FromVisual(_anchor) is not { CompositionTarget: { } target }) return;
        var fromDevice = target.TransformFromDevice;
        var bottomRight = fromDevice.Transform(_anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight)));
        Left = bottomRight.X - Width;
        Top = bottomRight.Y + 4;
    }

    public void HideBox()
    {
        _vm.ProviderMenuOpen = false;
        Hide();
        Owner?.Activate();
    }

    private void OnHide(object sender, RoutedEventArgs e) => HideBox();

    private void OnProviderMenuClosed(object? sender, EventArgs e) => _vm.ProviderMenuOpen = false;

    private void OnKeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.ConnectCommand.CanExecute(null)) { _vm.ConnectCommand.Execute(null); e.Handled = true; }
    }

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Follow the reply while it streams, unless the user scrolled up to read.
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0) _stickToBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
        else if (_stickToBottom) Scroller.ScrollToEnd();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AiAssistantViewModel.Theme):
                ApplyCorners();
                break;
            case nameof(AiAssistantViewModel.Page):
                if (_vm.IsAuthPage) KeyBox.Clear();
                if (_vm.ShowAccountView && IsVisible) _ = EnsureAccountViewAsync();
                break;
            case nameof(AiAssistantViewModel.AccountSite):
                if (_vm.ShowAccountView && IsVisible) _ = EnsureAccountViewAsync();
                break;
        }
    }

    /// <summary>Account sign-in: the provider's own site, in a profile kept apart from web entries.</summary>
    private async Task EnsureAccountViewAsync()
    {
        if (_vm.AccountSite is not { } site) return;
        if (_accountView is null)
        {
            _accountView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x20, 0x22, 0x26) };
            AccountHost.Content = _accountView;
            await _accountView.EnsureCoreWebView2Async(await WebViewEnvironment.GetAiAsync());
            _accountView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            // Sign-in pop-ups (Google, Apple, Microsoft) open in the same view.
            _accountView.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; _accountView.CoreWebView2.Navigate(a.Uri); };
        }
        if (_accountView.Source?.Host != new Uri(site).Host) _accountView.CoreWebView2.Navigate(site);
    }

    private async void OnSignedOut(AiProviderConfig cfg)
    {
        if (cfg.AuthMode != AiAuthMode.Account || _accountView?.CoreWebView2 is not { } core || cfg.Info.AccountSite is not { } site) return;
        // Forget that provider's cookies so "Sign out" means signed out.
        var host = new Uri(site).Host;
        var domain = string.Join('.', host.Split('.').TakeLast(2));
        foreach (var c in await core.CookieManager.GetCookiesAsync(null))
            if (c.Domain.TrimStart('.').EndsWith(domain, StringComparison.OrdinalIgnoreCase)) core.CookieManager.DeleteCookie(c);
        core.Navigate("about:blank");
    }

    // Windows 11 draws the rounded corners (and the shadow) for us; RemoteMan's own theme stays square.
    private void ApplyCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var pref = _vm.Theme.Rounded ? 2 /* round */ : 1 /* do not round */;
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
