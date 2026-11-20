using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.Core.Transfer;

namespace RemoteMan.App.Views;

/// <summary>Export to an encrypted .rmx file, automatic daily backups, and restore.</summary>
public partial class BackupWindow : Window
{
    private sealed record Scope(Guid? FolderId, string Name);

    private readonly MainViewModel _main;
    private ExportBundle? _opened;

    public BackupWindow(MainViewModel main, int section = 0)
    {
        InitializeComponent();
        _main = main;
        var all = main.Store?.Entries.Where(e => e.Kind != EntryKind.Snippet).ToList() ?? new();

        var scopes = new List<Scope> { new(null, $"Everything ({all.Count(e => e.Kind != EntryKind.Folder)} entries)") };
        var sel = main.Selected?.Entry;
        var folderId = sel?.Kind == EntryKind.Folder ? sel.Id : sel?.ParentId;
        if (folderId is { } fid && main.Store?.Get(fid) is { } folder)
            scopes.Add(new(fid, $"Folder: {folder.Name}"));
        ScopeBox.ItemsSource = scopes;
        ScopeBox.SelectedIndex = 0;
        ExportPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"RemoteMan {DateTime.Now:yyyy-MM-dd}{ExportFile.Extension}");

        var b = main.Settings.Backup;
        AutoEnabled.IsChecked = b.Enabled;
        AutoFolder.Text = BackupService.Folder(b);
        AutoKeep.Text = b.Keep.ToString();
        AutoSecrets.IsChecked = b.IncludeSecrets;
        AutoPwState.Text = BackupService.LoadPassword() is null ? "not set" : "saved on this PC";
        AutoLast.Text = b.LastBackup is { } last ? $"Last backup {last.LocalDateTime:g}" : "No backup yet";
        RefreshBackups();

