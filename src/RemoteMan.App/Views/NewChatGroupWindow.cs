using System.Windows;
using System.Windows.Controls;

namespace RemoteMan.App.Views;

/// <summary>Chat > New group: a name and the people in it.</summary>
public sealed class NewChatGroupWindow : Window
{
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly List<CheckBox> _people = new();
    private readonly Button _ok = new() { Content = "Create group", IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };

    public string GroupName => _name.Text.Trim();
    public IReadOnlyList<string> Members => _people.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

    public NewChatGroupWindow(Window owner, IReadOnlyList<string> users)
    {
        Owner = owner;
        Title = "New chat group";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "RemoteManWindow");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "Name" });
        root.Children.Add(_name);
        root.Children.Add(new TextBlock { Text = "People" });
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var u in users)
        {
            var c = new CheckBox { Content = u, Tag = u, Margin = new Thickness(0, 3, 0, 3) };
            c.Click += (_, _) => Validate();
            _people.Add(c);
            list.Children.Add(c);
        }
        if (users.Count == 0)
        {
            var none = new TextBlock { Text = "Nobody else has used chat on this database yet. You can still make the group and add people later by making a new one.", TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            list.Children.Add(none);
        }
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        _name.TextChanged += (_, _) => Validate();
        _ok.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => _name.Focus();
    }

    private void Validate() => _ok.IsEnabled = GroupName.Length > 0;
}
