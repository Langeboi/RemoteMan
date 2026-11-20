using System.Windows;
using RemoteMan.Core.Model;

namespace RemoteMan.App.Views;

/// <summary>Edits a snippet in place; the caller saves it (or deletes it when <see cref="DeleteRequested"/>).</summary>
public partial class SnippetEditorWindow : Window
{
    private readonly Entry _e;

    public SnippetEditorWindow(Entry snippet, IEnumerable<string> groups)
    {
        InitializeComponent();
        _e = snippet;
        var s = snippet.Snippet ??= new SnippetData();
        Heading.Text = snippet.Revision == 0 ? "New snippet" : $"Edit snippet: {snippet.Name}";
        DeleteButton.Visibility = snippet.Revision == 0 ? Visibility.Collapsed : Visibility.Visible;
        NameBox.Text = snippet.Name;
        GroupBox.ItemsSource = groups;
        GroupBox.Text = s.Group ?? "";
        CommandBox.Text = s.Command;
        EnterBox.IsChecked = s.PressEnter;
        Loaded += (_, _) => (snippet.Name.Length == 0 ? NameBox : CommandBox).Focus();
    }

    public bool DeleteRequested { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var command = CommandBox.Text.Replace("\r\n", "\n").TrimEnd('\n');
        if (command.Trim().Length == 0) { CommandBox.Focus(); return; }
        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = command.Split('\n')[0].Trim() is { Length: > 40 } l ? l[..40] : command.Split('\n')[0].Trim();
        _e.Name = name;
        _e.Snippet!.Command = command;
        _e.Snippet.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim();
        _e.Snippet.PressEnter = EnterBox.IsChecked == true;
        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Delete snippet \"{_e.Name}\"?", "RemoteMan", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        DeleteRequested = true;
        DialogResult = true;
    }
}
