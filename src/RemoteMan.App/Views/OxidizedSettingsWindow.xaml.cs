using System.Windows;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

public partial class OxidizedSettingsWindow : Window
{
    public sealed record Choice<T>(T Value, string Name) { public override string ToString() => Name; }

    private readonly AppSettings _settings;
    private readonly EntryStore? _store;
    private readonly ISecretProtector? _protector;

    public OxidizedSettingsWindow(AppSettings settings, EntryStore? store, ISecretProtector? protector)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _protector = protector;
        var o = settings.Oxidized;

        EnabledBox.IsChecked = o.Enabled || string.IsNullOrEmpty(o.Url);
        UrlBox.Text = o.Url;
        UserBox.Text = o.UserName ?? "";
        if (!string.IsNullOrEmpty(o.ProtectedPassword))
            PasswordHint.Text = "A password is saved. Leave this empty to keep it, or type a new one. Clear the username to stop using a login.";

        var via = new List<Choice<Guid?>> { new(null, "Direct") };
        if (store is not null)
            via.AddRange(store.Entries.Where(e => e.Kind == EntryKind.Ssh).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new Choice<Guid?>(e.Id, $"SSH jump host: {e.Name}" + (e.Endpoints.Count > 0 ? $" ({e.Endpoints[0]})" : ""))));
        ViaBox.ItemsSource = via;
        ViaBox.SelectedItem = via.FirstOrDefault(v => v.Value == o.JumpHostEntryId) ?? via[0];

        IgnoreCertBox.IsChecked = o.IgnoreCertificateErrors;
        var polls = new[] { 1, 2, 5, 15, 30, 60 }.Select(m => new Choice<int>(m, m == 1 ? "1 minute" : m == 60 ? "1 hour" : $"{m} minutes")).ToList();
        PollBox.ItemsSource = polls;
        PollBox.SelectedItem = polls.FirstOrDefault(p => p.Value == o.PollMinutes) ?? polls[2];
        ByAddressBox.IsChecked = o.MatchByAddress;
        ByNameBox.IsChecked = o.MatchByName;
        BracketBox.IsChecked = o.IgnoreBracketSuffix;
    }

    private OxidizedConfig Read(string? newProtectedPassword)
    {
        var user = UserBox.Text.Trim();
        return new()
        {
            Enabled = EnabledBox.IsChecked == true,
            Url = UrlBox.Text.Trim(),
            UserName = user.Length == 0 ? null : user,
            ProtectedPassword = user.Length == 0 ? null : newProtectedPassword ?? _settings.Oxidized.ProtectedPassword,
            JumpHostEntryId = (ViaBox.SelectedItem as Choice<Guid?>)?.Value,
            IgnoreCertificateErrors = IgnoreCertBox.IsChecked == true,
            PollMinutes = (PollBox.SelectedItem as Choice<int>)?.Value ?? 5,
            MatchByAddress = ByAddressBox.IsChecked == true,
            MatchByName = ByNameBox.IsChecked == true,
            IgnoreBracketSuffix = BracketBox.IsChecked == true,
        };
    }

    private string? Password => PasswordBox.Password is { Length: > 0 } p ? p : OxidizedConnector.LoadPassword(_settings.Oxidized);

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var cfg = Read(null);
        if (cfg.Url.Length == 0) { Show("Enter the Oxidized URL first.", false); return; }
        TestButton.IsEnabled = false;
        Show("Connecting…", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var client = await OxidizedConnector.ConnectAsync(cfg, cfg.UserName is null ? null : Password, _store, _protector, cts.Token);
            var nodes = await client.GetNodesAsync(cts.Token);
            var matcher = new OxidizedMatcher(nodes, cfg.MatchOptions);
            var matched = _store?.Entries.Count(x => x.IsConnectable && matcher.Match(x) is not null) ?? 0;
            var failing = nodes.Count(n => n.IsFailing);
            var git = "";
            if (nodes.FirstOrDefault(n => n.IsOk) is { } sample)
                git = (await client.GetVersionsAsync(sample, cts.Token)).Count > 0 ? " Version history is available." : " No version history (Oxidized's git output is off), only the latest config.";
            Show($"Connected. {nodes.Count} device{(nodes.Count == 1 ? "" : "s")}, {failing} failing, {matched} match entries.{git}", true);
        }
        catch (Exception ex) { Show(ex.Message, false); }
        finally { TestButton.IsEnabled = true; }
    }

    private void Show(string text, bool? ok)
    {
        TestResult.Text = text;
        TestResult.Foreground = ok switch
        {
            true => (Brush)FindResource("Ok"),
            false => (Brush)FindResource("Bad"),
            _ => (Brush)FindResource("Faint"),
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var typed = PasswordBox.Password;
        var cfg = Read(typed.Length > 0 ? OxidizedConnector.ProtectPassword(typed) : null);
        if (cfg.Enabled && cfg.Url.Length > 0)
        {
            try { OxidizedClient.BaseUri(cfg.Url); }
            catch (OxidizedApiException ex) { Show(ex.Message, false); return; }
        }
        _settings.Oxidized = cfg;
        _settings.Save();
        DialogResult = true;
    }
}
