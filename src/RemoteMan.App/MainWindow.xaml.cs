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
        // Opens maximized (see WindowState in the XAML). The restored size is what "Restore down" gives:
        // keep it inside a smaller screen so it never comes back bigger than the monitor.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.9);
        _settings = settings;
        _vm = new MainViewModel(settings)
        {
            PromptPassword = title => PasswordPrompt.Show(this, title),
            Choose = (title, text, buttons) => Views.ChoiceDialog.Show(this, title, text, buttons, Views.DialogIcon.Question),
            Confirm = (title, text, action) => Views.ChoiceDialog.Confirm(this, title, text, action, danger: true),
        };
        _vm.OpenWindow = session => new Views.SessionWindow(session, this).Show();
        _vm.PickInvitees = (online, entryName, chat) =>
        {
            var w = new Views.ShareInviteWindow(this, online, entryName, chat);
            return w.ShowDialog() == true ? w.Choice : null;
        };
        _vm.TemplatesDialog = () => new TemplatesWindow(_vm) { Owner = this }.ShowDialog();
        _vm.SaveAsTemplateDialog = entry => new TemplatesWindow(_vm, RemoteMan.Core.Model.EntryTemplates.FromEntry(entry, entry.Name + " template")) { Owner = this }.ShowDialog();
        _vm.EditDialog = (entry, store, protector) =>
        {
            var w = new EntryEditorWindow(entry, store, protector)
            {
                Owner = this, Zabbix = _vm.ZabbixSnapshot,
                TemplateHeading = _vm.EditingFromTemplate is { } t ? $"New {EntryIcons.KindName(EntryNode.KindTagFor(entry.Kind))} entry from template {t.Name}"
                    + (t.Template?.CompanionKind is { } ck ? $" (also makes a {ck} entry)" : "") : null,
            };
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
            var dlg = new ZabbixImportWindow(snap, store, _vm.ZabbixCfg.Url, _vm.ImportParentFolder()) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Plan : null;
        };
        _vm.ZabbixSettingsDialog = () =>
            new ZabbixSettingsWindow(_vm.Integrations, _vm.Store, _vm.Protector) { Owner = this }.ShowDialog() == true;
        _vm.OxidizedLinkDialog = (snap, entry) =>
        {
            var dlg = new OxidizedLinkWindow(snap, entry) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.ChosenNode ?? "" : null;
        };
        _vm.OxidizedSettingsDialog = () =>
            new OxidizedSettingsWindow(_vm.Integrations, _vm.Store, _vm.Protector) { Owner = this }.ShowDialog() == true;
        _vm.OxidizedFeedDialog = () => new OxidizedFeedWindow(_vm) { Owner = this }.ShowDialog();
        DataContext = _vm;
        SetUpNotices();

        // The Dashboard tab (and the entry overview inside it) isn't a connection: the assistant sees no session while it's showing.
        _ai = new AiAssistantViewModel(settings, () => _vm.ActiveSession is { IsConnection: false } ? null : _vm.ActiveSession) { StatusMessage = m => _vm.Message = m };
        _vm.RevealRequested += (_, node) => Dispatcher.BeginInvoke(() => RevealInTree(node), System.Windows.Threading.DispatcherPriority.Loaded);
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.PanelMode)) SyncRail(); };
        AiButton.DataContext = _ai;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ActiveSession)) _ai.RefreshSession(); };
        LocationChanged += (_, _) => _aiWindow?.Reposition();
        SizeChanged += (_, _) => _aiWindow?.Reposition();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _aiWindow?.HideBox(); else _aiWindow?.Reposition(); };
        Loaded += async (_, _) =>
        {
            await _vm.InitializeAsync();
            // The first-run setup picked an import source: open the import window on it once the entries are loaded.
            if (ImportAfterLoad is { } source) { ImportAfterLoad = null; new ImportWindow(_vm, source) { Owner = this }.ShowDialog(); }
            // First start after an update: show what changed since the version that ran before.
            if (RemoteMan.App.Updates.WhatsNew.Pending.Count > 0) _vm.OpenWhatsNew();
        };
        Closing += async (_, _) => { _vm.FreezeOpenSessions(); await _vm.DisposeAsync(); };
        _vm.AskReopen = AskReopen;
        _vm.AskMove = AskMove;
        PreviewKeyDown += OnWindowKeyDown;
        SetUpChat();
        SetUpGatewayPill();
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

    /// <summary>"Move X to Y?" before drag-drop or Move to moves entries in the tree.</summary>
    private (bool Move, bool DontAsk) AskMove(string title, string text)
    {
        var dontAsk = new CheckBox { Content = "Don't ask again (turn it back on under More > Ask before moving entries)", Margin = new Thickness(0, 12, 0, 0) };
        var pick = ChoiceDialog.Show(this, title, text, ["Move", "Cancel"], DialogIcon.Question, extra: dontAsk);
        return (pick == 0, dontAsk.IsChecked == true);
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
        void Update() => NoticePopup.IsOpen = (_vm.HasNotices || _vm.Chat.Toasts.Count > 0) && IsActive && WindowState != WindowState.Minimized;
        _vm.Chat.Toasts.CollectionChanged += (_, _) => { Update(); Dispatcher.BeginInvoke(Nudge, System.Windows.Threading.DispatcherPriority.Loaded); };
        _vm.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(MainViewModel.HasNotices)) Update(); };
        Deactivated += (_, _) => Update();
        Activated += (_, _) => Update();
        StateChanged += (_, _) => Update();
    }

    // ---------------- team chat ----------------

    private void SetUpChat()
    {
        var chat = _vm.Chat;
        chat.NewGroupDialog = users =>
        {
            var w = new NewChatGroupWindow(this, users);
            return w.ShowDialog() == true ? (w.GroupName, w.Members) : null;
        };
        ChatPanel.ResolveEntry = id => _vm.Store?.Get(id);
        Activated += (_, _) => { _vm.IsWindowActive = true; chat.WindowActivated(); };
        Deactivated += (_, _) => { _vm.IsWindowActive = false; chat.IsPickerOpen = false; };
        _vm.ChatAttention += (_, _) => FlashTaskbar();
    }

    /// <summary>Clicking someone in "who's online" opens a direct conversation with them.</summary>
    private void OnAvatarClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PresenceChip chip || !_vm.Chat.IsAvailable) return;
        if (chip.User is { } user) _vm.Chat.OpenDirect(user); else _vm.Chat.IsOpen = true;
        e.Handled = true;
    }

    private void OnChatToastClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;   // the buttons do their own thing
        if ((sender as FrameworkElement)?.DataContext is ChatToast t) _vm.Chat.OpenToastCommand.Execute(t);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>A chat message for you while RemoteMan is in the background: flash the taskbar button until it's opened.</summary>
    private void FlashTaskbar()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = new FlashInfo { Hwnd = hwnd, Flags = 0x2 | 0xC /* tray, until foreground */, Count = 3 };
        info.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
        FlashWindowEx(ref info);
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
        menu.Items.Add(Click("Templates…", (_, _) => _vm.TemplatesDialog?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Click("Data sources…", OnDataSources));
        menu.Items.Add(Click("Import…", OnImport));
        menu.Items.Add(Click("Export and backup…", OnExportBackup));
        menu.Items.Add(Click("Restore from backup…", OnRestore));
        menu.Items.Add(Cmd("Zabbix settings…", _vm.ZabbixSettingsCommand));
        if (_vm.ZabbixActive) menu.Items.Add(Cmd("Import from Zabbix…", _vm.ImportFromZabbixCommand));
        menu.Items.Add(Cmd("Oxidized settings…", _vm.OxidizedSettingsCommand));
        menu.Items.Add(Cmd("Oxidized device list…", _vm.OxidizedDevicesCommand));
        menu.Items.Add(Click("Gateway settings…", (_, _) => OpenGatewaySettings()));
        var startup = new MenuItem { Header = "On startup, reopen last sessions" };
        foreach (var (mode, label) in new[] { (ReopenMode.Ask, "Ask"), (ReopenMode.Always, "Always"), (ReopenMode.Never, "Never") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _vm.ReopenSessions == mode };
            item.Click += (_, _) => _vm.ReopenSessions = mode;
            startup.Items.Add(item);
        }
        menu.Items.Add(startup);
        var askMove = new MenuItem { Header = "Ask before moving entries", IsCheckable = true, IsChecked = _vm.ConfirmMoves,
            ToolTip = "Asks \"Move X to Y?\" when you drag entries to another folder or use Move to." };
        askMove.Click += (_, _) => _vm.ConfirmMoves = askMove.IsChecked;
        menu.Items.Add(askMove);
        menu.Items.Add(Click("Theme…", OnTheme));
        var vivid = new MenuItem { Header = "Vivid colors in SSH and web tabs", IsCheckable = true, IsChecked = _vm.VividColors,
            ToolTip = "Shows colours unmanaged, like RDM and most native apps, so they look saturated on wide-gamut screens. Applies after a restart." };
        vivid.Click += (_, _) => _vm.VividColors = vivid.IsChecked;
        menu.Items.Add(vivid);
        menu.Items.Add(Click("Run setup again…", OnRunSetup));
        if (_vm.Chat.IsAvailable)
        {
            var keep = new MenuItem { Header = "Keep chat messages" };
            foreach (var (days, label) in new[] { (0, "Forever"), (365, "1 year"), (90, "90 days") })
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _vm.ChatRetentionDays == days,
                    ToolTip = "Older messages are deleted for everyone when RemoteMan starts. The shortest setting on any PC wins." };
                item.Click += (_, _) => _vm.ChatRetentionDays = days;
                keep.Items.Add(item);
            }
            menu.Items.Add(keep);
        }
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
        menu.Items.Add(Click("What's new", (_, _) => _vm.OpenWhatsNew()));
        menu.Items.Add(new MenuItem { Header = "Release notes", Command = updates.OpenReleaseNotesCommand });
        menu.Items.Add(new MenuItem { Header = $"RemoteMan {updates.CurrentVersion}", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Click("Exit", OnExit));
        menu.IsOpen = true;
    }

    /// <summary>More > Theme: pick a colour theme and shading; it applies after a restart, which can happen right away.</summary>
    private void OnTheme(object sender, RoutedEventArgs e)
    {
        var w = new Views.ThemeWindow(_vm.Theme, _vm.Shading) { Owner = this };
        if (w.ShowDialog() != true) return;
        if (w.Picked == Services.Themes.Current.Id && w.PickedShading == Services.Themes.CurrentShading) return;
        _vm.Theme = w.Picked;
        _vm.Shading = w.PickedShading;
        var name = $"{Services.Themes.Find(w.Picked).Name} with {w.PickedShading.ToString().ToLowerInvariant()} shading";
        var choice = Views.ChoiceDialog.Show(this, "Restart to apply the theme",
            $"{name} is used from the next start. Restart RemoteMan now? Open sessions are closed; you can reopen them when it starts.",
            ["Restart now", "Later"], Views.DialogIcon.Question);
        if (choice == 0) RestartApp();
    }

    /// <summary>Import source ("rdm", "rdp" or "putty") the first-run setup asked for, opened once the entries have loaded.</summary>
    public string? ImportAfterLoad { get; set; }

    /// <summary>More > Run setup again: the first-run steps, starting from the current settings.</summary>
    private void OnRunSetup(object sender, RoutedEventArgs e)
    {
        var w = new Views.SetupWindow(_settings, rerun: true) { Owner = this };
        w.ShowDialog();
        if (w.ImportAfter is { } source) new ImportWindow(_vm, source) { Owner = this }.ShowDialog();
        if (!w.NeedsRestart) return;
        var choice = Views.ChoiceDialog.Show(this, "Restart to apply setup",
            "Some of the changes are used from the next start. Restart RemoteMan now? Open sessions are closed; you can reopen them when it starts.",
            ["Restart now", "Later"], Views.DialogIcon.Question);
        if (choice == 0) RestartApp();
    }

    /// <summary>Closes this window (which saves the open tabs for reopening) and starts RemoteMan again.</summary>
    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) { Close(); return; }
        Closed += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Restart failed: {ex.Message}"); }
        };
        Close();
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

    private void OnShareTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionViewModel s) _ = _vm.ShareSessionAsync(s);
    }

    private void OnStopShareTab(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionViewModel s) _ = _vm.StopSharingAsync(s);
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

    // ── Gateway pill (bottom left): shown only with a gateway set up on this PC ──────────────────────────────────
    private Views.GatewayStatusBox? _gwBox;
    private string? _gwLed;

    private void SetUpGatewayPill()
    {
        var gw = Services.GatewayService.Instance;
        gw.Changed += () => Dispatcher.BeginInvoke(SyncGatewayPill);
        SyncGatewayPill();
    }

    private void SyncGatewayPill()
    {
        var gw = Services.GatewayService.Instance;
        GwPill.Visibility = gw.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        if (!gw.IsConfigured) { GwButton.IsChecked = false; return; }
        var site = gw.LiveSites.FirstOrDefault(s => string.Equals(s.Name, gw.ActiveSite, StringComparison.OrdinalIgnoreCase));
        var (led, tip) = gw.State switch
        {
            Services.GatewayState.Down => ("Bad", $"Gateway: can't reach the relay {gw.RelayHost}"),
            Services.GatewayState.Connecting => ("Accent", $"Gateway: connecting to {gw.RelayHost}"),
            _ when site is { Online: false } => ("Warn", $"Gateway: {site.Name} is offline"),
            _ => ("Ok", $"Gateway: connected to {gw.RelayHost}{(gw.Latency is { } l ? $" ({l.TotalMilliseconds:0} ms)" : "")}"),
        };
        GwButton.ToolTip = tip;
        if (led == _gwLed) return;
        _gwLed = led;
        GwLed.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, led);
        GwHalo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, led);
        // A slow ripple from the LED while it's up (or connecting); none when something's wrong.
        var ripple = led is "Ok" or "Accent";
        var span = TimeSpan.FromSeconds(led == "Accent" ? 1.1 : 2.4);
        GwHalo.BeginAnimation(OpacityProperty, ripple ? new System.Windows.Media.Animation.DoubleAnimation(0.55, 0, span) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever } : null);
        foreach (var dp in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            GwHaloScale.BeginAnimation(dp, ripple ? new System.Windows.Media.Animation.DoubleAnimation(0.7, 1.9, span) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever } : null);
        if (!ripple) GwHalo.Opacity = 0;
    }

    private void OnGwPopupOpened(object? sender, EventArgs e)
    {
        if (_gwBox is null)
        {
            _gwBox = new Views.GatewayStatusBox();
            _gwBox.SettingsRequested += () => { GwButton.IsChecked = false; OpenGatewaySettings(); };
            GwPopup.Child = _gwBox;
        }
        // While open, a click on the pill itself should close the box, not close and reopen it.
        GwButton.IsHitTestVisible = false;
    }

    private void OnGwPopupClosed(object? sender, EventArgs e)
    {
        GwButton.IsChecked = false;
        Dispatcher.BeginInvoke(() => GwButton.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OpenGatewaySettings() => Views.GatewayDialog.Show(this, _settings);
    private void OnZPopupButton(object sender, RoutedEventArgs e) => ZButton.IsChecked = false;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleAi();
            e.Handled = true;
        }
        else if (e.Key == Key.M && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _vm.Chat.IsAvailable)
        {
            _vm.Chat.IsOpen = !_vm.Chat.IsOpen;
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !(Keyboard.FocusedElement is DependencyObject d && IsInSession(d)))
        {
            // Ctrl F: the tree filter (what Ctrl K used to do).
            Search.Focus();
            Search.SelectAll();
            e.Handled = true;
        }
    }

    private static bool IsInSession(DependencyObject d)
    {
        for (var p = d; p is not null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is SshSessionView or WebSessionView or RdpSessionView) return true;
        return false;
    }

    private void OnSearchEverything(object sender, MouseButtonEventArgs e)
    {
        OpenSearch();
        e.Handled = true;
    }

    /// <summary>Ctrl K: the search window under the title bar search box, starting from what is typed there.</summary>
    private void OpenSearch()
    {
        if (OwnedWindows.OfType<SearchWindow>().FirstOrDefault() is { } open) { open.Activate(); return; }
        var w = new SearchWindow(_vm, _vm.SearchText.Trim().Length > 0 ? _vm.SearchText.Trim() : null);
        w.ShowUnder(Search);
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

    /// <summary>
    /// Ctrl+click: on a credential while one connection is selected, links the credential to it (without changing
    /// the selection), as before. Anywhere else, Ctrl+click adds or removes the entry from a multi-selection, and
    /// Shift+click takes the range from the anchor (the entry selected or Ctrl+clicked last). A plain click goes back to a single selection.
    /// </summary>
    private async void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _treeClickCount = e.ClickCount;
        _clearMultiOnUp = false;
        var clicked = FindItem(e.OriginalSource as DependencyObject)?.DataContext as EntryNode;
        _dragNode = clicked?.IsRoot == true ? null : clicked;
        if (clicked is null) return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_vm.CanCtrlClickLink(clicked)) await _vm.TryCtrlClickLinkAsync(clicked);
            else _vm.ToggleMultiSelect(clicked);
            Tree.Focus();
        }
        else if (mods is ModifierKeys.Shift or (ModifierKeys.Shift | ModifierKeys.Control))
        {
            e.Handled = true;
            _vm.RangeSelect(clicked, additive: mods.HasFlag(ModifierKeys.Control));
            Tree.Focus();
        }
        else if (mods == ModifierKeys.None)
        {
            // Pressing on a picked entry keeps the set so it can be dragged; letting go without a drag narrows to that entry.
            if (_vm.IsInMultiSelection(clicked)) _clearMultiOnUp = true;
            else _vm.ClearMultiSelection();
        }
    }

    private bool _clearMultiOnUp;

    private int _treeClickCount;

    /// <summary>A plain click on an entry shows its overview in the Dashboard tab (folders just open and close).
    /// The release of a double-click is skipped: the first click already showed the overview, and doing it
    /// again would pull the Dashboard in front of the session the double-click just opened.</summary>
    private void OnTreeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_clearMultiOnUp) { _clearMultiOnUp = false; _vm.ClearMultiSelection(); }
        if (Keyboard.Modifiers != ModifierKeys.None || _treeClickCount > 1) return;
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is not EntryNode n || n != _vm.Selected) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(d) is not null) return;
        _vm.ShowOverview();
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p is not null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
        {
            if (p is T t) return t;
            if (p is TreeViewItem) return null;
        }
        return null;
    }

    // ---------------- right-click menu ----------------

    /// <summary>Right-click selects the entry under the mouse first, so the menu acts on it.</summary>
    private bool _rightClickOnEmpty;
    private bool _rightClickOnMulti;

    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindItem(e.OriginalSource as DependencyObject);
        _rightClickOnEmpty = item is null;
        // Right-click inside a multi-selection keeps it, so the menu can move them all.
        _rightClickOnMulti = item?.DataContext is EntryNode n && _vm.IsInMultiSelection(n);
        if (_rightClickOnMulti) return;
        _vm.ClearMultiSelection();
        if (item is not null) { item.IsSelected = true; item.Focus(); }
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Tree.ContextMenu is not { } menu) { e.Handled = true; return; }
        // Several entries picked (right-clicked on one of them, or the menu key on the focused one): bulk actions.
        var onMulti = _rightClickOnMulti || (!_rightClickOnEmpty && _vm.Selected is { } focused && _vm.IsInMultiSelection(focused));
        _rightClickOnMulti = false;
        if (onMulti && _vm.HasMultiSelection)
        {
            _rightClickOnEmpty = false;
            EntryMenu.FillMulti(menu, _vm);
            return;
        }
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
    private bool _dragging;
    private (List<Guid> Ids, Guid? Folder)? _pendingDrop;

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragNode is null || Keyboard.Modifiers == ModifierKeys.Control) return;
        var d = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = _dragNode;
        _dragNode = null;
        _clearMultiOnUp = false;
        // Dragging a picked entry drags the whole multi-selection.
        var ids = string.Join(",", _vm.MoveSetFor(node));
        _dragging = true;
        try { DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, ids), DragDropEffects.Move); }
        finally { _dragging = false; }
        SetDropHighlight(null);
        // The drop only noted where to go: ask and move now that the drag is over and the mouse is free.
        if (_pendingDrop is { } drop) { _pendingDrop = null; _ = _vm.MoveManyAsync(drop.Ids, drop.Folder); }
    }

    private static List<Guid>? DraggedIds(DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string s) return null;
        var ids = new List<Guid>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(part, out var g)) ids.Add(g);
        return ids.Count > 0 ? ids : null;
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
        if (DraggedIds(e) is { } moving)
        {
            var folder = DropFolder(e, out var highlight);
            if (_vm.CanMoveAny(moving, folder)) { e.Effects = DragDropEffects.Move; SetDropHighlight(highlight); }
            else SetDropHighlight(null);
        }
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => SetDropHighlight(null);

    private async void OnTreeDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if (DraggedIds(e) is not { } moving) return;
        var folder = DropFolder(e, out _);
        e.Handled = true;
        if (!_vm.CanMoveAny(moving, folder)) return;
        // A drag from this tree moves once DoDragDrop returns (see OnTreeMouseMove); asking inside the drop
        // would open the question while the OS drag loop still holds the mouse.
        if (_dragging) { _pendingDrop = (moving, folder); return; }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        await _vm.MoveManyAsync(moving, folder);
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

    /// <summary>Shift+Up/Down (and Shift+Home/End) grow or shrink a range from the anchor, like Explorer.
    /// Escape drops a multi-selection; moving with the arrow keys (without Shift) goes back to one entry.</summary>
    private void OnTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.Up or Key.Down or Key.Home or Key.End && e.OriginalSource is TreeViewItem or TreeView)
        {
            var step = e.Key switch { Key.Up => -1, Key.Down => 1, Key.Home => int.MinValue, _ => int.MaxValue };
            if (_vm.ExtendRangeByKey(step) is { } target)
            {
                _vm.FocusWithinRange(target, FocusTreeRow);
                e.Handled = true;
            }
            return;
        }
        if (!_vm.HasMultiSelection) return;
        if (e.Key == Key.Escape) { _vm.ClearMultiSelection(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown
                 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _vm.ClearMultiSelection();
    }

    /// <summary>Moves the tree's focus and selection to <paramref name="node"/> and scrolls it into view, so the
    /// arrow keys carry on from there.</summary>
    private void FocusTreeRow(EntryNode node)
    {
        if (FindContainer(Tree, node) is not { } item)
        {
            // Not realized yet (scrolled far away): select it, let the tree lay it out, then try again.
            node.IsSelected = true;
            Tree.UpdateLayout();
            item = FindContainer(Tree, node);
            if (item is null) return;
        }
        item.BringIntoView();
        item.Focus();
        node.IsSelected = true;
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
        else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _vm.Selected is { } sh && _vm.CanShareSessions)
        {
            _ = _vm.ShareEntryAsync(sh);
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