        Sections.SelectedIndex = section;
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ExportPage is null) return;
        var i = Sections.SelectedIndex;
        ExportPage.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoPage.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        RestorePage.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.Content = i switch { 0 => "Export", 1 => "Save and back up now", _ => "Restore" };
        Status.Text = "";
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        ActionButton.IsEnabled = false;
        try
        {
            switch (Sections.SelectedIndex)
            {
                case 0: await ExportAsync(); break;
                case 1: await BackupNowAsync(); break;
                default: await RestoreAsync(); break;
            }
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { ActionButton.IsEnabled = true; }
    }

    // ---------------- export ----------------

    private void OnBrowseExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = $"RemoteMan export (*{ExportFile.Extension})|*{ExportFile.Extension}",
            FileName = Path.GetFileName(ExportPath.Text),
            InitialDirectory = Path.GetDirectoryName(ExportPath.Text),
        };
        if (dlg.ShowDialog(this) == true) ExportPath.Text = dlg.FileName;
    }

    private async Task ExportAsync()
    {
        if (_main.Store is not { } store) return;
        if (ExportPw1.Password.Length < 8) { Status.Text = "Use a password of at least 8 characters."; ExportPw1.Focus(); return; }
        if (ExportPw1.Password != ExportPw2.Password) { Status.Text = "The passwords don't match."; ExportPw2.Focus(); return; }
        var path = ExportPath.Text.Trim();
        if (path.Length == 0) { OnBrowseExport(this, new RoutedEventArgs()); path = ExportPath.Text.Trim(); if (path.Length == 0) return; }
        if (!path.EndsWith(ExportFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ExportFile.Extension;

        var options = new ExportOptions
        {
            IncludeSecrets = ExportSecrets.IsChecked == true,
            IncludeSnippets = ExportSnippets.IsChecked == true,
            RootFolderId = (ScopeBox.SelectedItem as Scope)?.FolderId,
        };
        Status.Text = "Encrypting…";
        var snapshot = store.Entries.Select(EntryStore.Clone).ToList();
        var protector = _main.Protector;
        var password = ExportPw1.Password;
        var bundle = await Task.Run(() => ExportFile.CreateBundle(snapshot, protector, options, store.User, store.Machine));
        await ExportFile.WriteFileAsync(path, bundle, password);
        Status.Text = $"Exported {bundle.Entries.Count(x => x.Kind != EntryKind.Folder)} entries to {Path.GetFileName(path)}";
    }

    // ---------------- automatic backup ----------------

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Backup folder", InitialDirectory = Directory.Exists(AutoFolder.Text) ? AutoFolder.Text : null };
        if (dlg.ShowDialog(this) == true) AutoFolder.Text = dlg.FolderName;
    }

    private bool SaveAutoSettings()
    {
        var b = _main.Settings.Backup;
        if (!int.TryParse(AutoKeep.Text, out var keep) || keep < 1) { Status.Text = "Keep at least 1 backup."; AutoKeep.Focus(); return false; }
        if (AutoPw.Password.Length > 0)
        {
            if (AutoPw.Password.Length < 8) { Status.Text = "Use a password of at least 8 characters."; AutoPw.Focus(); return false; }
            BackupService.SavePassword(AutoPw.Password);
            AutoPw.Clear();
            AutoPwState.Text = "saved on this PC";
        }
        if (AutoEnabled.IsChecked == true && BackupService.LoadPassword() is null) { Status.Text = "Set a backup password first."; AutoPw.Focus(); return false; }
        b.Enabled = AutoEnabled.IsChecked == true;
        b.Folder = AutoFolder.Text.Trim() is var f && (f.Length == 0 || string.Equals(Path.GetFullPath(f), BackupService.DefaultFolder, StringComparison.OrdinalIgnoreCase)) ? null : f;
        b.Keep = keep;
        b.IncludeSecrets = AutoSecrets.IsChecked == true;
        _main.Settings.Save();
        return true;
    }

    private async Task BackupNowAsync()
    {
        if (!SaveAutoSettings() || _main.Store is not { } store) return;
        if (BackupService.LoadPassword() is not { } pw) { Status.Text = "Set a backup password first."; AutoPw.Focus(); return; }
        Status.Text = "Backing up…";
        var file = await BackupService.RunAsync(_main.Settings, store, _main.Protector, pw);
        AutoLast.Text = $"Last backup {DateTime.Now:g}";
        Status.Text = $"Saved {Path.GetFileName(file)}";
        RefreshBackups();
    }

    private void RefreshBackups() => BackupList.ItemsSource = BackupService.List(_main.Settings.Backup);

    private void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupService.BackupFile f) return;
        RestorePath.Text = f.Path;
        _opened = null;
        RestoreInfo.Text = "";
        Sections.SelectedIndex = 2;
        // The saved backup password is the likely one; it is never shown.
        if (BackupService.LoadPassword() is { } pw) RestorePw.Password = pw;
        RestorePw.Focus();
    }

    // ---------------- restore ----------------

    private void OnBrowseRestore(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = $"RemoteMan export (*{ExportFile.Extension})|*{ExportFile.Extension}|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        RestorePath.Text = dlg.FileName;
        _opened = null;
        RestoreInfo.Text = "";
    }

    private async void OnOpenRestore(object sender, RoutedEventArgs e) => await OpenRestoreAsync();

    private async void OnRestorePwKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenRestoreAsync(); }
    }

    private async Task<bool> OpenRestoreAsync()
    {
        var path = RestorePath.Text.Trim();
        if (!File.Exists(path)) { Status.Text = "Pick an .rmx file."; return false; }
        var pw = RestorePw.Password;
        Status.Text = "Opening…";
        try
        {
            _opened = await Task.Run(() => ExportFile.ReadFile(path, pw));
        }
        catch (Exception ex) when (ex is WrongPasswordException or InvalidDataException or IOException or FormatException)
        {
            _opened = null;
            Status.Text = ex.Message;
            RestoreInfo.Text = "";
            return false;
        }
        var b = _opened;
        RestoreInfo.Text = $"Contains {b.Entries.Count(x => x.Kind is not (EntryKind.Folder or EntryKind.Credential or EntryKind.Snippet))} connections, " +
                           $"{b.Count(EntryKind.Credential)} credentials, {b.Count(EntryKind.Folder)} folders and {b.Count(EntryKind.Snippet)} snippets" +
                           (b.IncludesSecrets ? ", with passwords" : ", without passwords") +
                           $". Made {b.CreatedAt.LocalDateTime:g}" + (b.CreatedBy is { } by ? $" by {by}" : "") + (b.Machine is { } m ? $" on {m}" : "") + ".";
        Status.Text = "";
        return true;
    }

    private async Task RestoreAsync()
    {
        if (_main.Store is not { } store || _main.Protector is not { } protector) return;
        if (!store.IsOnline) { Status.Text = "Offline: restore needs the database. Try again when it's back."; return; }
        if (_opened is null && !await OpenRestoreAsync()) return;
        var bundle = _opened!;
        var items = ImportPlanner.FromBundle(bundle).Items;

        if (RestoreMerge.IsChecked == true)
        {
            var overwrite = items.Count(i => store.Get(i.Entry.Id) is not null);
            if (!ChoiceDialog.Confirm(this, $"Merge {items.Count} entries?", $"Merge {items.Count} entries? {overwrite} existing entr{(overwrite == 1 ? "y" : "ies")} will be overwritten with the backed-up version.", "Merge", danger: true)) return;
            var plan = await Task.Run(() => ImportPlanner.Plan(items, store.Entries.ToList(), protector, ImportMode.Merge));
            var n = await _main.SaveImportedAsync(plan, new Progress<int>(i => Status.Text = $"Restoring {i} of {plan.Count}…"));
            Status.Text = $"Restored {n} entries";
        }
        else
        {
            // Snippets go back next to the others; everything else into a new folder.
            var folder = new Entry { Kind = EntryKind.Folder, Name = $"Restored {bundle.CreatedAt.LocalDateTime:yyyy-MM-dd HH.mm}" };
            await store.SaveAsync(folder);
            foreach (var i in items.Where(i => i.Entry.Kind == EntryKind.Snippet)) i.Entry.ParentId = null;
            var plan = await Task.Run(() => ImportPlanner.Plan(items, store.Entries.ToList(), protector, ImportMode.AddCopies, folder.Id, skipDuplicates: false));
            foreach (var s in plan.Where(x => x.Kind == EntryKind.Snippet)) s.ParentId = null;
            // Snippets that are already there aren't doubled.
            var have = store.Entries.Where(x => x.Kind == EntryKind.Snippet).Select(x => $"{x.Snippet?.Group}|{x.Name}|{x.Snippet?.Command}").ToHashSet();
            plan.RemoveAll(x => x.Kind == EntryKind.Snippet && have.Contains($"{x.Snippet?.Group}|{x.Name}|{x.Snippet?.Command}"));
            var n = await _main.SaveImportedAsync(plan, new Progress<int>(i => Status.Text = $"Restoring {i} of {plan.Count}…"));
            Status.Text = $"Restored {n} entries into \"{folder.Name}\"";
        }
    }
}
