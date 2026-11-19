using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tether.App.Services;
using Tether.Core.Model;
using Tether.Core.Networking;
using Tether.Core.Security;
using Tether.Core.Storage;
using Tether.Data;

namespace Tether.App.ViewModels;

public sealed record PropertyRow(string Key, string Value, string? State = null);

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private EntryStore? _store;
    private ISecretProtector? _protector;
    private readonly Dictionary<Guid, EntryNode> _nodes = new();

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
    }

    // --- dialogs supplied by the window ---
    public Func<Entry, EntryStore, ISecretProtector, Entry?>? EditDialog { get; set; }
    public Func<string, bool>? Confirm { get; set; }

    public ObservableCollection<EntryNode> Roots { get; } = new();
    public ObservableCollection<PropertyRow> Properties { get; } = new();
    public ObservableCollection<SessionViewModel> Sessions { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedName), nameof(SelectedKindTag))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(EditCommand), nameof(DeleteCommand))]
    private EntryNode? _selected;

    [ObservableProperty] private SessionViewModel? _activeSession;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _dataSourceText = "Opening…";
    [ObservableProperty] private bool _dataSourceOk;
    [ObservableProperty] private string _onlineText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _sessionStatus = "";

    public bool HasSelection => Selected is not null;
    public string SelectedName => Selected?.Name ?? "";
    public string SelectedKindTag => Selected?.KindTag ?? "";
    public EntryStore? Store => _store;

    public async Task InitializeAsync()
    {
        var cfg = _settings.Active;
        try
        {
            var source = DataSourceFactory.Create(cfg);
            if (source is RedundantDataSource r)
                r.FailedOver += (_, now) => OnUi(() => { DataSourceText = r.DisplayName; Message = $"Data source failed over to {now.DisplayName}"; });
            _protector = Secrets.For(cfg);
            var user = _settings.DisplayName ?? Environment.UserName;
            _store = new EntryStore(source, user, Environment.MachineName);
            _store.EntriesChanged += (_, _) => OnUi(RebuildTree);
            _store.PresenceChanged += (_, _) => OnUi(ApplyPresence);
            await _store.OpenAsync();
            DataSourceText = source.DisplayName;
            DataSourceOk = true;
            RebuildTree();
        }
        catch (Exception ex)
        {
            DataSourceOk = false;
            DataSourceText = $"{cfg.Name}: {ex.Message}";
        }
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    // ---------------- tree ----------------

    partial void OnSearchTextChanged(string value) => RebuildTree();

    private void RebuildTree()
    {
        if (_store is null) return;
        var expanded = _nodes.Values.Where(n => !n.IsExpanded).Select(n => n.Id).ToHashSet();
        var selectedId = Selected?.Id;
        _nodes.Clear();
        Roots.Clear();

        var all = _store.Entries.ToList();
        var filter = SearchText.Trim();
        if (filter.Length > 0)
        {
            // Flat result list while searching; matches name, host or tags.
            foreach (var e in all.Where(e => e.Kind != EntryKind.Folder && Matches(e, filter)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                Roots.Add(_nodes[e.Id] = new EntryNode(e, 0));
        }
        else
        {
            var byParent = all.ToLookup(e => e.ParentId is { } p && all.Any(x => x.Id == p) ? p : (Guid?)null);
            void Add(Guid? parent, ObservableCollection<EntryNode> into, int depth)
            {
                foreach (var e in byParent[parent].OrderBy(e => e.Kind == EntryKind.Folder ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var n = new EntryNode(e, depth) { IsExpanded = !expanded.Contains(e.Id) };
                    _nodes[e.Id] = n;
                    into.Add(n);
                    if (e.Kind == EntryKind.Folder) Add(e.Id, n.Children, depth + 1);
                }
            }
            Add(null, Roots, 0);
        }

        if (selectedId is { } id && _nodes.TryGetValue(id, out var sel)) { sel.IsSelected = true; Selected = sel; }
        else Selected = null;
        ApplyPresence();
        RefreshProperties();
    }

    private static bool Matches(Entry e, string f) =>
        e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        (e.Tags?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false) ||
        e.Endpoints.Any(p => p.Host.Contains(f, StringComparison.OrdinalIgnoreCase)) ||
        (e.Web?.Url.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);

    partial void OnSelectedChanged(EntryNode? value)
    {
        RefreshProperties();
        if (_store is not null) _ = _store.SetMyPresenceAsync(value?.Id, PresenceState.Viewing);
    }

    private void ApplyPresence()
    {
        if (_store is null) return;
        foreach (var n in _nodes.Values)
        {
            n.People.Clear();
            foreach (var p in _store.PresenceFor(n.Id))
                n.People.Add(new PresenceChip(p.Initials, $"{p.User} ({p.Machine}) is {p.State.ToString().ToLowerInvariant()}"));
        }
        var users = _store.Presence.Where(p => p.IsAlive(DateTimeOffset.UtcNow)).Select(p => p.User).Append(_store.User).Distinct().Count();
        OnlineText = _store.Source.SupportsPresence && _store.Source.Kind != DataSourceKind.Sqlite ? $"{users} user{(users == 1 ? "" : "s")} online" : "";
        RefreshProperties();
    }

    private void RefreshProperties()
    {
        Properties.Clear();
        foreach (var n in _nodes.Values) n.IsLinkTarget = false;
        if (Selected?.Entry is not { } e || _store is null) return;

        for (var i = 0; i < e.Endpoints.Count; i++)
            Properties.Add(new PropertyRow(i == 0 ? "Primary" : "Fallback", e.Endpoints[i].ToString()));
        if (e.Kind == EntryKind.Web) Properties.Add(new PropertyRow("URL", e.Web?.Url ?? ""));

        if (e.Kind == EntryKind.Credential)
        {
            Properties.Add(new PropertyRow("Login", (e.Credential?.Domain is { Length: > 0 } d ? d + "\\" : "") + e.Credential?.UserName));
            var usedBy = _store.Entries.Where(x => x.CredentialId == e.Id).Select(x => x.Name).ToList();
            Properties.Add(new PropertyRow("Used by", usedBy.Count > 0 ? string.Join(", ", usedBy) : "nothing"));
        }
        else if (e.IsConnectable)
        {
            var cred = e.CredentialId is { } cid ? _store.Get(cid) : null;
            Properties.Add(new PropertyRow("Login", cred?.Name ?? "none (Ctrl+click a credential)"));
            if (cred is not null && _nodes.TryGetValue(cred.Id, out var cn)) cn.IsLinkTarget = true;
        }
        switch (e.Kind)
        {
            case EntryKind.Ssh:
                Properties.Add(new PropertyRow("Host key", e.Ssh?.AutoAcceptHostKey != false ? "auto-accept, logged" : "strict"));
                Properties.Add(new PropertyRow("Algos", e.Ssh?.AllowLegacyAlgorithms != false ? "modern, legacy fallback" : "modern only"));
                break;
            case EntryKind.Rdp:
                Properties.Add(new PropertyRow("Certs", e.Rdp?.IgnoreCertificateErrors != false ? "skip warnings" : "prompt"));
                Properties.Add(new PropertyRow("Display", e.Rdp is { DesktopWidth: > 0 } r ? $"{r.DesktopWidth}x{r.DesktopHeight}" : "fit to tab"));
                break;
            case EntryKind.Web:
                Properties.Add(new PropertyRow("Browser", (e.Web?.Browser ?? BrowserKind.Embedded).ToString().ToLowerInvariant()));
                break;
        }
        var people = _store.PresenceFor(e.Id).Select(p => $"{p.User} ({p.State.ToString().ToLowerInvariant()})").ToList();
        if (people.Count > 0) Properties.Add(new PropertyRow("Users", string.Join(", ", people), "presence"));
        if (e.UpdatedBy is not null) Properties.Add(new PropertyRow("Changed", $"{e.UpdatedAt.ToLocalTime():g} by {e.UpdatedBy}"));
    }

    /// <summary>Ctrl+click on a credential while a connection is selected.</summary>
    public async Task<bool> TryCtrlClickLinkAsync(EntryNode clicked)
    {
        if (_store is null || Selected is not { } sel || !sel.Entry.IsConnectable || clicked.Entry.Kind != EntryKind.Credential) return false;
        try
        {
            await _store.LinkCredentialAsync(sel.Id, clicked.Id);
            Message = $"Linked {clicked.Name} to {sel.Name}";
        }
        catch (Exception ex) { Message = ex.Message; }
        return true;
    }

    // ---------------- commands ----------------

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(EntryNode? node)
    {
        node ??= Selected;
        if (node?.Entry is not { IsConnectable: true } e || _store is null || _protector is null) return;
        await ConnectEntryAsync(e);
    }

    private bool CanConnect(EntryNode? node) => (node ?? Selected)?.Entry.IsConnectable == true;

    private async Task ConnectEntryAsync(Entry e)
    {
        var probe = EntryStore.Clone(e);
        if (probe.Kind == EntryKind.Web && probe.Endpoints.Count == 0 && probe.Web?.Url is { } url)
            probe.Endpoints.Add(EndpointResolver.Parse(url) is var ep && ep.Port == 0
                ? ep with { Port = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 80 : 443 } : ep);

        Message = $"Connecting to {e.Name}…";
        var resolver = new EndpointResolver { Timeout = TimeSpan.FromMilliseconds(_settings.FallbackTimeoutMs) };
        var result = await resolver.ResolveAsync(probe);
        if (!result.Success)
        {
            Message = $"{e.Name}: no host answered ({string.Join("; ", result.Attempts.Select(a => $"{a.Endpoint} {a.Error}"))})";
            return;
        }

        var cred = _store is null || _protector is null ? new Tether.Protocols.ResolvedCredential(null, null, null, null, null) : Secrets.Resolve(e, _store, _protector);
        var endpoint = result.Endpoint!;
        SessionViewModel? s = e.Kind switch
        {
            EntryKind.Ssh => new SshSessionViewModel(e, endpoint, cred, result.UsedFallback),
            EntryKind.Rdp => new RdpSessionViewModel(e, endpoint, cred, result.UsedFallback),
            EntryKind.Web when (e.Web?.Browser ?? BrowserKind.Embedded) == BrowserKind.Embedded => new WebSessionViewModel(e, endpoint, cred, result.UsedFallback),
            _ => null,
        };

        if (s is null)
        {
            // External: VNC viewer or a chosen external browser.
            try
            {
                if (e.Kind == EntryKind.Vnc) Tether.Protocols.Vnc.VncLauncher.Open(endpoint, e.Vnc ?? new VncSettings());
                else Tether.Protocols.Web.ExternalBrowser.Open(new WebSessionViewModel(e, endpoint, cred, false).Url, e.Web!.Browser, e.Web.IgnoreCertificateErrors);
                Message = $"Opened {e.Name} externally";
            }
            catch (Exception ex) { Message = ex.Message; }
            return;
        }

        Message = result.UsedFallback ? $"{result.Attempts[0].Endpoint} {result.Attempts[0].Error}, using fallback {endpoint}" : "";
        s.CloseRequested += (_, _) => CloseSession(s);
        Sessions.Add(s);
        ActiveSession = s;
    }

    [RelayCommand]
    private async Task QuickConnectAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;
        // A single search hit connects that entry; otherwise treat the text as a host.
        if (Roots.Count == 1 && Roots[0].Entry.IsConnectable) { await ConnectEntryAsync(Roots[0].Entry); return; }
        var kind = text.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase) ? EntryKind.Rdp
                 : text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? EntryKind.Web
                 : EntryKind.Ssh;
        var adhoc = new Entry { Kind = kind, Name = text };
        if (kind == EntryKind.Web) adhoc.Web = new WebSettings { Url = text };
        else adhoc.Endpoints.Add(EndpointResolver.Parse(text));
        await ConnectEntryAsync(adhoc);
    }

    partial void OnActiveSessionChanged(SessionViewModel? oldValue, SessionViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        SessionStatus = newValue?.StatusText ?? "";
        if (_store is not null && newValue is not null && newValue.Entry.Revision > 0)
            _ = _store.SetMyPresenceAsync(newValue.Entry.Id, PresenceState.Connected);
    }

    [RelayCommand]
    private void CloseSession(SessionViewModel? s)
    {
        if (s is null) return;
        var idx = Sessions.IndexOf(s);
        Sessions.Remove(s);
        (s as IDisposable)?.Dispose();
        if (ActiveSession == s) ActiveSession = Sessions.Count == 0 ? null : Sessions[Math.Min(idx, Sessions.Count - 1)];
    }

    [RelayCommand]
    private Task NewEntryAsync(string kindName)
    {
        var kind = Enum.Parse<EntryKind>(kindName);
        var parent = Selected?.Entry is { } s ? (s.Kind == EntryKind.Folder ? s.Id : s.ParentId) : null;
        var e = new Entry { Kind = kind, ParentId = parent, Name = kind == EntryKind.Folder ? "New folder" : "" };
        switch (kind)
        {
            case EntryKind.Rdp: e.Rdp = new(); e.Endpoints.Add(new HostEndpoint("", 3389)); break;
            case EntryKind.Ssh: e.Ssh = new(); e.Endpoints.Add(new HostEndpoint("", 22)); break;
            case EntryKind.Vnc: e.Vnc = new(); e.Endpoints.Add(new HostEndpoint("", 5900)); break;
            case EntryKind.Web: e.Web = new(); break;
            case EntryKind.Credential: e.Credential = new(); break;
        }
        return EditAndSaveAsync(e);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditAsync() => Selected is { } s ? EditAndSaveAsync(EntryStore.Clone(s.Entry)) : Task.CompletedTask;

    private async Task EditAndSaveAsync(Entry e)
    {
        if (_store is null || _protector is null || EditDialog is null) return;
        await _store.SetMyPresenceAsync(e.Id, PresenceState.Editing);
        var edited = EditDialog(e, _store, _protector);
        await _store.SetMyPresenceAsync(Selected?.Id, PresenceState.Viewing);
        if (edited is null) return;
        try
        {
            var saved = await _store.SaveAsync(edited);
            if (_nodes.TryGetValue(saved.Id, out var n)) { n.IsSelected = true; Selected = n; }
        }
        catch (ConcurrencyException ex)
        {
            Message = $"{ex.Message} Your edit was not saved; reopen the entry to see their change.";
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (_store is null || Selected is not { } s) return;
        var what = s.IsFolder ? $"folder \"{s.Name}\" and everything in it" : $"\"{s.Name}\"";
        if (Confirm?.Invoke($"Delete {what}?") != true) return;
        await _store.DeleteAsync(s.Id);
    }

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (_store is null || Selected?.Entry is not { } e || e.Kind == EntryKind.Folder) return;
        var copy = EntryStore.Clone(e);
        copy.Id = Guid.NewGuid();
        copy.Revision = 0;
        copy.Name += " (copy)";
        await _store.SaveAsync(copy);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Sessions.ToList()) (s as IDisposable)?.Dispose();
        if (_store is not null) await _store.DisposeAsync();
    }
}
