using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>A colored initials circle: who is on an entry, or who is online.</summary>
public sealed record PresenceChip(string Initials, string ToolTip, string Color = "#5B9BEA", string? User = null)
{
    private static readonly string[] Palette = ["#D98C5F", "#B48AE6", "#5B9BEA", "#54C08A", "#E0A84A", "#3FB8A4", "#E57A9A"];

    /// <summary>Same user, same color, on every machine.</summary>
    public static string ColorFor(string user)
    {
        var h = 0;
        foreach (var c in user.ToLowerInvariant()) h = unchecked(h * 31 + c);
        return Palette[(int)((uint)h % Palette.Length)];
    }
}

/// <summary>A row in the navigation tree.</summary>
public sealed partial class EntryNode : ObservableObject
{
    public EntryNode(Entry entry, int depth)
    {
        _entry = entry;
        Depth = depth;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Name), nameof(KindTag), nameof(IsFolder), nameof(HasLinkedCredential), nameof(FolderGlyph), nameof(FolderBrush), nameof(FolderFill), nameof(HasCustomIcon), nameof(IconImage), nameof(KindName))]
    private Entry _entry;

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLinkTarget;
    /// <summary>Tiny initials badge on the corner of the protocol icon: the first person on this entry, tooltip lists everyone.</summary>
    [ObservableProperty] private PresenceChip? _badge;
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>Zabbix glyph on the right of the row: ok, warn, high, unreach, maint, or null for none.</summary>
    [ObservableProperty] private string? _health;
    [ObservableProperty] private string? _healthTip;
    /// <summary>Oxidized: the last config backup of this device failed (small orange dot on the row).</summary>
    [ObservableProperty] private bool _backupFailed;
    [ObservableProperty] private string? _backupTip;
    /// <summary>Folders: hosts with problems inside (any depth). 0 hides the count.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblemCount))] private int _problemCount;
    [ObservableProperty] private bool _problemCountHigh;
    public bool HasProblemCount => ProblemCount > 0;

    public int Depth { get; }
    /// <summary>18px per level; the chevron column sits inside the row template.</summary>
    public Thickness Indent => new(4 + Depth * 18, 0, 6, 0);
    /// <summary>The data source itself, shown as the top node of the navigation tree (not a stored entry).</summary>
    public bool IsRoot { get; init; }
    public string Name => Entry.Name;
    public bool IsFolder => Entry.Kind == EntryKind.Folder;
    public bool HasLinkedCredential => Entry.CredentialId is not null;
    public string KindTag => KindTagFor(Entry.Kind);
    public Guid Id => Entry.Id;
    /// <summary>Folder icon chosen in the folder icon picker (plain folder by default).</summary>
    public System.Windows.Media.Geometry FolderGlyph => RemoteMan.App.Views.FolderIcons.GlyphFor(Entry.Icon);
    public System.Windows.Media.Brush FolderBrush => RemoteMan.App.Views.FolderIcons.BrushFor(Entry.IconColor);
    /// <summary>Plain folders are drawn filled in their colour; folders with a glyph (server, network…) stay outlined.</summary>
    public System.Windows.Media.Brush? FolderFill => Entry.Icon is null or "" or "folder" ? FolderBrush : null;
    /// <summary>Any entry: a glyph or vendor mark picked with Change icon, shown instead of the protocol or folder icon.</summary>
    public bool HasCustomIcon => RemoteMan.App.Views.EntryIcons.IsCustom(Entry.Icon, Entry.IconColor, IsFolder);
    public System.Windows.Media.ImageSource? IconImage =>
        HasCustomIcon ? RemoteMan.App.Views.EntryIcons.Render(Entry.Icon, Entry.IconColor, IsFolder ? "DIR" : KindTag) : null;
    /// <summary>Tooltip on a custom icon, so the protocol stays visible.</summary>
    public string KindName => RemoteMan.App.Views.EntryIcons.KindName(IsFolder ? "DIR" : KindTag);

    public ObservableCollection<EntryNode> Children { get; } = new();
    public ObservableCollection<PresenceChip> People { get; } = new();

    public static string KindTagFor(EntryKind k) => k switch
    {
        EntryKind.Rdp => "RDP",
        EntryKind.Ssh => "SSH",
        EntryKind.Vnc => "VNC",
        EntryKind.Web => "WEB",
        EntryKind.Credential => "KEY",
        EntryKind.Telnet => "TEL",
        EntryKind.Serial => "SER",
        EntryKind.Sftp => "SFTP",
        EntryKind.Ftp => "FTP",
        EntryKind.Proxmox => "PVE",
        EntryKind.PowerShell => "PS",
        EntryKind.WinBox => "WBX",
        _ => "",
    };
}
