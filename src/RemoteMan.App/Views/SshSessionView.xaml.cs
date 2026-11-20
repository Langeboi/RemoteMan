using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
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
            if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                FilesToggle.IsChecked = FilesToggle.IsChecked != true;
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
    }

    // ---------------- terminal ----------------

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                Post(new { t = "config", copyOnSelect = _vm?.Settings.CopyOnSelect ?? true });
                await StartAsync((uint)m.GetProperty("cols").GetInt32(), (uint)m.GetProperty("rows").GetInt32());
                break;
            case "data":
                _ssh?.Send(m.GetProperty("d").GetString() ?? "");
                break;
            case "resize":
                _ssh?.Resize((uint)m.GetProperty("cols").GetInt32(), (uint)m.GetProperty("rows").GetInt32());
                break;
            case "copy":
                if (m.GetProperty("d").GetString() is { Length: > 0 } text) TrySetClipboard(text);
                break;
            case "paste":
                if (Clipboard.ContainsText()) _ssh?.Send(Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r'));
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
        if (_vm is null || _ssh is not null) return;
        _ssh = new SshSession();
        _ssh.Output += s => { lock (_pending) _pending.Append(s); _vm.Tail.Append(s); };
        _ssh.Info += s => Dispatcher.BeginInvoke(() => Post(new { t = "info", d = s }));
        _ssh.Closed += ex => Dispatcher.BeginInvoke(() =>
        {
            Post(new { t = "info", d = ex is null ? "\r\n[session closed]" : $"\r\n[disconnected: {ex.Message}]" });
            _vm.StatusText = "SSH disconnected";
        });

        var fb = _vm.UsedFallback ? " (fallback)" : "";
        Post(new { t = "info", d = $"Connecting to {_vm.Endpoint}{fb}…" });
        try
        {
            await _ssh.ConnectAsync(_vm.Endpoint, _vm.Credential, _vm.Settings, KnownHosts, cols, rows);
            Post(new { t = "info", d = _ssh.NegotiatedSummary ?? "" });
            _vm.StatusText = $"SSH {_vm.Endpoint}  {_ssh.NegotiatedSummary}";
            _vm.SendToTerminal = text =>
            {
                // Typed, not run: no trailing Enter. Multi-line text goes in a bracketed paste so a
                // modern shell holds every line until the user presses Enter.
                text = text.TrimEnd('\r', '\n').Replace("\r\n", "\r").Replace('\n', '\r');
                _ssh?.Send(text.Contains('\r') ? "\x1b[200~" + text + "\x1b[201~" : text);
                Post(new { t = "focus" });
            };
            _flush.Start();
        }
        catch (Exception ex)
        {
            Post(new { t = "info", d = $"Connection failed: {ex.Message}" });
            _vm.StatusText = "SSH failed";
        }
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
        FilesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FilesCol.Width = open ? new GridLength(300) : new GridLength(0);
        SplitCol.Width = open ? new GridLength(1) : new GridLength(0);
        if (open) await EnsureSftpAsync();
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
        if (MessageBox.Show(Window.GetWindow(this), $"Delete {what} on {_vm?.Endpoint.Host}?", "RemoteMan", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
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

    private void Close()
    {
        if (_vm is not null) _vm.SendToTerminal = null;
        _transferCts?.Cancel();
        _flush.Stop();
        _sftp?.Dispose();
        _sftp = null;
        _ssh?.Dispose();
        _ssh = null;
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
