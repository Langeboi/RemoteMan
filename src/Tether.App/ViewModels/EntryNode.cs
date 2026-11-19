using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Tether.Core.Model;

namespace Tether.App.ViewModels;

public sealed record PresenceChip(string Initials, string ToolTip);

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
    public Thickness Indent => new(8 + Depth * 14, 0, 8, 0);
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
