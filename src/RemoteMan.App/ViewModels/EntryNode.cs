using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>A colored initials circle: who is on an entry, or who is online.</summary>
public sealed record PresenceChip(string Initials, string ToolTip, string Color = "#5B9BEA")
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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Name), nameof(KindTag), nameof(IsFolder), nameof(HasLinkedCredential))]
    private Entry _entry;

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLinkTarget;

    public int Depth { get; }
    public Thickness Indent => new(8 + Depth * 16, 0, 8, 0);
    public string Name => Entry.Name;
    public bool IsFolder => Entry.Kind == EntryKind.Folder;
    public bool HasLinkedCredential => Entry.CredentialId is not null;
    public string KindTag => KindTagFor(Entry.Kind);
    public Guid Id => Entry.Id;

    public ObservableCollection<EntryNode> Children { get; } = new();
    public ObservableCollection<PresenceChip> People { get; } = new();

    public static string KindTagFor(EntryKind k) => k switch
    {
        EntryKind.Rdp => "RDP",
        EntryKind.Ssh => "SSH",
        EntryKind.Vnc => "VNC",
        EntryKind.Web => "WEB",
        EntryKind.Credential => "KEY",
        _ => "",
    };
}
