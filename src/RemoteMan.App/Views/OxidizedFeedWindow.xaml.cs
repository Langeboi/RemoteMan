using System.Windows;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;

namespace RemoteMan.App.Views;

/// <summary>
/// Every SSH entry as Oxidized gets it from the RemoteMan server: which ones Oxidized already has, which are missing
/// (they arrive on its next reload), which are left out, and the devices that are only in Oxidized.
/// </summary>
public partial class OxidizedFeedWindow : Window
{
    public sealed record Choice<T>(T Value, string Name) { public override string ToString() => Name; }

    public sealed record Row(OxidizedFeedItem Item, Brush Dot, string EntryName, string OxName, string Address, string Model, string Login, string Why);

    private enum Show { All, Missing, InOxidized, LeftOut }

    private readonly MainViewModel _vm;
    private readonly bool _canChange;
    private bool _loading = true;
    private List<Row> _rows = [];
    private IReadOnlyList<OxidizedNode> _only = [];

    public OxidizedFeedWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        var ints = vm.Integrations;
        var cfg = vm.OxidizedCfg;
        _canChange = ints?.CanChange == true;

        var groups = new List<Choice<OxidizedFeedGroup>> { new(OxidizedFeedGroup.None, "No group"), new(OxidizedFeedGroup.TopFolder, "Top-level folder") };
        GroupBox.ItemsSource = groups;
        GroupBox.SelectedItem = groups.First(g => g.Value == cfg.FeedOptions.Group);
        ModelBox.Text = cfg.FeedDefaultModel ?? "";
        TagBox.Text = cfg.FeedExcludeTag ?? "";
        LoginsBox.IsChecked = cfg.FeedSendLogins;
        foreach (var c in new FrameworkElement[] { GroupBox, ModelBox, TagBox, LoginsBox }) c.IsEnabled = _canChange;

        var shows = new List<Choice<Show>> { new(Show.All, "All SSH entries"), new(Show.Missing, "Missing from Oxidized"), new(Show.InOxidized, "Already in Oxidized"), new(Show.LeftOut, "Left out") };
        ShowBox.ItemsSource = shows;
        ShowBox.SelectedItem = shows[1];

