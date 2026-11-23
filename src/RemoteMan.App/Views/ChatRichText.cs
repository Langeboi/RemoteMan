using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

/// <summary>
/// A message's text: selectable and copyable like a document, with links as clickable chips and
/// `inline code` in the mono font. Copying a selection writes the links' names, not blanks.
/// Right-click: Copy (the selection) and Copy message (all of it).
/// </summary>
public sealed class ChatRichText : RichTextBox
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(
        nameof(Parts), typeof(IReadOnlyList<ChatInlinePart>), typeof(ChatRichText), new PropertyMetadata(null, (d, _) => ((ChatRichText)d).Build()));

    public IReadOnlyList<ChatInlinePart>? Parts
    {
        get => (IReadOnlyList<ChatInlinePart>?)GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    public ChatRichText()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;   // the chips inside stay clickable
        IsReadOnlyCaretVisible = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Focusable = true;
        IsUndoEnabled = false;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        Template = PlainTemplate();
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(SelectionBrushProperty, "Accent");
        SelectionOpacity = 0.35;
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewExecuted);
        ContextMenuOpening += OnMenuOpening;
        ContextMenu = new ContextMenu();
        // Clicking elsewhere clears the highlight, like a web page.
        LostKeyboardFocus += (_, _) => Selection.Select(Document.ContentStart, Document.ContentStart);
    }

    /// <summary>Only the text area: no box, no scroll bars, so it sizes to its content inside the message list.</summary>
    private static ControlTemplate PlainTemplate()
    {
        var t = new ControlTemplate(typeof(RichTextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.PaddingProperty, new Thickness(0));
        t.VisualTree = host;
        return t;
    }

    private void Build()
    {
        var p = new Paragraph { Margin = new Thickness(0), LineHeight = 20 };
        foreach (var part in Parts ?? [])
        {
            if (part.Link is { } link)
            {
                var chip = new Button { DataContext = link, Content = link, Command = link.OpenCommand, ToolTip = link.ToolTip, Cursor = Cursors.Hand, Focusable = false };
                chip.SetResourceReference(StyleProperty, "ChatChip");
                p.Inlines.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center, Tag = link.Label });
            }
            else if (part.Text == "\n") p.Inlines.Add(new LineBreak());
            else if (part.IsCode)
            {
                var run = new Run(part.Text) { FontSize = FontSize - 0.5 };
                run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                run.SetResourceReference(TextElement.BackgroundProperty, "SelectRow");
                run.SetResourceReference(TextElement.ForegroundProperty, "TextStrong");
                p.Inlines.Add(run);
            }
            else p.Inlines.Add(new Run(part.Text));
        }
        Document = new FlowDocument(p) { PagePadding = new Thickness(0) };
        Document.SetResourceReference(FlowDocument.FontFamilyProperty, "UiFont");
        Document.FontSize = 12;
    }

    /// <summary>The selected text, with each chip in the selection written as its name.</summary>
    public string SelectedPlainText()
    {
        var sb = new StringBuilder();
        var end = Selection.End;
        for (var pos = Selection.Start; pos is not null && pos.CompareTo(end) < 0; pos = pos.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (pos.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var run = pos.GetTextInRun(LogicalDirection.Forward);
                    var left = pos.GetOffsetToPosition(end);
                    sb.Append(left < run.Length ? run[..Math.Max(0, left)] : run);
                    break;
                case TextPointerContext.EmbeddedElement:
                    if (pos.Parent is InlineUIContainer { Tag: string label }) sb.Append(label);
                    break;
                case TextPointerContext.ElementStart when pos.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    sb.Append(Environment.NewLine);
                    break;
            }
        }
        return sb.ToString();
    }

    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut) return;
        e.Handled = true;
        if (Selection.IsEmpty) return;
        try { Clipboard.SetText(SelectedPlainText()); } catch (Exception) { }
    }

    private void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = ContextMenu!;
        menu.Items.Clear();
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C", IsEnabled = !Selection.IsEmpty };
        copy.Click += (_, _) => { try { Clipboard.SetText(SelectedPlainText()); } catch (Exception) { } };
        menu.Items.Add(copy);
        var message = FindMessage();
        var all = new MenuItem { Header = "Copy message", IsEnabled = message is not null };
        all.Click += (_, _) => { try { Clipboard.SetText(message!.PlainText); } catch (Exception) { } };
        menu.Items.Add(all);
        var select = new MenuItem { Header = "Select all" };
        select.Click += (_, _) => { Focus(); SelectAll(); };
        menu.Items.Add(select);
    }

    private ChatMessageItem? FindMessage()
    {
        for (DependencyObject? d = this; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: ChatMessageItem m }) return m;
        return null;
    }
}
