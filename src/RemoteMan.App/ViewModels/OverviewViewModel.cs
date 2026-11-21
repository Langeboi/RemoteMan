using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// One line on the Overview: a small label, the value, and the buttons that act on it.
/// Secret values are never held here: <see cref="Reveal"/> decrypts on demand and the eye shows the result.
/// </summary>
public sealed partial class OverviewRow : ObservableObject
{
    public OverviewRow(string key, string value) { Key = key; _value = value; }

    public string Key { get; }
    /// <summary>Small grey tag after the label: "main", "linked", "from folder".</summary>
    public string? Badge { get; init; }
    /// <summary>plain | mono | link | dim | secret</summary>
    public string Style { get; init; } = "plain";
    /// <summary>Fallback hosts, shown under the main one in the order they're tried.</summary>
    public IReadOnlyList<string> Alternates { get; init; } = [];
    public bool HasAlternates => Alternates.Count > 0;

    /// <summary>Copy button (null hides it).</summary>
    public ICommand? Copy { get; init; }
    public string CopyTip { get; init; } = "Copy";
    /// <summary>Open button: a session to the host, or the URL in its browser tab.</summary>
    public ICommand? Open { get; init; }
    public string OpenTip { get; init; } = "Open";
    /// <summary>Clicking the value itself (a linked credential jumps to it).</summary>
    public ICommand? Follow { get; init; }
    /// <summary>Decrypts a secret for the eye button; null for values that aren't secret.</summary>
    public Func<string?>? Reveal { get; init; }
    public bool CanReveal => Reveal is not null;

    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _isRevealed;

    [RelayCommand]
    private void ToggleReveal()
    {
        if (Reveal is null) return;
        if (IsRevealed) { Value = Mask; IsRevealed = false; return; }
        Value = Reveal() ?? "";
        IsRevealed = true;
    }

    public const string Mask = "●●●●●●●●";
}

/// <summary>A status chip under the header: Zabbix, Oxidized, someone on the entry.</summary>
public sealed record OverviewChip(string Kind, string Text, string Sub, string? Tag = null, string? Action = null, ICommand? Command = null, string? Initials = null, string? Color = null);

/// <summary>Created by / modified by at the bottom.</summary>
public sealed record OverviewPerson(string Label, string User, string Initials, string Color, string When, string Exact);

/// <summary>
/// The Overview tab: what the selected entry is, where it points and how it logs in, with copy buttons.
/// One tab, reused: it follows the selection in the tree and a click on an entry brings it to the front.
/// Filled by <see cref="MainViewModel"/>; the buttons run the main window's commands on the selected entry.
/// </summary>
public sealed partial class OverviewViewModel : SessionViewModel
{
    public const string FolderTag = "DIR";

    public OverviewViewModel()
        : base(new Entry { Id = Guid.Empty, Kind = EntryKind.Folder, Name = "Overview" }, new HostEndpoint(""), RemoteMan.Protocols.ResolvedCredential.None, false)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickTotp();
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsActive)) UpdateTimer(); };
    }

    public override bool IsConnection => false;
    public override string KindTag => _shownTag;
    public override bool IsPreview => true;

    private string _shownTag = FolderTag;
    private readonly DispatcherTimer _timer;

    /// <summary>The main window's view model: the buttons bind to its commands, which act on the selected entry.</summary>
    public MainViewModel? Main { get; internal set; }

    [ObservableProperty] private EntryNode? _node;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _folderText = "";
    /// <summary>The Login card shows: a login row or a 2FA code.</summary>
    [ObservableProperty] private bool _hasLogin;
    /// <summary>Folders without a custom icon: the folder glyph in the big icon.</summary>
    [ObservableProperty] private bool _showFolderGlyph;
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private bool _isCredential;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _canConnect;
    [ObservableProperty] private bool _canBackups;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _detailsTitle = "Connection";
    [ObservableProperty] private string _connectionNote = "";

    public ObservableCollection<string> Tags { get; } = new();
    public ObservableCollection<OverviewChip> Chips { get; } = new();
    public ObservableCollection<OverviewRow> ConnectionRows { get; } = new();
    public ObservableCollection<OverviewRow> LoginRows { get; } = new();
    public ObservableCollection<OverviewRow> FieldRows { get; } = new();
    public ObservableCollection<OverviewPerson> People { get; } = new();

    // --- two-factor code, ticking while the tab is showing ---
    private Func<string?>? _totpSecret;
    [ObservableProperty] private bool _hasTotp;
    [ObservableProperty] private string _totpCode = "";
    [ObservableProperty] private int _totpSeconds;
    /// <summary>0..1 of the 30 s window still left (the ring).</summary>
    [ObservableProperty] private double _totpLeft;
    public ICommand? CopyTotp { get; internal set; }

    /// <summary>Shows a new entry: the main view model fills the lists after this.</summary>
    internal void Show(EntryNode node, string kindTag)
    {
        Node = node;
        _shownTag = kindTag;
        OnPropertyChanged(nameof(KindTag));
        Title = node.Name;
        Tags.Clear();
        Chips.Clear();
        ConnectionRows.Clear();
        LoginRows.Clear();
        FieldRows.Clear();
        People.Clear();
        SetTotp(null);
    }

    internal void SetTotp(Func<string?>? secret)
    {
        _totpSecret = secret;
        HasTotp = secret is not null;
        TickTotp();
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (HasTotp && IsActive) { TickTotp(); _timer.Start(); }
        else _timer.Stop();
    }

    private void TickTotp()
    {
        if (_totpSecret?.Invoke() is not { } s || !RemoteMan.Core.Security.Totp.IsValid(s))
        {
            TotpCode = HasTotp ? "invalid secret" : "";
            TotpSeconds = 0;
            TotpLeft = 0;
            return;
        }
        var code = RemoteMan.Core.Security.Totp.Code(s);
        TotpCode = code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
        TotpSeconds = RemoteMan.Core.Security.Totp.SecondsLeft(s);
        TotpLeft = TotpSeconds / 30.0;
    }

    /// <summary>Stops the 2FA timer when the tab is closed.</summary>
    internal void Stop() => _timer.Stop();
}
