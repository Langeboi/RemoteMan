using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RemoteMan.App.Services;
using RemoteMan.Protocols.Web;

namespace RemoteMan.App.Views;

/// <summary>
/// "Pick on page": opens the entry's login page and lets you click the user name box, the password
/// box and the login button in turn. Clicks are caught before the page sees them, so nothing is
/// submitted. What the automatic detection would pick is shown at the top and can be taken as is.
/// </summary>
public sealed class FieldPickerWindow : Window
{
    public sealed record Fields(string User, string Password, string Submit);

    private static readonly string[] Steps = ["Click the user name box", "Click the password box", "Click the login button"];
    private readonly string[] _picked = ["", "", ""];
    private int _step;
    private Fields? _guess;
    private readonly TextBlock _prompt = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _found = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _useGuess = new() { Content = "Use these", Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _skip = new() { Content = "Skip", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Leave this one to automatic detection" };
    private readonly Button _back = new() { Content = "Back", Margin = new Thickness(8, 0, 0, 0) };
    private readonly WebView2 _web = new();
    private readonly string _url;
    private readonly bool _ignoreCert;

    public Fields? Result { get; private set; }

    private FieldPickerWindow(Window owner, string url, bool ignoreCert)
    {
        _url = url;
        _ignoreCert = ignoreCert;
        Owner = owner;
        Title = "Pick the login fields";
        Width = 1000; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "RemoteManWindow");
        _found.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var top = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(_useGuess);
        buttons.Children.Add(_back);
        buttons.Children.Add(_skip);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        var texts = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(_prompt);
        line.Children.Add(_status);
        texts.Children.Add(line);
        texts.Children.Add(_found);
        top.Children.Add(texts);

        var bar = new Border { Child = top, BorderThickness = new Thickness(0, 0, 0, 1) };
        bar.SetResourceReference(Border.BackgroundProperty, "Panel");
        bar.SetResourceReference(Border.BorderBrushProperty, "Border");
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_web);
        Content = root;

        _useGuess.Click += (_, _) => { if (_guess is not null) Finish(_guess); };
        _skip.Click += (_, _) => Next("");
        _back.Click += (_, _) => { if (_step > 0) { _step--; _picked[_step] = ""; Update(); } };
        cancel.Click += (_, _) => Close();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _web.Dispose();
        Update();
    }

    /// <summary>Returns the picked selectors (an empty one means "detect automatically"), or null when cancelled.</summary>
    public static Fields? Pick(Window owner, string url, bool ignoreCertificateErrors)
    {
        var w = new FieldPickerWindow(owner, url, ignoreCertificateErrors);
        w.ShowDialog();
        return w.Result;
    }

    private async Task StartAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
            var core = _web.CoreWebView2;
            // Self-signed firewalls and appliances: allow the certificate before the page (and its form) loads.
            core.ServerCertificateErrorDetected += (_, a) => { if (_ignoreCert) a.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow; };
            core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoFillScript.PickerScript);
            core.WebMessageReceived += (_, a) => OnMessage(a);
            core.NavigationCompleted += (_, a) =>
            {
                _status.Text = a.IsSuccess ? "" : $"The page didn't load ({a.WebErrorStatus}).";
            };
            _status.Text = "Loading…";
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            _status.Text = "The browser couldn't start: " + ex.Message;
        }
    }

    private void OnMessage(CoreWebView2WebMessageReceivedEventArgs a)
    {
        try
        {
            using var doc = JsonDocument.Parse(a.TryGetWebMessageAsString());
            var r = doc.RootElement;
            if (r.TryGetProperty("pick", out var pick)) Next(pick.GetString() ?? "");
            else if (r.TryGetProperty("guess", out var g))
            {
                _guess = new Fields(g.GetProperty("user").GetString() ?? "", g.GetProperty("password").GetString() ?? "", g.GetProperty("submit").GetString() ?? "");
                Update();
            }
        }
        catch (Exception) { /* a page posting its own messages */ }
    }

    private void Next(string selector)
    {
        _picked[_step] = selector;
        if (_step == Steps.Length - 1) { Finish(new Fields(_picked[0], _picked[1], _picked[2])); return; }
        _step++;
        Update();
    }

    private void Finish(Fields f)
    {
        Result = f;
        Close();
    }

    private void Update()
    {
        _prompt.Text = $"{_step + 1} of 3: {Steps[_step]}";
        _skip.Content = _step == Steps.Length - 1 ? "Skip and finish" : "Skip";
        _back.IsEnabled = _step > 0;
        var chosen = string.Join("   ", new[] { ("User", 0), ("Password", 1), ("Button", 2) }
            .Where(x => x.Item2 < _step).Select(x => $"{x.Item1}: {(_picked[x.Item2].Length > 0 ? _picked[x.Item2] : "automatic")}"));
        if (_guess is not null)
        {
            _useGuess.Visibility = Visibility.Visible;
            _found.Text = (chosen.Length > 0 ? chosen + "\n" : "") +
                $"Detected: user {Or(_guess.User)}, password {Or(_guess.Password)}, button {Or(_guess.Submit)}. Use these, or click the fields yourself.";
        }
        else _found.Text = chosen.Length > 0 ? chosen : "Clicks on the page are caught here, so nothing is sent to the site.";
    }

    private static string Or(string s) => s.Length > 0 ? s : "(none)";
}
