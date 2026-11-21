using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using RemoteMan.Core.Model;

namespace RemoteMan.App.Views;

/// <summary>
/// Change icon for any entry: the normal icon, a line glyph or a vendor mark, and a colour, with a live preview of the tree row.
/// Returns false when cancelled; icon and color come back null for "the normal one".
/// </summary>
public static class EntryIconPicker
{
    public static bool Show(Window owner, Entry entry, out string? icon, out string? color)
    {
        var folder = entry.Kind == EntryKind.Folder;
        var tag = folder ? "DIR" : RemoteMan.App.ViewModels.EntryNode.KindTagFor(entry.Kind);
        var kindName = EntryIcons.KindName(tag);
        string? glyph = folder && entry.Icon == "folder" ? null : entry.Icon;
        string? hex = folder && string.Equals(entry.IconColor, FolderIcons.DefaultColor, StringComparison.OrdinalIgnoreCase) ? null : entry.IconColor;
        var accent = Application.Current.TryFindResource("Accent") as Brush ?? Brushes.Teal;
        var soft = Application.Current.TryFindResource("Border") as Brush ?? Brushes.Gray;
        var muted = Application.Current.TryFindResource("Muted") as Brush ?? Brushes.Gray;
        var panelBg = Application.Current.TryFindResource("Panel") as Brush;

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Width = 344 };

        // Preview: how the row looks in the tree.
        var preview = new Image { Width = 16, Height = 16, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { Height = 26, Margin = new Thickness(0, 0, 0, 12), Background = panelBg };
        row.Children.Add(preview);
        row.Children.Add(new TextBlock { Text = entry.Name, FontWeight = folder ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(row);

        TextBlock Label(string text) => new() { Text = text, Foreground = muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };

        panel.Children.Add(Label($"Glyph (the first one is the normal {kindName} icon)"));
        var glyphs = new UniformGrid { Columns = 8, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(glyphs);
        panel.Children.Add(Label("Vendors and products"));
        var vendors = new UniformGrid { Columns = 8, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(vendors);
        var colorLabel = Label("Colour");
        panel.Children.Add(colorLabel);
        var colors = new WrapPanel();
        panel.Children.Add(colors);

        var glyphButtons = new List<ToggleButton>();
        var colorButtons = new List<ToggleButton>();
        Brush CurrentBrush() => hex is null ? EntryIcons.DefaultBrush(tag) : FolderIcons.BrushFor(hex);

        void Refresh()
        {
            preview.Source = EntryIcons.RenderWith(glyph, CurrentBrush(), tag);
            foreach (var b in glyphButtons)
            {
                var key = (string?)b.Tag;
                var on = key == glyph;
                b.IsChecked = on;
                b.BorderBrush = on ? accent : soft;
                if (!EntryIcons.IsVendor(key)) ((Image)b.Content).Source = EntryIcons.RenderWith(key, on ? CurrentBrush() : muted, tag);
            }
            var vendor = EntryIcons.IsVendor(glyph);
            colors.Opacity = vendor ? 0.35 : 1;
            colorLabel.Text = vendor ? "Colour (vendor icons keep their own colours)" : "Colour";
            foreach (var b in colorButtons)
            {
                var on = string.Equals((string?)b.Tag, hex, StringComparison.OrdinalIgnoreCase);
                b.IsChecked = on;
                b.BorderBrush = on ? Brushes.White : Brushes.Transparent;
            }
        }

        void AddGlyph(Panel host, string? key, string name)
        {
            var b = new ToggleButton
            {
                Tag = key, Height = 34, Margin = new Thickness(0, 0, 5, 5), ToolTip = name,
                Content = new Image { Width = 16, Height = 16, Source = EntryIcons.RenderWith(key, muted, tag) },
                Background = panelBg, BorderThickness = new Thickness(1), Template = FlatTemplate(),
            };
            AutomationName(b, name);
            b.Click += (_, _) => { glyph = key; Refresh(); };
            glyphButtons.Add(b);
            host.Children.Add(b);
        }

        AddGlyph(glyphs, null, $"Normal {kindName} icon");
        foreach (var g in FolderIcons.Glyphs)
            if (!(folder && g.Key == "folder")) AddGlyph(glyphs, g.Key, g.Name);
        foreach (var m in VendorMarks.All) AddGlyph(vendors, m.Key, m.Name);

        void AddColor(string? value, string name, Brush fill)
        {
            var dot = new Ellipse { Width = 16, Height = 16, Fill = fill };
            var b = new ToggleButton
            {
                Tag = value, Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0), ToolTip = name, Content = dot,
                Background = Brushes.Transparent, BorderThickness = new Thickness(2), Template = RoundTemplate(),
            };
            AutomationName(b, name);
            b.Click += (_, _) => { hex = value; Refresh(); };
            colorButtons.Add(b);
            colors.Children.Add(b);
        }

        AddColor(null, $"Normal {kindName} colour", EntryIcons.DefaultBrush(tag));
        foreach (var c in FolderIcons.Colors)
            if (!string.Equals(c.Hex, FolderIcons.DefaultColor, StringComparison.OrdinalIgnoreCase) || !folder) AddColor(c.Hex, c.Name.Replace(" (default)", ""), c.Brush);
        Refresh();

        var pick = ChoiceDialog.Show(owner, $"Icon for \"{entry.Name}\"", "Shown in the tree for everyone on this data source.",
            ["OK", "Reset to default", "Cancel"], DialogIcon.None, extra: panel);
        icon = null; color = null;
        if (pick == 1) return true;                 // reset: the normal icon and colour
        if (pick != 0) return false;
        icon = glyph;
        color = EntryIcons.IsVendor(glyph) ? null : hex;
        return true;
    }

    private static void AutomationName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static ControlTemplate FlatTemplate()
    {
        var t = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = border;
        return t;
    }

    private static ControlTemplate RoundTemplate()
    {
        var t = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        return t;
    }
}
