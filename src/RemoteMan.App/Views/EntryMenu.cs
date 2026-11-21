using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

/// <summary>
/// The entry right-click menu, built for the entry it opens on so it only shows what applies:
/// connect (and connect with), edit, organise, copy login details, link a credential, open with,
/// properties and delete. Styling comes from the theme's ContextMenu/MenuItem styles.
/// </summary>
public static class EntryMenu
{
    // 14x14 line icons, same stroke style as the tree's protocol icons.
    private static readonly Dictionary<string, Geometry> Icons = new()
    {
        ["connect"] = G("M4,2 L11,7 L4,12 Z"),
        ["connectwith"] = G("M4,2 L11,7 L4,12 Z M1,12 H3"),
        ["edit"] = G("M2,12 L3,9 L9,3 L11,5 L5,11 Z"),
        ["duplicate"] = G("M2,4 H9 V12 H2 Z M5,4 V2 H12 V10 H9"),
        ["move"] = G("M1,4 H5 L6,5 H13 V12 H1 Z"),
        ["star"] = G("M7,1.5 L8.7,5.1 L12.5,5.5 L9.6,8.1 L10.5,11.9 L7,10 L3.5,11.9 L4.4,8.1 L1.5,5.5 L5.3,5.1 Z"),
        ["user"] = G("M7.5,5 A2.5,2.5 0 1 1 2.5,5 A2.5,2.5 0 1 1 7.5,5 Z M1,12 C1,9.5 3,8 5,8 C7,8 9,9.5 9,12"),
        ["password"] = G("M2,6 H12 V12 H2 Z M4,6 V4 A3,3 0 0 1 10,4 V6"),
        ["both"] = G("M2,4 H9 V12 H2 Z M5,4 V2 H12 V10 H9"),
        ["host"] = G("M2,4 H12 M2,7 H12 M2,10 H8"),
        ["link"] = G("M5,9 L9,5 M4,6 L2.5,7.5 A2.5,2.5 0 0 0 6,11 L7.5,9.5 M10,8 L11.5,6.5 A2.5,2.5 0 0 0 8,3 L6.5,4.5"),
        ["openwith"] = G("M8,2 H12 V6 M12,2 L6,8 M10,8 V12 H2 V4 H6"),
        ["info"] = G("M12.5,7 A5.5,5.5 0 1 1 1.5,7 A5.5,5.5 0 1 1 12.5,7 Z M7,4 V7 L9,8.5"),
        ["delete"] = G("M2,4 H12 M5,4 V2 H9 V4 M3.5,4 L4.3,12 H9.7 L10.5,4"),
        ["new"] = G("M7,2 V12 M2,7 H12"),
        ["copy"] = G("M2,4 H9 V12 H2 Z M5,4 V2 H12 V10 H9"),
        ["paste"] = G("M3,3 H11 V13 H3 Z M5,3 V1.5 H9 V3 M5,7 H9 M5,10 H9"),
        ["expand"] = G("M3,5 L7,9 L11,5"),
        ["collapse"] = G("M3,9 L7,5 L11,9"),
        ["refresh"] = G("M11.5,4.5 A5,5 0 1 0 12,8 M11.5,1.5 V4.5 H8.5"),
        ["import"] = G("M7,1.5 V9 M4,6 L7,9 L10,6 M2,10 V12.5 H12 V10"),
        ["rdp"] = G("M1.5,2.5 H12.5 V9.5 H1.5 Z M4.5,12.5 H9.5 M7,9.5 V12.5"),
        ["ssh"] = G("M1.5,2.5 H12.5 V11.5 H1.5 Z M4,5.5 L6,7 L4,8.5 M7.5,9 H10"),
        ["web"] = G("M12.5,7 A5.5,5.5 0 1 1 1.5,7 A5.5,5.5 0 1 1 12.5,7 Z M1.5,7 H12.5 M7,1.5 C4.5,4 4.5,10 7,12.5 M7,1.5 C9.5,4 9.5,10 7,12.5"),
        ["vnc"] = G("M1.5,8.5 V1.5 H9.5 V4 M4,4 H12.5 V11 H4 Z M6.5,12.5 H10"),
        ["key"] = G("M6,7 A2.5,2.5 0 1 1 1,7 A2.5,2.5 0 1 1 6,7 Z M6,7 H12.5 M10.5,7 V9.5 M12.5,7 V9"),
        ["folder"] = G("M1,4 H5 L6,5 H13 V12 H1 Z"),
    };

