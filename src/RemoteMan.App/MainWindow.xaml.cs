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

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _vm = new MainViewModel(settings)
        {
            EditDialog = (entry, store, protector) => new EntryEditorWindow(entry, store, protector) { Owner = this }.ShowDialog() == true ? entry : null,
            Choose = (title, text, buttons) => Views.ChoiceDialog.Show(this, title, text, buttons),
            Confirm = text => MessageBox.Show(this, text, "RemoteMan", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
        };
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closing += async (_, _) => await _vm.DisposeAsync();
        PreviewKeyDown += OnWindowKeyDown;
        StateChanged += (_, _) => MaxGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    // ---------------- title bar ----------------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Everything that used to live in the menu bar.</summary>
    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Cmd(string header, ICommand cmd, string? gesture = null) => new() { Header = header, Command = cmd, InputGestureText = gesture ?? "" };
        MenuItem Click(string header, RoutedEventHandler h) { var m = new MenuItem { Header = header }; m.Click += h; return m; }
        menu.Items.Add(Cmd("Duplicate…", _vm.DuplicateCommand, "Ctrl+D"));
        menu.Items.Add(Cmd("Delete", _vm.DeleteCommand, "Del"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Copy user name", _vm.CopyUserNameCommand, "Ctrl+Shift+U"));
        menu.Items.Add(Cmd("Copy password", _vm.CopyPasswordCommand, "Ctrl+Shift+P"));
        menu.Items.Add(Cmd("Copy both (paste twice)", _vm.CopyBothCommand, "Ctrl+Shift+B"));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Close tab", Command = _vm.CloseSessionCommand, CommandParameter = _vm.ActiveSession, InputGestureText = "Ctrl+W" });
        menu.Items.Add(new Separator());
        menu.Items.Add(Click("Data sources…", OnDataSources));
        menu.Items.Add(Click("Exit", OnExit));
        menu.IsOpen = true;
    }

    // ---------------- icon rail ----------------

    private void OnRailChecked(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is FrameworkElement { Tag: string mode }) _vm.PanelMode = mode;
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        static void Walk(IEnumerable<EntryNode> nodes)
        {
            foreach (var n in nodes) { if (n.IsFolder) n.IsExpanded = false; Walk(n.Children); }
        }
        Walk(_vm.Roots);
    }

    // ---------------- panes ----------------

    private void OnLeftPaneMouseDown(object sender, MouseButtonEventArgs e) => _vm.FocusedPane = 0;
    private void OnRightPaneMouseDown(object sender, MouseButtonEventArgs e) { if (_vm.IsSplit) _vm.FocusedPane = 1; }

    private void OnSessionFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionViewModel s && _vm.ActiveSession != s) _vm.ActiveSession = s;
    }

    private void OnSessionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionViewModel s && _vm.ActiveSession != s) _vm.ActiveSession = s;
    }

    private void OnMoveTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionViewModel s) _vm.MoveToOtherPaneCommand.Execute(s);
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionViewModel s) _vm.CloseSessionCommand.Execute(s);
    }

    // ---------------- status bar ----------------

    private void OnQuickHostKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.QuickConnectHostCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.QuickHost = ""; Tree.Focus(); e.Handled = true; }
    }

    private void OnQPopupClosed(object? sender, EventArgs e) => QButton.IsChecked = false;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
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
        _dragStart = e.GetPosition(Tree);
        _dragNode = FindItem(e.OriginalSource as DependencyObject)?.DataContext as EntryNode;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is not EntryNode clicked) return;
        if (clicked.Entry.Kind != Core.Model.EntryKind.Credential) return;
        e.Handled = true;
        await _vm.TryCtrlClickLinkAsync(clicked);
    }

    // ---------------- drag and drop: move entries between folders ----------------

    private const string DragFormat = "RemoteMan.EntryId";
    private Point _dragStart;
    private EntryNode? _dragNode;
    private EntryNode? _dropHighlight;

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode is null || Keyboard.Modifiers == ModifierKeys.Control) return;
        var d = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = _dragNode;
        _dragNode = null;
        DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, node.Id.ToString()), DragDropEffects.Move);
        SetDropHighlight(null);
    }

    /// <summary>Dropping on a folder moves into it; on an entry, into that entry's folder; on empty space, to the top level.</summary>
    private Guid? DropFolder(DragEventArgs e, out EntryNode? highlight)
    {
        highlight = null;
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is not EntryNode target) return null;
        if (target.IsFolder) { highlight = target; return target.Id; }
        if (target.Entry.ParentId is { } p && _vm.FindNode(p) is { } parent) { highlight = parent; return p; }
        return null;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(DragFormat) is string id && Guid.TryParse(id, out var moving))
        {
            var folder = DropFolder(e, out var highlight);
            if (_vm.CanMove(moving, folder)) { e.Effects = DragDropEffects.Move; SetDropHighlight(highlight); }
            else SetDropHighlight(null);
        }
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => SetDropHighlight(null);

    private async void OnTreeDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if (e.Data.GetData(DragFormat) is not string id || !Guid.TryParse(id, out var moving)) return;
        var folder = DropFolder(e, out _);
        e.Handled = true;
        await _vm.MoveAsync(moving, folder);
    }

    private void SetDropHighlight(EntryNode? node)
    {
        if (ReferenceEquals(node, _dropHighlight)) return;
        if (_dropHighlight is not null) _dropHighlight.IsDropTarget = false;
        _dropHighlight = node;
        if (node is not null) node.IsDropTarget = true;
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
        foreach (var (label, kind) in new[] { ("New RDP entry", "Rdp"), ("New SSH entry", "Ssh"), ("New web entry", "Web"), ("New VNC entry", "Vnc"), ("New credential", "Credential"), ("New folder", "Folder") })
            menu.Items.Add(new MenuItem { Header = label, Command = _vm.NewEntryCommand, CommandParameter = kind });
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnDataSources(object sender, RoutedEventArgs e)
    {
        var dlg = new DataSourceWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
            MessageBox.Show(this, "Restart RemoteMan to switch data source.", "RemoteMan");
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private static TreeViewItem? FindItem(DependencyObject? d)
    {
        while (d is not null and not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }
}
