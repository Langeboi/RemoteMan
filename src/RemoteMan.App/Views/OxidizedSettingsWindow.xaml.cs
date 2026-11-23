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

    private readonly IntegrationSettings? _shared;
    private readonly EntryStore? _store;
    private readonly ISecretProtector? _protector;

    /// <param name="shared">The open data source's settings, where these are saved for everyone on it.</param>
    public OxidizedSettingsWindow(IntegrationSettings? shared, EntryStore? store, ISecretProtector? protector)
    {
        InitializeComponent();
        _shared = shared;
        _store = store;
        _protector = protector;
        var o = shared?.Oxidized ?? new OxidizedConfig();
        ShowSharedNote();

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
            ProtectedPassword = user.Length == 0 ? null : newProtectedPassword ?? _shared?.Oxidized.ProtectedPassword,
            JumpHostEntryId = (ViaBox.SelectedItem as Choice<Guid?>)?.Value,
            IgnoreCertificateErrors = IgnoreCertBox.IsChecked == true,
            PollMinutes = (PollBox.SelectedItem as Choice<int>)?.Value ?? 5,
            MatchByAddress = ByAddressBox.IsChecked == true,
            MatchByName = ByNameBox.IsChecked == true,
            IgnoreBracketSuffix = BracketBox.IsChecked == true,
            // Set in the Oxidized device list window; kept as they are here.
            FeedGroup = _shared?.Oxidized.FeedGroup ?? nameof(OxidizedFeedGroup.None),
            FeedDefaultModel = _shared?.Oxidized.FeedDefaultModel,
            FeedExcludeTag = _shared?.Oxidized.FeedExcludeTag ?? OxidizedFeed.DefaultLeaveOutTag,
            FeedSendLogins = _shared?.Oxidized.FeedSendLogins ?? true,
        };
    }

    private string? Password => PasswordBox.Password is { Length: > 0 } p ? p : _shared?.OxidizedPassword;

    private void ShowSharedNote()
    {
        if (_shared is null) { SharedNote.Text = "Open a data source first: Oxidized settings are saved in it."; SaveButton.IsEnabled = false; }
        else
        {
            var note = _shared.Note(_shared.OxidizedOrigin, _shared.OxidizedRow);
            if (_shared.OxidizedProblem is { } p) note += " " + p;
            if (_shared.WhyNotChangeable is { } why) { note += " " + why; SaveButton.IsEnabled = false; }
            SharedNote.Text = note;
        }
        SharedNoteBox.Visibility = Visibility.Visible;
    }

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

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_shared is null) return;
        var typed = PasswordBox.Password;
        var cfg = Read(null);
        if (cfg.Enabled && cfg.Url.Length > 0)
        {
            try { OxidizedClient.BaseUri(cfg.Url); }
            catch (OxidizedApiException ex) { Show(ex.Message, false); return; }
        }
        SaveButton.IsEnabled = false;
        Show("Saving…", null);
        try
        {
            await _shared.SaveOxidizedAsync(cfg, typed.Length > 0 ? typed : null);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Show(ex.Message, false);
            SaveButton.IsEnabled = true;
        }
    }
}
