using System.Windows;
using System.Windows.Controls;

namespace RemoteMan.App.Views;

/// <summary>A small themed question box with any number of buttons (the first one is the default).</summary>
public static class ChoiceDialog
{
    public static int Show(Window owner, string title, string text, IReadOnlyList<string> buttons)
    {
        var result = -1;
        var w = new Window
        {
            Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, MinWidth = 380, MaxWidth = 560,
        };
        w.SetResourceReference(FrameworkElement.StyleProperty, "RemoteManWindow");

        var root = new DockPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        DockPanel.SetDock(bar, Dock.Bottom);
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 90, Height = 26, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), IsDefault = i == 0, IsCancel = i == buttons.Count - 1 };
            b.Click += (_, _) => { result = index; w.Close(); };
            bar.Children.Add(b);
        }
        root.Children.Add(bar);
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(body);
        w.Content = root;
        w.ShowDialog();
        return result;
    }
}
