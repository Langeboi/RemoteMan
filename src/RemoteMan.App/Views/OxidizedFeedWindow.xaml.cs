using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Integrations.Oxidized;

namespace RemoteMan.App.Views;

/// <summary>
/// Every SSH entry as Oxidized gets it from the RemoteMan server, with a tick per entry for whether it goes to Oxidized,
/// and the devices that are only in Oxidized (tick to keep them in its list).
/// </summary>
public partial class OxidizedFeedWindow : Window
{
    public sealed record Choice<T>(T Value, string Name) { public override string ToString() => Name; }

    /// <summary>One line in the list: an SSH entry, or a device that's only in Oxidized.</summary>
    public sealed partial class Row : ObservableObject
    {
        private readonly OxidizedFeedWindow _w;

        public Row(OxidizedFeedWindow w, OxidizedFeedItem? item, OxidizedNode? only, bool isChecked)
        {
            _w = w;
            Item = item;
            Only = only;
            _isChecked = isChecked;
        }

        public OxidizedFeedItem? Item { get; }
        /// <summary>A device only in Oxidized (ticked = kept in its list).</summary>
        public OxidizedNode? Only { get; init; }
        public OxidizedKeptNode? Kept { get; init; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusBrush), nameof(Dot))]
        private bool _isChecked;

        partial void OnIsCheckedChanged(bool value) => _w.OnRowTicked();

        public bool CanCheck => Item is null || Item.State != OxidizedFeedState.LeftOut;
        public bool IsDevice => Item is null;
        /// <summary>Oxidized has this device now.</summary>
        public bool InOxidized => Item?.Node is not null || Only is not null;

        public string Name => Item?.Entry.Name ?? Only?.Name ?? Kept?.Name ?? "";
        public string Sub => Item is { } i
            ? (i.State != OxidizedFeedState.LeftOut && !string.Equals(i.FullName, i.Entry.Name, StringComparison.Ordinal) ? $"In Oxidized as {i.FullName}" : "")
            : "Only in Oxidized, no entry in RemoteMan";
        public bool HasSub => Sub.Length > 0;
        public string Address => Item is { Ip: { } ip } i ? (i.Port == 22 ? ip : $"{ip}:{i.Port}") : Only?.Ip ?? Kept?.Ip ?? "";
        private string Id => Item is null ? "" : OxidizedFeed.IdKey(Item.Entry.Id);
        public bool CanPickModel => Item is { State: not OxidizedFeedState.LeftOut } && _w._canChange;
        public bool ShowModelText => !CanPickModel;
        private string AutoModel => OxidizedFeed.ModelFor(Item!.Entry, _w.Options.DefaultModel) ?? "Oxidized's default";
        public string AutoLabel => $"auto: {AutoModel}";
        public IReadOnlyList<string> ModelChoices => [AutoLabel, .. OxidizedFeed.CommonModels];
        public string ModelTip => _w._models.ContainsKey(Id)
            ? "Picked here. Choose the auto line to let RemoteMan work it out again."
            : $"Worked out by RemoteMan ({AutoModel}). Pick or type another Oxidized model to change it.";

        /// <summary>The model picked for this entry, or the auto line.</summary>
        public string ModelPick
        {
            get => Item is null ? "" : _w._models.TryGetValue(Id, out var m) ? m : AutoLabel;
            set
            {
                if (Item is null) return;
                var v = (value ?? "").Trim().ToLowerInvariant();
                var auto = v.Length == 0 || v.StartsWith("auto", StringComparison.Ordinal);
                var had = _w._models.TryGetValue(Id, out var old) ? old : null;
                if (auto) _w._models.Remove(Id); else _w._models[Id] = v;
                if ((auto ? null : v) != had) _w.OnModelPicked();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelTip));
            }
        }

        public string Model => Item is { } it ? it.Model ?? (it.State == OxidizedFeedState.LeftOut ? "" : "default") : Only?.Model ?? Kept?.Model ?? "";
        public string Login => Item is null ? "Oxidized's" : Item.State == OxidizedFeedState.LeftOut ? "" : _w.HasLogin(Item) ? "Entry's" : "Oxidized's";
        public string Tip => Item is { State: OxidizedFeedState.LeftOut } i ? $"{i.Why}: not sent to Oxidized" : Login == "Oxidized's" && Item is not null
            ? "No login on this entry (or sending logins is off): Oxidized uses the username and password in its config." : "";

        public string StatusText => (Item, IsChecked) switch
        {
            ({ State: OxidizedFeedState.LeftOut } i, _) => i.Why,
            (not null, true) when InOxidized => "In Oxidized",
            (not null, true) => _w.HasSnapshot ? "Missing: added on next reload" : "Sent to Oxidized",
            (not null, false) when InOxidized => "Removed on next reload",
            (not null, false) => "Not sent",
            (null, true) => "Kept in Oxidized",
            (null, false) => "Leaves Oxidized on next reload",
        };

        public Brush StatusBrush => _w.Brush(StatusText.StartsWith("Missing", StringComparison.Ordinal) ? "Warn"
            : StatusText.StartsWith("Removed", StringComparison.Ordinal) || StatusText.StartsWith("Leaves", StringComparison.Ordinal) ? "Bad"
            : IsChecked ? "Text" : "Muted");

        public Brush Dot => _w.Brush(!CanCheck || !IsChecked ? "Faint" : InOxidized ? "Ok" : "Warn");
    }

    private enum Show { All, Missing, InOxidized, NotSent, OnlyInOxidized }

    private readonly MainViewModel _vm;
    private readonly bool _canChange;
    private bool _loading = true;
    private bool _bulk;
    private List<Row> _rows = [];
    private OxidizedFeedSync _sync = new();
    /// <summary>Entry id to the model picked in this window (saved with Save).</summary>
    private Dictionary<string, string> _models = new();

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
        AutoAddBox.IsChecked = cfg.FeedAutoAdd;
        foreach (var c in new FrameworkElement[] { GroupBox, ModelBox, TagBox, LoginsBox, AutoAddBox, TickAllButton, TickNoneButton, HeaderTick, BulkModelBox, BulkModelButton }) c.IsEnabled = _canChange;
        BulkModelBox.ItemsSource = new[] { "auto" }.Concat(OxidizedFeed.CommonModels).ToList();
        _models = new(vm.ReadOxidizedFeedSync().Models, StringComparer.OrdinalIgnoreCase);

        var shows = new List<Choice<Show>>
        {
            new(Show.All, "Everything"), new(Show.Missing, "Missing from Oxidized"), new(Show.InOxidized, "Already in Oxidized"),
            new(Show.NotSent, "Not sent"), new(Show.OnlyInOxidized, "Only in Oxidized"),
        };
        ShowBox.ItemsSource = shows;
        ShowBox.SelectedItem = shows[0];

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
        Rebuild(keepTicks: false);
    }

    internal bool HasSnapshot => _vm.OxidizedSnapshot is not null;
    internal Brush Brush(string key) => (Brush)FindResource(key);
    internal bool HasLogin(OxidizedFeedItem i) => LoginsBox.IsChecked == true && _sync.Logins.ContainsKey(OxidizedFeed.IdKey(i.Entry.Id));

    private OxidizedFeedOptions Options => new(
        (GroupBox.SelectedItem as Choice<OxidizedFeedGroup>)?.Value ?? OxidizedFeedGroup.None,
        ModelBox.Text.Trim(), TagBox.Text.Trim(), LoginsBox.IsChecked == true);

    private void Rebuild(bool keepTicks)
    {
        var ints = _vm.Integrations;
        var snap = _vm.OxidizedSnapshot;
        var store = _vm.Store;
        _sync = _vm.ReadOxidizedFeedSync();

        if (ints is null || store is null) { Heading.Text = "Open a data source first."; SaveButton.IsEnabled = false; return; }
        var feed = ints.OxidizedFeedInfo?.PublicKey is { Length: > 0 };
        Heading.Text = feed
            ? "Tick the SSH entries Oxidized should back up. The RemoteMan server gives Oxidized exactly this list."
            : ints.IsLocalSource
                ? "Oxidized's device list comes from the RemoteMan server, and this data source is a local file."
                : "This data source's server doesn't offer Oxidized a device list yet.";
        SubHeading.Text = feed
            ? (_canChange
                ? "Changes reach Oxidized when it reloads its list; RemoteMan asks it to after saving. Devices Oxidized already has keep their name, so their history carries on."
                : "Only admins can change what goes to Oxidized. " + (ints.WhyNotChangeable ?? ""))
            : "It needs the RemoteMan server in Docker, updated to this version (docker compose pull && docker compose up -d on each server).";
        SaveButton.IsEnabled = _canChange;

        // Ticks the person changed but hasn't saved survive a change of options.
        var ticks = keepTicks ? _rows.ToDictionary(r => r.Item?.Entry.Id.ToString() ?? "dev:" + r.Name, r => r.IsChecked) : null;
        var plan = OxidizedFeed.Plan(store.Entries, Options, _sync.Links, snap, _sync.Choice(AutoAddBox.IsChecked == true), _models);
        var rows = new List<Row>();
        foreach (var i in plan)
        {
            var on = i.State is OxidizedFeedState.InOxidized or OxidizedFeedState.Missing;
            if (ticks?.TryGetValue(i.Entry.Id.ToString(), out var t) == true && i.State != OxidizedFeedState.LeftOut) on = t;
            rows.Add(new Row(this, i, null, on));
        }
        foreach (var k in _sync.Keep)
        {
            var on = ticks?.TryGetValue("dev:" + k.Name, out var t) != true || t;
            rows.Add(new Row(this, null, null, on) { Kept = k, Only = snap?.Nodes.FirstOrDefault(n => string.Equals(n.Name, k.Name, StringComparison.OrdinalIgnoreCase)) });
        }
        if (snap is not null)
            foreach (var n in OxidizedFeed.OnlyInOxidized(snap, plan, _sync.Keep))
                rows.Add(new Row(this, null, n, ticks?.TryGetValue("dev:" + n.Name, out var t) == true && t));
        _rows = rows.OrderBy(r => r.IsDevice ? 1 : 0).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        OnFilter(this, null!);
    }

    internal void OnRowTicked()
    {
        if (_loading || _bulk) return;
        UpdateCounts();
        Status.Text = "Not saved yet";
    }

    private IEnumerable<Row> Shown()
    {
        var show = (ShowBox.SelectedItem as Choice<Show>)?.Value ?? Show.All;
        var q = SearchBox.Text.Trim();
        return _rows.Where(r => show switch
            {
                Show.Missing => !r.IsDevice && r.CanCheck && r.IsChecked && !r.InOxidized,
                Show.InOxidized => r.InOxidized && r.IsChecked,
                Show.NotSent => !r.IsDevice && !r.IsChecked,
                Show.OnlyInOxidized => r.IsDevice,
                _ => true,
            })
            .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Address.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var shown = Shown().ToList();
        Rows.ItemsSource = shown;
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = _rows.Count == 0 ? "There are no SSH entries yet." : "Nothing matches.";
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var entries = _rows.Where(r => !r.IsDevice).ToList();
        var sent = entries.Count(r => r.CanCheck && r.IsChecked);
        var missing = entries.Count(r => r.CanCheck && r.IsChecked && !r.InOxidized);
        var devices = _rows.Count(r => r.IsDevice);
        Counts.Text = $"{sent} of {entries.Count} SSH entries go to Oxidized" +
            (HasSnapshot ? $", {missing} of them missing now" : ", Oxidized hasn't answered yet") +
            (devices > 0 ? $". {devices} device{(devices == 1 ? "" : "s")} only in Oxidized." : ".");
        var shown = Shown().Where(r => r.CanCheck).ToList();
        HeaderTick.IsChecked = shown.Count == 0 ? false : shown.All(r => r.IsChecked) ? true : shown.Any(r => r.IsChecked) ? null : false;
    }

    private void TickShown(bool on)
    {
        _bulk = true;
        try { foreach (var r in Shown().Where(r => r.CanCheck)) r.IsChecked = on; }
        finally { _bulk = false; }
        UpdateCounts();
        Status.Text = "Not saved yet";
    }

    internal void OnModelPicked()
    {
        if (_loading || _bulk) return;
        Status.Text = "Not saved yet";
    }

    private void OnBulkModel(object sender, RoutedEventArgs e)
    {
        var v = BulkModelBox.Text.Trim();
        if (v.Length == 0) { Status.Text = "Pick a model first"; return; }
        var rows = Shown().Where(r => r.CanPickModel && r.IsChecked).ToList();
        _bulk = true;
        try { foreach (var r in rows) r.ModelPick = v; }
        finally { _bulk = false; }
        var shown = v.StartsWith("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : v.ToLowerInvariant();
        Status.Text = rows.Count == 0 ? "No ticked entries shown" : $"Model {shown} set on {rows.Count} entr{(rows.Count == 1 ? "y" : "ies")}. Not saved yet";
    }

    private void OnTickAll(object sender, RoutedEventArgs e) => TickShown(true);
    private void OnTickNone(object sender, RoutedEventArgs e) => TickShown(false);
    private void OnHeaderTick(object sender, RoutedEventArgs e) => TickShown(HeaderTick.IsChecked == true);

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Rebuild(keepTicks: true);
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
            cfg.FeedAutoAdd = AutoAddBox.IsChecked == true;
            if (System.Text.Json.JsonSerializer.Serialize(cfg) != System.Text.Json.JsonSerializer.Serialize(ints.Oxidized))
                await ints.SaveOxidizedAsync(cfg, null);

            var entries = _rows.Where(r => r.Item is { State: not OxidizedFeedState.LeftOut }).ToList();
            var kept = _rows.Where(r => r.IsDevice && r.IsChecked)
                .Select(r => r.Kept ?? new OxidizedKeptNode { Name = r.Only!.Name, Group = r.Only.Group, Ip = r.Only.Ip, Model = r.Only.Model }).ToList();
            await _vm.SaveOxidizedChoiceAsync(
                entries.Where(r => !r.IsChecked).Select(r => r.Item!.Entry.Id),
                entries.Where(r => r.IsChecked).Select(r => r.Item!.Entry.Id),
                kept,
                _models.Where(kv => _rows.Any(r => r.Item is { } i && OxidizedFeed.IdKey(i.Entry.Id) == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
            _models = new(_vm.ReadOxidizedFeedSync().Models, StringComparer.OrdinalIgnoreCase);
            var note = await _vm.SyncOxidizedFeedAsync();
            Status.Text = note is null || note == "Oxidized device list updated" ? "Saved. Oxidized picks it up on its next reload." : note;
            Rebuild(keepTicks: false);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SaveButton.IsEnabled = _canChange; }
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
            Rebuild(keepTicks: true);
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
