using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.App.Views;

namespace RemoteMan.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly AiAssistantViewModel _ai;
    private AiAssistantWindow? _aiWindow;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _vm = new MainViewModel(settings)
        {
            EditDialog = (entry, store, protector) => new EntryEditorWindow(entry, store, protector) { Owner = this }.ShowDialog() == true ? entry : null,
            Confirm = text => MessageBox.Show(this, text, "RemoteMan", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
        };
        DataContext = _vm;

        _ai = new AiAssistantViewModel(settings, () => _vm.ActiveSession) { StatusMessage = m => _vm.Message = m };
        AiButton.DataContext = _ai;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ActiveSession)) _ai.RefreshSession(); };
        LocationChanged += (_, _) => _aiWindow?.Reposition();
        SizeChanged += (_, _) => _aiWindow?.Reposition();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _aiWindow?.HideBox(); else _aiWindow?.Reposition(); };
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closing += async (_, _) => await _vm.DisposeAsync();
        PreviewKeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleAi();
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Search.Focus();
            Search.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.QuickConnectCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.SearchText = ""; Tree.Focus(); e.Handled = true; }
        else if (e.Key == Key.Down) { Tree.Focus(); e.Handled = true; }
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.Selected = e.NewValue as EntryNode;

    /// <summary>Ctrl+click a credential to link it to the selected connection, without changing the selection.</summary>
    private async void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is not EntryNode clicked) return;
        if (clicked.Entry.Kind != Core.Model.EntryKind.Credential) return;
        e.Handled = true;
        await _vm.TryCtrlClickLinkAsync(clicked);
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is EntryNode { Entry.IsConnectable: true } n)
        {
            _vm.ConnectCommand.Execute(n);
            e.Handled = true;
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.Selected is { } n)
        {
            if (n.Entry.IsConnectable) _vm.ConnectCommand.Execute(n); else n.IsExpanded = !n.IsExpanded;
            e.Handled = true;
        }
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is SessionViewModel s)
            _vm.CloseSessionCommand.Execute(s);
    }

    private void OnNewMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (label, kind) in new[] { ("RDP", "Rdp"), ("SSH", "Ssh"), ("Web", "Web"), ("VNC", "Vnc"), ("Credential", "Credential"), ("Folder", "Folder") })
            menu.Items.Add(new MenuItem { Header = label, Command = _vm.NewEntryCommand, CommandParameter = kind });
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void OnDataSources(object sender, RoutedEventArgs e)
    {
        var dlg = new DataSourceWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
            MessageBox.Show(this, "Restart RemoteMan to switch data source.", "RemoteMan");
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAiButton(object sender, RoutedEventArgs e) => ToggleAi();

    /// <summary>Opens the chat box under the AI button, or puts it back on standby.</summary>
    private void ToggleAi()
    {
        _aiWindow ??= new AiAssistantWindow(_ai) { Owner = this };
        if (_aiWindow.IsVisible) _aiWindow.HideBox();
        else _aiWindow.ShowUnder(AiButton);
    }

    private static TreeViewItem? FindItem(DependencyObject? d)
    {
        while (d is not null and not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }
}
