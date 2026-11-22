using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.Services;

namespace RemoteMan.App.Views;

/// <summary>
/// More > Theme: the shading level, then a card per colour theme, each with a small picture of the app drawn in that theme's colours
/// (tree with a selected row, a tab, a button). Picking one and pressing Apply returns its id.
/// </summary>
public sealed class ThemeWindow : Window
{
    private readonly Dictionary<string, Border> _cards = new();
    private string _picked;

    private Shading _shading;
    private readonly Dictionary<Shading, Button> _shadingButtons = new();

    /// <summary>The theme picked when Apply was pressed.</summary>
    public string Picked => _picked;

    /// <summary>The shading picked when Apply was pressed.</summary>
    public Shading PickedShading => _shading;

    public ThemeWindow(string currentId, Shading currentShading)
    {
        _picked = Themes.Find(currentId).Id;
        _shading = currentShading;
        Title = "Theme";
        Width = 860; Height = 640; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (TryFindResource("RemoteManWindow") is Style ws) Style = ws;

        var root = new DockPanel();

        var footer = new Border { Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "Header");
        footer.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(footer, Dock.Bottom);
        var bar = new DockPanel { LastChildFill = false };
        var note = new TextBlock { Text = "RemoteMan restarts to apply a new theme or shading. Your open tabs can be reopened.", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        bar.Children.Add(note);
        var cancel = new Button { Content = "Cancel", MinWidth = 76, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        var apply = new Button { Content = "Apply", MinWidth = 76, IsDefault = true };
        if (TryFindResource("PrimaryButton") is Style primary) apply.Style = primary;
        apply.Click += (_, _) => { DialogResult = true; };
        DockPanel.SetDock(cancel, Dock.Right); DockPanel.SetDock(apply, Dock.Right);
        bar.Children.Add(cancel); bar.Children.Add(apply);
        footer.Child = bar;
        root.Children.Add(footer);

        // Shading: Flat / Subtle / Raised, above the theme cards.
        var shadingRow = new DockPanel { Margin = new Thickness(16, 14, 16, 2) };
        var label = new TextBlock { Text = "Shading", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        shadingRow.Children.Add(label);
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (level, text, tip) in new[]
                 {
                     (Shading.Flat, "Flat", "Solid fills and thin borders, no shadows"),
                     (Shading.Subtle, "Subtle", "A faint light on buttons and cards, soft shadows along the panels"),
                     (Shading.Raised, "Raised", "Stronger light and shadow; controls clearly stand off the background"),
                 })
        {
            var b = new Button { Content = text, MinWidth = 76, Height = 26, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            b.Click += (_, _) => { _shading = level; HighlightShading(); };
            _shadingButtons[level] = b;
            choices.Children.Add(b);
        }
        shadingRow.Children.Add(choices);
        var hint = new TextBlock { Text = "Light and shadow on buttons, fields, cards and panels. Works with every theme.", FontSize = 11,
                                   VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        shadingRow.Children.Add(hint);

        var cards = new WrapPanel { Margin = new Thickness(10) };
        foreach (var t in Themes.All) cards.Children.Add(Card(t));
        var themeLabel = new TextBlock { Text = "Theme", FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 12, 16, 0) };
        themeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        var stack = new StackPanel();
        stack.Children.Add(shadingRow);
        stack.Children.Add(themeLabel);
        stack.Children.Add(cards);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        scroll.SetResourceReference(BackgroundProperty, "Panel");
        root.Children.Add(scroll);

        Content = root;
        Highlight();
        HighlightShading();
    }

    private void HighlightShading()
    {
        var primary = TryFindResource("PrimaryButton") as Style;
        foreach (var (level, b) in _shadingButtons)
        {
            if (level == _shading && primary is not null) b.Style = primary;
            else b.ClearValue(StyleProperty);
        }
    }

    private Border Card(ThemeDef t)
    {
        var c = t.Resolve();
        SolidColorBrush B(string k) { var b = new SolidColorBrush(c[k]); b.Freeze(); return b; }

        // A tiny window: title strip, tree on the left with one selected row, a tab and a button on the right.
        var mini = new Grid { Height = 96, Background = B("Bg"), ClipToBounds = true };
        mini.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        mini.RowDefinitions.Add(new RowDefinition());
        mini.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        mini.ColumnDefinitions.Add(new ColumnDefinition());

        var titleBar = new Border { Background = B("Header"), BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
        var mark = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = B("Accent"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0) };
        titleBar.Child = mark;
        Grid.SetColumnSpan(titleBar, 2);
        mini.Children.Add(titleBar);

        var tree = new StackPanel { Background = B("Panel") };
        var treeBorder = new Border { BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 1, 0), Child = tree };
        Grid.SetRow(treeBorder, 1);
        mini.Children.Add(treeBorder);
        for (var i = 0; i < 6; i++)
        {
            var selected = i == 2;
            var row = new Border
            {
                Height = 11, Margin = new Thickness(0, i == 0 ? 3 : 0, 0, 0),
                Background = selected ? B("SelectRow") : Brushes.Transparent,
                BorderBrush = selected ? B("Accent") : Brushes.Transparent, BorderThickness = new Thickness(2, 0, 0, 0),
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i is 0 or 4 ? 3 : 10, 0, 0, 0) };
            line.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(1), Background = i is 0 or 4 ? B("FolderIcon") : B("EntryIcon") });
            line.Children.Add(new Border { Width = 24 + (i * 7 % 18), Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(4, 0, 0, 0), Background = selected ? B("TextStrong") : B("Muted"), Opacity = selected ? 1 : 0.7 });
            row.Child = line;
            tree.Children.Add(row);
        }

