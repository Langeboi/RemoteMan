using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Web.WebView2.Core;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Protocols.Ssh;

namespace RemoteMan.App.Views;

/// <summary>
/// xterm.js in WebView2 fed by an SSH.NET shell (output batched per frame), plus an SFTP
/// Files panel that reuses the session's login. Copy on select, paste on right click,
/// drop files on the terminal or panel to upload.
/// </summary>
public partial class SshSessionView : UserControl
{
    private static readonly KnownHosts KnownHosts = new(AppPaths.KnownHosts);

    private SshSession? _ssh;
    /// <summary>What the terminal talks to: the SSH shell, Telnet or a serial port.</summary>
    private RemoteMan.Protocols.Terminal.ITerminalChannel? _chan;
    private SftpBrowser? _sftp;
    private SshSessionViewModel? _vm;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _flush;
    private bool _started;
    private CancellationTokenSource? _transferCts;

    public SshSessionView()
    {
        InitializeComponent();
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) => Flush(), Dispatcher);
        Loaded += OnLoaded;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Post(new { t = "focus" }); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && FilesToggle.IsVisible)
            {
                FilesToggle.IsChecked = FilesToggle.IsChecked != true;
                e.Handled = true;
            }
            else if (e.Key == Key.M && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ToggleBroadcast();
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                SnippetsToggle.IsChecked = SnippetsToggle.IsChecked != true;
                if (SnippetsToggle.IsChecked == true) SnippetFilter.Focus();
                e.Handled = true;
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not SshSessionViewModel vm) return;
        _started = true;
        _vm = vm;
        var env = await WebViewEnvironment.GetAsync();
        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("remoteman-terminal.local",
            Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"), CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://remoteman-terminal.local/terminal.html");
        vm.Disposed += (_, _) => Close();
        HookBroadcast();
    }

    // ---------------- terminal ----------------

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                Post(new { t = "config", copyOnSelect = _vm?.Settings.CopyOnSelect ?? true, backspaceCtrlH = _vm?.Settings.BackspaceCtrlH ?? false });
                _cols = (uint)m.GetProperty("cols").GetInt32(); _rows = (uint)m.GetProperty("rows").GetInt32();
                _vm?.SetSize((int)_cols, (int)_rows);
                PostLook();
                TerminalPrefs.Changed += OnPrefsChanged;
                await StartAsync((uint)m.GetProperty("cols").GetInt32(), (uint)m.GetProperty("rows").GetInt32());
                break;
            case "data":
                SendInput(m.GetProperty("d").GetString() ?? "");
                break;
            case "resize":
                _cols = (uint)m.GetProperty("cols").GetInt32(); _rows = (uint)m.GetProperty("rows").GetInt32();
                _chan?.Resize(_cols, _rows);
                _vm?.SetSize((int)_cols, (int)_rows);
                break;
            case "prefs":
                TerminalPrefs.Save(m.GetProperty("d").GetRawText());
                break;
            case "copy":
                if (m.GetProperty("d").GetString() is { Length: > 0 } text) TrySetClipboard(text);
                break;
            case "paste":
                if (Clipboard.ContainsText()) SendInput(Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r'));
                else if (Clipboard.ContainsFileDropList()) await UploadAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
                break;
            case "upload":
                var paths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToList() ?? new List<string>();
                if (paths.Count > 0) await UploadAsync(paths);
                break;
        }
    }

    private static void TrySetClipboard(string text)
    {
        // The clipboard can be briefly locked by another app; retry a few times.
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(20); }
        }
    }

    private async Task StartAsync(uint cols, uint rows)
    {
        if (_vm is null || _chan is not null) return;
        var kind = _vm.Entry.Kind;
        if (kind != EntryKind.Ssh)
        {
            // Telnet and serial: same terminal, no SFTP panel.
            FilesToggle.Visibility = Visibility.Collapsed;
            await StartPlainAsync(kind, cols, rows);
            return;
        }
        _ssh = new SshSession();
        _chan = _ssh;
        var ssh0 = _ssh;
        _ssh.Output += s => { lock (_pending) _pending.Append(s); _vm.Tail.Append(s); OnOutput(s); };
        _ssh.Info += s => Dispatcher.BeginInvoke(() => Post(new { t = "info", d = s }));
        _ssh.Closed += ex => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_ssh, ssh0)) return;  // an older connection, already replaced
            Post(new { t = "info", d = ex is null ? "\r\n[session closed]" : $"\r\n[disconnected: {ex.Message}]" });
            _vm.StatusText = "SSH disconnected";
            _vm.Sender = null;
            EndLog();
            if (ex is not null && !TryReconnect()) _vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromException(ex, EntryKind.Ssh));
        });

        var fb = _vm.UsedFallback ? " (fallback)" : "";
        var targets = _vm.Endpoints ?? [_vm.Endpoint];
        Post(new { t = "info", d = $"Connecting to {targets[0]}{fb}…" });
        try
        {
            await _ssh.ConnectAsync(targets, _vm.Jumps, _vm.Credential, _vm.Settings, KnownHosts, cols, rows);
            Post(new { t = "info", d = _ssh.NegotiatedSummary ?? "" });
            var via = _vm.Jumps.Count > 0 ? $" via {string.Join(" → ", _vm.Jumps.Select(j => j.Name))}" : "";
            var fwd = _ssh.ActiveForwards.Count > 0 ? $"  ·  {_ssh.ActiveForwards.Count} forward{(_ssh.ActiveForwards.Count == 1 ? "" : "s")}" : "";
            _vm.StatusText = $"SSH {_ssh.ConnectedEndpoint ?? _vm.Endpoint}{via}  {_ssh.NegotiatedSummary}{fwd}";
            _reconnects = 0;
            StartLogAndScript("\r");
            var ssh = _ssh;
            _vm.Sender = text => ssh.Send(text);
            _vm.SendToTerminal = text =>
            {
                // Typed, not run: no trailing Enter. Multi-line text goes in a bracketed paste so a
                // modern shell holds every line until the user presses Enter.
                text = text.TrimEnd('\r', '\n').Replace("\r\n", "\r").Replace('\n', '\r');
                _chan?.Send(text.Contains('\r') ? "\x1b[200~" + text + "\x1b[201~" : text);
                Post(new { t = "focus" });
            };
            _flush.Start();
        }
        catch (Exception ex)
        {
            Post(new { t = "info", d = $"Connection failed: {ex.Message}" });
            _vm.StatusText = "SSH failed";
            if (_reconnects > 0 && TryReconnect()) return;
            _vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromException(ex, kind));
        }
    }

    /// <summary>Telnet or a serial console in the same terminal.</summary>
    private async Task StartPlainAsync(EntryKind kind, uint cols, uint rows)
    {
        var vm = _vm!;
        var label = kind == EntryKind.Serial ? "Serial" : "Telnet";
        try
        {
            if (kind == EntryKind.Serial)
            {
                var s = vm.Entry.Serial ?? new SerialSettings();
                var serial = new RemoteMan.Protocols.Terminal.SerialChannel { Encoding = RemoteMan.Protocols.Terminal.TerminalEncoding.For(vm.Settings.Encoding) };
                Hook(serial, label);
                Post(new { t = "info", d = $"Opening {s.PortName} at {s.BaudRate} baud…" });
                serial.Open(s);
                _chan = serial;
                vm.StatusText = $"Serial {s.PortName}  {s.BaudRate} {s.DataBits}{s.Parity[0]}{(s.StopBits == "Two" ? 2 : 1)}";
            }
            else
            {
                var telnet = new RemoteMan.Protocols.Terminal.TelnetChannel { Encoding = RemoteMan.Protocols.Terminal.TerminalEncoding.For(vm.Settings.Encoding) };
                Hook(telnet, label);
                Post(new { t = "info", d = $"Connecting to {vm.Endpoint}…" });
                await telnet.ConnectAsync(vm.Endpoint, vm.Credential, cols, rows);
                _chan = telnet;
                vm.StatusText = $"Telnet {vm.Endpoint}";
            }
            var chan = _chan;
            vm.Sender = text => chan.Send(text);
            vm.SendToTerminal = text => { _chan?.Send(text.TrimEnd('\r', '\n').Replace("\r\n", "\r").Replace('\n', '\r')); Post(new { t = "focus" }); };
            _flush.Start();
            _reconnects = 0;
            StartLogAndScript("\r");
        }
        catch (Exception ex)
        {
            Post(new { t = "info", d = $"Connection failed: {ex.Message}" });
            vm.StatusText = $"{label} failed";
            if (_reconnects > 0 && TryReconnect()) return;
            vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromException(ex, kind));
        }
    }

    private void Hook(RemoteMan.Protocols.Terminal.ITerminalChannel c, string label)
    {
        c.Output += s => { lock (_pending) _pending.Append(s); _vm?.Tail.Append(s); OnOutput(s); };
        c.Info += s => Dispatcher.BeginInvoke(() => Post(new { t = "info", d = s }));
        c.Closed += ex => Dispatcher.BeginInvoke(() =>
        {
            if (_chan is not null && !ReferenceEquals(_chan, c)) return;  // an older connection, already replaced
            Post(new { t = "info", d = ex is null ? "\r\n[session closed]" : $"\r\n[disconnected: {ex.Message}]" });
            if (_vm is null) return;
            _vm.StatusText = $"{label} disconnected";
            _vm.Sender = null;
            EndLog();
            if (ex is not null && !TryReconnect()) _vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromException(ex, _vm.Entry.Kind));
        });
    }

    private void Flush()
    {
        string chunk;
        lock (_pending)
        {
            if (_pending.Length == 0) return;
            chunk = _pending.ToString();
            _pending.Clear();
        }
        Post(new { t = "out", d = chunk });
    }

    private void Post(object message)
    {
        if (Web.CoreWebView2 is { } core) core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    // ---------------- SFTP Files panel ----------------

    private async void OnFilesToggled(object sender, RoutedEventArgs e)
    {
        var open = FilesToggle.IsChecked == true;
        if (open) SnippetsToggle.IsChecked = false; // one side panel at a time
        FilesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LayoutSidePanel();
        if (open) await EnsureSftpAsync();
    }

    private void LayoutSidePanel()
    {
        var open = FilesToggle.IsChecked == true || SnippetsToggle.IsChecked == true;
        FilesCol.Width = open ? new GridLength(FilesToggle.IsChecked == true ? 300 : 290) : new GridLength(0);
        SplitCol.Width = open ? new GridLength(1) : new GridLength(0);
    }

    private async Task<bool> EnsureSftpAsync()
    {
        if (_sftp is not null) return true;
        if (_ssh is null) { TransferText.Text = "Not connected yet."; return false; }
        try
        {
            TransferText.Text = "Opening SFTP…";
            _sftp = await _ssh.OpenSftpAsync();
            await NavigateAsync(_sftp.CurrentDirectory);
            TransferText.Text = "";
            return true;
        }
        catch (Exception ex)
        {
            TransferText.Text = $"SFTP unavailable: {ex.Message}";
            return false;
        }
    }

    private async Task NavigateAsync(string path)
    {
        if (_sftp is null) return;
        try
        {
            RemoteList.ItemsSource = await _sftp.ListAsync(path);
            RemotePath.Text = _sftp.CurrentDirectory;
        }
        catch (Exception ex) { TransferText.Text = ex.Message; }
    }

    private async void OnUp(object sender, RoutedEventArgs e) { if (_sftp is not null) await NavigateAsync(SftpBrowser.Parent(_sftp.CurrentDirectory)); }
    private async void OnRefresh(object sender, RoutedEventArgs e) { if (_sftp is not null) await NavigateAsync(_sftp.CurrentDirectory); }
    private async void OnPathKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await NavigateAsync(RemotePath.Text.Trim()); }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteList.SelectedItem is not RemoteItem item) return;
        if (item.IsDirectory) await NavigateAsync(item.FullPath);
        else await DownloadAsync(new[] { item }, reveal: true);
    }

    private async void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && _sftp is not null) await NavigateAsync(SftpBrowser.Parent(_sftp.CurrentDirectory));
        else if (e.Key == Key.Enter && RemoteList.SelectedItem is RemoteItem { IsDirectory: true } d) await NavigateAsync(d.FullPath);
        else if (e.Key == Key.Delete) OnDelete(sender, e);
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var paths = string.Join("\n", RemoteList.SelectedItems.Cast<RemoteItem>().Select(i => i.FullPath));
            if (paths.Length > 0) TrySetClipboard(paths);
        }
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await UploadAsync(files);
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Upload to " + _sftp?.CurrentDirectory };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) await UploadAsync(dlg.FileNames);
    }

    private async void OnDownload(object sender, RoutedEventArgs e) =>
        await DownloadAsync(RemoteList.SelectedItems.Cast<RemoteItem>().ToList(), reveal: true);

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_sftp is null) return;
        var items = RemoteList.SelectedItems.Cast<RemoteItem>().ToList();
        if (items.Count == 0) return;
        var what = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        if (!ChoiceDialog.Confirm(Window.GetWindow(this), $"Delete {what}?", $"It is deleted on {_vm?.Endpoint.Host} and can't be undone.", "Delete", danger: true)) return;
        try { foreach (var i in items) await _sftp.DeleteAsync(i); }
        catch (Exception ex) { TransferText.Text = ex.Message; }
        await NavigateAsync(_sftp.CurrentDirectory);
    }

    /// <summary>Uploads into the folder shown in the Files panel (opening it if needed).</summary>
    private async Task UploadAsync(IReadOnlyCollection<string> paths)
    {
        if (FilesToggle.IsChecked != true) FilesToggle.IsChecked = true;
        if (!await EnsureSftpAsync() || _sftp is null) return;
        var target = _sftp.CurrentDirectory;
        await RunTransfer($"Uploading to {target}", ct => _sftp.UploadAsync(paths, target, Progress(), ct));
        await NavigateAsync(target);
    }

    private async Task DownloadAsync(IReadOnlyCollection<RemoteItem> items, bool reveal)
    {
        if (_sftp is null || items.Count == 0) return;
        var folder = string.IsNullOrWhiteSpace(_vm?.Settings.DownloadFolder) ? KnownFolders.Downloads : _vm!.Settings.DownloadFolder!;
        Directory.CreateDirectory(folder);
        await RunTransfer($"Downloading to {folder}", ct => _sftp.DownloadAsync(items, folder, Progress(), ct));
        if (reveal)
        {
            var first = Path.Combine(folder, items.First().Name);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
        }
    }

    private IProgress<TransferProgress> Progress()
    {
        var sw = Stopwatch.StartNew();
        long lastDone = 0; var lastTick = 0L; double speed = 0;
        return new Progress<TransferProgress>(p =>
        {
            var now = sw.ElapsedMilliseconds;
            if (now - lastTick > 250)
            {
                speed = (p.Done - lastDone) * 1000.0 / Math.Max(1, now - lastTick);
                lastTick = now; lastDone = p.Done;
            }
            if (p.Done < lastDone) lastDone = 0; // next file started
            TransferBar.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
            TransferText.Text = $"{p.File}  {p.Done / 1048576.0:0.0}/{p.Total / 1048576.0:0.0} MB  {speed / 1048576.0:0.0} MB/s";
        });
    }

    private async Task RunTransfer(string label, Func<CancellationToken, Task> work)
    {
        _transferCts?.Cancel();
        _transferCts = new CancellationTokenSource();
        TransferBar.Visibility = Visibility.Visible;
        TransferText.Text = label + "…";
        var sw = Stopwatch.StartNew();
        try
        {
            await work(_transferCts.Token);
            TransferText.Text = $"Done in {sw.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException) { TransferText.Text = "Cancelled"; }
        catch (Exception ex) { TransferText.Text = ex.Message; }
        finally { TransferBar.Visibility = Visibility.Collapsed; }
    }

    // ---------------- Snippets panel ----------------

    private MainViewModel? _main;
    private ICollectionView? _snippetView;
    private readonly ObservableCollection<SendTarget> _targets = new();

    /// <summary>An open SSH tab in the "Send to" row.</summary>
    public sealed partial class SendTarget(SshSessionViewModel session) : ObservableObject
    {
        public SshSessionViewModel Session { get; } = session;
        public string Title => Session.Title;
        [ObservableProperty] private bool _isChecked;
    }

    private void OnSnippetsToggled(object sender, RoutedEventArgs e)
    {
        var open = SnippetsToggle.IsChecked == true;
        if (open) FilesToggle.IsChecked = false;
        SnippetsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LayoutSidePanel();
        if (open) EnsureSnippets();
    }

    private void EnsureSnippets()
    {
        if (_main is not null) { RefreshTargets(); return; }
        _main = Window.GetWindow(this)?.DataContext as MainViewModel;
        if (_main is null) return;
        _snippetView = new ListCollectionView(_main.Snippets);
        _snippetView.GroupDescriptions.Add(new PropertyGroupDescription(null, new SnippetGroupConverter(
            () => SnippetFilter.Text.Trim().Length > 0 || CategoryBox.SelectedItem is null or AllCategories)));
        _snippetView.Filter = o => o is Entry en && InView(en);
        SnippetList.ItemsSource = _snippetView;
        TargetList.ItemsSource = _targets;
        _main.Sessions.CollectionChanged += OnSessionsChanged;
        _main.SnippetsChanged += OnSnippetsChanged;
        RefreshCategories();
        RefreshTargets();
        if (SnippetList.Items.Count > 0) SnippetList.SelectedIndex = 0;
    }

    private void OnSessionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshTargets();

    /// <summary>The store rebuilds the list on every change (also other users' edits): keep the selection.</summary>
    private void OnSnippetsChanged(object? sender, EventArgs e)
    {
        RefreshCategories();
        if (_main is not null && _selectedSnippet is { } id && _main.Snippets.FirstOrDefault(x => x.Id == id) is { } again)
            SnippetList.SelectedItem = again;
    }

    // ---- categories and collapsible groups ----

    private const string AllCategories = "All snippets";
    /// <summary>Shared by all SSH tabs for this run: the chosen category and folded groups.</summary>
    private static string? _category;
    private static readonly HashSet<string> Folded = new(StringComparer.OrdinalIgnoreCase);
    private bool _settingCategories;

    /// <summary>
    /// One category at a time keeps the list short; typing in the filter searches every category.
    /// </summary>
    private bool InView(Entry e)
    {
        var f = SnippetFilter.Text.Trim();
        if (f.Length > 0) return MatchesFilter(e, f);
        var cat = CategoryBox.SelectedItem as string;
        return cat is null or AllCategories || string.Equals(e.Snippet?.Category, cat, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshCategories()
    {
        if (_main is null) return;
        var cats = _main.Snippets.Select(s => s.Snippet?.Category ?? SnippetData.DefaultCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).Prepend(AllCategories).ToList();
        var want = _category ?? cats.Skip(1).FirstOrDefault() ?? AllCategories;
        _settingCategories = true;
        CategoryBox.ItemsSource = cats;
        CategoryBox.SelectedItem = cats.FirstOrDefault(c => string.Equals(c, want, StringComparison.OrdinalIgnoreCase)) ?? AllCategories;
        _settingCategories = false;
        _snippetView?.Refresh();
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is not string cat) return;
        if (!_settingCategories) _category = cat;
        _snippetView?.Refresh();
        if (SnippetList.SelectedItem is null && SnippetList.Items.Count > 0) SnippetList.SelectedIndex = 0;
    }

    private static string GroupKey(Expander x) =>
        (x.DataContext as CollectionViewGroup)?.Name?.ToString() ?? "";

    private void OnGroupLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander x) x.IsExpanded = !Folded.Contains(GroupKey(x));
    }

    private void OnGroupToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander x || !x.IsLoaded) return;
        if (x.IsExpanded) Folded.Remove(GroupKey(x)); else Folded.Add(GroupKey(x));
    }

    private static bool MatchesFilter(Entry e, string f) =>
        f.Length == 0 || e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        (e.Snippet?.Command.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Snippet?.Group?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Keeps the "Send to" row in step with the open SSH tabs; this tab is ticked by default.</summary>
    private void RefreshTargets()
    {
        if (_main is null || _vm is null) return;
        var open = _main.SshSessions.ToList();
        foreach (var t in _targets.Where(t => !open.Contains(t.Session)).ToList()) _targets.Remove(t);
        foreach (var s in open.Where(s => _targets.All(t => t.Session != s)))
        {
            var t = new SendTarget(s) { IsChecked = s == _vm };
            t.PropertyChanged += (_, _) => UpdateSendLabel();
            _targets.Add(t);
        }
        UpdateSendLabel();
    }

    private void UpdateSendLabel()
    {
        var n = _targets.Count(t => t.IsChecked);
        SendButton.Content = SnippetList.SelectedItem is not Entry sel ? "Send"
            : n == 0 ? "Tick a session"
            : $"Send \"{sel.Name}\" to {n} session{(n == 1 ? "" : "s")}";
    }

    private Guid? _selectedSnippet;

    private void OnSnippetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SnippetList.SelectedItem is Entry sel) _selectedSnippet = sel.Id;
        UpdateSendLabel();
    }

    private void OnSnippetFilterChanged(object sender, TextChangedEventArgs e)
    {
        _snippetView?.Refresh();
        if (SnippetList.SelectedItem is null && SnippetList.Items.Count > 0) SnippetList.SelectedIndex = 0;
    }

    private void OnSnippetFilterKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { SnippetList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter) { SendSelected(onlyHere: Keyboard.Modifiers != ModifierKeys.Control); e.Handled = true; }
        else if (e.Key == Key.Escape) { SnippetFilter.Text = ""; e.Handled = true; }
    }

    private void OnSnippetKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendSelected(onlyHere: Keyboard.Modifiers != ModifierKeys.Control); e.Handled = true; }
        else if (e.Key == Key.Delete) OnDeleteSnippet(sender, e);
        else if (e.Key == Key.F2) OnEditSnippet(sender, e);
    }

    private void OnSnippetDoubleClick(object sender, MouseButtonEventArgs e) => SendSelected(onlyHere: true);
    private void OnSendHere(object sender, RoutedEventArgs e) => SendSelected(onlyHere: true);
    private void OnSendSnippet(object sender, RoutedEventArgs e) => SendSelected(onlyHere: false);

    private void SendSelected(bool onlyHere)
    {
        if (_main is null || _vm is null || SnippetList.SelectedItem is not Entry sel || sel.Snippet is null) return;
        List<SshSessionViewModel> targets = onlyHere ? [_vm] : _targets.Where(t => t.IsChecked).Select(t => t.Session).ToList();
        if (targets.Count == 0) { SnippetStatus.Text = "Tick at least one session under Send to."; return; }
        // Typed only, unless the snippet is set to run or "Also press Enter" is ticked for this send.
        var toSend = sel;
        if (PressEnterBox.IsChecked == true && !sel.Snippet.PressEnter)
        {
            toSend = EntryStore.Clone(sel);
            toSend.Snippet!.PressEnter = true;
        }
        var sent = _main.SendSnippet(toSend, targets);
        var missed = targets.Count - sent;
        SnippetStatus.Text = $"Sent \"{sel.Name}\" to {string.Join(", ", targets.Where(t => t.IsConnected).Select(t => t.Title))}"
            + (missed > 0 ? $" ({missed} not connected)" : "");
        if (onlyHere) Post(new { t = "focus" });
    }

    private async void OnNewSnippet(object sender, RoutedEventArgs e)
    {
        EnsureSnippets();
        var group = (SnippetList.SelectedItem as Entry)?.Snippet?.Group;
        await EditSnippetAsync(new Entry { Kind = EntryKind.Snippet, Snippet = new SnippetData { Group = group } });
    }

    private async void OnEditSnippet(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is Entry sel) await EditSnippetAsync(EntryStore.Clone(sel));
    }

    private async void OnDuplicateSnippet(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not Entry sel) return;
        var copy = EntryStore.Clone(sel);
        copy.Id = Guid.NewGuid();
        copy.Revision = 0;
        copy.Name += " (copy)";
        await EditSnippetAsync(copy);
    }

    private async Task EditSnippetAsync(Entry snippet)
    {
        if (_main is null) return;
        var groups = _main.Snippets.Select(s => s.Snippet?.Group).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToList();
        var dlg = new SnippetEditorWindow(snippet, groups) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (dlg.DeleteRequested) { await _main.DeleteSnippetAsync(snippet); return; }
        if (await _main.SaveSnippetAsync(snippet))
            SnippetList.SelectedItem = _main.Snippets.FirstOrDefault(s => s.Id == snippet.Id);
    }

    private async void OnDeleteSnippet(object sender, RoutedEventArgs e)
    {
        if (_main is null || SnippetList.SelectedItem is not Entry sel) return;
        if (ChoiceDialog.Confirm(Window.GetWindow(this), $"Delete snippet \"{sel.Name}\"?", "", "Delete", danger: true))
            await _main.DeleteSnippetAsync(sel);
    }

    private void OnSnippetMore(object sender, RoutedEventArgs e)
    {
        EnsureSnippets();
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var builtIn = new MenuItem { Header = "Add missing built-in snippets" };
        builtIn.Click += async (_, _) =>
        {
            if (_main is null) return;
            var n = await _main.AddBuiltInSnippetsAsync();
            SnippetStatus.Text = n == 0 ? "All built-in snippets are already there." : $"Added {n} built-in snippet{(n == 1 ? "" : "s")}.";
        };
        var all = new MenuItem { Header = "Tick all sessions" };
        all.Click += (_, _) => { foreach (var t in _targets) t.IsChecked = true; UpdateSendLabel(); };
        var none = new MenuItem { Header = "Only this session" };
        none.Click += (_, _) => { foreach (var t in _targets) t.IsChecked = t.Session == _vm; UpdateSendLabel(); };
        menu.Items.Add(builtIn);
        menu.Items.Add(new Separator());
        menu.Items.Add(all);
        menu.Items.Add(none);
        menu.IsOpen = true;
    }

    // ---------------- terminal look ----------------

    private void OnPrefsChanged() => Dispatcher.BeginInvoke(PostLook);

    private void PostLook()
    {
        var look = TerminalPrefs.Load();
        // The entry's own terminal settings (Advanced > Terminal) win over the app-wide look.
        if (_vm?.Settings is { } st)
        {
            if (!string.IsNullOrWhiteSpace(st.Theme)) look["theme"] = st.Theme;
            if (st.FontSize > 0) look["fontSize"] = st.FontSize;
            if (st.Scrollback > 0) look["scrollback"] = st.Scrollback;
        }
        if (Web.CoreWebView2 is { } core) core.PostWebMessageAsJson("{\"t\":\"look\",\"d\":" + look.ToJsonString() + "}");
    }

    private void OnLook(object sender, RoutedEventArgs e)
    {
        var prefs = TerminalPrefs.Load();
        var menu = new ContextMenu { PlacementTarget = LookButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Left };
        MenuItem Sub(string header, IEnumerable<(string Label, bool On, Action Click)> items)
        {
            var m = new MenuItem { Header = header };
            foreach (var (label, on, click) in items)
            {
                var i = new MenuItem { Header = label, IsCheckable = true, IsChecked = on };
                i.Click += (_, _) => click();
                m.Items.Add(i);
            }
            return m;
        }
        var theme = prefs["theme"]?.ToString();
        var font = prefs["fontFamily"]?.ToString();
        var size = (int?)prefs["fontSize"] ?? 13;
        var scroll = (int?)prefs["scrollback"] ?? 10000;
        menu.Items.Add(Sub("Theme", TerminalPrefs.Themes.Select(t => (t, t == theme, (Action)(() => TerminalPrefs.Set("theme", t))))));
        menu.Items.Add(Sub("Font", TerminalPrefs.Fonts.Select(f => (f, f == font, (Action)(() => TerminalPrefs.Set("fontFamily", f))))));
        menu.Items.Add(Sub($"Font size ({size})", new[] { 10, 11, 12, 13, 14, 15, 16, 18, 20 }.Select(n => (n.ToString(), n == size, (Action)(() => TerminalPrefs.Set("fontSize", n))))));
        menu.Items.Add(Sub("Scrollback", TerminalPrefs.Scrollbacks.Select(n => ($"{n:N0} lines", n == scroll, (Action)(() => TerminalPrefs.Set("scrollback", n))))));
        menu.Items.Add(new Separator());
        var find = new MenuItem { Header = "Find in scrollback", InputGestureText = "Ctrl+Shift+F" };
        find.Click += (_, _) => Post(new { t = "find" });
        menu.Items.Add(find);
        var clear = new MenuItem { Header = "Clear scrollback" };
        clear.Click += (_, _) => Post(new { t = "clear" });
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    // ---------------- session log, login script, reconnect ----------------

    private uint _cols = 80, _rows = 24;
    private int _reconnects;
    private bool _closing;
    private System.IO.StreamWriter? _log;
    private bool _logAtLineStart = true;
    private RemoteMan.Core.Terminal.LoginScriptRunner? _script;

    private void OnOutput(string s)
    {
        _vm?.TapOutput(s);
        _script?.Feed(s);
        var log = _log;
        if (log is null) return;
        var text = RemoteMan.Core.Terminal.TerminalText.ForLog(s);
        if (text.Length == 0) return;
        lock (log)
        {
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (_logAtLineStart) { sb.Append(DateTime.Now.ToString("HH:mm:ss ")); _logAtLineStart = false; }
                sb.Append(ch);
                if (ch == '\n') _logAtLineStart = true;
            }
            try { log.Write(sb.ToString()); } catch (Exception) { }
        }
    }

    private void StartLogAndScript(string enter)
    {
        if (_vm is null) return;
        var st = _vm.Settings;
        if (st.LogSession && _log is null)
        {
            try
            {
                var folder = string.IsNullOrWhiteSpace(st.LogFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RemoteMan logs")
                    : Environment.ExpandEnvironmentVariables(st.LogFolder);
                Directory.CreateDirectory(folder);
                var safe = string.Concat(_vm.Entry.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                var file = Path.Combine(folder, $"{safe}_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
                _log = new System.IO.StreamWriter(file, append: true, new UTF8Encoding(false)) { AutoFlush = true };
                _log.WriteLine($"# {_vm.Entry.Name} ({_vm.Endpoint}) {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _logAtLineStart = true;
                Post(new { t = "info", d = $"Logging to {file}" });
            }
            catch (Exception ex) { Post(new { t = "info", d = $"Session log not started: {ex.Message}" }); }
        }
        if (st.LoginScript is { Count: > 0 } steps)
        {
            var vm = _vm;
            _script = new RemoteMan.Core.Terminal.LoginScriptRunner(steps,
                t => RemoteMan.Core.Terminal.LoginScriptRunner.Expand(t, vm.FieldValues, () => vm.Credential.TotpCode, vm.Endpoint.Host, vm.Credential.UserName, vm.Entry.Name),
                text => _chan?.Send(text), enter);
            _script.Start();
        }
    }

    private void EndLog()
    {
        var log = _log;
        _log = null;
        _script = null;
        if (log is null) return;
        lock (log) { try { log.WriteLine(); log.WriteLine($"# closed {DateTime.Now:yyyy-MM-dd HH:mm:ss}"); log.Dispose(); } catch (Exception) { } }
    }

    /// <summary>The connection dropped: try again a few times (Advanced > Connection > Reconnect). False when it won't.</summary>
    private bool TryReconnect()
    {
        if (_closing || _vm is not { AutoReconnect: true } || _reconnects >= 5 || _vm.Entry.Kind == EntryKind.Serial) return false;
        _reconnects++;
        var wait = TimeSpan.FromSeconds(Math.Min(30, 3 * _reconnects));
        Post(new { t = "info", d = $"Reconnecting in {wait.TotalSeconds:0} s (try {_reconnects} of 5)…" });
        _vm.StatusText = "Reconnecting…";
        var timer = new DispatcherTimer { Interval = wait };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (_closing) return;
            _sftp?.Dispose(); _sftp = null;
            var old = _chan;
            _ssh = null; _chan = null;
            try { old?.Dispose(); } catch (Exception) { }
            await StartAsync(_cols, _rows);
        };
        timer.Start();
        return true;
    }

    private void Close()
    {
        _closing = true;
        EndLog();
        TerminalPrefs.Changed -= OnPrefsChanged;
        if (_vm is not null) _vm.Sender = null;
        if (_main is not null)
        {
            _main.Sessions.CollectionChanged -= OnSessionsChanged;
            _main.SnippetsChanged -= OnSnippetsChanged;
        }
        if (_vm is not null) _vm.SendToTerminal = null;
        UnhookBroadcast();
        _transferCts?.Cancel();
        _flush.Stop();
        _sftp?.Dispose();
        _sftp = null;
        _ssh?.Dispose();
        if (!ReferenceEquals(_chan, _ssh)) _chan?.Dispose();
        _ssh = null;
        _chan = null;
        Web.Dispose();
    }
}

internal static class KnownFolders
{
    public static string Downloads
    {
        get
        {
            var hr = SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var path);
            return hr == 0 ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);
}

/// <summary>Groups snippets by group; with several categories on screen, as "Category · Group".</summary>
internal sealed class SnippetGroupConverter(Func<bool> showCategory) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not Entry { Snippet: { } s } ? "Other" : showCategory() ? $"{s.Category} · {s.SubGroup}" : s.SubGroup;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
