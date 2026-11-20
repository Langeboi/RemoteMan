using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Import;
using RemoteMan.Core.Model;
using RemoteMan.Core.Transfer;

namespace RemoteMan.App.Views;

/// <summary>One row of the import preview.</summary>
public sealed partial class ImportRow : ObservableObject
{
    private readonly Action<ImportRow, bool> _changed;

    public ImportRow(ImportItem item, Action<ImportRow, bool> changed)
    {
        Item = item;
        _changed = changed;
        _include = item.Include;
    }

    public ImportItem Item { get; }
    public string Kind => Item.Entry.Kind == EntryKind.Folder ? "DIR" : EntryNode.KindTagFor(Item.Entry.Kind);
    public string Name => Item.Entry.Name;
    public Thickness Indent => new(Item.Depth * 14, 0, 4, 0);
    public string Host => Item.Entry.Kind == EntryKind.Web ? Item.Entry.Web?.Url ?? "" : Item.Entry.Endpoints.FirstOrDefault()?.ToString() ?? "";
    public string Login { get; init; } = "";

    [ObservableProperty] private bool _include;
    partial void OnIncludeChanged(bool value)
    {
        Item.Include = value;
        _changed(this, value);
    }
}

public partial class ImportWindow : Window
{
    public sealed record FolderChoice(Guid? Id, string Name) { public override string ToString() => Name; }

    private readonly MainViewModel _main;
    private ImportResult? _result;
    private List<ImportRow> _rows = new();
    private bool _cascading;
    private string[] _rdpFiles = [];

    public ImportWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;

