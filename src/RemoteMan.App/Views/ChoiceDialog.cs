using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace RemoteMan.App.Views;

public enum DialogIcon { None, Question, Warning, Error, Info }

/// <summary>
/// The app's own message box: dark, borderless, with a header like the main window, an icon, selectable
/// text and any number of buttons. The first button is the default and gets the accent (or red, for a
/// destructive action); the last one is what Esc and the × pick. Every pop-up in the app goes through here
/// instead of the white Windows MessageBox.
/// </summary>
public static class ChoiceDialog
{
    /// <summary>Returns the index of the pressed button, or -1 when the dialog was closed with ×.</summary>
    public static int Show(Window? owner, string title, string text, IReadOnlyList<string> buttons,
        DialogIcon icon = DialogIcon.None, bool danger = false, UIElement? extra = null)
    {
        var result = -1;
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
                  ?? (Application.Current?.MainWindow is { IsVisible: true } m ? m : null);

        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, ShowInTaskbar = owner is null, MinWidth = 400, MaxWidth = 580,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Background = Brush("Bg", "#101215"), Foreground = Brush("Text", "#C9CED4"),
            FontFamily = Res<FontFamily>("UiFont") ?? new FontFamily("Segoe UI"), FontSize = 12,
            UseLayoutRounding = true,
        };
        if (owner is not null) w.Owner = owner;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 32, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0),
        });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: same look as the main window's title bar.
        var header = new DockPanel { Height = 32, Background = Brush("Header", "#16191C") };
        var close = new Button
        {
            Content = "✕", Width = 40, Height = 32, FontSize = 11, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Brush("Muted", "#8A939C"), ToolTip = "Close",
        };
        if (Res<Style>("FlatButton") is { } flat) close.Style = flat;
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => { result = -1; w.Close(); };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = "RemoteMan", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11, Foreground = Brush("Muted", "#8A939C"),
        });
        grid.Children.Add(header);

        // Body: icon, bold title, message (selectable so error text can be copied).
        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 18) };
        if (icon != DialogIcon.None)
        {
            var (glyph, color) = icon switch
            {
                DialogIcon.Warning => ("!", Brush("Warn", "#D7A54A")),
                DialogIcon.Error => ("✕", Brush("Bad", "#E0605E")),
                DialogIcon.Info => ("i", Brush("Accent", "#3FB8A5")),
                _ => ("?", danger ? Brush("Bad", "#E0605E") : Brush("Accent", "#3FB8A5")),
            };
            var badge = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1.5),
                BorderBrush = color, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = glyph, Foreground = color, FontWeight = FontWeights.SemiBold, FontSize = icon == DialogIcon.Error ? 12 : 15,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            DockPanel.SetDock(badge, Dock.Left);
            body.Children.Add(badge);
        }
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextStrong", "#E3E6EA"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6),
        });
        if (!string.IsNullOrWhiteSpace(text))
        {
            var message = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = Brush("Text", "#C9CED4"), Padding = new Thickness(0),
                MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FocusVisualStyle = null,
                Cursor = System.Windows.Input.Cursors.Arrow, MaxWidth = 470,
            };
            message.Style = new Style(typeof(TextBox)); // no theme border or hover on a read-only message
            texts.Children.Add(message);
        }
        if (extra is not null) texts.Children.Add(extra);
        body.Children.Add(texts);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        // Footer: buttons on a slightly lighter strip.
        var footer = new Border
        {
            Background = Brush("Panel", "#15181B"), BorderBrush = Brush("Border", "#2A2F35"), BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button? first = null;
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = buttons[i], MinWidth = 90, Height = 28, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == 0, IsCancel = i == buttons.Count - 1,
            };
            if (i == 0 && buttons.Count > 1)
            {
                if (Res<Style>("PrimaryButton") is { } primary) b.Style = primary;
                if (danger) { b.Background = new SolidColorBrush(Color.FromRgb(0x6B, 0x26, 0x28)); b.BorderBrush = Brush("Bad", "#E0605E"); }
            }
            b.Click += (_, _) => { result = index; w.Close(); };
            bar.Children.Add(b);
            first ??= b;
        }
        footer.Child = bar;
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        w.Content = new Border { BorderBrush = Brush("Border", "#2A2F35"), BorderThickness = new Thickness(1), Child = grid };
        // Destructive questions start on the safe answer; others on the first button.
        w.Loaded += (_, _) => (danger && bar.Children.Count > 1 ? (Button)bar.Children[^1] : first)?.Focus();
        if (danger && bar.Children.Count > 1) { ((Button)bar.Children[0]).IsDefault = false; ((Button)bar.Children[^1]).IsDefault = true; }
        w.ShowDialog();
        return result;
    }

    /// <summary>Yes/no question. With <paramref name="danger"/> the action button is red and Enter picks Cancel.</summary>
    public static bool Confirm(Window? owner, string title, string text, string action = "OK", bool danger = false) =>
        Show(owner, title, text, [action, "Cancel"], danger ? DialogIcon.Warning : DialogIcon.Question, danger) == 0;

    public static void Info(Window? owner, string title, string text) => Show(owner, title, text, ["OK"], DialogIcon.Info);

    public static void Error(Window? owner, string title, string text) => Show(owner, title, text, ["OK"], DialogIcon.Error);

    private static T? Res<T>(string key) where T : class => Application.Current?.TryFindResource(key) as T;

    private static Brush Brush(string key, string fallback) =>
        Res<Brush>(key) ?? (Brush)new BrushConverter().ConvertFromString(fallback)!;
}
