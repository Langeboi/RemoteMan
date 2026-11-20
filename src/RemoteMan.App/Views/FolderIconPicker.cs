using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using RemoteMan.Core.Model;

namespace RemoteMan.App.Views;

/// <summary>Pick a folder's glyph and colour, with a live preview. Returns false when cancelled.</summary>
public static class FolderIconPicker
{
    public static bool Show(Window owner, Entry folder, out string? icon, out string? color)
    {
        var glyph = folder.Icon ?? "folder";
        var hex = folder.IconColor ?? FolderIcons.DefaultColor;
        var accent = Application.Current.TryFindResource("Accent") as Brush ?? Brushes.Teal;
        var soft = Application.Current.TryFindResource("Border") as Brush ?? Brushes.Gray;
        var muted = Application.Current.TryFindResource("Muted") as Brush ?? Brushes.Gray;

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Width = 300 };

        // Preview: how the row looks in the tree.
        var preview = Icon(FolderIcons.GlyphFor(glyph), FolderIcons.BrushFor(hex));
        var row = new DockPanel { Height = 26, Margin = new Thickness(0, 0, 0, 12), Background = Application.Current.TryFindResource("Panel") as Brush };
        preview.Margin = new Thickness(8, 0, 8, 0);
        row.Children.Add(preview);
        row.Children.Add(new TextBlock { Text = folder.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(row);

        panel.Children.Add(new TextBlock { Text = "Glyph", Foreground = muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
        var glyphs = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 12) };
        var glyphButtons = new List<ToggleButton>();
        panel.Children.Add(glyphs);

        panel.Children.Add(new TextBlock { Text = "Colour", Foreground = muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
        var colors = new WrapPanel();
        var colorButtons = new List<ToggleButton>();
        panel.Children.Add(colors);

        void Refresh()
        {
            preview.Data = FolderIcons.GlyphFor(glyph);
            preview.Stroke = FolderIcons.BrushFor(hex);
            foreach (var b in glyphButtons)
            {
                var on = (string)b.Tag == glyph;
                b.IsChecked = on;
                b.BorderBrush = on ? accent : soft;
                ((Path)b.Content).Stroke = on ? FolderIcons.BrushFor(hex) : muted;
            }
            foreach (var b in colorButtons)
            {
                var on = string.Equals((string)b.Tag, hex, StringComparison.OrdinalIgnoreCase);
                b.IsChecked = on;
                b.BorderBrush = on ? Brushes.White : Brushes.Transparent;
            }
        }

        foreach (var g in FolderIcons.Glyphs)
        {
            var b = new ToggleButton
            {
                Tag = g.Key, Height = 40, Margin = new Thickness(0, 0, 6, 6), ToolTip = g.Name, Content = Icon(g.Data, muted),
                Background = Application.Current.TryFindResource("Panel") as Brush, BorderThickness = new Thickness(1),
                Template = FlatTemplate(),
            };
            AutomationName(b, g.Name);
            b.Click += (_, _) => { glyph = (string)b.Tag; Refresh(); };
            glyphButtons.Add(b);
            glyphs.Children.Add(b);
        }
        foreach (var c in FolderIcons.Colors)
        {
            var dot = new Ellipse { Width = 16, Height = 16, Fill = c.Brush };
            var b = new ToggleButton
            {
                Tag = c.Hex, Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0), ToolTip = c.Name, Content = dot,
                Background = Brushes.Transparent, BorderThickness = new Thickness(2), Template = RoundTemplate(),
            };
            AutomationName(b, c.Name);
            b.Click += (_, _) => { hex = c.Hex; Refresh(); };
            colorButtons.Add(b);
            colors.Children.Add(b);
        }
        Refresh();

        var pick = ChoiceDialog.Show(owner, $"Icon for \"{folder.Name}\"", "Shown in the tree for everyone on this data source.",
            ["OK", "Reset to default", "Cancel"], DialogIcon.None, extra: panel);
        icon = null; color = null;
        if (pick == 1) return true;                 // reset: plain grey folder
        if (pick != 0) return false;
        icon = glyph == "folder" ? null : glyph;
        color = string.Equals(hex, FolderIcons.DefaultColor, StringComparison.OrdinalIgnoreCase) ? null : hex;
        return true;
    }

    private static Path Icon(Geometry g, Brush stroke) => new()
    {
        Data = g, Width = 16, Height = 16, Stretch = Stretch.None, Stroke = stroke, StrokeThickness = 1.3,
        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true,
    };

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
