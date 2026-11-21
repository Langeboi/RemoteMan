using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;

namespace RemoteMan.App.Views;

/// <summary>Pick the Oxidized device for an entry. Likely matches come first.</summary>
public partial class OxidizedLinkWindow : Window
{
    public sealed record NodeRow(OxidizedNode Node, Brush Dot, string State, string Name, string Address, string Group, string Suggestion)
    {
        public bool HasSuggestion => Suggestion.Length > 0;
        public string SearchText => $"{Name} {Address} {Group} {Node.Model}";
    }

    private readonly List<NodeRow> _all;
    private string? _chosen;

    /// <summary>The picked device's full name, or null to unlink.</summary>
    public string? ChosenNode => _chosen;

    public OxidizedLinkWindow(OxidizedSnapshot snapshot, Entry entry)
    {
        InitializeComponent();
        Heading.Text = $"Link to Oxidized device: {entry.Name}";
        _chosen = entry.OxidizedNode;

        var suggestions = snapshot.Matcher.Suggestions(entry).GroupBy(s => s.Node.FullName).ToDictionary(g => g.Key, g => g.First().Why);
        _all = snapshot.Nodes
            .Select(n => new NodeRow(n, (Brush)FindResource(n.IsOk ? "Ok" : n.IsFailing ? "Warn" : "Faint"), $"Last backup: {n.StatusText}",
                n.Name + (string.IsNullOrEmpty(n.Model) ? "" : $"  ({n.Model})"), n.Ip ?? "", n.Group ?? "",
                suggestions.TryGetValue(n.FullName, out var why) ? $"Likely match: {why}" : ""))
            .OrderBy(r => r.HasSuggestion ? 0 : 1).ThenBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Hosts.ItemsSource = _all;
        Hosts.SelectionChanged += (_, _) =>
        {
            if (Hosts.SelectedItem is NodeRow r) { _chosen = r.Node.FullName; ShowCurrent(); }
        };
        var current = _all.FirstOrDefault(r => r.Node.FullName == _chosen);
        if (current is not null) { Hosts.SelectedItem = current; Hosts.ScrollIntoView(current); }
        ShowCurrent();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void ShowCurrent()
    {
        var r = _all.FirstOrDefault(x => x.Node.FullName == _chosen);
        Current.Text = r is null ? (_chosen is null ? "Not linked. Pick a device below." : $"{_chosen} (no longer in Oxidized)") : r.Node.FullName;
        Current.Foreground = (Brush)FindResource(r is null ? "Faint" : "Text");
        UnlinkButton.IsEnabled = _chosen is not null;
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
        if (Hosts.SelectedItem is NodeRow) DialogResult = true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
