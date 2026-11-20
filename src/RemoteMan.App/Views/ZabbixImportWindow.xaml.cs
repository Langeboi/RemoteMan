using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

public sealed partial class ZabbixImportRow : ObservableObject
{
    public ZabbixImportRow(ZabbixImportCandidate c, bool selected)
    {
        Candidate = c;
        _kind = c.Kind;
        _selected = selected && c.Existing is null;
    }

    public ZabbixImportCandidate Candidate { get; }
    public static IReadOnlyList<EntryKind> KindChoices { get; } = [EntryKind.Rdp, EntryKind.Ssh, EntryKind.Web, EntryKind.Vnc];
    public IReadOnlyList<EntryKind> Kinds => KindChoices;

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private EntryKind _kind;

    public bool CanSelect => Candidate.Existing is null;
    public string Name => Candidate.Host.DisplayName;
    public string Address => Candidate.Address;
    public string Folder => Candidate.FolderPath;
    public string Hint => Candidate.Existing is { } e
        ? $"Already in RemoteMan as \"{e.Name}\"" + (e.ZabbixHostId == Candidate.Host.HostId ? "" : ", will be linked")
        : Candidate.Host.Templates.Count > 0 ? string.Join(", ", Candidate.Host.Templates) : "No templates";
}

/// <summary>Choose Zabbix hosts to turn into entries. Hosts that already have an entry just get linked.</summary>
public partial class ZabbixImportWindow : Window
{
    private const string AllGroups = "All groups";
    private readonly ZabbixSnapshot _snapshot;
    private readonly EntryStore _store;
    private readonly Guid? _parent;
    private List<ZabbixImportRow> _rows = new();
    private readonly HashSet<string> _deselected = new();
    private readonly Dictionary<string, EntryKind> _kinds = new();
    private bool _ready;

    public ZabbixImportPlan? Plan { get; private set; }

    public ZabbixImportWindow(ZabbixSnapshot snapshot, EntryStore store, string zabbixUrl, Guid? parentFolder)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _store = store;
        _parent = parentFolder;
        Source.Text = $"{TryHost(zabbixUrl)} · {snapshot.Hosts.Count(h => h.Enabled)} hosts";
        GroupBox.ItemsSource = new[] { AllGroups }.Concat(snapshot.Hosts.SelectMany(h => h.Groups).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase)).ToList();
        GroupBox.SelectedIndex = 0;
        _ready = true;
        Rebuild();
    }

    private static string TryHost(string url)
    {
        try { return ZabbixClient.ApiEndpoint(url).Host; } catch { return url; }
    }

    private void Rebuild()
    {
        if (!_ready) return;
        // Keep the person's choices across option changes.
        foreach (var r in _rows)
        {
            if (!r.Selected && r.CanSelect) _deselected.Add(r.Candidate.Host.HostId); else _deselected.Remove(r.Candidate.Host.HostId);
            _kinds[r.Candidate.Host.HostId] = r.Kind;
        }
        var address = AddressBox.SelectedIndex == 1 ? ZabbixAddressChoice.HostName : ZabbixAddressChoice.InterfaceAddress;
        var folders = FolderBox.SelectedIndex == 1 ? ZabbixFolderChoice.SingleFolder : ZabbixFolderChoice.PerHostGroup;
        var group = GroupBox.SelectedItem as string ?? AllGroups;
        _rows = ZabbixImport.Candidates(_snapshot, _store.Entries, address, folders)
            .Where(c => group == AllGroups || c.Host.Groups.Contains(group))
            .Where(c => HideExisting.IsChecked != true || c.Existing is null)
            .Select(c =>
            {
                var row = new ZabbixImportRow(c, !_deselected.Contains(c.Host.HostId));
                if (_kinds.TryGetValue(c.Host.HostId, out var k)) row.Kind = k;
                row.PropertyChanged += (_, _) => UpdateSummary();
                return row;
            })
            .ToList();
        Rows.ItemsSource = _rows;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var n = _rows.Count(r => r.Selected);
        var link = _rows.Count(r => r.Candidate.Existing is { } e && e.ZabbixHostId != r.Candidate.Host.HostId);
        Summary.Text = $"{n} new entr{(n == 1 ? "y" : "ies")}" + (link > 0 ? $", {link} existing one{(link == 1 ? "" : "s")} get linked" : "");
        ImportButton.Content = n > 0 ? $"Import {n}" : link > 0 ? "Link" : "Import";
        ImportButton.IsEnabled = n > 0 || link > 0;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Rebuild();
    private void OnOptionChanged(object sender, SelectionChangedEventArgs e) => Rebuild();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows.Where(r => r.CanSelect)) r.Selected = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = false;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Selected || (r.Candidate.Existing is not null))
            .Select(r => r.Candidate with { Kind = r.Kind });
        Plan = ZabbixImport.Plan(chosen, _store.Entries, _parent, _store.User);
        DialogResult = true;
    }
}
