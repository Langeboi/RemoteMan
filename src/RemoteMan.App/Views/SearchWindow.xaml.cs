using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Model;

namespace RemoteMan.App.Views;

/// <summary>
/// Ctrl K: one box that searches every entry's name, hosts, tags, custom fields, description and folder,
/// says where each hit matched, and connects, jumps to the entry in the tree, or opens the editor.
/// </summary>
public partial class SearchWindow : Window
{
    /// <summary>One result row: the hit split around the matched text so the row can highlight it.</summary>
    public sealed class Row(SearchHit hit, bool showHeading)
    {
        public SearchHit Hit { get; } = hit;
        public string Name => Hit.Entry.Name;
        public string Path => Hit.Path.Length > 0 ? Hit.Path : "(top level)";
        public string Label => Hit.Label;
        public string Before => Hit.Context[..Hit.MatchStart];
        public string Match => Hit.Context.Substring(Hit.MatchStart, Hit.MatchLength);
        public string After => Hit.Context[(Hit.MatchStart + Hit.MatchLength)..];
        public string KindName => Hit.Entry.Kind switch { EntryKind.Folder => "Folder", EntryKind.Credential => "Credential", _ => EntryNode.KindTagFor(Hit.Entry.Kind) };
        public ImageSource Icon => EntryIcons.Render(Hit.Entry.Icon, Hit.Entry.IconColor, Hit.Entry.Kind == EntryKind.Folder ? "DIR" : EntryNode.KindTagFor(Hit.Entry.Kind));
        public bool ShowHeading { get; } = showHeading;
        public string GroupHeading => Hit.Field switch
        {
            SearchField.Name => "NAMES",
            SearchField.Host => "HOSTS AND ADDRESSES",
            SearchField.Tag => "TAGS",
            SearchField.Field => "CUSTOM FIELDS",
            SearchField.Description => "DESCRIPTIONS",
            _ => "FOLDERS",
        };
    }

    /// <summary>Kept for the session, shown when the box is empty.</summary>
    private static readonly List<string> Recent = new();

    private readonly MainViewModel _vm;
    private List<SearchHit> _hits = new();
    private bool _closing;

    public SearchWindow(MainViewModel vm, string? initial = null)
    {
        InitializeComponent();
        _vm = vm;
        Loaded += (_, _) =>
        {
            Query.Text = initial ?? "";
            Query.Focus();
            Query.SelectAll();
            Run();
        };
        Deactivated += (_, _) => CloseOnce();
    }