        var host = ints?.FirstServerHost ?? "<RemoteMan server>";
        ConfigBox.Text = $"""
            source:
              default: http
              http:
                url: https://{host}:4433/oxidized/nodes.json
                secure: false          # the RemoteMan server's certificate is self-signed
                read_timeout: 30
                headers:
                  Authorization: "Bearer <token from the admin page>"
                map:
                  name: name
                  ip: ip
                  model: model
                  group: group
                  username: username
                  password: password
                vars_map:
                  ssh_port: ssh_port
                  enable: enable
            """;
        ReloadButton.IsEnabled = vm.OxidizedActive;
        _loading = false;
        Rebuild();
    }

    private OxidizedFeedOptions Options => new(
        (GroupBox.SelectedItem as Choice<OxidizedFeedGroup>)?.Value ?? OxidizedFeedGroup.None,
        ModelBox.Text.Trim(), TagBox.Text.Trim(), LoginsBox.IsChecked == true);

    private void Rebuild()
    {
        var ints = _vm.Integrations;
        var snap = _vm.OxidizedSnapshot;
        var sync = _vm.ReadOxidizedFeedSync();
        var store = _vm.Store;

        // Where things stand
        if (ints is null || store is null) { Heading.Text = "Open a data source first."; SaveButton.IsEnabled = false; return; }
        var feed = ints.OxidizedFeedInfo?.PublicKey is { Length: > 0 };
        Heading.Text = feed
            ? "The RemoteMan server gives Oxidized every SSH entry as its device list."
            : ints.IsLocalSource
                ? "Oxidized's device list comes from the RemoteMan server, and this data source is a local file."
                : "This data source's server doesn't offer Oxidized a device list yet.";
        SubHeading.Text = feed
            ? (_canChange
                ? "Your RemoteMan keeps the logins and names on the server up to date. Entries added later reach Oxidized on its next reload."
                : "An admin's RemoteMan keeps the logins on the server up to date. " + (ints.WhyNotChangeable ?? ""))
            : "It needs the RemoteMan server in Docker, updated to this version (docker compose pull && docker compose up -d on each server).";
        SaveButton.IsEnabled = _canChange;

        var plan = OxidizedFeed.Plan(store.Entries, Options, sync.Links, snap);
        var sendLogins = LoginsBox.IsChecked == true;
        _rows = plan.OrderBy(i => i.State switch { OxidizedFeedState.Missing => 0, OxidizedFeedState.InOxidized => 1, _ => 2 })
            .ThenBy(i => i.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new Row(i,
                (Brush)FindResource(i.State switch { OxidizedFeedState.InOxidized => "Ok", OxidizedFeedState.Missing => "Warn", _ => "Faint" }),
                i.Entry.Name,
                i.State == OxidizedFeedState.LeftOut ? "" : i.FullName,
                i.Ip is null ? "" : i.Port == 22 ? i.Ip : $"{i.Ip}:{i.Port}",
                i.Model ?? (i.State == OxidizedFeedState.LeftOut ? "" : "(default)"),
                i.State == OxidizedFeedState.LeftOut ? "" : sendLogins && sync.Logins.ContainsKey(OxidizedFeed.IdKey(i.Entry.Id)) ? "Yes" : "Default",
                i.State switch
                {
                    OxidizedFeedState.Missing when snap is not null => "Missing: added on next reload",
                    _ => i.Why,
                }))
            .ToList();

        var missing = plan.Count(i => i.State == OxidizedFeedState.Missing);
        var inOx = plan.Count(i => i.State == OxidizedFeedState.InOxidized);
        var left = plan.Count(i => i.State == OxidizedFeedState.LeftOut);
        Counts.Text = snap is null
            ? $"{plan.Count} SSH entries. Oxidized hasn't answered yet, so RemoteMan can't tell which it already has" + (_vm.OxidizedActive ? "." : " (set it up in More, Oxidized settings).")
            : $"{plan.Count} SSH entries: {inOx} already in Oxidized, {missing} missing, {left} left out.";

        // Devices only in Oxidized
        if (snap is null) { OnlyBox.Visibility = Visibility.Collapsed; }
        else
        {
            _only = OxidizedFeed.OnlyInOxidized(snap, plan, sync.Keep);
            OnlyBox.Visibility = _only.Count > 0 || sync.Keep.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            OnlyText.Text = _only.Count > 0
                ? $"{_only.Count} device{(_only.Count == 1 ? " is" : "s are")} in Oxidized but not RemoteMan ({string.Join(", ", _only.Take(4).Select(n => n.Name))}{(_only.Count > 4 ? ", …" : "")}). " +
                  "They drop out of Oxidized once it reads RemoteMan's list, unless you keep them. Kept devices use the login in Oxidized's config."
                : $"{sync.Keep.Count} device{(sync.Keep.Count == 1 ? "" : "s")} from Oxidized {(sync.Keep.Count == 1 ? "is" : "are")} kept in the list without an entry in RemoteMan.";
            KeepButton.Content = _only.Count > 0 ? "Keep them" : "Stop keeping";
            KeepButton.IsEnabled = _canChange;
        }
        OnFilter(this, null!);
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var show = (ShowBox.SelectedItem as Choice<Show>)?.Value ?? Show.All;
        Rows.ItemsSource = show switch
        {
            Show.Missing => _rows.Where(r => r.Item.State == OxidizedFeedState.Missing).ToList(),
            Show.InOxidized => _rows.Where(r => r.Item.State == OxidizedFeedState.InOxidized).ToList(),
            Show.LeftOut => _rows.Where(r => r.Item.State == OxidizedFeedState.LeftOut).ToList(),
            _ => _rows,
        };
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Rebuild();
        Status.Text = "Not saved yet";
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.Integrations is not { } ints) return;
        SaveButton.IsEnabled = false;
        Status.Text = "Saving…";
        try
        {
            var o = Options;
            var cfg = System.Text.Json.JsonSerializer.Deserialize<OxidizedConfig>(System.Text.Json.JsonSerializer.Serialize(ints.Oxidized))!;
            cfg.FeedGroup = o.Group.ToString();
            cfg.FeedDefaultModel = string.IsNullOrWhiteSpace(o.DefaultModel) ? null : o.DefaultModel;
            cfg.FeedExcludeTag = string.IsNullOrWhiteSpace(o.LeaveOutTag) ? null : o.LeaveOutTag;
            cfg.FeedSendLogins = o.SendLogins;
            await ints.SaveOxidizedAsync(cfg, null);
            var note = await _vm.SyncOxidizedFeedAsync();
            Status.Text = note ?? "Saved";
            Rebuild();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SaveButton.IsEnabled = _canChange; }
    }

    private async void OnKeep(object sender, RoutedEventArgs e)
    {
        KeepButton.IsEnabled = false;
        try
        {
            var sync = _vm.ReadOxidizedFeedSync();
            var kept = _only.Count > 0
                ? sync.Keep.Concat(_only.Select(n => new OxidizedKeptNode { Name = n.Name, Group = n.Group, Ip = n.Ip, Model = n.Model })).ToList()
                : [];
            await _vm.SaveOxidizedKeptAsync(kept);
            Status.Text = kept.Count > 0 ? $"{kept.Count} device{(kept.Count == 1 ? "" : "s")} kept in Oxidized" : "Oxidized-only devices are no longer kept";
            Rebuild();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { KeepButton.IsEnabled = _canChange; }
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        ReloadButton.IsEnabled = false;
        Status.Text = "Asking Oxidized to reload…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using (var client = await _vm.ConnectOxidizedAsync(cts.Token)) await client.ReloadAsync(cts.Token);
            await Task.Delay(1500, cts.Token);
            await _vm.RefreshOxidizedAsync(cts.Token);
            Status.Text = "Oxidized read its device list again";
            Rebuild();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { ReloadButton.IsEnabled = true; }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ConfigBox.Text); Status.Text = "Copied. Put the token from the admin page in it."; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