    private static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    public static void Fill(ContextMenu menu, EntryNode node, MainViewModel vm, Window owner)
    {
        menu.Items.Clear();
        var e = node.Entry;
        if (e.Id == MainViewModel.FavoritesId) return;
        var isFolder = e.Kind == EntryKind.Folder;
        var isCred = e.Kind == EntryKind.Credential;

        // --- connect ---
        if (e.IsConnectable)
        {
            menu.Items.Add(Item("Connect", "connect", () => vm.ConnectCommand.Execute(node), "Enter", bold: true));
            var with = Item("Connect with", "connectwith");
            if (e.Kind == EntryKind.Rdp)
            {
                with.Items.Add(Item("Embedded tab", null, () => _ = vm.ConnectWithAsync(node, new(External: false))));
                with.Items.Add(Item("External window (mstsc)", null, () => _ = vm.ConnectWithAsync(node, new(External: true))));
                with.Items.Add(Item("Admin (console) session", null, () => _ = vm.ConnectWithAsync(node, new(AdminSession: true))));
            }
            if (e.Kind == EntryKind.Web)
            {
                with.Items.Add(Item("Built-in browser", null, () => _ = vm.ConnectWithAsync(node, new(External: false))));
                with.Items.Add(Item("External browser", null, () => _ = vm.ConnectWithAsync(node, new(External: true))));
            }
            if (with.Items.Count > 0) with.Items.Add(new Separator());
            var others = Item("Other credential");
            foreach (var c in vm.Credentials)
            {
                var id = c.Id;
                others.Items.Add(Item(c.Name, null, () => _ = vm.ConnectWithAsync(node, new(Login: LoginSource.Saved, CredentialId: id))));
            }
            others.IsEnabled = others.Items.Count > 0;
            with.Items.Add(others);
            if (e.Kind is EntryKind.Rdp or EntryKind.Web)
                with.Items.Add(Item("My Windows login", null, () => _ = vm.ConnectWithAsync(node, new(Login: LoginSource.WindowsLogin))));
            with.Items.Add(Item("No stored login (ask)", null, () => _ = vm.ConnectWithAsync(node, new(Login: LoginSource.None))));
            menu.Items.Add(with);
            menu.Items.Add(new Separator());
        }

        // --- folder: create inside it ---
        if (isFolder)
        {
            menu.Items.Add(NewSubmenu(vm, e.Id));
            menu.Items.Add(PasteItem(vm, e.Id));
            menu.Items.Add(Item("Expand all inside", "expand", () => vm.SetExpanded(e.Id, true)));
            menu.Items.Add(Item("Collapse all inside", "collapse", () => vm.SetExpanded(e.Id, false)));
            menu.Items.Add(new Separator());
        }

        // --- organise ---
        menu.Items.Add(Item(isFolder ? "Edit folder (name, login for entries inside)" : "Edit", "edit", () => vm.EditCommand.Execute(null), "F2"));
        menu.Items.Add(Item("Change icon…", "folder", () =>
        {
            if (EntryIconPicker.Show(owner, e, out var icon, out var color))
                _ = vm.SaveChangeAsync(e, x => { x.Icon = icon; x.IconColor = color; }, $"Changed the icon of {e.Name}");
        }));
        menu.Items.Add(Item("Copy", "copy", () => vm.CopyEntry(e), "Ctrl+C"));
        if (!isFolder) menu.Items.Add(Item("Duplicate…", "duplicate", () => vm.DuplicateCommand.Execute(null), "Ctrl+D"));
        var move = Item("Move to", "move");
        move.Items.Add(Item("(top level)", null, () => _ = vm.MoveAsync(e.Id, null), enabled: vm.CanMove(e.Id, null)));
        foreach (var f in vm.Folders.Select(f => (f, path: vm.FolderPath(f))).OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase))
        {
            var fid = f.f.Id;
            move.Items.Add(Item(f.path, null, () => _ = vm.MoveAsync(e.Id, fid), enabled: vm.CanMove(e.Id, fid)));
        }
        menu.Items.Add(move);
        if (!isFolder) menu.Items.Add(Item(vm.IsFavorite(e) ? "Remove from favorites" : "Add to favorites", "star", () => vm.ToggleFavoriteCommand.Execute(null)));

        // --- copy login details ---
        if (e.IsConnectable || isCred)
        {
            menu.Items.Add(new Separator());
            var type = isCred ? e.Credential?.Type ?? CredentialType.Password : CredentialType.Password;
            if (type != CredentialType.Secret && type != CredentialType.Certificate)
                menu.Items.Add(Item("Copy user name", "user", () => vm.CopyUserNameCommand.Execute(null), "Ctrl+Shift+U"));
            menu.Items.Add(Item(type == CredentialType.Secret ? "Copy secret" : "Copy password", "password", () => vm.CopyPasswordCommand.Execute(null), "Ctrl+Shift+P"));
            if (type == CredentialType.Password)
                menu.Items.Add(Item("Copy both (paste twice)", "both", () => vm.CopyBothCommand.Execute(null), "Ctrl+Shift+B"));
            if (e.IsConnectable) menu.Items.Add(Item("Copy host", "host", () => vm.CopyHost(e)));
        }

