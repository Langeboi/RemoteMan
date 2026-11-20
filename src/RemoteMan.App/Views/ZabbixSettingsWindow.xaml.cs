using System.Windows;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

public partial class ZabbixSettingsWindow : Window
{
    private sealed record Choice<T>(T Value, string Name);

    private readonly AppSettings _settings;
    private readonly EntryStore? _store;
    private readonly ISecretProtector? _protector;

    public ZabbixSettingsWindow(AppSettings settings, EntryStore? store, ISecretProtector? protector)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _protector = protector;
        var z = settings.Zabbix;

        EnabledBox.IsChecked = z.Enabled || string.IsNullOrEmpty(z.Url);
        UrlBox.Text = z.Url;
        if (!string.IsNullOrEmpty(z.ProtectedToken))
            TokenHint.Text = "A token is saved. Leave this empty to keep it, or paste a new one. " + TokenHint.Text;

        var via = new List<Choice<Guid?>> { new(null, "Direct (or your NAT address)") };
        if (store is not null)
            via.AddRange(store.Entries.Where(e => e.Kind == EntryKind.Ssh).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new Choice<Guid?>(e.Id, $"SSH jump host: {e.Name}" + (e.Endpoints.Count > 0 ? $" ({e.Endpoints[0]})" : ""))));
        ViaBox.ItemsSource = via;
        ViaBox.SelectedItem = via.FirstOrDefault(v => v.Value == z.JumpHostEntryId) ?? via[0];

        IgnoreCertBox.IsChecked = z.IgnoreCertificateErrors;
        var polls = new[] { 15, 30, 60, 120, 300 }.Select(s => new Choice<int>(s, s < 60 ? $"{s} seconds" : s == 60 ? "1 minute" : $"{s / 60} minutes")).ToList();
        PollBox.ItemsSource = polls;
        PollBox.SelectedItem = polls.FirstOrDefault(p => p.Value == z.PollSeconds) ?? polls[1];
        var sev = new[]
        {
            new Choice<ZabbixSeverity>(ZabbixSeverity.Information, "Information and up"),
            new Choice<ZabbixSeverity>(ZabbixSeverity.Warning, "Warning and up"),
            new Choice<ZabbixSeverity>(ZabbixSeverity.Average, "Average and up"),
            new Choice<ZabbixSeverity>(ZabbixSeverity.High, "High and up"),
        };
        SeverityBox.ItemsSource = sev;
        SeverityBox.SelectedItem = sev.FirstOrDefault(s => s.Value == z.MinimumSeverity) ?? sev[1];
        ByAddressBox.IsChecked = z.MatchByAddress;
        ByNameBox.IsChecked = z.MatchByName;
        BracketBox.IsChecked = z.IgnoreBracketSuffix;
    }

    private ZabbixConfig Read(string? newProtectedToken) => new()
    {
        Enabled = EnabledBox.IsChecked == true,
        Url = UrlBox.Text.Trim(),
        ProtectedToken = newProtectedToken ?? _settings.Zabbix.ProtectedToken,
        JumpHostEntryId = (ViaBox.SelectedItem as Choice<Guid?>)?.Value,
        IgnoreCertificateErrors = IgnoreCertBox.IsChecked == true,
        PollSeconds = (PollBox.SelectedItem as Choice<int>)?.Value ?? 30,
        MinimumSeverity = (SeverityBox.SelectedItem as Choice<ZabbixSeverity>)?.Value ?? ZabbixSeverity.Warning,
        MatchByAddress = ByAddressBox.IsChecked == true,
        MatchByName = ByNameBox.IsChecked == true,
        IgnoreBracketSuffix = BracketBox.IsChecked == true,
        ShowOk = _settings.Zabbix.ShowOk,
    };

    private string? Token => TokenBox.Password.Trim() is { Length: > 0 } t ? t : ZabbixConnector.LoadToken(_settings.Zabbix);

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var cfg = Read(null);
        if (cfg.Url.Length == 0 || Token is not { } token) { Show("Enter the Zabbix URL and an API token first.", false); return; }
        TestButton.IsEnabled = false;
        Show("Connecting…", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = await ZabbixConnector.ConnectAsync(cfg, token, _store, _protector, cts.Token);
            var hosts = await client.GetHostsAsync(cts.Token);
            var matcher = new ZabbixMatcher(hosts, cfg.MatchOptions);
            var matched = _store?.Entries.Count(x => x.IsConnectable && matcher.Match(x) is not null) ?? 0;
            var via = cfg.JumpHostEntryId is not null ? $" through {(ViaBox.SelectedItem as Choice<Guid?>)?.Name.Replace("SSH jump host: ", "")}" : "";
            Show($"Connected to Zabbix {client.Version}{via}. {hosts.Count} hosts, {matched} match entries.", true);
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
        var typed = TokenBox.Password.Trim();
        var cfg = Read(typed.Length > 0 ? ZabbixConnector.ProtectToken(typed) : null);
        if (cfg.Enabled && cfg.Url.Length > 0)
        {
            try { ZabbixClient.ApiEndpoint(cfg.Url); }
            catch (ZabbixApiException ex) { Show(ex.Message, false); return; }
            if (string.IsNullOrEmpty(cfg.ProtectedToken)) { Show("Paste an API token to turn this on.", false); return; }
        }
        _settings.Zabbix = cfg;
        _settings.Save();
        DialogResult = true;
    }
}
