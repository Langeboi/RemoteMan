using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Model;

namespace RemoteMan.App.ViewModels;

/// <summary>A big number at the top of the dashboard.</summary>
public sealed record DashTile(string Label, string Value, string Sub);

/// <summary>A labelled bar: a protocol or a folder. Fill and Rest split the track as star widths.</summary>
public sealed record DashBar(Guid? Id, string Tag, string Name, int Count, double Fraction)
{
    public GridLength Fill => new(Math.Max(Fraction, 0.0001), GridUnitType.Star);
    public GridLength Rest => new(Math.Max(1 - Fraction, 0.0001), GridUnitType.Star);
}

/// <summary>An entry on the dashboard (favorites, recent, a housekeeping hit). Health is the Zabbix glyph tag, or null.</summary>
public sealed record DashEntry(Guid Id, string Name, string Tag, string Detail, string Side, string? Health = null);

/// <summary>A Zabbix problem, with the entry it belongs to when one matches.</summary>
public sealed record DashProblem(string Tag, string Name, string Where, string Age, Guid? EntryId);

/// <summary>
/// An Oxidized device on the Config backups widget. State is ok, stale, fail, never or changed and picks the archive
/// glyph's colour. EntryId is the entry it matches, or null when nothing in RemoteMan points at it.
/// </summary>
public sealed record DashBackup(Guid? EntryId, string NodeName, string Name, string Detail, string Side, string State, string Hover)
{
    public bool HasEntry => EntryId is not null;
}

/// <summary>A housekeeping line; click it to list the entries behind the count.</summary>
public sealed partial class DashCheck : ObservableObject
{
    public DashCheck(string label, bool warn, IReadOnlyList<DashEntry> items)
    {
        Label = label;
        Warn = warn;
        Items = items;
    }

    public string Label { get; }
    public bool Warn { get; }
    public IReadOnlyList<DashEntry> Items { get; }
    public int Count => Items.Count;
    public bool IsWarn => Warn && Count > 0;
    /// <summary>The first 12 when open; the rest are summed up in <see cref="MoreText"/>.</summary>
    public IEnumerable<DashEntry> Shown => Items.Take(12);
    public string MoreText => Count > 12 ? $"and {Count - 12} more" : "";
    [ObservableProperty] private bool _isOpen;
}

/// <summary>
/// The Dashboard tab: always first in the left pane, never closed. Filled by <see cref="MainViewModel"/> from data
/// it already has (the entries and the last Zabbix poll), so it costs nothing while it isn't looked at.
/// </summary>
public sealed partial class DashboardViewModel : SessionViewModel
{
    public const string DashTag = "DASH";

