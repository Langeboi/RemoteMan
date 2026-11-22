using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

/// <summary>Fills a TextBlock with a message's text, with each link as a small clickable chip that wraps with the text.</summary>
public static class ChatInlines
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.RegisterAttached(
        "Parts", typeof(IReadOnlyList<ChatInlinePart>), typeof(ChatInlines), new PropertyMetadata(null, OnPartsChanged));

    public static void SetParts(DependencyObject d, IReadOnlyList<ChatInlinePart>? v) => d.SetValue(PartsProperty, v);
    public static IReadOnlyList<ChatInlinePart>? GetParts(DependencyObject d) => (IReadOnlyList<ChatInlinePart>?)d.GetValue(PartsProperty);

    private static void OnPartsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<ChatInlinePart> parts) return;
        foreach (var p in parts)
        {
            if (p.Link is null)
            {
                if (p.Text == "\n") tb.Inlines.Add(new LineBreak());
                else tb.Inlines.Add(new Run(p.Text));
                continue;
            }
            var chip = new Button
            {
                DataContext = p.Link,
                Content = p.Link,
                Command = p.Link.OpenCommand,
                ToolTip = p.Link.ToolTip,
                Cursor = Cursors.Hand,
                Focusable = false,
            };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "ChatChip");
            tb.Inlines.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center });
        }
    }
}
