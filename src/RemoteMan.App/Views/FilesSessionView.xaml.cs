using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Protocols.Files;
using RemoteMan.Protocols.Ssh;

namespace RemoteMan.App.Views;

/// <summary>A row in either pane.</summary>
public sealed record FileRow(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified, RemoteItem? Remote = null)
{
    public string SizeText => IsDirectory ? "" : Remote?.SizeText ?? new RemoteItem(Name, FullPath, false, Size, Modified).SizeText;
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>SFTP / FTP tab: this PC on the left, the server on the right; drag, drop or use the arrows.</summary>
public partial class FilesSessionView : UserControl
{
    private const string DragFormat = "RemoteMan.Files";
    private static readonly KnownHosts KnownHosts = new(AppPaths.KnownHosts);
    private FilesSessionViewModel? _vm;
    private IRemoteFiles? _remote;
    private bool _started;
    private CancellationTokenSource? _cts;
    private string _localDir = "";
    private Point _dragStart;

    public FilesSessionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not FilesSessionViewModel vm) return;
        _started = true;
        _vm = vm;
        vm.Disposed += (_, _) => { _cts?.Cancel(); _remote?.Dispose(); _remote = null; };
        var start = vm.Entry.Kind == EntryKind.Sftp && !string.IsNullOrWhiteSpace(vm.Ssh.DownloadFolder) ? vm.Ssh.DownloadFolder! : KnownFolders.Downloads;
        NavigateLocal(Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var label = vm.Entry.Kind == EntryKind.Ftp ? (vm.Ftp.UseTls ? "FTPS" : "FTP") : "SFTP";
        RemoteTitle.Text = vm.Endpoint.Host;
        RemoteState.Text = $"Connecting to {vm.Endpoint}…";
        vm.StatusText = $"{label} connecting to {vm.Endpoint}…";
        try
        {
            _remote = vm.Entry.Kind == EntryKind.Ftp
                ? await FtpBrowser.ConnectAsync(vm.Endpoint, vm.Credential, vm.Ftp)
                : await SftpBrowser.ConnectAsync(vm.Endpoint, vm.Credential, vm.Ssh, KnownHosts, s => Dispatcher.BeginInvoke(() => TransferText.Text = s));
            vm.StatusText = $"{label} {vm.Endpoint}";
            RemoteState.Text = "";
            await NavigateRemoteAsync(_remote.CurrentDirectory);
        }
        catch (Exception ex)
        {
            RemoteState.Text = $"Couldn't connect: {ex.Message}";
            vm.StatusText = $"{label} failed";
            vm.ReportProblem(ConnectionProblem.FromException(ex, vm.Entry.Kind));
        }
    }

    // ---------------- this PC ----------------

