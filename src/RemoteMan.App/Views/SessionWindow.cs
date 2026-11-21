using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

/// <summary>A session in its own window ("Open in: own window" or full screen). F11 switches between the two;
/// closing the window ends the session.</summary>
public sealed class SessionWindow : Window
{
    private readonly SessionViewModel _vm;

    public SessionWindow(SessionViewModel vm, Window owner)
    {
        _vm = vm;
        Style = (Style)Application.Current.FindResource("RemoteManWindow");
        Title = $"{vm.Entry.Name} - RemoteMan";
        Icon = owner.Icon;
        Width = Math.Min(1280, SystemParameters.WorkArea.Width * 0.8);
        Height = Math.Min(800, SystemParameters.WorkArea.Height * 0.8);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // The same views as the tabs use, chosen by session type.
        Resources.Add(new DataTemplateKey(typeof(SshSessionViewModel)), Template(typeof(SshSessionView)));
        Resources.Add(new DataTemplateKey(typeof(RdpSessionViewModel)), Template(typeof(RdpSessionView)));
        Resources.Add(new DataTemplateKey(typeof(WebSessionViewModel)), Template(typeof(WebSessionView)));
        Resources.Add(new DataTemplateKey(typeof(FilesSessionViewModel)), Template(typeof(FilesSessionView)));

        var hint = new TextBlock
        {
            Text = "F11: window / full screen",
            Style = (Style)Application.Current.FindResource("MutedText"),
            FontSize = 11, Margin = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right,
        };
        var status = new TextBlock { Style = (Style)Application.Current.FindResource("MutedText"), FontSize = 11, Margin = new Thickness(10, 3, 10, 3) };
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SessionViewModel.StatusText)) { Source = vm });
        var bar = new DockPanel { Background = (System.Windows.Media.Brush)Application.Current.FindResource("Panel") };
        DockPanel.SetDock(hint, Dock.Right);
        bar.Children.Add(hint);
        bar.Children.Add(status);
        _bar = bar;

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        if (vm.TabBrush is { } brush) { var stripe = new Border { Height = 3, Background = brush }; DockPanel.SetDock(stripe, Dock.Top); root.Children.Add(stripe); }
        root.Children.Add(new ContentControl { Content = vm, Focusable = false });
        Content = root;

        vm.IsActive = true;
        vm.CloseRequested += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F11) { SetFullScreen(WindowStyle != WindowStyle.None); e.Handled = true; } };
        Closed += (_, _) => { vm.IsActive = false; vm.Dispose(); };
        if (vm.FullScreen) SetFullScreen(true);
    }

    private readonly DockPanel _bar;

    private void SetFullScreen(bool on)
    {
        WindowState = WindowState.Normal;   // re-apply so the borderless window covers the task bar
        WindowStyle = on ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = on ? ResizeMode.NoResize : ResizeMode.CanResize;
        _bar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) WindowState = WindowState.Maximized;
    }

    private static DataTemplate Template(Type view) => new() { VisualTree = new FrameworkElementFactory(view) };
}
