using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Integrations.Oxidized;

namespace RemoteMan.App.Views;

/// <summary>A saved config in the version list.</summary>
public sealed partial class BackupVersionRow : ObservableObject
{
    public BackupVersionRow(OxidizedVersion? version, DateTimeOffset date, bool isLatest)
    {
        Version = version;
        Date = date;
        IsLatest = isLatest;
    }

    /// <summary>Null for "the latest config" when Oxidized keeps no history.</summary>
    public OxidizedVersion? Version { get; }
    public DateTimeOffset Date { get; }
    public bool IsLatest { get; }
    public string Key => Version?.Oid ?? "latest";

    public string DateText => Date.ToLocalTime().ToString("d MMM HH:mm");
    public string AgeText => OxidizedSnapshot.Age(Date, DateTimeOffset.UtcNow);
    public string CompareName => (IsLatest ? "latest, " : "") + Date.ToLocalTime().ToString("d MMM yyyy HH:mm");
    public string Tip => Version is { } v
        ? $"{Date.ToLocalTime():dddd d MMMM yyyy HH:mm:ss}\nVersion {v.Number}, {v.Oid[..Math.Min(10, v.Oid.Length)]}" + (v.Message is { Length: > 0 } m ? $"\n{m}" : "")
        : $"{Date.ToLocalTime():dddd d MMMM yyyy HH:mm}";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AddedText))] private int? _added;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RemovedText))] private int? _removed;
    /// <summary>"first" on the oldest version.</summary>
    [ObservableProperty] private string _noteText = "";
    /// <summary>The version the selected one is being compared against (orange bar).</summary>
    [ObservableProperty] private bool _isCompare;

    public string AddedText => Added is { } a ? $"+{a}" : "";
    public string RemovedText => Removed is { } r ? $"−{r}" : "";
}

/// <summary>One line in the config view. Kind: "" (unchanged), add, del or gap.</summary>
public sealed partial class ConfigLineRow(string no, string mark, string text, string kind) : ObservableObject
{
    public string No { get; } = no;
    public string Mark { get; } = mark;
    public string Text { get; } = text;
    public string Kind { get; } = kind;
    public bool IsComment => Kind != "gap" && (Text.StartsWith('!') || Text.StartsWith('#'));
    [ObservableProperty] private bool _hit;
}

/// <summary>Config backups tab: a device's configs from Oxidized, every version, and what changed between them.</summary>
public partial class ConfigBackupsView : UserControl
{
    private const int CountedVersions = 40;
    private const int Context = 3;

    private ConfigBackupsSessionViewModel? _vm;
    private OxidizedClient? _client;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, string[]> _texts = new();
    private List<BackupVersionRow> _rows = new();
    private List<ConfigLineRow> _lines = new();
    private bool _started, _quietCompare;
    private int _render;

