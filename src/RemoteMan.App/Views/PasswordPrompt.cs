using System.Windows;
using System.Windows.Controls;

namespace RemoteMan.App.Views;

/// <summary>Asks for a password that the credential doesn't store ("ask when connecting").</summary>
public static class PasswordPrompt
{
    public static string? Show(Window owner, string title)
    {
        string? result = null;
        var w = new Window
        {
            Owner = owner, Title = "RemoteMan", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, MinWidth = 360,
        };
        w.SetResourceReference(FrameworkElement.StyleProperty, "RemoteManWindow");
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 0), MinWidth = 320 };
        var ok = new Button { Content = "Connect", MinWidth = 90, Height = 26, IsDefault = true };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Height = 26, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => { result = box.Password; w.Close(); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        body.Children.Add(box);
        body.Children.Add(bar);
        w.Content = body;
        w.Loaded += (_, _) => box.Focus();
        w.ShowDialog();
        return result;
    }
}
