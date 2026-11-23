using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteMan.App.ViewModels;

namespace RemoteMan.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private DashboardViewModel? Vm => DataContext as DashboardViewModel;

    /// <summary>Single click selects the entry in the tree (the button's command); double-click connects.</summary>
    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DashEntry row && e.ChangedButton == MouseButton.Left)
        {
            Vm?.ConnectCommand.Execute(row.Id);
            e.Handled = true;
        }
    }

    /// <summary>Double-click on a backup row: its Config backups tab, or the device's page in Oxidized.</summary>
    private void OnBackupDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DashBackup row && e.ChangedButton == MouseButton.Left)
        {
            Vm?.OpenBackupCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void OnWidgetsMenu(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (key, label) in vm.Widgets)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = vm.IsShown(key), StaysOpenOnClick = true };
            if (key == "zabbix" && !vm.ZabbixOn) item.Header = $"{label} (set up Zabbix to see it)";
            if (key == "backups" && !vm.BackupsOn) item.Header = $"{label} (set up Oxidized to see it)";
            item.Click += (_, _) => vm.SetShown(key, item.IsChecked);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