    public DashboardViewModel()
        : base(new Entry { Id = Guid.Empty, Kind = EntryKind.Folder, Name = "Dashboard" }, new HostEndpoint(""), RemoteMan.Protocols.ResolvedCredential.None, false)
    {
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsActive)) SyncEntryInfo(); };
    }

    /// <summary>The overview of the entry clicked in the tree, shown in place of the widgets; null shows the dashboard.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowingEntry))] private OverviewViewModel? _entryInfo;
    public bool ShowingEntry => EntryInfo is not null;

    partial void OnEntryInfoChanged(OverviewViewModel? oldValue, OverviewViewModel? newValue)
    {
        if (oldValue is not null && oldValue != newValue) oldValue.IsActive = false;
        SyncEntryInfo();
    }

    /// <summary>The overview's 2FA countdown only runs while it is on screen.</summary>
    private void SyncEntryInfo()
    {
        if (EntryInfo is { } o) o.IsActive = IsActive;
    }

    /// <summary>The "Dashboard" link above an entry's overview.</summary>
    [RelayCommand] private void BackToDashboard() => EntryInfo = null;

    public override bool IsPinned => true;
    public override bool IsConnection => false;
    public override string KindTag => DashTag;

    /// <summary>Set by the main view model.</summary>
    internal Action<Guid>? ConnectAction { get; set; }
    internal Action<Guid>? RevealAction { get; set; }
    internal Action? OpenZabbixAction { get; set; }
    internal Action<string, bool>? WidgetToggled { get; set; }
    /// <summary>Opens the Config backups tab for an Oxidized device (its entry), or its Oxidized page when no entry matches.</summary>
    internal Action<DashBackup>? OpenBackupAction { get; set; }
    internal Action? OpenOxidizedAction { get; set; }
    internal Action? RefreshBackupsAction { get; set; }

    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private string _sourceState = "";
    [ObservableProperty] private bool _sourceOk;
    [ObservableProperty] private string _updated = "";

    public ObservableCollection<DashTile> Tiles { get; } = new();
    public ObservableCollection<DashBar> Protocols { get; } = new();
    public ObservableCollection<DashBar> Folders { get; } = new();
    public ObservableCollection<DashEntry> Favorites { get; } = new();
    public ObservableCollection<DashEntry> Recent { get; } = new();
    public ObservableCollection<DashCheck> Checks { get; } = new();
    public ObservableCollection<DashProblem> Problems { get; } = new();

    [ObservableProperty] private string _protocolTotal = "";

    // --- charts ---
    [ObservableProperty] private IReadOnlyList<RemoteMan.App.Views.ChartSlice> _protocolSlices = [];
    [ObservableProperty] private IReadOnlyList<RemoteMan.App.Views.ChartColumn> _connections = [];
    [ObservableProperty] private string _connectionsMeta = "";
    [ObservableProperty] private bool _hasConnections;
    [ObservableProperty] private int[,]? _heat;
    [ObservableProperty] private bool _hasHeat;
    [ObservableProperty] private IReadOnlyList<RemoteMan.App.Views.ChartPoint> _growth = [];
    [ObservableProperty] private string _growthMeta = "";
    [ObservableProperty] private IReadOnlyList<RemoteMan.App.Views.ChartPoint> _zabbixTrend = [];
    [ObservableProperty] private bool _hasZabbixTrend;
    [ObservableProperty] private bool _hasFavorites;
    [ObservableProperty] private bool _hasRecent;
    [ObservableProperty] private bool _hasFolders;

    // --- Zabbix ---
    [ObservableProperty] private bool _zabbixOn;
    [ObservableProperty] private bool _zabbixHasData;
    [ObservableProperty] private string _zabbixMeta = "";
    [ObservableProperty] private string _zabbixWaiting = "";
    [ObservableProperty] private int _zabbixOk;
    [ObservableProperty] private int _zabbixWarn;
    [ObservableProperty] private int _zabbixHigh;
    [ObservableProperty] private int _zabbixUnreach;
    [ObservableProperty] private string _problemsMore = "";
    [ObservableProperty] private bool _noProblems;

    // --- Oxidized config backups ---
    [ObservableProperty] private bool _backupsOn;
    /// <summary>A node list has arrived; until then the widget shows <see cref="BackupsWaiting"/>.</summary>
    [ObservableProperty] private bool _backupsHasData;
    [ObservableProperty] private string _backupsWaiting = "";
    [ObservableProperty] private string _backupsMeta = "";
    [ObservableProperty] private int _backupDevices;
    [ObservableProperty] private int _backupsOk;
    [ObservableProperty] private int _backupsStale;
    [ObservableProperty] private int _backupsFailing;
    [ObservableProperty] private int _backupsNever;
    [ObservableProperty] private int _backupsChanged;
    /// <summary>Devices Oxidized backs up that no entry points at.</summary>
    [ObservableProperty] private int _backupsUnlinked;
    [ObservableProperty] private IReadOnlyList<RemoteMan.App.Views.ChartSlice> _backupSlices = [];
    /// <summary>Failing first, then stale, then never backed up.</summary>
    public ObservableCollection<DashBackup> BackupAttention { get; } = new();
    [ObservableProperty] private string _backupAttentionMore = "";
    [ObservableProperty] private bool _noBackupAttention;
    /// <summary>Configs that changed in the last 7 days, newest first.</summary>
    public ObservableCollection<DashBackup> BackupChanges { get; } = new();
    [ObservableProperty] private string _backupChangesMore = "";
    [ObservableProperty] private bool _noBackupChanges;
    /// <summary>Every device with its last backup, shown when <see cref="ShowAllBackups"/> is on.</summary>
    public ObservableCollection<DashBackup> BackupAll { get; } = new();
    [ObservableProperty] private bool _showAllBackups;
    public bool BackupsVisible => ShowBackups && BackupsOn;
    partial void OnBackupsOnChanged(bool value) => OnPropertyChanged(nameof(BackupsVisible));

    // --- which widgets show (More > Widgets on the dashboard) ---
    [ObservableProperty] private bool _showTiles = true;
    [ObservableProperty] private bool _showProtocols = true;
    [ObservableProperty] private bool _showZabbix = true;
    [ObservableProperty] private bool _showFavorites = true;
    [ObservableProperty] private bool _showRecent = true;
    [ObservableProperty] private bool _showFolders = true;
    [ObservableProperty] private bool _showChecks = true;
    [ObservableProperty] private bool _showConnections = true;
    [ObservableProperty] private bool _showHeat = true;
    [ObservableProperty] private bool _showGrowth = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BackupsVisible))] private bool _showBackups = true;

    public bool ZabbixVisible => ShowZabbix && ZabbixOn;
    partial void OnShowZabbixChanged(bool value) => OnPropertyChanged(nameof(ZabbixVisible));
    partial void OnZabbixOnChanged(bool value) => OnPropertyChanged(nameof(ZabbixVisible));

    /// <summary>Widget keys, labels and getters/setters, for the Widgets menu and settings.</summary>
    public IReadOnlyList<(string Key, string Label)> Widgets { get; } =
    [
        ("tiles", "Totals"), ("protocols", "Entries by protocol"), ("zabbix", "Zabbix"),
        ("connections", "Your connections"), ("heat", "When you connect"),
        ("favorites", "Favorites"), ("recent", "Recent"), ("folders", "Folders"), ("growth", "Entries over time"), ("checks", "Housekeeping"),
        ("backups", "Config backups"),
    ];

    public bool IsShown(string key) => key switch
    {
        "tiles" => ShowTiles, "protocols" => ShowProtocols, "zabbix" => ShowZabbix, "favorites" => ShowFavorites,
        "recent" => ShowRecent, "folders" => ShowFolders, "checks" => ShowChecks,
        "connections" => ShowConnections, "heat" => ShowHeat, "growth" => ShowGrowth, "backups" => ShowBackups, _ => false,
    };

    public void SetShown(string key, bool on)
    {
        switch (key)
        {
            case "tiles": ShowTiles = on; break;
            case "protocols": ShowProtocols = on; break;
            case "zabbix": ShowZabbix = on; break;
            case "favorites": ShowFavorites = on; break;
            case "recent": ShowRecent = on; break;
            case "folders": ShowFolders = on; break;
            case "checks": ShowChecks = on; break;
            case "connections": ShowConnections = on; break;
            case "heat": ShowHeat = on; break;
            case "growth": ShowGrowth = on; break;
            case "backups": ShowBackups = on; break;
            default: return;
        }
        WidgetToggled?.Invoke(key, on);
    }

    [RelayCommand] private void Connect(Guid? id) { if (id is { } g) ConnectAction?.Invoke(g); }
    [RelayCommand] private void Reveal(Guid? id) { if (id is { } g) RevealAction?.Invoke(g); }
    [RelayCommand] private void RevealProblem(DashProblem? p) { if (p?.EntryId is { } id) RevealAction?.Invoke(id); }
    [RelayCommand] private void OpenZabbix() => OpenZabbixAction?.Invoke();
    [RelayCommand] private void ToggleCheck(DashCheck? c) { if (c is { Count: > 0 }) c.IsOpen = !c.IsOpen; }
    [RelayCommand] private void RevealBackup(DashBackup? b) { if (b?.EntryId is { } id) RevealAction?.Invoke(id); }
    [RelayCommand] private void OpenBackup(DashBackup? b) { if (b is not null) OpenBackupAction?.Invoke(b); }
    [RelayCommand] private void OpenOxidized() => OpenOxidizedAction?.Invoke();
    [RelayCommand] private void RefreshBackups() => RefreshBackupsAction?.Invoke();
    [RelayCommand] private void ToggleAllBackups() => ShowAllBackups = !ShowAllBackups;
}