    private void NavigateLocal(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var rows = info.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden))
                .Select(d => new FileRow(d.Name, d.FullName, true, 0, d.LastWriteTime)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Concat(info.EnumerateFiles().Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden))
                    .Select(f => new FileRow(f.Name, f.FullName, false, f.Length, f.LastWriteTime)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            _localDir = info.FullName;
            LocalPath.Text = _localDir;
            LocalList.ItemsSource = rows;
        }
        catch (Exception ex) { TransferText.Text = ex.Message; }
    }

    private void OnLocalUp(object sender, RoutedEventArgs e) { if (Directory.GetParent(_localDir) is { } p) NavigateLocal(p.FullName); }
    private void OnLocalRefresh(object sender, RoutedEventArgs e) => NavigateLocal(_localDir);
    private void OnLocalPathKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) NavigateLocal(LocalPath.Text.Trim()); }

    private void OnLocalOpen(object sender, MouseButtonEventArgs e)
    {
        if (LocalList.SelectedItem is not FileRow r) return;
        if (r.IsDirectory) NavigateLocal(r.FullPath);
        else try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.FullPath) { UseShellExecute = true }); } catch (Exception) { }
    }

    private void OnLocalKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnLocalOpen(sender, null!);
        else if (e.Key == Key.Back) OnLocalUp(sender, e);
    }

    // ---------------- server ----------------

    private async Task NavigateRemoteAsync(string path)
    {
        if (_remote is null) return;
        try
        {
            var items = await _remote.ListAsync(path);
            RemotePath.Text = _remote.CurrentDirectory;
            RemoteList.ItemsSource = items.Select(i => new FileRow(i.Name, i.FullPath, i.IsDirectory, i.Size, i.Modified, i)).ToList();
        }
        catch (Exception ex) { TransferText.Text = ex.Message; }
    }

    private async void OnRemoteUp(object sender, RoutedEventArgs e) { if (_remote is not null) await NavigateRemoteAsync(_remote.Parent(_remote.CurrentDirectory)); }
    private async void OnRemoteRefresh(object sender, RoutedEventArgs e) { if (_remote is not null) await NavigateRemoteAsync(_remote.CurrentDirectory); }
    private async void OnRemotePathKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await NavigateRemoteAsync(RemotePath.Text.Trim()); }

    private async void OnRemoteOpen(object sender, MouseButtonEventArgs? e)
    {
        if (RemoteList.SelectedItem is not FileRow r) return;
        if (r.IsDirectory) await NavigateRemoteAsync(r.FullPath);
        else await DownloadAsync([r]);
    }

    private async void OnRemoteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRemoteOpen(sender, null);
        else if (e.Key == Key.Back) OnRemoteUp(sender, e);
        else if (e.Key == Key.Delete) await DeleteSelectedAsync();
        else if (e.Key == Key.F5) OnRemoteRefresh(sender, e);
    }

    private void OnRemoteMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var mk = new MenuItem { Header = "New folder…" };
        mk.Click += async (_, _) =>
        {
            if (_remote is null) return;
            var name = PasswordPrompt.AskText(Window.GetWindow(this), "New folder", "Name of the new folder");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { await _remote.CreateDirectoryAsync(_remote.Combine(_remote.CurrentDirectory, name.Trim())); await NavigateRemoteAsync(_remote.CurrentDirectory); }
            catch (Exception ex) { TransferText.Text = ex.Message; }
        };
        menu.Items.Add(mk);
        var del = new MenuItem { Header = "Delete selected", InputGestureText = "Del" };
        del.Click += async (_, _) => await DeleteSelectedAsync();
        menu.Items.Add(del);
        menu.IsOpen = true;
    }

    private async Task DeleteSelectedAsync()
    {
        if (_remote is null) return;
        var rows = RemoteList.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0) return;
        var what = rows.Count == 1 ? $"\"{rows[0].Name}\"" : $"{rows.Count} items";
        if (!ChoiceDialog.Confirm(Window.GetWindow(this), $"Delete {what}?", $"It is deleted on {_vm?.Endpoint.Host} and can't be undone.", "Delete", danger: true)) return;
        await RunAsync("Deleting", async ct => { foreach (var r in rows) await _remote.DeleteAsync(r.Remote!, ct); });
        await NavigateRemoteAsync(_remote.CurrentDirectory);
    }

    // ---------------- transfers ----------------

    private async void OnUpload(object sender, RoutedEventArgs e) =>
        await UploadAsync(LocalList.SelectedItems.OfType<FileRow>().Select(r => r.FullPath).ToList());

    private async void OnDownload(object sender, RoutedEventArgs e) =>
        await DownloadAsync(RemoteList.SelectedItems.OfType<FileRow>().ToList());

    private async Task UploadAsync(IReadOnlyCollection<string> paths)
    {
        if (_remote is null || paths.Count == 0) return;
        var dir = _remote.CurrentDirectory;
        await RunAsync($"Uploading {paths.Count} item(s)", ct => _remote.UploadAsync(paths, dir, Progress(), ct));
        await NavigateRemoteAsync(dir);
    }

    private async Task DownloadAsync(IReadOnlyCollection<FileRow> rows)
    {
        if (_remote is null || rows.Count == 0) return;
        var dir = _localDir;
        await RunAsync($"Downloading {rows.Count} item(s)", ct => _remote.DownloadAsync(rows.Select(r => r.Remote!).ToList(), dir, Progress(), ct));
        NavigateLocal(dir);
    }

    private IProgress<TransferProgress> Progress() => new Progress<TransferProgress>(p =>
    {
        TransferText.Text = p.Total > 0 ? $"{p.File}  {p.Done * 100 / Math.Max(1, p.Total)}%" : p.File;
        TransferBar.Visibility = Visibility.Visible;
        TransferBar.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
    });

    private async Task RunAsync(string label, Func<CancellationToken, Task> work)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        TransferText.Text = label + "…";
        CancelButton.Visibility = Visibility.Visible;
        try { await work(_cts.Token); TransferText.Text = label + ": done"; }
        catch (OperationCanceledException) { TransferText.Text = label + ": cancelled"; }
        catch (Exception ex) { TransferText.Text = $"{label}: {ex.Message}"; }
        finally { CancelButton.Visibility = Visibility.Collapsed; TransferBar.Visibility = Visibility.Collapsed; }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------------- drag and drop ----------------

    private void OnDragStart(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListView list) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var rows = list.SelectedItems.OfType<FileRow>().ToList();
        if (rows.Count == 0) return;
        var data = new DataObject(DragFormat, list == LocalList ? "local" : "remote");
        // Local files also drag out to Explorer and other apps.
        if (list == LocalList) data.SetData(DataFormats.FileDrop, rows.Select(r => r.FullPath).ToArray());
        DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
    }

    private void OnDragOverList(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(DragFormat) as string;
        var ok = sender == RemoteList ? source != "remote" && e.Data.GetDataPresent(DataFormats.FileDrop) : source == "remote";
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnRemoteDrop(object sender, DragEventArgs e)
    {
        if ((e.Data.GetData(DragFormat) as string) == "remote") return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await UploadAsync(files);
    }

    private async void OnLocalDrop(object sender, DragEventArgs e)
    {
        if ((e.Data.GetData(DragFormat) as string) == "remote") await DownloadAsync(RemoteList.SelectedItems.OfType<FileRow>().ToList());
    }
}
