using System.Windows;
using System.Windows.Controls;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

/// <summary>"Start shared session": who to invite, who starts with control, and a couple of options.</summary>
public sealed class ShareInviteWindow : Window
{
    private readonly List<CheckBox> _people = new();
    private readonly TextBox _others = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _control = new() { Margin = new Thickness(0, 4, 0, 12), MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _ask = new() { Content = "Teammates can ask for control", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
    private readonly CheckBox _chat = new() { Content = "Also post the invite in team chat", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
    private readonly Button _ok = new() { Content = "Invite", IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };

    public ShareInviteWindow(Window owner, IReadOnlyList<string> online, string entryName, bool chatAvailable)
    {
        Owner = owner;
        Title = $"Share {entryName}";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "RemoteManWindow");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Heading("Invite"));
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var u in online)
        {
            var c = new CheckBox { Content = $"{Short(u)}   ● online", Tag = u, Margin = new Thickness(0, 3, 0, 3), ToolTip = u };
            c.Click += (_, _) => Validate();
            _people.Add(c);
            list.Children.Add(c);
        }
        if (online.Count == 0) list.Children.Add(Muted("Nobody else is online right now. Type their login below; they get the invite in RemoteMan (and chat) for 10 minutes."));
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(Muted("Others (database logins, comma separated)", top: 10));
        root.Children.Add(_others);
        _others.TextChanged += (_, _) => Validate();

        root.Children.Add(Heading("Starts with control", top: 14));
        root.Children.Add(_control);
        root.Children.Add(_ask);
        if (chatAvailable) root.Children.Add(_chat); else _chat.IsChecked = false;
        root.Children.Add(Muted("They see only this session, never the stored password, and don't need their own route to the server.", top: 8));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        root.Children.Add(buttons);
        Content = root;

        _ok.Click += (_, _) => DialogResult = true;
        if (_people.Count == 1) _people[0].IsChecked = true;
        Validate();
        Loaded += (_, _) => { if (_people.Count == 0) _others.Focus(); };
    }

    public ShareInviteChoice Choice => new(Users, _control.SelectedItem is ComboBoxItem { Tag: string u } ? u : null, _ask.IsChecked == true, _chat.IsChecked == true);

    private IReadOnlyList<string> Users =>
        _people.Where(c => c.IsChecked == true).Select(c => (string)c.Tag)
            .Concat(_others.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Validate()
    {
        var users = Users;
        _ok.IsEnabled = users.Count > 0;
        _ok.Content = users.Count > 1 ? $"Invite {users.Count}" : "Invite";
        var keep = _control.SelectedItem is ComboBoxItem { Tag: string k } ? k : null;
        _control.Items.Clear();
        _control.Items.Add(new ComboBoxItem { Content = "You" });
        foreach (var u in users) _control.Items.Add(new ComboBoxItem { Content = Short(u), Tag = u });
        _control.SelectedItem = _control.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is string t && t == keep) ?? _control.Items[0];
    }

    private static string Short(string user) => user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;

    private static TextBlock Heading(string text, double top = 0) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0) };

    private static TextBlock Muted(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), FontSize = 11 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }
}