        // --- login and external tools ---
        if (e.IsConnectable || isFolder)
        {
            menu.Items.Add(new Separator());
            var link = Item("Link credential", "link");
            foreach (var c in vm.Credentials)
            {
                var id = c.Id;
                var mi = Item(c.Name, null, () => _ = vm.SetLoginAsync(e, LoginSource.Saved, id));
                mi.IsChecked = e.Login == LoginSource.Saved && e.CredentialId == id;
                link.Items.Add(mi);
            }
            if (link.Items.Count > 0) link.Items.Add(new Separator());
            if (e.Kind is EntryKind.Rdp or EntryKind.Web or EntryKind.Folder)
                link.Items.Add(Item("My Windows login", null, () => _ = vm.SetLoginAsync(e, LoginSource.WindowsLogin)));
            if (e.ParentId is not null)
                link.Items.Add(Item("Inherit from folder", null, () => _ = vm.SetLoginAsync(e, LoginSource.Inherited)));
            link.Items.Add(Item("Remove link", null, () => _ = vm.SetLoginAsync(e, LoginSource.None), enabled: e.Login != LoginSource.None));
            menu.Items.Add(link);
        }
        if (e.IsConnectable)
        {
            var open = Item("Open with", "openwith");
            if (e.Kind == EntryKind.Rdp)
            {
                open.Items.Add(Item("Remote Desktop (mstsc)", null, () => _ = vm.OpenInMstscAsync(e)));
                open.Items.Add(Item("Save as .rdp file…", null, () =>
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog { FileName = e.Name + ".rdp", Filter = "Remote Desktop file|*.rdp" };
                    if (dlg.ShowDialog(owner) == true) vm.SaveRdpFile(e, dlg.FileName);
                }));
            }
            if (e.Kind == EntryKind.Ssh) open.Items.Add(Item("Browse files in WinSCP", null, () => _ = vm.OpenInWinScpAsync(e)));
            if (e.Kind == EntryKind.Web) open.Items.Add(Item("Default browser", null, () => _ = vm.ConnectWithAsync(node, new(External: true))));
            open.Items.Add(Item("Ping", null, () => vm.Ping(e)));
            if (vm.SelectedZabbixLinked) open.Items.Add(Item("Zabbix", null, () => vm.OpenInZabbixCommand.Execute(null)));
            if (vm.SelectedOxidizedLinked) open.Items.Add(Item("Oxidized", null, () => vm.OpenInOxidizedCommand.Execute(null)));
            menu.Items.Add(open);
            if (vm.ZabbixActive) menu.Items.Add(Item(vm.SelectedZabbixLinked ? "Change Zabbix link…" : "Link to Zabbix host…", "link", () => vm.LinkToZabbixCommand.Execute(null)));
            if (vm.OxidizedActive)
            {
                menu.Items.Add(Item("Config backups", null, () => vm.OpenConfigBackupsCommand.Execute(null)));
                menu.Items.Add(Item(vm.SelectedOxidizedLinked && !string.IsNullOrEmpty(e.OxidizedNode) ? "Change Oxidized link…" : "Link to Oxidized device…", "link",
                    () => vm.LinkToOxidizedCommand.Execute(null)));
            }
        }

        // --- details and delete ---
        // Properties opens the editor, like Edit; created/modified history is shown at its bottom.
        menu.Items.Add(Item("Properties", "info", () => vm.EditCommand.Execute(null), "Alt+Enter"));
        menu.Items.Add(new Separator());
        var del = Item("Delete", "delete", () => vm.DeleteCommand.Execute(null), "Del");
        del.SetResourceReference(Control.ForegroundProperty, "Bad");
        menu.Items.Add(del);
    }

    /// <summary>Everything that can be created, in menu order: group, name, one line on what it's for, icon tag, icon colour.</summary>
    public static readonly IReadOnlyList<(string Group, EntryKind Kind, string Name, string Description, string Tag, string Color)> EntryTypes =
    [
        ("Remote desktop", EntryKind.Rdp, "RDP", "Windows desktops and servers", "RDP", "#5B9BEA"),
        ("Remote desktop", EntryKind.Vnc, "VNC", "Linux desktops, appliances", "VNC", "#5B9BEA"),
        ("Remote desktop", EntryKind.Proxmox, "Proxmox console", "A VM or container console", "PVE", "#5B9BEA"),
        ("Terminal", EntryKind.Ssh, "SSH", "Linux, switches, firewalls", "SSH", "#3FB8A4"),
        ("Terminal", EntryKind.Telnet, "Telnet", "Older switches and devices", "TEL", "#3FB8A4"),
        ("Terminal", EntryKind.Serial, "Serial console", "COM port / USB console cable", "SER", "#3FB8A4"),
        ("Files", EntryKind.Sftp, "SFTP / SCP", "Browse and copy files over SSH", "SFTP", "#D6A85E"),
        ("Files", EntryKind.Ftp, "FTP / FTPS", "Old-style file servers", "FTP", "#D6A85E"),
        ("Web", EntryKind.Web, "Web page", "Admin pages, dashboards", "WEB", "#8C9BF0"),
        ("Tools", EntryKind.PowerShell, "PowerShell remoting", "Enter-PSSession to a server", "PS", "#C77DDB"),
        ("Tools", EntryKind.WinBox, "WinBox", "MikroTik routers", "WBX", "#C77DDB"),
        ("Organize", EntryKind.Folder, "Folder", "Group entries, share a login", "DIR", "#9AA3AD"),
        ("Organize", EntryKind.Credential, "Credential", "Login to reuse across entries", "KEY", "#D6C25E"),
    ];

    /// <summary>The New entry menu: icon, name and a line of explanation per type, grouped under small headings.</summary>
    public static void FillNew(ItemsControl menu, MainViewModel vm, Guid? parent)
    {
        string? group = null;
        foreach (var t in EntryTypes)
        {
            if (t.Group != group)
            {
                if (group is not null) menu.Items.Add(new Separator());
                var head = new MenuItem
                {
                    Header = t.Group.ToUpperInvariant(), IsEnabled = false, FontSize = 10, Height = 22,
                    // Air above and below the heading so it doesn't sit on the first row under it.
                    Margin = new Thickness(0, group is null ? 2 : 6, 0, 4), VerticalContentAlignment = VerticalAlignment.Bottom,
                };
                head.SetResourceReference(Control.ForegroundProperty, "Faint");
                menu.Items.Add(head);
                group = t.Group;
            }
            var kind = t.Kind.ToString();
            var color = (Color)ColorConverter.ConvertFromString(t.Color);
            var icon = new System.Windows.Shapes.Path
            {
                Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.3, Stroke = new SolidColorBrush(color),
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, SnapsToDevicePixels = true,
            };
            icon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "Icon." + t.Tag);
            var text = new StackPanel { Margin = new Thickness(2, 3, 0, 3) };
            text.Children.Add(new TextBlock { Text = t.Name });
            var desc = new TextBlock { Text = t.Description, FontSize = 11 };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(desc);
            var mi = new MenuItem { Header = text, Icon = icon, MinWidth = 260 };
            mi.Click += (_, _) => _ = vm.NewEntryInAsync(kind, parent);
            menu.Items.Add(mi);
        }
    }

    /// <summary>"New entry" submenu that creates in <paramref name="parent"/> (null = top level).</summary>
    private static MenuItem NewSubmenu(MainViewModel vm, Guid? parent)
    {
        var mi = Item("New entry", "new");
        FillNew(mi, vm, parent);
        return mi;
    }

    private static MenuItem PasteItem(MainViewModel vm, Guid? folder) =>
        Item(vm.CopiedEntry is { } c ? $"Paste \"{c.Name}\"" : "Paste", "paste", () => _ = vm.PasteAsync(folder), enabled: vm.CopiedEntry is not null);

    /// <summary>Right-click on empty space in the tree.</summary>
    public static void FillBackground(ContextMenu menu, MainViewModel vm, Action import)
    {
        menu.Items.Clear();
        menu.Items.Add(NewSubmenu(vm, null));
        menu.Items.Add(PasteItem(vm, null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Expand all", "expand", () => vm.SetExpanded(null, true)));
        menu.Items.Add(Item("Collapse all", "collapse", () => vm.SetExpanded(null, false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Import…", "import", import));
        menu.Items.Add(Item("Refresh", "refresh", () => _ = vm.RefreshAsync(), "F5"));
    }

    private static MenuItem Item(string header, string? icon = null, Action? click = null, string? gesture = null, bool bold = false, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        if (bold) mi.FontWeight = FontWeights.SemiBold;
        if (icon is not null && Icons.TryGetValue(icon, out var g))
        {
            var path = new System.Windows.Shapes.Path { Data = g, Width = 14, Height = 14, Stretch = Stretch.None, StrokeThickness = 1.2, SnapsToDevicePixels = true };
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(MenuItem), 1) });
            mi.Icon = path;
        }
        if (click is not null) mi.Click += (_, _) => click();
        return mi;
    }
}
