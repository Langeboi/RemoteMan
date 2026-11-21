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
            PromptPassword = title => PasswordPrompt.Show(this, title),
            Choose = (title, text, buttons) => Views.ChoiceDialog.Show(this, title, text, buttons, Views.DialogIcon.Question),
            Confirm = (title, text, action) => Views.ChoiceDialog.Confirm(this, title, text, action, danger: true),
        };
        _vm.OpenWindow = session => new Views.SessionWindow(session, this).Show();
        _vm.EditDialog = (entry, store, protector) =>
        {
            var w = new EntryEditorWindow(entry, store, protector) { Owner = this, Zabbix = _vm.ZabbixSnapshot };
            if (w.ShowDialog() != true) return null;
            // A login typed in the entry and kept as a reusable credential is saved first.
            return w.NewCredential is { } cred ? [cred, entry] : [entry];
        };
        _vm.ZabbixLinkDialog = (snap, entry) =>
        {
            var dlg = new ZabbixLinkWindow(snap, entry) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.ChosenHostId ?? "" : null;
        };
        _vm.ZabbixImportDialog = (snap, store) =>
        {
            var dlg = new ZabbixImportWindow(snap, store, _settings.Zabbix.Url, _vm.ImportParentFolder()) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Plan : null;
        };
        _vm.ZabbixSettingsDialog = () =>
            new ZabbixSettingsWindow(_settings, _vm.Store, _vm.Protector) { Owner = this }.ShowDialog() == true;
        _vm.OxidizedLinkDialog = (snap, entry) =>
        {
            var dlg = new OxidizedLinkWindow(snap, entry) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.ChosenNode ?? "" : null;
        };
        _vm.OxidizedSettingsDialog = () =>
            new OxidizedSettingsWindow(_settings, _vm.Store, _vm.Protector) { Owner = this }.ShowDialog() == true;
        DataContext = _vm;
        SetUpNotices();

        // The Dashboard tab isn't a connection: the assistant sees no session while it's showing.
        _ai = new AiAssistantViewModel(settings, () => _vm.ActiveSession is DashboardViewModel ? null : _vm.ActiveSession) { StatusMessage = m => _vm.Message = m };
        _vm.RevealRequested += (_, node) => Dispatcher.BeginInvoke(() => RevealInTree(node), System.Windows.Threading.DispatcherPriority.Loaded);
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.PanelMode)) SyncRail(); };
        AiButton.DataContext = _ai;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ActiveSession)) _ai.RefreshSession(); };
        LocationChanged += (_, _) => _aiWindow?.Reposition();
        SizeChanged += (_, _) => _aiWindow?.Reposition();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _aiWindow?.HideBox(); else _aiWindow?.Reposition(); };
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closing += async (_, _) => { _vm.FreezeOpenSessions(); await _vm.DisposeAsync(); };
        _vm.AskReopen = AskReopen;
        PreviewKeyDown += OnWindowKeyDown;
        StateChanged += (_, _) => MaxGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    /// <summary>Startup question: reopen what was open when RemoteMan last closed.</summary>
    private (bool Reopen, bool Remember) AskReopen(IReadOnlyList<string> names)
    {
        var shown = names.Take(8).Select(n => "  •  " + n).ToList();
        if (names.Count > 8) shown.Add($"  …and {names.Count - 8} more");
        var remember = new CheckBox { Content = "Don't ask again (you can change this under More > On startup)", Margin = new Thickness(0, 12, 0, 0) };
        var text = (names.Count == 1 ? "This session was open" : $"These {names.Count} sessions were open") +
                   " when you closed RemoteMan:\n\n" + string.Join("\n", shown);
        var pick = ChoiceDialog.Show(this, "Reopen last sessions?", text, ["Reopen", "Not now"], DialogIcon.Question, extra: remember);
        return (pick == 0, remember.IsChecked == true && pick >= 0);
    }

    // ---------------- connection notices ----------------

    /// <summary>Bottom-right of the window, above the status bar; follows the window and hides while it's in the background.</summary>
    private void SetUpNotices()
    {
        NoticePopup.PlacementTarget = Root;
        NoticePopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(target.Width - popup.Width - 12, target.Height - popup.Height - 30),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.None)];
        void Nudge()
        {
            if (!NoticePopup.IsOpen) return;
            NoticePopup.HorizontalOffset += 0.1;
            NoticePopup.HorizontalOffset -= 0.1;
        }
        LocationChanged += (_, _) => Nudge();
        SizeChanged += (_, _) => Nudge();
        StateChanged += (_, _) => Nudge();
        _vm.Notices.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(Nudge, System.Windows.Threading.DispatcherPriority.Loaded);
        // A popup floats above other apps; show it only while RemoteMan is the active window.
        void Update() => NoticePopup.IsOpen = _vm.HasNotices && IsActive && WindowState != WindowState.Minimized;
        _vm.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(MainViewModel.HasNotices)) Update(); };
        Deactivated += (_, _) => Update();
        Activated += (_, _) => Update();
        StateChanged += (_, _) => Update();
    }

    private void OnNoticeEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Notice n) { n.IsHovered = true; n.IsFaded = false; }
    }

    private void OnNoticeLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Notice n) { n.IsHovered = false; n.Shown = DateTimeOffset.Now; }
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
        menu.Items.Add(Click("Import…", OnImport));
        menu.Items.Add(Click("Export and backup…", OnExportBackup));
        menu.Items.Add(Click("Restore from backup…", OnRestore));
        menu.Items.Add(Cmd("Zabbix settings…", _vm.ZabbixSettingsCommand));
        if (_vm.ZabbixActive) menu.Items.Add(Cmd("Import from Zabbix…", _vm.ImportFromZabbixCommand));
        menu.Items.Add(Cmd("Oxidized settings…", _vm.OxidizedSettingsCommand));
        var startup = new MenuItem { Header = "On startup, reopen last sessions" };
        foreach (var (mode, label) in new[] { (ReopenMode.Ask, "Ask"), (ReopenMode.Always, "Always"), (ReopenMode.Never, "Never") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _vm.ReopenSessions == mode };
            item.Click += (_, _) => _vm.ReopenSessions = mode;
            startup.Items.Add(item);
        }
        menu.Items.Add(startup);
        menu.Items.Add(new Separator());
        var updates = RemoteMan.App.Updates.UpdateService.Instance;
        menu.Items.Add(new MenuItem { Header = "Check for updates", Command = updates.CheckNowCommand });
        var auto = new MenuItem { Header = "Check for updates automatically", IsCheckable = true, IsChecked = updates.AutoCheck };
        auto.Click += (_, _) => updates.AutoCheck = auto.IsChecked;
        menu.Items.Add(auto);
        var channel = new MenuItem { Header = "Update channel" };
        channel.Items.Add(new MenuItem { Header = "Releases", IsCheckable = true, IsChecked = !updates.UsesTestBuilds, Command = updates.SetChannelCommand, CommandParameter = "stable" });
        channel.Items.Add(new MenuItem { Header = "Test builds (newest changes, may have bugs)", IsCheckable = true, IsChecked = updates.UsesTestBuilds, Command = updates.SetChannelCommand, CommandParameter = "test" });
        menu.Items.Add(channel);
        menu.Items.Add(new MenuItem { Header = "Release notes", Command = updates.OpenReleaseNotesCommand });
        menu.Items.Add(new MenuItem { Header = $"RemoteMan {updates.CurrentVersion}", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Click("Exit", OnExit));
        menu.IsOpen = true;
    }

    // ---------------- icon rail ----------------

    private void OnRailChecked(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is FrameworkElement { Tag: string mode }) _vm.PanelMode = mode;
    }

    /// <summary>Keeps the rail's checked button in step when the panel changes from code (the dashboard leaving Favorites, say).</summary>
    private void SyncRail()
    {
        foreach (var rb in FindChildren<RadioButton>(this))
            if (rb.GroupName == "Rail" && rb.Tag as string == _vm.PanelMode && rb.IsChecked != true) rb.IsChecked = true;
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) yield return t;
            foreach (var d in FindChildren<T>(c)) yield return d;
        }
    }

    /// <summary>Scrolls the tree to an entry the dashboard picked and focuses it.</summary>
    private void RevealInTree(EntryNode node)
    {
        Tree.UpdateLayout();
        if (FindContainer(Tree, node) is { } item) { item.BringIntoView(); item.Focus(); }
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, EntryNode node)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem direct) return direct;
        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem tvi || !tvi.IsExpanded) continue;
            tvi.UpdateLayout();
            if (FindContainer(tvi, node) is { } found) return found;
        }
        return null;
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        static void Walk(IEnumerable<EntryNode> nodes)
        {
            foreach (var n in nodes) { if (n.IsFolder && !n.IsRoot) n.IsExpanded = false; Walk(n.Children); }
        }
        Walk(_vm.Roots);
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        static void Walk(IEnumerable<EntryNode> nodes)
        {
            foreach (var n in nodes) { if (n.IsFolder) n.IsExpanded = true; Walk(n.Children); }
        }
        Walk(_vm.Roots);
    }

    /// <summary>Clicking a folder's chevron opens or closes it without selecting it.</summary>
    private void OnChevronDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if ((sender as FrameworkElement)?.DataContext is EntryNode { IsFolder: true } n)
        {
            n.IsExpanded = !n.IsExpanded;
            e.Handled = true;
        }
    }

    // ---------------- bottom bar: data source picker and panel tabs ----------------

    private void OnDataSourcePicker(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var ds in _settings.DataSources)
        {
            var name = ds.Name;
            var item = new MenuItem { Header = $"{ds.Name}  ({ds.Kind})", IsCheckable = true, IsChecked = ds.Name == _settings.Active.Name };
            item.Click += (_, _) => SwitchDataSource(name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage data sources…" };
        manage.Click += OnDataSources;
        menu.Items.Add(manage);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void SwitchDataSource(string name)
    {
        if (name == _settings.Active.Name) return;
        _settings.ActiveDataSource = name;
        _settings.Save();
        Views.ChoiceDialog.Info(this, "Restart to switch", $"Restart RemoteMan to open {name}.");
    }

    private void OnPanelMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string header, RoutedEventHandler click) { var i = new MenuItem { Header = header }; i.Click += click; menu.Items.Add(i); }
        Add("Expand all", OnExpandAll);
        Add("Collapse all", OnCollapseAll);
        menu.Items.Add(new Separator());
        Add("Import…", OnImport);
        Add("Manage data sources…", OnDataSources);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OnPanelRefresh(object sender, RoutedEventArgs e) => _ = _vm.RefreshAsync();

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

    private void OnTabMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionViewModel { IsPinned: true }) e.Handled = true;
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
    private void OnZPopupClosed(object? sender, EventArgs e) => ZButton.IsChecked = false;
    private void OnZPopupButton(object sender, RoutedEventArgs e) => ZButton.IsChecked = false;

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

    // The data source node can be highlighted but is not an entry: commands see no selection (new, paste go to the top level).
    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.Selected = e.NewValue is EntryNode { IsRoot: false } n ? n : null;

    /// <summary>Ctrl+click a credential to link it to the selected connection, without changing the selection.</summary>
    private async void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragNode = FindItem(e.OriginalSource as DependencyObject)?.DataContext as EntryNode;
        if (_dragNode?.IsRoot == true) _dragNode = null;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is not EntryNode clicked) return;
        if (clicked.Entry.Kind != Core.Model.EntryKind.Credential) return;
        e.Handled = true;
        await _vm.TryCtrlClickLinkAsync(clicked);
    }

    // ---------------- right-click menu ----------------

    /// <summary>Right-click selects the entry under the mouse first, so the menu acts on it.</summary>
    private bool _rightClickOnEmpty;

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindItem(e.OriginalSource as DependencyObject);
        _rightClickOnEmpty = item is null;
        if (item is not null) { item.IsSelected = true; item.Focus(); }
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Tree.ContextMenu is not { } menu) { e.Handled = true; return; }
        // Empty space (or the keyboard menu key with nothing selected): new, paste, expand, import.
        if (_rightClickOnEmpty || _vm.Selected is not { } node)
        {
            _rightClickOnEmpty = false;
            EntryMenu.FillBackground(menu, _vm, () => OnImport(this, new RoutedEventArgs()));
            return;
        }
        EntryMenu.Fill(menu, node, _vm, this);
        if (menu.Items.Count == 0) e.Handled = true;
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
        if (target.IsRoot) { highlight = target; return null; }
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
        if (e.Key == Key.Enter && Tree.SelectedItem is EntryNode { IsRoot: true } root)
        {
            root.IsExpanded = !root.IsExpanded;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.Selected is { } n)
        {
            if (n.Entry.IsConnectable) _vm.ConnectCommand.Execute(n); else n.IsExpanded = !n.IsExpanded;
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _vm.Selected is { } c && c.Id != MainViewModel.FavoritesId)
        {
            _vm.CopyEntry(c.Entry);
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var target = _vm.Selected?.Entry is { } s ? (s.Kind == RemoteMan.Core.Model.EntryKind.Folder ? s.Id : s.ParentId) : null;
            _ = _vm.PasteAsync(target == MainViewModel.FavoritesId ? null : target);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = _vm.RefreshAsync();
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
        var parent = _vm.Selected?.Entry is { } s ? (s.Kind == RemoteMan.Core.Model.EntryKind.Folder ? s.Id : s.ParentId) : null;
        EntryMenu.FillNew(menu, _vm, parent == MainViewModel.FavoritesId ? null : parent);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnDataSources(object sender, RoutedEventArgs e)
    {
        var dlg = new DataSourceWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
            Views.ChoiceDialog.Info(this, "Restart to switch", "Restart RemoteMan to switch to the new data source.");
    }

    private void OnImport(object sender, RoutedEventArgs e) => new ImportWindow(_vm) { Owner = this }.ShowDialog();
    private void OnExportBackup(object sender, RoutedEventArgs e) => new BackupWindow(_vm) { Owner = this }.ShowDialog();
    private void OnRestore(object sender, RoutedEventArgs e) => new BackupWindow(_vm, section: 2) { Owner = this }.ShowDialog();

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