    /// <summary>Opens under <paramref name="anchor"/> (the title bar search box), centred on it.</summary>
    public void ShowUnder(FrameworkElement anchor)
    {
        var owner = Window.GetWindow(anchor);
        Owner = owner;
        var pt = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight + 6));
        var dpi = VisualTreeHelper.GetDpi(anchor);
        var x = pt.X / dpi.DpiScaleX - Width / 2;
        var y = pt.Y / dpi.DpiScaleY;
        if (owner is not null)
        {
            var left = owner.WindowState == WindowState.Maximized ? 0 : owner.Left;
            var right = left + owner.ActualWidth;
            if (owner.ActualWidth > Width + 16) x = Math.Max(left + 8, Math.Min(x, right - Width - 8));
            Height = Math.Min(Height, Math.Max(300, owner.ActualHeight - (y - (owner.WindowState == WindowState.Maximized ? 0 : owner.Top)) - 40));
        }
        Left = x;
        Top = y;
        Show();
        Activate();
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Run();
    private void OnFilter(object sender, RoutedEventArgs e) { if (IsLoaded) Run(); }

    private void Run()
    {
        var text = Query.Text;
        var store = _vm.Store;
        if (text.Trim().Length == 0 || store is null)
        {
            _hits = new();
            Results.ItemsSource = null;
            Empty.Visibility = Visibility.Visible;
            Empty.Text = Recent.Count == 0
                ? "Type to search every entry. Passwords and secret fields are never searched."
                : "Recent searches:\n\n" + string.Join("\n", Recent);
            AllLabel.Text = "All"; EntriesLabel.Text = "Entries"; CredsLabel.Text = "Credentials"; FoldersLabel.Text = "Folders";
            ShowPreview(null);
            return;
        }
        var all = GlobalSearch.Find(store.Entries.ToList(), text);
        AllLabel.Text = $"All  {all.Count}";
        EntriesLabel.Text = $"Entries  {all.Count(h => h.Entry.Kind is not (EntryKind.Credential or EntryKind.Folder))}";
        CredsLabel.Text = $"Credentials  {all.Count(h => h.Entry.Kind == EntryKind.Credential)}";
        FoldersLabel.Text = $"Folders  {all.Count(h => h.Entry.Kind == EntryKind.Folder)}";
        _hits = all.Where(h => FilterEntries.IsChecked == true ? h.Entry.Kind is not (EntryKind.Credential or EntryKind.Folder)
            : FilterCreds.IsChecked == true ? h.Entry.Kind == EntryKind.Credential
            : FilterFolders.IsChecked != true || h.Entry.Kind == EntryKind.Folder).ToList();
        var rows = _hits.Select((h, i) => new Row(h, i == 0 || _hits[i - 1].Field != h.Field)).ToList();
        Results.ItemsSource = rows;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = rows.Count == 0 ? $"Nothing matches \"{text.Trim()}\". Press Enter to connect to it as a host." : "";
        if (rows.Count > 0) Results.SelectedIndex = 0; else ShowPreview(null);
    }

    private SearchHit? Current => (Results.SelectedItem as Row)?.Hit;

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        ShowPreview(Current);
        if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) { if (Current is { } h) Act(h, Keyboard.Modifiers == ModifierKeys.Shift ? "reveal" : "open"); }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        var count = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0: Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, count - 1); e.Handled = true; break;
            case Key.Up when count > 0: Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0); e.Handled = true; break;
            case Key.PageDown when count > 0: Results.SelectedIndex = Math.Min(Results.SelectedIndex + 8, count - 1); e.Handled = true; break;
            case Key.PageUp when count > 0: Results.SelectedIndex = Math.Max(Results.SelectedIndex - 8, 0); e.Handled = true; break;
            case Key.Escape: CloseOnce(); e.Handled = true; break;
            case Key.Enter:
                e.Handled = true;
                if (Current is { } h) Act(h, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "reveal" : "open");
                else if (Query.Text.Trim() is { Length: > 0 } host && !GlobalSearch.Prefixes.Any(p => host.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    Remember();
                    CloseOnce();
                    _vm.QuickHost = host;
                    _vm.QuickConnectHostCommand.Execute(null);
                }
                break;
            case Key.E when Keyboard.Modifiers == ModifierKeys.Control && Current is { } he: Act(he, "edit"); e.Handled = true; break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && Current is { } hc: Act(hc, "copyhost"); e.Handled = true; break;
        }
    }

    private void Remember()
    {
        var q = Query.Text.Trim();
        if (q.Length == 0) return;
        Recent.RemoveAll(r => r.Equals(q, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, q);
        if (Recent.Count > 6) Recent.RemoveAt(Recent.Count - 1);
    }

    /// <summary>open: connect (folders and credentials: show them); reveal: select in the tree with its Overview; edit; copyhost.</summary>
    private void Act(SearchHit hit, string what)
    {
        Remember();
        var e = hit.Entry;
        if (what == "copyhost")
        {
            var host = e.Endpoints.FirstOrDefault()?.ToString() ?? e.Web?.Url;
            if (host is { Length: > 0 }) { try { Clipboard.SetText(host); _vm.Message = $"Copied {host}"; } catch (Exception) { } }
            return;
        }
        CloseOnce();
        _vm.RevealEntry(e.Id);
        if (what == "edit") { _vm.EditCommand.Execute(null); return; }
        if (what == "open" && e.IsConnectable) { _vm.ConnectCommand.Execute(null); return; }
        if (e.Kind != EntryKind.Folder) _vm.ShowOverview();
    }

    // ---------------- preview ----------------

    private void ShowPreview(SearchHit? hit)
    {
        Preview.Children.Clear();
        if (hit is null) return;
        var e = hit.Entry;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), Source = new Row(hit, false).Icon });
        var title = new TextBlock { Text = e.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        head.Children.Add(title);
        Preview.Children.Add(head);
        Preview.Children.Add(Muted(hit.Path.Length > 0 ? hit.Path : "Top level", 12));

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void AddRow(string k, string? v, bool mono = false)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = Muted(k, 12);
            key.Margin = new Thickness(0, 2, 8, 2);
            Grid.SetRow(key, r);
            grid.Children.Add(key);
            var val = new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            if (mono) val.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            Grid.SetRow(val, r); Grid.SetColumn(val, 1);
            grid.Children.Add(val);
        }
        if (e.Kind == EntryKind.Web) AddRow("Address", e.Web?.Url, mono: true);
        for (var i = 0; i < e.Endpoints.Count; i++) AddRow(i == 0 && e.Kind != EntryKind.Web ? "Host" : "Fallback", e.Endpoints[i].ToString(), mono: true);
        var login = e.Login switch
        {
            LoginSource.Saved when e.CredentialId is { } cid => "Credential: " + (_vm.Store?.Get(cid)?.Name ?? "(deleted)"),
            LoginSource.Embedded => e.Credential?.UserName,
            LoginSource.Inherited => "From the folder",
            LoginSource.WindowsLogin => "My Windows login",
            _ => e.Kind == EntryKind.Credential ? e.Credential?.UserName : null,
        };
        AddRow(e.Kind == EntryKind.Credential ? "User name" : "Login", login);
        AddRow("Tags", e.Tags);
        foreach (var f in e.Fields ?? []) AddRow(f.Name, f.Secret ? "●●●●●●" : f.Value);
        AddRow("Description", e.Description);
        Preview.Children.Add(grid);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (e.IsConnectable) buttons.Children.Add(MakeButton("Connect", () => Act(hit, "open"), primary: true));
        buttons.Children.Add(MakeButton("Show in tree", () => Act(hit, "reveal")));
        buttons.Children.Add(MakeButton("Edit", () => Act(hit, "edit")));
        Preview.Children.Add(buttons);
    }

    private static TextBlock Muted(string text, double size)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private Button MakeButton(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = 70, Margin = new Thickness(0, 0, 6, 6), Focusable = false };
        if (primary) b.SetResourceReference(StyleProperty, "PrimaryButton");
        b.Click += (_, _) => click();
        return b;
    }
}