        var folders = new List<FolderChoice> { new(null, "(top level)") };
        if (main.Store is { } store)
        {
            var all = store.Entries.Where(e => e.Kind == EntryKind.Folder).ToList();
            string PathOf(Entry f) => f.ParentId is { } p && all.FirstOrDefault(x => x.Id == p) is { } parent ? PathOf(parent) + " / " + f.Name : f.Name;
            folders.AddRange(all.Select(f => new FolderChoice(f.Id, PathOf(f))).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
        }
        TargetBox.ItemsSource = folders;
        // Default target: the folder selected in the tree.
        var sel = main.Selected?.Entry;
        var selFolder = sel?.Kind == EntryKind.Folder ? sel.Id : sel?.ParentId;
        TargetBox.SelectedItem = folders.FirstOrDefault(f => f.Id == selFolder) ?? folders[0];
        Loaded += (_, _) => OnSourceChanged(this, new RoutedEventArgs());
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _result = null;
        ShowRows();
        if (SrcRdm.IsChecked == true)
        {
            FileLabel.Text = "File"; BrowseButton.Content = "Browse…"; FileBox.Text = "";
            Notes.Text = "Folders, RDP, SSH, web and VNC entries and credentials come across with hosts, ports and user names. " +
                         "Passwords come along only if the export holds them in clear text.";
        }
        else if (SrcRdp.IsChecked == true)
        {
            FileLabel.Text = "Files"; BrowseButton.Content = "Add files…"; FileBox.Text = "";
            _rdpFiles = [];
            Notes.Text = "Reads address, port, gateway, user name and domain, screen size and drive/clipboard redirection. " +
                         "Saved passwords in .rdp files are locked to the PC that saved them and are skipped.";
        }
        else
        {
            FileLabel.Text = "From"; BrowseButton.Content = "Load .reg…";
            Notes.Text = "SSH sessions with host, port, user name, startup command and the .ppk key (stored as a key credential).";
            LoadPuttyRegistry();
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (SrcRdm.IsChecked == true)
        {
            var dlg = new OpenFileDialog { Filter = "RDM export (*.rdm;*.json;*.xml)|*.rdm;*.json;*.xml|All files|*.*", Title = "Open an RDM export" };
            if (dlg.ShowDialog(this) != true) return;
            FileBox.Text = dlg.FileName;
            Run(() => RdmImporter.Import(File.ReadAllText(dlg.FileName)));
        }
        else if (SrcRdp.IsChecked == true)
        {
            var dlg = new OpenFileDialog { Filter = "Remote Desktop files (*.rdp)|*.rdp", Multiselect = true, Title = "Add .rdp files" };
            if (dlg.ShowDialog(this) != true) return;
            _rdpFiles = _rdpFiles.Concat(dlg.FileNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            FileBox.Text = _rdpFiles.Length == 1 ? _rdpFiles[0] : $"{_rdpFiles.Length} files: {string.Join(", ", _rdpFiles.Select(Path.GetFileName))}";
            Run(() => RdpFileImporter.Import(_rdpFiles.Select(f => (Path.GetFileName(f), File.ReadAllText(f)))));
        }
        else
        {
            var dlg = new OpenFileDialog { Filter = "Registry export (*.reg)|*.reg", Title = "Open a PuTTY sessions .reg export" };
            if (dlg.ShowDialog(this) != true) return;
            FileBox.Text = dlg.FileName;
            Run(() => PuttyImporter.Import(PuttyImporter.ParseRegFile(File.ReadAllText(dlg.FileName)), ReadKeyFile));
        }
    }

    private void LoadPuttyRegistry()
    {
        FileBox.Text = @"HKCU\" + PuttyImporter.RegistryPath;
        Run(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(PuttyImporter.RegistryPath);
            if (key is null) throw new InvalidDataException("No PuTTY sessions are saved on this PC. Load a .reg export instead.");
            var sessions = key.GetSubKeyNames().Select(name =>
            {
                using var sk = key.OpenSubKey(name)!;
                var values = sk.GetValueNames().ToDictionary(v => v, v => Convert.ToString(sk.GetValue(v), System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    StringComparer.OrdinalIgnoreCase);
                return new PuttySession(name, values);
            }).ToList();
            return PuttyImporter.Import(sessions, ReadKeyFile);
        });
    }

    private static string? ReadKeyFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Run(Func<ImportResult> read)
    {
        try
        {
            _result = read();
            ShowRows();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _result = null;
            ShowRows();
            Summary.Text = ex.Message;
        }
    }

    private void ShowRows()
    {
        if (_result is null) { _rows = new(); Preview.ItemsSource = null; ImportButton.IsEnabled = false; Summary.Text = ""; return; }
        var byId = _result.Items.ToDictionary(i => i.Entry.Id);
        string LoginOf(Entry e) =>
            e.Credential is { } c ? (c.Domain is { Length: > 0 } d ? $"{d}\\{c.UserName}" : c.UserName ?? "") :
            e.CredentialId is { } id && byId.TryGetValue(id, out var cred) ? cred.Entry.Name : "";
        _rows = _result.Items.Select(i => new ImportRow(i, OnRowChanged) { Login = LoginOf(i.Entry) }).ToList();
        Preview.ItemsSource = _rows;
        if (_result.Warnings.Count > 0) Notes.Text = string.Join("\n", _result.Warnings);
        UpdateSummary();
    }

    /// <summary>Unticking a folder unticks what's inside it (and ticking ticks it again).</summary>
    private void OnRowChanged(ImportRow row, bool include)
    {
        if (!_cascading && row.Item.Entry.Kind == EntryKind.Folder)
        {
            _cascading = true;
            var inside = new HashSet<Guid> { row.Item.Entry.Id };
            foreach (var r in _rows.SkipWhile(r => r != row).Skip(1))
            {
                if (r.Item.Entry.ParentId is not { } p || !inside.Contains(p)) continue;
                inside.Add(r.Item.Entry.Id);
                r.Include = include;
            }
            _cascading = false;
        }
        UpdateSummary();
    }

    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        _cascading = true;
        foreach (var r in _rows) r.Include = AllBox.IsChecked == true;
        _cascading = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var entries = _rows.Where(r => r.Item.Entry.Kind != EntryKind.Folder).ToList();
        var n = entries.Count(r => r.Include);
        Summary.Text = _result is null ? Summary.Text : $"{n} of {entries.Count} selected";
        ImportButton.Content = n == 1 ? "Import 1 entry" : $"Import {n} entries";
        ImportButton.IsEnabled = _rows.Any(r => r.Include);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_result is null || _main.Store is not { } store || _main.Protector is not { } protector) return;
        if (!store.IsOnline) { Summary.Text = "Offline: import needs the database. Try again when it's back."; return; }
        ImportButton.IsEnabled = false;
        var target = (TargetBox.SelectedItem as FolderChoice)?.Id;
        try
        {
            var plan = await Task.Run(() => ImportPlanner.Plan(_result.Items, store.Entries.ToList(), protector, ImportMode.AddCopies, target, SkipDupes.IsChecked == true));
            var wanted = _result.Items.Count(i => i.Include);
            var saved = await _main.SaveImportedAsync(plan, new Progress<int>(n => Summary.Text = $"Saving {n} of {plan.Count}…"));
            var skipped = wanted - saved;
            _main.Message = $"Imported {saved} entr{(saved == 1 ? "y" : "ies")}" + (skipped > 0 ? $", {skipped} already there" : "");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Summary.Text = $"Import stopped: {ex.Message}";
            ImportButton.IsEnabled = true;
        }
    }
}
