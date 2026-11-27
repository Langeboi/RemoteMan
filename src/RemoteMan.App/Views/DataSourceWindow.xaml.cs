using System.Windows;
using System.Windows.Controls;
using RemoteMan.App.Services;
using RemoteMan.Core.Storage;
using RemoteMan.Data;

namespace RemoteMan.App.Views;

public partial class DataSourceWindow : Window
{
    private readonly AppSettings _settings;
    private DataSourceConfig? _current;

    private static readonly Dictionary<DataSourceKind, string> Hints = new()
    {
        [DataSourceKind.Sqlite] = "Local file, one path per line. Default and fastest for a single user.",
        [DataSourceKind.PostgreSql] = "Recommended for teams: changes reach other users instantly (LISTEN/NOTIFY). A primary and standby pair: one line per server, or Host=pg1,pg2 (RemoteMan always uses whichever is primary). deploy/postgres sets up such a pair on Debian; admins then manage the team's logins on the Users tab.\nHost=pg1;Database=remoteman;Username=remoteman;Password=…",
        [DataSourceKind.SqlServer] = "Changes show within about half a second. Use an Always On listener with MultiSubnetFailover=True, or list a second server.\nServer=sql1;Database=RemoteMan;Integrated Security=True;TrustServerCertificate=True",
        [DataSourceKind.MySql] = "MySQL or MariaDB. Changes show within about half a second.\nServer=db1;Database=remoteman;User ID=remoteman;Password=…",
    };

