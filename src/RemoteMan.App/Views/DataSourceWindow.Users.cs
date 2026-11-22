using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.Core.Storage;
using RemoteMan.Data;

namespace RemoteMan.App.Views;

/// <summary>
/// The Users tab: an admin (the shared login from setup, or a personal login with Admin access) adds,
/// resets, re-scopes, disables and removes the team's database logins. Everything goes through the
/// remoteman_admin functions on the server (deploy/postgres/remoteman-db.sh), so it works on the
/// primary + standby pair without anyone being a database superuser.
/// </summary>
public partial class DataSourceWindow
{
    private const string UpdateCommand = "sudo bash remoteman-db.sh update";

    private enum MessageAction { None, Retry, CopyUpdateCommand, ChangeMyPassword }

    private PostgresLogins? _logins;
    private DbLoginSelf? _self;
    private MessageAction _messageAction;
    private int _usersLoad;

    /// <summary>One row of the login list.</summary>
    public sealed class LoginRow
    {
        public required DbLogin Login { get; init; }
        public required bool IsMe { get; init; }
        public string Name => Login.Name;
        public string Initials => Login.Name.Length <= 2 ? Login.Name.ToUpperInvariant() : Login.Name[..2].ToUpperInvariant();
        public string Note => IsMe ? "you" : Login.Access == DbLoginAccess.Shared ? "default login" : "";
        public string AccessLabel => Login.CanLogin ? AccessText(Login.Access) : "Disabled";
        public Brush AccessBrush => Login.CanLogin ? AccessColor(Login.Access) : Res("Faint");
        public Brush NameBrush => Login.CanLogin ? Res("TextStrong") : Res("Faint");
        public Brush AvatarBrush => Login.CanLogin ? AvatarColor(Login.Name) : Res("FolderIcon");
        public Brush OnlineBrush => Login.Connections > 0 ? Res("Ok") : new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x58));
        public string OnlineText => Login.Connections > 0 ? (Login.Connections == 1 ? "yes" : $"yes ({Login.Connections})") : "no";
        public string LastChangeText => Login.LastChange is { } at ? at.ToLocalTime().ToString("dd MMM HH:mm") : "never";
    }

    private static string AccessText(DbLoginAccess a) => a switch
    {
        DbLoginAccess.Shared => "Shared admin",
        DbLoginAccess.Admin => "Admin",
        DbLoginAccess.Edit => "Can edit",
        _ => "Read only",
    };

    private static Brush AccessColor(DbLoginAccess a) => a switch
    {
        DbLoginAccess.Shared => Res("Warn"),
        DbLoginAccess.Admin => new SolidColorBrush(Color.FromRgb(0xB4, 0x9C, 0xF0)),
        DbLoginAccess.Edit => Res("Link"),
        _ => Res("Muted"),
    };

    private static readonly Color[] Avatars =
    [
        Color.FromRgb(0x7F, 0xD3, 0xC4), Color.FromRgb(0xE0, 0xA8, 0x4A), Color.FromRgb(0xB4, 0x9C, 0xF0),
        Color.FromRgb(0x5B, 0x9B, 0xEA), Color.FromRgb(0xE3, 0x8B, 0x8B), Color.FromRgb(0x9C, 0xC9, 0x6B),
    ];

    private static Brush AvatarColor(string name)
    {
        var h = 0;
        foreach (var c in name) h = unchecked(h * 31 + c);
        return new SolidColorBrush(Avatars[(h & 0x7fffffff) % Avatars.Length]);
    }

    private static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    // ------------------------------------------------------------------ tab and loading

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (ConnectionPanel is null || UsersPanel is null) return; // during InitializeComponent
        var users = UsersTab.IsChecked == true;
        ConnectionPanel.Visibility = users ? Visibility.Collapsed : Visibility.Visible;
        UsersPanel.Visibility = users ? Visibility.Visible : Visibility.Collapsed;
        if (users) _ = LoadUsersAsync();
    }

    private void OnRefreshUsers(object sender, RoutedEventArgs e) => _ = LoadUsersAsync();

    /// <summary>The connection the Users tab uses: the data source as typed on the Connection tab (both servers).</summary>
    private static string? AdminConnectionString(DataSourceConfig cfg)
    {
        var lines = cfg.ConnectionStrings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (lines.Count == 0) return null;
        var cs = DataSourceFactory.PostgresPair(lines);
        return cfg.UseWindowsLogin ? DataSourceFactory.WithWindowsLogin(DataSourceKind.PostgreSql, cs) : cs;
    }

    private async Task LoadUsersAsync(string? selectName = null)
    {
        Commit();
        var load = ++_usersLoad;
        _logins = null;
        _self = null;
        ServerDots.Children.Clear();
        SignedInText.Text = "";
        SignedInPill.Visibility = Visibility.Collapsed;

        if (_current is null) { ShowMessage("No data source selected", ""); return; }
        if (_current.Kind != DataSourceKind.PostgreSql)
        {
            ShowMessage("Logins are managed on the server",
                _current.Kind == DataSourceKind.Sqlite
                    ? "A local file has no logins. The Users tab is for shared PostgreSQL data sources."
                    : "For MySQL and SQL Server, create and remove logins on the database server. The Users tab works with PostgreSQL data sources set up with remoteman-db.");
            return;
        }
        string? cs;
        try { cs = AdminConnectionString(_current); }
        catch (Exception ex) { ShowMessage("The connection string isn't valid", ex.GetBaseException().Message); return; }
        if (cs is null) { ShowMessage("No servers yet", "Fill in the servers on the Connection tab first."); return; }

        ShowMessage("Loading logins…", "");
        var logins = new PostgresLogins(cs);
        try
        {
            var self = await logins.GetSelfAsync();
            if (load != _usersLoad) return;
            if (self is null || self.Version < PostgresLogins.RequiredVersion)
            {
                ShowMessage("The servers need a one-time update",
                    "Copy the new remoteman-db.sh (in RemoteMan's deploy/postgres folder) to both servers and run this on each, then press Refresh.",
                    UpdateCommand, "Copy command", MessageAction.CopyUpdateCommand);
                return;
            }
            _logins = logins;
            _self = self;
            SignedInText.Text = $"Signed in as {self.Login}";
            if (self.Access is { } access)
            {
                SignedInPillText.Text = AccessText(access);
                SignedInPillText.Foreground = SignedInPill.BorderBrush = AccessColor(access);
                SignedInPill.Visibility = Visibility.Visible;
            }
            if (!self.IsAdmin)
            {
                ShowMessage("Only admins manage logins",
                    "You can change your own password here. Ask an admin for a new login or different access.",
                    button: "Change my password…", action: MessageAction.ChangeMyPassword);
                return;
            }

            var list = await logins.ListAsync();
            IReadOnlyList<DbServerState> servers = [];
            try { servers = await logins.ServersAsync(); } catch (DbLoginException) { }
            if (load != _usersLoad) return;

            var rows = list.Select(l => new LoginRow { Login = l, IsMe = l.Name == self.Login }).ToList();
            LoginList.ItemsSource = rows;
            UsersTabLabel.Text = $"Users  {rows.Count}";
            LoginList.SelectedItem = rows.FirstOrDefault(r => r.Name == selectName) ?? rows.FirstOrDefault(r => !r.IsMe && r.Login.Access != DbLoginAccess.Shared) ?? rows.FirstOrDefault();
            ShowServers(servers);
            MessageView.Visibility = Visibility.Collapsed;
            AdminView.Visibility = Visibility.Visible;
            UpdateLoginButtons();
        }
        catch (DbLoginException ex)
        {
            if (load != _usersLoad) return;
            ShowMessage("Can't reach the database", ex.Message, button: "Try again", action: MessageAction.Retry);
        }
    }

    private void ShowServers(IReadOnlyList<DbServerState> servers)
    {
        ServerDots.Children.Clear();
        foreach (var s in servers)
        {
            var text = s.Role == "primary" ? $"{s.Address} primary"
                : s.InSync ? $"{s.Address} standby, in sync"
                : s.State == "streaming" ? $"{s.Address} standby, {s.LagBytes / (1024 * 1024)} MB behind"
                : $"{s.Address} standby, {s.State}";
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
            item.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = s.InSync ? Res("Ok") : Res("Warn"),
            });
            item.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
            ServerDots.Children.Add(item);
        }
        if (servers.Count == 1)
            ServerDots.Children.Add(new TextBlock
            {
                Text = "no standby connected", FontSize = 11, Foreground = Res("Faint"), Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
    }

    private void ShowMessage(string title, string text, string? code = null, string? button = null, MessageAction action = MessageAction.None)
    {
        AdminView.Visibility = Visibility.Collapsed;
        MessageView.Visibility = Visibility.Visible;
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        MessageCode.Text = code ?? "";
        MessageCode.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        MessageButton.Content = button;
        MessageButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        _messageAction = action;
        if (action == MessageAction.None) UsersTabLabel.Text = "Users";
    }

    private void OnMessageButton(object sender, RoutedEventArgs e)
    {
        switch (_messageAction)
        {
            case MessageAction.Retry: _ = LoadUsersAsync(); break;
            case MessageAction.CopyUpdateCommand:
                if (Copy(UpdateCommand)) MessageButton.Content = "Copied";
                break;
            case MessageAction.ChangeMyPassword: _ = ChangeMyPasswordAsync(); break;
        }
    }

    // ------------------------------------------------------------------ selection

    private LoginRow? Selected => LoginList.SelectedItem as LoginRow;

    private void OnLoginSelected(object sender, SelectionChangedEventArgs e) => UpdateLoginButtons();

    private void OnLoginDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Selected is not null) OnResetPassword(sender, e);
    }

    private void UpdateLoginButtons()
    {
        var row = Selected;
        var isShared = row?.Login.Access == DbLoginAccess.Shared;
        var isMe = row?.IsMe == true;
        ResetButton.IsEnabled = row is not null;
        AccessButton.IsEnabled = row is not null && !isShared && !isMe;
        EnableButton.IsEnabled = row is not null && (!isMe || row.Login.CanLogin == false);
        EnableButton.Content = row is { Login.CanLogin: false } ? "Enable" : "Disable";
        RemoveLoginButton.IsEnabled = row is not null && !isShared && !isMe;
        AccessButton.ToolTip = isShared ? "The shared login always has full access." : isMe ? "Ask another admin to change your own access." : null;
    }

    // ------------------------------------------------------------------ actions

    private async void OnAddLogin(object sender, RoutedEventArgs e)
    {
        if (_logins is null) return;
        var name = new TextBox { MinWidth = 260 };
        var password = new TextBox { MinWidth = 200, FontFamily = MonoFont(), Text = PostgresLogins.GeneratePassword() };
        var access = AccessPicker(DbLoginAccess.Edit);
        var error = ErrorLine();

        var form = FormGrid(
            ("Login name", name),
            ("Password", WithGenerate(password)),
            ("Access", access.Panel));
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(form);
        body.Children.Add(new TextBlock
        {
            Text = "Lowercase letters, digits and _ only. The password is shown once more on the next step, with their connection string.",
            Style = MutedStyle(), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 420,
        });
        body.Children.Add(error);
        name.Loaded += (_, _) => name.Focus();

        while (true)
        {
            if (ChoiceDialog.Show(this, "Add login", "", ["Add", "Cancel"], extra: body) != 0) return;
            Detach(body);
            var n = name.Text.Trim();
            var pw = password.Text;
            string? problem = !PostgresLogins.IsValidName(n) ? "Login names: lowercase letters, digits and _ only, starting with a letter or _."
                : pw.Length < 8 ? "The password must be at least 8 characters." : null;
            if (problem is null)
            {
                try
                {
                    await _logins.AddAsync(n, pw, access.Selected);
                    ShowConnectionString($"Added {n} ({AccessText(access.Selected).ToLowerInvariant()})", n, pw);
                    await LoadUsersAsync(selectName: n);
                    return;
                }
                catch (DbLoginException ex) { problem = ex.Message; }
            }
            error.Text = problem;
            error.Visibility = Visibility.Visible;
        }
    }

    private async void OnResetPassword(object sender, RoutedEventArgs e)
    {
        if (_logins is null || Selected is not { } row) return;
        var password = new TextBox { MinWidth = 200, FontFamily = MonoFont(), Text = PostgresLogins.GeneratePassword() };
        var error = ErrorLine();
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(FormGrid(("New password", WithGenerate(password))));
        var note = row.IsMe
            ? "This is the login you're signed in with. RemoteMan updates the password in this data source for you."
            : row.Login.Connections > 0
                ? $"{row.Name} is connected right now. The open session keeps working until RemoteMan restarts, then {row.Name} needs the new connection string."
                : row.Login.CanLogin ? "" : $"{row.Name} is disabled. A new password turns the login back on.";
        if (note.Length > 0)
            body.Children.Add(new TextBlock { Text = note, Style = MutedStyle(), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 420 });
        body.Children.Add(error);

        while (true)
        {
            if (ChoiceDialog.Show(this, $"Reset password for {row.Name}", "", ["Reset", "Cancel"], extra: body) != 0) return;
            Detach(body);
            var pw = password.Text;
            string? problem = pw.Length < 8 ? "The password must be at least 8 characters." : null;
            if (problem is null)
            {
                try
                {
                    await _logins.SetPasswordAsync(row.Name, pw);
                    if (row.IsMe) UseNewOwnPassword(row.Name, pw);
                    else ShowConnectionString($"New password for {row.Name}", row.Name, pw);
                    await LoadUsersAsync(selectName: row.Name);
                    return;
                }
                catch (DbLoginException ex) { problem = ex.Message; }
            }
            error.Text = problem;
            error.Visibility = Visibility.Visible;
        }
    }

    private void OnChangeAccess(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        foreach (var item in AccessMenu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is string t && Enum.TryParse<DbLoginAccess>(t, out var a) && a == row.Login.Access;
        AccessMenu.PlacementTarget = AccessButton;
        AccessMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AccessMenu.IsOpen = true;
    }

    private async void OnAccessPicked(object sender, RoutedEventArgs e)
    {
        if (_logins is null || Selected is not { } row) return;
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<DbLoginAccess>(tag, out var access) || access == row.Login.Access) return;
        try
        {
            await _logins.SetAccessAsync(row.Name, access);
            TestResult.Text = $"{row.Name} now: {AccessText(access).ToLowerInvariant()}. Applies the next time {row.Name} starts RemoteMan.";
            await LoadUsersAsync(selectName: row.Name);
        }
        catch (DbLoginException ex) { ChoiceDialog.Error(this, "Couldn't change access", ex.Message); }
    }

    private async void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (_logins is null || Selected is not { } row) return;
        var enable = !row.Login.CanLogin;
        if (!enable)
        {
            var text = row.Login.Access == DbLoginAccess.Shared
                ? "Nobody can sign in with the shared login until it's turned back on. It keeps owning the data. Make sure you have your own admin login first."
                : $"{row.Name} can't sign in until you enable the login again. Their open connections close now.";
            if (!ChoiceDialog.Confirm(this, $"Disable {row.Name}?", text, "Disable", danger: true)) return;
        }
        try
        {
            await _logins.SetEnabledAsync(row.Name, enable);
            TestResult.Text = enable ? $"Enabled {row.Name}" : $"Disabled {row.Name}";
            await LoadUsersAsync(selectName: row.Name);
        }
        catch (DbLoginException ex) { ChoiceDialog.Error(this, enable ? "Couldn't enable the login" : "Couldn't disable the login", ex.Message); }
    }

    private async void OnRemoveLogin(object sender, RoutedEventArgs e)
    {
        if (_logins is null || Selected is not { } row) return;
        if (!ChoiceDialog.Confirm(this, $"Remove {row.Name}?",
                $"This removes the login and closes their open connections. Their name stays on the changes they made.", "Remove", danger: true))
            return;
        try
        {
            var gone = await _logins.RemoveAsync(row.Name);
            TestResult.Text = gone ? $"Removed {row.Name}" : $"{row.Name} owns something in the database, so the login was disabled instead";
            await LoadUsersAsync();
        }
        catch (DbLoginException ex) { ChoiceDialog.Error(this, "Couldn't remove the login", ex.Message); }
    }

    /// <summary>A non-admin changing their own password.</summary>
    private async Task ChangeMyPasswordAsync()
    {
        if (_current is null || _self is null) return;
        string? cs;
        try { cs = AdminConnectionString(_current); } catch (Exception) { return; }
        if (cs is null) return;
        var logins = new PostgresLogins(cs);

        var first = new PasswordBox { MinWidth = 240 };
        var second = new PasswordBox { MinWidth = 240 };
        var error = ErrorLine();
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(FormGrid(("New password", first), ("Again", second)));
        body.Children.Add(new TextBlock
        {
            Text = "RemoteMan updates the password in this data source for you.", Style = MutedStyle(), FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });
        body.Children.Add(error);
        first.Loaded += (_, _) => first.Focus();

        while (true)
        {
            if (ChoiceDialog.Show(this, "Change my password", "", ["Change", "Cancel"], extra: body) != 0) return;
            Detach(body);
            string? problem = first.Password.Length < 8 ? "The password must be at least 8 characters."
                : first.Password != second.Password ? "The two passwords don't match." : null;
            if (problem is null)
            {
                try
                {
                    await logins.SetPasswordAsync(_self.Login, first.Password);
                    UseNewOwnPassword(_self.Login, first.Password);
                    return;
                }
                catch (DbLoginException ex) { problem = ex.Message; }
            }
            error.Text = problem;
            error.Visibility = Visibility.Visible;
        }
    }

    /// <summary>After changing the password we're signed in with: put it into this data source's server lines.</summary>
    private void UseNewOwnPassword(string login, string password)
    {
        if (_current is null) return;
        var lines = _current.ConnectionStrings.Select(line =>
        {
            try
            {
                var b = new Npgsql.NpgsqlConnectionStringBuilder(line);
                if (!string.Equals(b.Username, login, StringComparison.Ordinal)) return line;
                b.Password = password;
                return b.ConnectionString;
            }
            catch (Exception) { return line; }
        }).ToList();
        _current.ConnectionStrings = lines;
        ConnBox.Text = string.Join(Environment.NewLine, lines);
        _settings.Save();
        TestResult.Text = "Password changed and saved in this data source.";
    }

    /// <summary>Shows the connection string for a teammate, once, with copy buttons.</summary>
    private void ShowConnectionString(string title, string login, string password)
    {
        if (_logins is null) return;
        var cs = PostgresLogins.ConnectionStringFor(_logins.ConnectionString, login, password);
        var box = new TextBox
        {
            Text = cs, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = MonoFont(), FontSize = 11,
            MaxWidth = 470, Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 0),
        };
        var copied = new TextBlock { Foreground = Res("Ok"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var copyCs = new Button { Content = "Copy connection string", Padding = new Thickness(10, 0, 10, 0), Height = 26 };
        copyCs.SetResourceReference(StyleProperty, "PrimaryButton");
        copyCs.Click += (_, _) => copied.Text = Copy(cs) ? "Copied the connection string" : "Select the text and copy it";
        var copyPw = new Button { Content = "Copy password", Padding = new Thickness(10, 0, 10, 0), Height = 26, Margin = new Thickness(6, 0, 0, 0) };
        copyPw.Click += (_, _) => copied.Text = Copy(password) ? "Copied the password" : "Select the text and copy it";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(copyCs);
        buttons.Children.Add(copyPw);
        buttons.Children.Add(copied);

        var body = new StackPanel();
        body.Children.Add(box);
        body.Children.Add(buttons);
        body.Children.Add(new TextBlock
        {
            Text = "They paste it into Data sources > Servers. They also need the team vault passphrase, which you share separately.",
            Style = MutedStyle(), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 470,
        });
        ChoiceDialog.Show(this, title, $"Send {login} this connection string. It holds the password and is shown only now.", ["Done"],
            DialogIcon.Info, extra: body);
    }

    // ------------------------------------------------------------------ small helpers

    private static bool Copy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (Exception) { return false; }
    }

    private static FontFamily MonoFont() => Application.Current?.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas");

    private static Style? MutedStyle() => Application.Current?.TryFindResource("MutedText") as Style;

    private static TextBlock ErrorLine() => new()
    {
        Foreground = Res("Bad"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 420,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>ChoiceDialog puts its extra content into its own window; take it back so the dialog can be shown again.</summary>
    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel p) p.Children.Remove(element);
    }

    private static UIElement WithGenerate(TextBox password)
    {
        var generate = new Button { Content = "Generate", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
        generate.Click += (_, _) => password.Text = PostgresLogins.GeneratePassword();
        var row = new DockPanel();
        DockPanel.SetDock(generate, Dock.Right);
        row.Children.Add(generate);
        row.Children.Add(password);
        return row;
    }

    private static Grid FormGrid(params (string Label, UIElement Control)[] rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = rows[i].Label, Style = MutedStyle(), Margin = new Thickness(0, 6, 8, 0),
                VerticalAlignment = rows[i].Control is StackPanel ? VerticalAlignment.Top : VerticalAlignment.Center,
            };
            Grid.SetRow(label, i);
            g.Children.Add(label);
            if (rows[i].Control is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(rows[i].Control, i);
            Grid.SetColumn(rows[i].Control, 1);
            g.Children.Add(rows[i].Control);
        }
        return g;
    }

    private sealed record AccessChoice(StackPanel Panel, Func<DbLoginAccess> Get)
    {
        public DbLoginAccess Selected => Get();
    }

    private static AccessChoice AccessPicker(DbLoginAccess initial)
    {
        var panel = new StackPanel();
        var group = "access" + Guid.NewGuid().ToString("N");
        var buttons = new List<(RadioButton, DbLoginAccess)>();
        foreach (var (access, help) in new[]
                 {
                     (DbLoginAccess.ReadOnly, "Sees everything and connects, can't change entries"),
                     (DbLoginAccess.Edit, "Adds and changes entries, folders and credentials"),
                     (DbLoginAccess.Admin, "Can edit, and manages logins like this one"),
                 })
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = AccessText(access) });
            text.Children.Add(new TextBlock { Text = help, Style = MutedStyle(), FontSize = 11 });
            var rb = new RadioButton { GroupName = group, Content = text, IsChecked = access == initial, Margin = new Thickness(0, 2, 0, 4) };
            buttons.Add((rb, access));
            panel.Children.Add(rb);
        }
        return new AccessChoice(panel, () => buttons.Where(b => b.Item1.IsChecked == true).Select(b => b.Item2).DefaultIfEmpty(initial).First());
    }
}