    public ConfigBackupsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not ConfigBackupsSessionViewModel vm) return;
        _started = true;
        _vm = vm;
        vm.Disposed += (_, _) => { _cts?.Cancel(); _client?.Dispose(); _client = null; };
        await LoadAsync();
    }

    // ---------------- loading ----------------

    private async Task LoadAsync()
    {
        if (_vm is not { } vm) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        ShowNode(vm.Node);
        Overlay.Text = "Loading from Oxidized…";
        Overlay.Visibility = Visibility.Visible;
        vm.StatusText = $"Config backups of {vm.Node.Name}: loading…";
        try
        {
            _client ??= await vm.Connect(ct);
            var nodes = await _client.GetNodesAsync(ct);
            if (nodes.FirstOrDefault(n => n.FullName.Equals(vm.Node.FullName, StringComparison.OrdinalIgnoreCase)) is { } fresh) vm.Node = fresh;
            else throw new OxidizedApiException($"Oxidized no longer has a device called {vm.Node.FullName}.");
            ShowNode(vm.Node);

            var versions = await _client.GetVersionsAsync(vm.Node, ct);
            _texts.Clear();
            if (versions.Count == 0)
            {
                // File output: only the current config, no history.
                _texts["latest"] = LineDiff.Split(await _client.GetLatestAsync(vm.Node, ct));
                _rows = [new BackupVersionRow(null, vm.Node.LastChanged ?? vm.Node.LastRun ?? DateTimeOffset.UtcNow, true)];
                VersionsNote.Text = "Only the latest config. Oxidized keeps every version when its git output is on.";
                VersionsNote.Visibility = Visibility.Visible;
            }
            else
            {
                _rows = versions.Select((v, i) => new BackupVersionRow(v, v.Date, i == 0)).ToList();
                _rows[^1].NoteText = "first";
                VersionsNote.Visibility = Visibility.Collapsed;
            }
            VersionsTitle.Text = _rows.Count == 1 ? "1 VERSION" : $"{_rows.Count} VERSIONS";
            Versions.ItemsSource = _rows;
            Versions.SelectedIndex = 0;
            vm.StatusText = $"Config backups of {vm.Node.Name}: {_rows.Count} version{(_rows.Count == 1 ? "" : "s")}";
            _ = CountChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _client?.Dispose();
            _client = null;
            Overlay.Text = $"Couldn't load the configs.\n{ex.Message}";
            Overlay.Visibility = Visibility.Visible;
            vm.StatusText = $"Config backups of {vm.Node.Name}: {ex.Message}";
        }
    }

    private void ShowNode(OxidizedNode n)
    {
        NodeName.Text = n.Name;
        ModelText.Text = string.IsNullOrEmpty(n.Model) ? "" : $"Model {n.Model}";
        GroupText.Text = string.IsNullOrEmpty(n.Group) ? "" : $"Group {n.Group}";
        IpText.Text = n.Ip ?? "";
        var when = n.LastRun is { } t ? $"{(t.ToLocalTime().Date == DateTime.Today ? "today" : t.ToLocalTime().ToString("d MMM"))} {t.ToLocalTime():HH:mm}" : "";
        StatusText.Text = n.NeverRan ? "Not backed up yet" : $"Last run {when}, {n.StatusText}";
        StatusDot.Fill = (Brush)FindResource(n.IsOk ? "Ok" : n.IsFailing ? "Warn" : "Faint");
    }

    private async Task<string[]> TextAsync(BackupVersionRow row, CancellationToken ct)
    {
        if (_texts.TryGetValue(row.Key, out var t)) return t;
        if (_client is null || _vm is null || row.Version is null) return [];
        t = LineDiff.Split(await _client.GetVersionTextAsync(_vm.Node, row.Version, ct));
        _texts[row.Key] = t;
        return t;
    }

    /// <summary>Fills in lines added and removed for the newest versions, one at a time.</summary>
    private async Task CountChangesAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < Math.Min(_rows.Count - 1, CountedVersions); i++)
            {
                var newer = await TextAsync(_rows[i], ct);
                var older = await TextAsync(_rows[i + 1], ct);
                var (a, r) = LineDiff.Count(LineDiff.Compare(older, newer));
                _rows[i].Added = a;
                _rows[i].Removed = r;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* counts are a nicety; the selected version still loads */ }
        catch (OperationCanceledException) { }
    }

    // ---------------- showing ----------------

    private BackupVersionRow? Current => Versions.SelectedItem as BackupVersionRow;
    private BackupVersionRow? CompareTo => CompareBox.SelectedItem as BackupVersionRow;
    private bool ShowingChanges => ChangesMode.IsChecked == true;

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is not { } cur) return;
        _quietCompare = true;
        var idx = _rows.IndexOf(cur);
        var older = _rows.Skip(idx + 1).ToList();
        CompareBox.ItemsSource = older;
        CompareBox.SelectedIndex = older.Count > 0 ? 0 : -1;
        _quietCompare = false;
        _ = RenderAsync();
    }

    private void OnCompareChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_quietCompare) _ = RenderAsync();
    }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        var changes = sender == ChangesMode;
        ConfigMode.IsChecked = !changes;
        ChangesMode.IsChecked = changes;
        _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (Current is not { } cur || _cts is null) return;
        var ticket = ++_render;
        var ct = _cts.Token;
        foreach (var r in _rows) r.IsCompare = ShowingChanges && r == CompareTo;
        ComparePanel.Visibility = ShowingChanges && _rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (!_texts.ContainsKey(cur.Key)) { Overlay.Text = "Loading from Oxidized…"; Overlay.Visibility = Visibility.Visible; }
            var text = await TextAsync(cur, ct);
            if (ticket != _render) return;

            var rows = new List<ConfigLineRow>();
            if (!ShowingChanges)
            {
                for (var i = 0; i < text.Length; i++) rows.Add(new ConfigLineRow((i + 1).ToString(), "", text[i], ""));
                Summary.Text = $"{text.Length} lines · {cur.CompareName}";
                Overlay.Visibility = Visibility.Collapsed;
            }
            else if (CompareTo is not { } other)
            {
                Summary.Text = "";
                Overlay.Text = _rows.Count == 1
                    ? "There's only one version, so nothing to compare yet."
                    : "This is the first version Oxidized has. Pick a newer one to see what changed.";
                Overlay.Visibility = Visibility.Visible;
            }
            else
            {
                var old = await TextAsync(other, ct);
                if (ticket != _render) return;
                var diff = LineDiff.Compare(old, text);
                var (a, r) = LineDiff.Count(diff);
                // Changed lines with a few lines of context around them, like git.
                var keep = new bool[diff.Count];
                for (var i = 0; i < diff.Count; i++)
                    if (diff[i].Kind != DiffKind.Same)
                        for (var k = Math.Max(0, i - Context); k <= Math.Min(diff.Count - 1, i + Context); k++) keep[k] = true;
                var skipped = 0;
                for (var i = 0; i < diff.Count; i++)
                {
                    if (!keep[i]) { skipped++; continue; }
                    if (skipped > 0) { rows.Add(new ConfigLineRow("", "", $"⋯ {skipped} unchanged line{(skipped == 1 ? "" : "s")}", "gap")); skipped = 0; }
                    var d = diff[i];
                    rows.Add(d.Kind switch
                    {
                        DiffKind.Added => new ConfigLineRow(d.NewLine.ToString(), "+", d.Text, "add"),
                        DiffKind.Removed => new ConfigLineRow(d.OldLine.ToString(), "−", d.Text, "del"),
                        _ => new ConfigLineRow(d.NewLine.ToString(), "", d.Text, ""),
                    });
                }
                if (skipped > 0 && rows.Count > 0) rows.Add(new ConfigLineRow("", "", $"⋯ {skipped} unchanged line{(skipped == 1 ? "" : "s")}", "gap"));
                Summary.Text = $"+{a} −{r} against {other.CompareName}";
                if (a + r == 0) { Overlay.Text = $"No changes against {other.CompareName}."; Overlay.Visibility = Visibility.Visible; }
                else Overlay.Visibility = Visibility.Collapsed;
            }
            _lines = rows;
            Lines.ItemsSource = rows;
            ApplyFind();
            if (rows.Count > 0) Lines.ScrollIntoView(rows[0]);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ticket != _render) return;
            Overlay.Text = $"Couldn't load this version.\n{ex.Message}";
            Overlay.Visibility = Visibility.Visible;
        }
    }

    // ---------------- find, copy, save ----------------

    private void OnFind(object sender, TextChangedEventArgs e)
    {
        FindHint.Visibility = FindBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFind();
        NextHit(-1);
    }

    private void ApplyFind()
    {
        var q = FindBox.Text.Trim();
        foreach (var l in _lines) l.Hit = q.Length > 0 && l.Kind != "gap" && l.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        NextHit(Lines.SelectedIndex);
        e.Handled = true;
    }

    private void NextHit(int after)
    {
        if (_lines.Count == 0 || FindBox.Text.Trim().Length == 0) return;
        for (var n = 1; n <= _lines.Count; n++)
        {
            var i = (after + n + _lines.Count) % _lines.Count;
            if (!_lines[i].Hit) continue;
            Lines.SelectedIndex = i;
            Lines.ScrollIntoView(_lines[i]);
            return;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Current is not { } cur || !_texts.TryGetValue(cur.Key, out var text)) return;
        SetClipboard(string.Join("\r\n", text), $"Copied the config from {cur.CompareName}");
    }

    private void OnCopyLines(object sender, ExecutedRoutedEventArgs e)
    {
        var picked = Lines.SelectedItems.Cast<ConfigLineRow>().Where(l => l.Kind != "gap").OrderBy(l => _lines.IndexOf(l)).Select(l => l.Text).ToList();
        if (picked.Count > 0) SetClipboard(string.Join("\r\n", picked), $"Copied {picked.Count} line{(picked.Count == 1 ? "" : "s")}");
    }

    private void SetClipboard(string text, string done)
    {
        try { Clipboard.SetText(text); if (_vm is not null) _vm.StatusText = done; Summary.Text = done; }
        catch (Exception ex) { Summary.Text = $"Couldn't copy: {ex.Message}"; }
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_vm is null || Current is not { } cur || !_texts.TryGetValue(cur.Key, out var text)) return;
        var safe = string.Concat(_vm.Node.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dlg = new SaveFileDialog
        {
            FileName = $"{safe}_{cur.Date.ToLocalTime():yyyy-MM-dd_HHmm}.txt",
            Filter = "Text file|*.txt|Config file|*.cfg;*.conf|All files|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, text) + Environment.NewLine);
            Summary.Text = $"Saved {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { Summary.Text = $"Couldn't save: {ex.Message}"; }
    }

    // ---------------- Oxidized actions ----------------

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        if (_vm is not null) _ = _vm.RefreshNodes();
    }

    private async void OnBackUpNow(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        BackUpButton.IsEnabled = false;
        try
        {
            _client ??= await vm.Connect(_cts?.Token ?? default);
            await _client.BackUpNowAsync(vm.Node);
            StatusText.Text = "Oxidized will back this device up next. This tab refreshes in a minute.";
            StatusDot.Fill = (Brush)FindResource("Faint");
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (_vm is not null && _client is not null)
            {
                await LoadAsync();
                _ = vm.RefreshNodes();
            }
        }
        catch (Exception ex) { StatusText.Text = $"Back up now failed: {ex.Message}"; }
        finally { BackUpButton.IsEnabled = true; }
    }

    private void OnOpenInOxidized(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try { Process.Start(new ProcessStartInfo(OxidizedClient.NodePageUrl(_vm.OxidizedUrl, _vm.Node)) { UseShellExecute = true }); }
        catch (Exception ex) { Summary.Text = ex.Message; }
    }
}
