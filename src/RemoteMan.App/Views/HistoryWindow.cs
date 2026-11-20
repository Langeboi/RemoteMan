using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

/// <summary>
/// Every saved version of one entry, newest first: who, when and what changed field by field, with
/// restore. Passwords show as "changed", never as values. A restore is a new save, so it shows up here too.
/// Built in code like <see cref="ChoiceDialog"/>, in the same dark look.
/// </summary>
public sealed class HistoryWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Entry _entry;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private int _expanded;

    public static void Show(Window? owner, MainViewModel vm, Entry entry)
    {
        var w = new HistoryWindow(vm, entry);
        if (owner is not null) w.Owner = owner;
        w.ShowDialog();
    }

    private HistoryWindow(MainViewModel vm, Entry entry)
    {
        _vm = vm;
        _entry = entry;
        Title = $"History: {entry.Name}";
        Width = 600; Height = 560; MinWidth = 460; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (TryFindResource("RemoteManWindow") is Style style) Style = style;
        SetResourceReference(BackgroundProperty, "Bg");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var root = new DockPanel();

        var header = new Border { Height = 28, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 0, 10, 0) };
        header.SetResourceReference(Border.BackgroundProperty, "Header");
        header.SetResourceReference(Border.BorderBrushProperty, "Border");
        header.Child = new TextBlock { Text = $"History: {entry.Name}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 8, 10, 8) };
        footer.SetResourceReference(Border.BackgroundProperty, "Header");
        footer.SetResourceReference(Border.BorderBrushProperty, "Border");
        var fdock = new DockPanel();
        var close = new Button { Content = "Close", MinWidth = 76, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        fdock.Children.Add(close);
        _status.Text = "Versions are kept for 6 months.";
        fdock.Children.Add(_status);
        footer.Child = fdock;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(8, 6, 8, 6) };
        scroll.SetResourceReference(BackgroundProperty, "Panel");
        root.Children.Add(scroll);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _list.Children.Clear();
        _list.Children.Add(Muted("Loading…"));
        IReadOnlyList<HistoryRecord> history;
        try { history = _vm.Store is { } s ? await s.GetHistoryAsync(_entry.Id) : []; }
        catch (Exception ex) { _list.Children.Clear(); _list.Children.Add(Muted("Couldn't read the history: " + ex.Message)); return; }
        _list.Children.Clear();
        if (history.Count == 0)
        {
            _list.Children.Add(Muted(_vm.Store?.IsOnline == false
                ? "History needs the database, which can't be reached right now."
                : "No saved versions yet. Changes are recorded from this version of RemoteMan on."));
            return;
        }
        for (var i = 0; i < history.Count; i++)
            _list.Children.Add(Row(history, i));
        if (history.Count >= 200) _list.Children.Add(Muted("Showing the latest 200 versions."));
    }

    private UIElement Row(IReadOnlyList<HistoryRecord> history, int i)
    {
        var h = history[i];
        var previous = i + 1 < history.Count ? history[i + 1] : null;
        var snap = h.Snapshot;

        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var who = snap.UpdatedBy ?? "someone";
        grid.Children.Add(Avatar(who));

        var body = new StackPanel();
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(new Run(TeamText.Short(who)) { FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new Run(" " + TeamText.Describe(h)));
        if (i == 0 && !snap.Deleted) title.Inlines.Add(new Run("  (current)") { Foreground = Res("Faint") });
        body.Children.Add(title);

        var details = new StackPanel { Visibility = i == _expanded ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 2) };
        var changes = previous is null ? new List<FieldChange>() : EntryDiff.Diff(previous.Snapshot, snap, NameOf);
        if (changes.Count > 0) details.Children.Add(DiffGrid(changes));
        else if (h.Action == HistoryAction.Added) details.Children.Add(Muted("The first saved version."));
        else if (previous is null) details.Children.Add(Muted("Earlier versions weren't recorded."));

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (i == 0 && snap.Deleted)
            actions.Children.Add(Small("Restore", "Bring the entry back as it was before it was deleted", () => RestoreAsync(snap)));
        else if (i == 0 && previous is not null && !previous.Snapshot.Deleted)
            actions.Children.Add(Small("Undo this change", "Restore the version before this change", () => RestoreAsync(previous.Snapshot)));
        else if (i > 0 && !snap.Deleted)
            actions.Children.Add(Small("Restore this version", "Save this version as the newest one", () => RestoreAsync(snap)));
        if (actions.Children.Count > 0) details.Children.Add(actions);
        body.Children.Add(details);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var when = new TextBlock { Text = TeamText.When(snap.UpdatedAt), FontSize = 11, Margin = new Thickness(10, 1, 0, 0), ToolTip = snap.UpdatedAt.ToLocalTime().ToString("F") };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        Grid.SetColumn(when, 2);
        grid.Children.Add(when);

        var row = new Border
        {
            Padding = new Thickness(6, 7, 6, 7), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
            CornerRadius = new CornerRadius(3), Child = grid, Cursor = Cursors.Hand, Background = Brushes.Transparent,
        };
        row.SetResourceReference(Border.BorderBrushProperty, "BorderSoft");
        if (i == _expanded) row.SetResourceReference(Border.BackgroundProperty, "Panel2");
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) is not null) return;
            var open = details.Visibility != Visibility.Visible;
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) row.SetResourceReference(Border.BackgroundProperty, "Panel2"); else row.Background = Brushes.Transparent;
        };
        return row;
    }

    private string? NameOf(Guid id) => _vm.Store?.Get(id)?.Name;

    private static UIElement DiffGrid(IReadOnlyList<FieldChange> changes)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mono = Application.Current?.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas");
        for (var r = 0; r < changes.Count; r++)
        {
            var c = changes[r];
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = c.Label, FontSize = 11, Margin = new Thickness(0, 1, 8, 1), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = c.Label };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            Grid.SetRow(label, r);
            g.Children.Add(label);

            var value = new TextBlock { FontFamily = mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            if (c.IsSecret)
                value.Inlines.Add(new Run(c.Before is null ? "set" : c.After is null ? "removed" : "changed") { Foreground = Res("Muted") });
            else
            {
                if (c.Before is { } b)
                    value.Inlines.Add(new Run(" " + b + " ") { Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xA6, 0xA1)), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x1C, 0x1E)), TextDecorations = TextDecorations.Strikethrough });
                if (c.Before is not null && c.After is not null) value.Inlines.Add(new Run("  "));
                if (c.After is { } a)
                    value.Inlines.Add(new Run(" " + a + " ") { Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xE0, 0xC9)), Background = new SolidColorBrush(Color.FromRgb(0x14, 0x30, 0x2A)) });
                if (c.After is null && c.Before is null) value.Inlines.Add(new Run("cleared"));
            }
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            g.Children.Add(value);
        }
        return g;
    }

    private async Task RestoreAsync(Entry snapshot)
    {
        _status.Text = "Restoring…";
        var message = await _vm.RestoreAsync(snapshot);
        _status.Text = message;
        _expanded = 0;
        await LoadAsync();
    }

    internal static Border Avatar(string user, double size = 20)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)new BrushConverter().ConvertFromString(PresenceChip.ColorFor(user))!, ToolTip = user,
            Child = new TextBlock
            {
                Text = Presence.InitialsOf(user), FontSize = size * 0.42, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ((TextBlock)b.Child).SetResourceReference(TextBlock.ForegroundProperty, "Bg");
        return b;
    }

    private static Button Small(string text, string tip, Func<Task> click)
    {
        var b = new Button { Content = text, Height = 24, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0), FontSize = 11, ToolTip = tip };
        b.Click += async (_, _) => { b.IsEnabled = false; try { await click(); } finally { b.IsEnabled = true; } };
        return b;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }
}
