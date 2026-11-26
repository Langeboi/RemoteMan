using System.Windows;
using System.Windows.Input;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

/// <summary>
/// The AI apps box under the title bar's AI button: switch MCP on or off, connect an AI app, set what it may do,
/// and see what it did. A small owned window (not an overlay) so it shows on top of RDP and WebView2 tabs.
/// </summary>
public partial class McpWindow : Window
{
    private FrameworkElement? _anchor;

    public McpWindow(McpViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        PreviewKeyDown += (_, e) =>
        {
            var toggle = e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
            if (toggle || e.Key == Key.Escape) { HideBox(); e.Handled = true; }
        };
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Shows the box just under <paramref name="anchor"/> (the AI button), right-aligned to it.</summary>
    public void ShowUnder(FrameworkElement anchor)
    {
        _anchor = anchor;
        Reposition();
        if (!IsVisible) Show();
        Activate();
    }

    public void Reposition()
    {
        if (_anchor is null || PresentationSource.FromVisual(_anchor) is not { CompositionTarget: { } target }) return;
        var bottomRight = target.TransformFromDevice.Transform(_anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight)));
        Left = bottomRight.X - Width;
        Top = bottomRight.Y + 4;
    }

    public void HideBox()
    {
        Hide();
        Owner?.Activate();
    }

    private void OnHide(object sender, RoutedEventArgs e) => HideBox();
}