    public DataSourceWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        KindBox.ItemsSource = Enum.GetValues<DataSourceKind>();
        List.ItemsSource = _settings.DataSources;
        List.SelectedItem = _settings.Active;
        ShowAccount();
    }

    /// <summary>The RemoteMan account line under the list: who's signed in, or the button to sign in.</summary>
    private void ShowAccount()
    {
        var acc = HostedAccount.Instance;
        AccountText.Text = acc.IsSignedIn
            ? $"RemoteMan account: {acc.Account}{(acc.Problem is { } p ? $"\n{p}" : "")}"
            : "Have a RemoteMan account? Sign in and your workspaces show up here.";
        AccountButton.Content = acc.IsSignedIn ? "Account…" : "Sign in…";
    }

    private async void OnAccount(object sender, RoutedEventArgs e)
    {
        Commit();
        var selected = _current;
        _current = null;   // the list may change under it: don't write the boxes back into another source
        if (await AccountDialog.ShowAsync(this, _settings))
        {
            List.ItemsSource = null;
            List.ItemsSource = _settings.DataSources;
            List.SelectedItem = selected is not null && _settings.DataSources.Contains(selected) ? selected
                : _settings.DataSources.LastOrDefault(d => d.Hosted is not null) ?? _settings.Active;
        }
        else _current = selected;
        ShowAccount();
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        Commit();
        _current = List.SelectedItem as DataSourceConfig;
        if (_current is null) return;
        ShowHosted(_current);
        NameBox.Text = _current.Name;
        KindBox.SelectedItem = _current.Kind;
        ConnBox.Text = string.Join(Environment.NewLine, _current.ConnectionStrings);
        VaultBox.Password = Secrets.LoadVaultPassphrase(_current) ?? "";
        SsoBox.IsChecked = _current.UseWindowsLogin;
        GatewayBox.Text = _current.GatewaySite ?? "";
        GatewayAlwaysBox.IsChecked = _current.GatewayAlways;
        if (UsersTab.IsChecked == true) _ = LoadUsersAsync();
    }

    /// <summary>A workspace from the RemoteMan account: name, server and logins come from the server, so only the
    /// vault passphrase and the gateway are set here, and it can't be removed (the admin takes you out of it).</summary>
    private void ShowHosted(DataSourceConfig cfg)
    {
        var h = cfg.Hosted;
        HostedPanel.Visibility = h is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement c in ConnectionGrid.Children)
            if (Grid.GetRow(c) is 0 or 1 or 2 or 4) c.Visibility = h is null ? Visibility.Visible : Visibility.Collapsed;
        Hint.Visibility = h is null ? Visibility.Visible : Visibility.Collapsed;
        UsersTab.Visibility = h is null ? Visibility.Visible : Visibility.Collapsed;
        if (h is not null && UsersTab.IsChecked == true) ConnectionTab.IsChecked = true;
        RemoveButton.IsEnabled = h is null;
        RemoveButton.ToolTip = h is null ? null : "A workspace from your RemoteMan account. It goes when the admin takes you out of it, or when you sign out.";
        if (h is null) return;
        HostedTitle.Text = cfg.Name;
        var server = Uri.TryCreate(h.Server, UriKind.Absolute, out var u) ? u.Host : h.Server;
        HostedText.Text = $"{(h.Description is { Length: > 0 } d ? d + "\n" : "")}A workspace on {server}, from your RemoteMan account {h.Account}. " +
            $"Your access: {h.AccessLabel}.{(cfg.RuntimePassword is null ? " Sign in to open it." : "")}\n\n" +
            "Members and access are managed on the RemoteMan website. The vault passphrase below is shared by everyone in the workspace: " +
            "ask whoever runs it, or choose it if you're the first one in.";
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedItem is DataSourceKind k) Hint.Text = Hints[k];
    }

    private void Commit()
    {
        if (_current is null) return;
        if (_current.Hosted is not null)
        {
            _current.GatewaySite = GatewayBox.Text.Trim().Length == 0 ? null : GatewayBox.Text.Trim();
            _current.GatewayAlways = _current.GatewaySite is not null && GatewayAlwaysBox.IsChecked == true;
            if (VaultBox.Password.Length > 0) Secrets.SaveVaultPassphrase(_current, VaultBox.Password);
            return;
        }
        _current.Name = NameBox.Text.Trim();
        _current.Kind = (DataSourceKind)(KindBox.SelectedItem ?? DataSourceKind.Sqlite);
        _current.ConnectionStrings = ConnBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _current.UseWindowsLogin = SsoBox.IsChecked == true && _current.Kind != DataSourceKind.Sqlite;
        _current.GatewaySite = _current.Kind == DataSourceKind.Sqlite || GatewayBox.Text.Trim().Length == 0 ? null : GatewayBox.Text.Trim();
        _current.GatewayAlways = _current.GatewaySite is not null && GatewayAlwaysBox.IsChecked == true;
        _current.UseSharedVaultKey = _current.Kind != DataSourceKind.Sqlite || VaultBox.Password.Length > 0;
        if (VaultBox.Password.Length > 0) Secrets.SaveVaultPassphrase(_current, VaultBox.Password);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        Commit();
        var d = new DataSourceConfig { Name = "Team", Kind = DataSourceKind.PostgreSql };
        _settings.DataSources.Add(d);
        List.Items.Refresh();
        List.SelectedItem = d;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_current is null || _current.Hosted is not null || _settings.DataSources.Count <= 1) return;
        _settings.DataSources.Remove(_current);
        _current = null;
        List.Items.Refresh();
        List.SelectedIndex = 0;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        Commit();
        if (_current is null) return;
        TestResult.Text = "Testing…";
        try
        {
            await using var src = DataSourceFactory.Create(_current, Services.GatewayService.Instance.RouteFor(_current));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await src.InitializeAsync();
            var n = (await src.LoadAllAsync()).Count;
            TestResult.Text = $"OK: {n} entries, {sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex) { TestResult.Text = ex.GetBaseException().Message; }
    }

    private void OnUse(object sender, RoutedEventArgs e)
    {
        Commit();
        if (_current is null) return;
        if (_current.Kind != DataSourceKind.Sqlite && Secrets.LoadVaultPassphrase(_current) is null)
        {
            TestResult.Text = "Set a vault passphrase for a shared data source.";
            return;
        }
        _settings.ActiveDataSource = _current.Name;
        _settings.Save();
        DialogResult = true;
    }
}
