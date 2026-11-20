using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

/// <summary>The small team dialogs (reserve an entry, choose between two edits), built on <see cref="ChoiceDialog"/>.</summary>
public static class TeamDialogs
{
    /// <summary>How long and why. Null when cancelled.</summary>
    public static (DateTimeOffset Until, string? Note)? Reserve(Window? owner, Entry e)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), MinWidth = 380 };

        var choices = new (string Label, Func<DateTimeOffset> Until)[]
        {
            ("30 min", () => DateTimeOffset.Now.AddMinutes(30)),
            ("1 hour", () => DateTimeOffset.Now.AddHours(1)),
            ("2 hours", () => DateTimeOffset.Now.AddHours(2)),
            ("4 hours", () => DateTimeOffset.Now.AddHours(4)),
            ("Rest of today", () => new DateTimeOffset(DateTime.Today.AddDays(1).AddMinutes(-1))),
        };
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var radios = new List<RadioButton>();
        foreach (var (label, _) in choices)
        {
            var rb = new RadioButton { Content = label, GroupName = "reserve-for", Margin = new Thickness(0, 0, 12, 4) };
            radios.Add(rb);
            row.Children.Add(rb);
        }
        radios[1].IsChecked = true;
        panel.Children.Add(Caption("For"));
        panel.Children.Add(row);

        panel.Children.Add(Caption("Note for the others (optional)"));
        var note = new TextBox { MaxLength = 200, Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(note);
        var hint = new TextBlock
        {
            Text = "Others still can connect, after a warning. It ends by itself, or when you choose Release reservation.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(hint);
        note.Loaded += (_, _) => note.Focus();

        if (ChoiceDialog.Show(owner, $"Reserve {e.Name}", "", ["Reserve", "Cancel"], DialogIcon.None, extra: panel) != 0) return null;
        var picked = radios.FindIndex(r => r.IsChecked == true);
        return (choices[Math.Max(0, picked)].Until(), string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim());
    }

    /// <summary>
    /// Both of you changed the same fields: pick per field. Returns the paths to take theirs for (empty = all
    /// yours), or null for "Back to editing".
    /// </summary>
    public static IReadOnlyList<string>? Merge(Window? owner, MergeResult merge, string theirName)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0), MinWidth = 440 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var r = 0;
        void Cell(UIElement el, int col)
        {
            Grid.SetRow(el, r); Grid.SetColumn(el, col);
            grid.Children.Add(el);
        }
        void NewRow() { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }

        NewRow();
        Cell(Caption("FIELD"), 0); Cell(Caption("YOURS"), 1); Cell(Caption($"{theirName.ToUpperInvariant()}'S"), 2);
        r++;

        var theirsPicks = new List<(string Path, RadioButton Theirs)>();
        foreach (var c in merge.Conflicts)
        {
            NewRow();
            Cell(Label(c.Label), 0);
            var group = "merge-" + c.Path;
            var mine = new RadioButton { Content = Value(c.Mine, c.IsSecret), GroupName = group, IsChecked = true, Margin = new Thickness(0, 3, 8, 3) };
            var theirs = new RadioButton { Content = Value(c.Theirs, c.IsSecret), GroupName = group, Margin = new Thickness(0, 3, 8, 3) };
            Cell(mine, 1); Cell(theirs, 2);
            theirsPicks.Add((c.Path, theirs));
            r++;
        }
        foreach (var c in merge.KeptTheirs.Take(6))
        {
            NewRow();
            Cell(Label(c.Label), 0); Cell(Muted("unchanged"), 1); Cell(Kept(Value(c.After, c.IsSecret)), 2);
            r++;
        }
        foreach (var c in merge.KeptMine.Take(6))
        {
            NewRow();
            Cell(Label(c.Label), 0); Cell(Kept(Value(c.After, c.IsSecret)), 1); Cell(Muted("unchanged"), 2);
            r++;
        }

        var kept = merge.KeptTheirs.Count + merge.KeptMine.Count;
        var text = (kept > 0 ? $"{kept} change{(kept == 1 ? " was" : "s were")} combined. " : "")
                   + (merge.Conflicts.Count == 1 ? $"You both changed {merge.Conflicts[0].Label.ToLowerInvariant()}: choose which to keep."
                       : "You both changed these fields: choose which to keep.");
        var pick = ChoiceDialog.Show(owner, $"{theirName} saved {merge.Merged.Name} while you were editing it", text,
            ["Save", "Back to editing"], DialogIcon.Warning, extra: new ScrollViewer { Content = grid, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (pick != 0) return null;
        return theirsPicks.Where(p => p.Theirs.IsChecked == true).Select(p => p.Path).ToList();
    }

    private static string Value(string? v, bool secret) => secret ? (v is null ? "(none)" : "(hidden)") : v ?? "(empty)";

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, Margin = new Thickness(0, 0, 8, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        return t;
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        return t;
    }

    private static TextBlock Kept(string value)
    {
        var t = new TextBlock { Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var ok = new System.Windows.Documents.Run("✓ kept  ");
        ok.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Ok");
        t.Inlines.Add(ok);
        t.Inlines.Add(new System.Windows.Documents.Run(value));
        return t;
    }
}
