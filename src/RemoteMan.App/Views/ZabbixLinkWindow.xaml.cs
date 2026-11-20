using System.Windows;
using System.Windows.Controls;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;

namespace RemoteMan.App.Views;

/// <summary>Pick the Zabbix host for an entry. Likely matches come first.</summary>
public partial class ZabbixLinkWindow : Window
{
    public sealed record ZabbixHostRow(ZabbixHost Host, string Tag, string Name, string Address, string Group, string Suggestion)
    {
        public bool HasSuggestion => Suggestion.Length > 0;
        public string SearchText => $"{Name} {Host.Host} {Address} {Group}";
    }

    private readonly List<ZabbixHostRow> _all;
    private string? _chosen;

    /// <summary>The picked hostid, or null to unlink.</summary>
    public string? ChosenHostId => _chosen;

    public ZabbixLinkWindow(ZabbixSnapshot snapshot, Entry entry)
    {
        InitializeComponent();
        Heading.Text = $"Link to Zabbix host: {entry.Name}";
        _chosen = entry.ZabbixHostId;

        var suggestions = snapshot.Matcher.Suggestions(entry).ToDictionary(s => s.Host.HostId, s => s.Why);
        _all = snapshot.Hosts
            .Select(h =>
            {
                var health = snapshot.HealthOf(h);
                var tag = RemoteMan.App.ViewModels.MainViewModel.TagFor(health.State) ?? "";
                return new ZabbixHostRow(h, tag, h.DisplayName + (h.Enabled ? "" : " (disabled)"), h.MainInterface?.Address ?? "",
                    string.Join(", ", h.Groups), suggestions.TryGetValue(h.HostId, out var why) ? $"Likely match: {why}" : "");
            })
            .OrderBy(r => r.HasSuggestion ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Hosts.ItemsSource = _all;
        Hosts.SelectionChanged += (_, _) =>
        {
            if (Hosts.SelectedItem is ZabbixHostRow r) { _chosen = r.Host.HostId; ShowCurrent(); }
        };
        var current = _all.FirstOrDefault(r => r.Host.HostId == _chosen);
        if (current is not null) { Hosts.SelectedItem = current; Hosts.ScrollIntoView(current); }
        ShowCurrent();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void ShowCurrent()
    {
        var r = _all.FirstOrDefault(x => x.Host.HostId == _chosen);
        Current.Text = r is null ? "Not linked. Pick a host below." : $"{r.Host.DisplayName}  (hostid {r.Host.HostId})";
        Current.Foreground = (System.Windows.Media.Brush)FindResource(r is null ? "Faint" : "Text");
        UnlinkButton.IsEnabled = r is not null;
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Hosts.ItemsSource = q.Length == 0 ? _all : _all.Where(r => r.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnUnlink(object sender, RoutedEventArgs e)
    {
        _chosen = null;
        Hosts.SelectedItem = null;
        ShowCurrent();
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Hosts.SelectedItem is ZabbixHostRow) DialogResult = true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
