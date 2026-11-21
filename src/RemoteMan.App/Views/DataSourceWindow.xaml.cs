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
        [DataSourceKind.PostgreSql] = "Recommended for teams: changes reach other users instantly (LISTEN/NOTIFY). A primary and standby pair: one line per server, or Host=pg1,pg2 (RemoteMan always uses whichever is primary). deploy/postgres sets up such a pair on Debian.\nHost=pg1;Database=remoteman;Username=remoteman;Password=…",
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
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        Commit();
        _current = List.SelectedItem as DataSourceConfig;
        if (_current is null) return;
        NameBox.Text = _current.Name;
        KindBox.SelectedItem = _current.Kind;
        ConnBox.Text = string.Join(Environment.NewLine, _current.ConnectionStrings);
        VaultBox.Password = Secrets.LoadVaultPassphrase(_current.Name) ?? "";
        SsoBox.IsChecked = _current.UseWindowsLogin;
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedItem is DataSourceKind k) Hint.Text = Hints[k];
    }

    private void Commit()
    {
        if (_current is null) return;
        _current.Name = NameBox.Text.Trim();
        _current.Kind = (DataSourceKind)(KindBox.SelectedItem ?? DataSourceKind.Sqlite);
        _current.ConnectionStrings = ConnBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _current.UseWindowsLogin = SsoBox.IsChecked == true && _current.Kind != DataSourceKind.Sqlite;
        _current.UseSharedVaultKey = _current.Kind != DataSourceKind.Sqlite || VaultBox.Password.Length > 0;
        if (VaultBox.Password.Length > 0) Secrets.SaveVaultPassphrase(_current.Name, VaultBox.Password);
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
        if (_current is null || _settings.DataSources.Count <= 1) return;
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
            await using var src = DataSourceFactory.Create(_current);
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
        if (_current.Kind != DataSourceKind.Sqlite && Secrets.LoadVaultPassphrase(_current.Name) is null)
        {
            TestResult.Text = "Set a vault passphrase for a shared data source.";
            return;
        }
        _settings.ActiveDataSource = _current.Name;
        _settings.Save();
        DialogResult = true;
    }
}