        var work = new DockPanel { Background = B("Work") };
        Grid.SetRow(work, 1); Grid.SetColumn(work, 1);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Height = 13, Background = B("Panel") };
        DockPanel.SetDock(tabs, Dock.Top);
        var tab = new Border { Width = 46, Background = B("Bg"), BorderBrush = B("Accent"), BorderThickness = new Thickness(0, 2, 0, 0) };
        tab.Child = new Border { Width = 26, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B("TextStrong"), VerticalAlignment = VerticalAlignment.Center };
        tabs.Children.Add(tab);
        tabs.Children.Add(new Border { Width = 40, Child = new Border { Width = 22, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B("Faint"), VerticalAlignment = VerticalAlignment.Center } });
        work.Children.Add(tabs);
        var pane = new Grid { Background = B("Bg") };
        var lines = new StackPanel { Margin = new Thickness(8, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        lines.Children.Add(new Border { Width = 70, Height = 4, CornerRadius = new CornerRadius(2), Background = B("TextStrong"), HorizontalAlignment = HorizontalAlignment.Left });
        lines.Children.Add(new Border { Width = 96, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B("Muted"), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
        lines.Children.Add(new Border { Width = 54, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B("Link"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) });
        pane.Children.Add(lines);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 7, 7) };
        buttons.Children.Add(new Border { Width = 26, Height = 11, CornerRadius = new CornerRadius(2), Background = B("Button"), BorderBrush = B("ButtonBorder"), BorderThickness = new Thickness(1) });
        buttons.Children.Add(new Border { Width = 26, Height = 11, CornerRadius = new CornerRadius(2), Background = B("AccentDim"), BorderBrush = B("Accent"), BorderThickness = new Thickness(1), Margin = new Thickness(4, 0, 0, 0) });
        pane.Children.Add(buttons);
        work.Children.Add(pane);
        mini.Children.Add(work);

        var preview = new Border { BorderBrush = B("Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = mini, ClipToBounds = true };

        // Swatches: background, panels, accent, links.
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var k in new[] { "Bg", "Panel", "SelectRow", "Link", "Accent" })
            swatches.Children.Add(new Border { Width = k == "Accent" ? 28 : 14, Height = 8, Background = B(k) });

        var name = new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        var desc = new TextBlock { Text = t.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var body = new StackPanel();
        body.Children.Add(preview);
        body.Children.Add(swatches);
        body.Children.Add(name);
        body.Children.Add(desc);
        if (t.Id == Themes.Current?.Id)
        {
            var now = new TextBlock { Text = "In use", FontSize = 10.5, Margin = new Thickness(0, 4, 0, 0) };
            now.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            body.Children.Add(now);
        }

        var card = new Border
        {
            Width = 252, Margin = new Thickness(6), Padding = new Thickness(10), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), Child = body, Cursor = Cursors.Hand, Focusable = true, Tag = t.Id,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg");
        System.Windows.Automation.AutomationProperties.SetName(card, t.Name);
        card.MouseLeftButtonUp += (_, _) => Pick(t.Id);
        card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Pick(t.Id); DialogResult = true; } };
        card.KeyDown += (_, e) => { if (e.Key is Key.Space) { Pick(t.Id); e.Handled = true; } };
        _cards[t.Id] = card;
        return card;
    }

    private void Pick(string id)
    {
        _picked = id;
        Highlight();
    }

    private void Highlight()
    {
        foreach (var (id, card) in _cards)
            card.SetResourceReference(Border.BorderBrushProperty, id == _picked ? "Accent" : "Border");
    }
}
