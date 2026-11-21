using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Services;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using RemoteMan.Protocols.Rdp;
using RemoteMan.Protocols.Ssh;

namespace RemoteMan.App.ViewModels;

public sealed record PropertyRow(string Key, string Value, string? State = null, System.Windows.Input.ICommand? Copy = null);

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private EntryStore? _store;
    private ISecretProtector? _protector;
    private readonly Dictionary<Guid, EntryNode> _nodes = new();

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _isSplit = settings.SplitPane;
        CreateDashboard();
        Sessions.CollectionChanged += (_, _) => RefreshDashboard();
    }

    // --- dialogs supplied by the window ---
    /// <summary>Opens the editor; returns the entries to save in order (the edited one last), or null on cancel.</summary>
    public Func<Entry, EntryStore, ISecretProtector, IReadOnlyList<Entry>?>? EditDialog { get; set; }
    /// <summary>(title, text, action button) for a destructive question; true means go ahead.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }
    /// <summary>Shows a question with buttons; returns the index picked, or -1 when closed.</summary>
    public Func<string, string, string[], int>? Choose { get; set; }

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
    [ObservableProperty] private bool _isSharedSource;
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
            // Local files: your Windows account. Shared databases: the login the server reports (set on open).
            var user = Environment.UserDomainName is { Length: > 0 } dom && dom != Environment.MachineName
                ? $"{dom}\\{Environment.UserName}" : Environment.UserName;
            IsSharedSource = cfg.Kind != DataSourceKind.Sqlite;
            var cache = IsSharedSource ? new OfflineCache(cfg.Name) : null;
            var store = _store = new EntryStore(source, user, Environment.MachineName, cache);
            store.EntriesChanged += (_, _) => OnUi(RebuildTree);
            store.PresenceChanged += (_, _) => OnUi(ApplyPresence);
            // Lost connection = offline mode: red Q, entries stay usable, edits wait. Back online = green Q, live again.
            store.OnlineChanged += (_, up) => OnUi(() =>
            {
                ShowOnlineState(source, store);
                Message = up ? "Back online" : "Database unreachable: working offline, retrying every 15 seconds";
            });
            await store.OpenAsync();
            ShowOnlineState(source, store);
            RebuildTree();
            OfferReopen();
            await StartZabbixAsync();
            await StartOxidizedAsync();
            await SeedSnippetsAsync(cfg.Name);
            _ = BackupLoopAsync();
        }
        catch (Exception ex)
        {
            DataSourceOk = false;
            DataSourceText = $"{cfg.Name}: {ex.Message}";
            Message = DataSourceText;
        }
    }

    private void ShowOnlineState(IDataSource source, EntryStore store)
    {
        DataSourceOk = store.IsOnline;
        DataSourceText = store.IsOnline
            ? $"{source.DisplayName} as {store.User}"
            : $"{source.DisplayName}: offline, retrying every {store.ReconnectInterval.TotalSeconds:0} s" + (store.LastError is { } err ? $" ({err})" : "");
        RefreshDashboard();
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

        // Snippets live in the SSH tab's Snippets panel, not in the tree.
        var all = _store.Entries.Where(e => e.Kind != EntryKind.Snippet).ToList();
        RefreshSnippets();
        var filter = SearchText.Trim();
        if (filter.Length == 0 && PanelMode != "nav")
        {
            // Favorites, Recent, Credentials: flat lists from the icon rail.
            foreach (var e in PanelEntries(all))
                if (!_nodes.ContainsKey(e.Id)) Roots.Add(_nodes[e.Id] = new EntryNode(e, 0));
        }
        else if (filter.Length > 0)
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
            // The data source is the top node, like a drive in Explorer; everything stored sits under it.
            var root = new EntryNode(new Entry { Id = DataSourceRootId, Kind = EntryKind.Folder, Name = _settings.Active.Name }, 0)
            {
                IsRoot = true,
                IsExpanded = _rootExpanded,
            };
            root.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(EntryNode.IsExpanded)) _rootExpanded = root.IsExpanded; };
            Roots.Add(root);
            Add(null, root.Children, 1);

        }

        if (selectedId is { } id && _nodes.TryGetValue(id, out var sel)) { sel.IsSelected = true; Selected = sel; }
        else Selected = null;
        ApplyPresence();
        ApplyZabbix();
        ApplyOxidized();
    }

    /// <summary>Id of the data source node at the top of the tree (never stored).</summary>
    public static readonly Guid DataSourceRootId = new("d5000000-0000-4000-8000-000000000001");
    private bool _rootExpanded = true;

    /// <summary>Id of the virtual Favorites group (never stored).</summary>
    public static readonly Guid FavoritesId = new("f4f0a7e0-0000-4000-8000-000000000001");

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
                n.People.Add(new PresenceChip(p.Initials, $"{p.User} ({p.Machine}) is {p.State.ToString().ToLowerInvariant()}", PresenceChip.ColorFor(p.User)));
            n.Badge = n.People.Count == 0 ? null
                : n.People[0] with { Initials = n.People[0].Initials[..1], ToolTip = string.Join("\n", n.People.Select(c => c.ToolTip)) };
        }
        RefreshOnlineUsers();
        var users = _store.Presence.Where(p => p.IsAlive(DateTimeOffset.UtcNow)).Select(p => p.User).Append(_store.User).Distinct().Count();
        OnlineText = _store.Source.SupportsPresence && _store.Source.Kind != DataSourceKind.Sqlite ? $"{users} user{(users == 1 ? "" : "s")} online" : "";
        RefreshProperties();
    }

    private void RefreshProperties()
    {
        Properties.Clear();
        RefreshZabbixCard();
        RefreshOxidizedCard();
        foreach (var n in _nodes.Values) n.IsLinkTarget = false;
        if (Selected?.Entry is not { } e || _store is null) return;

        for (var i = 0; i < e.Endpoints.Count; i++)
            Properties.Add(new PropertyRow(i == 0 ? "Primary" : "Fallback", e.Endpoints[i].ToString()));
        if (e.Kind == EntryKind.Web) Properties.Add(new PropertyRow("URL", e.Web?.Url ?? ""));

        if (e.Kind == EntryKind.Credential)
        {
            var ct = e.Credential?.Type ?? CredentialType.Password;
            Properties.Add(new PropertyRow("Type", ct switch { CredentialType.SshKey => "SSH key", CredentialType.Certificate => "certificate", CredentialType.Secret => "secret", _ => "user name and password" }));
            if (e.Credential?.UserName is { Length: > 0 })
                Properties.Add(new PropertyRow("Login", (e.Credential?.Domain is { Length: > 0 } d ? d + "\\" : "") + e.Credential?.UserName));
            var usedBy = _store.Entries.Where(x => x.Login == LoginSource.Saved && x.CredentialId == e.Id).Select(x => x.Name).ToList();
            Properties.Add(new PropertyRow("Used by", usedBy.Count > 0 ? string.Join(", ", usedBy) : "nothing"));
        }
        else if (e.IsConnectable || e.Kind == EntryKind.Folder)
        {
            var (src, holder) = _store.ResolveLogin(e);
            var text = src switch
            {
                LoginSource.WindowsLogin => "Windows (SSO)",
                LoginSource.Saved => holder!.Name,
                LoginSource.Embedded => "typed in" + (holder?.Credential?.UserName is { Length: > 0 } u ? $" ({u})" : ""),
                _ => e.IsConnectable ? "none (Ctrl+click a credential)" : "none",
            };
            if (e.Login == LoginSource.Inherited && src != LoginSource.None) text += ", from folder";
            Properties.Add(new PropertyRow("Login", text));
            if (src == LoginSource.Saved && _nodes.TryGetValue(holder!.Id, out var cn)) cn.IsLinkTarget = true;
        }
        switch (e.Kind)
        {
            case EntryKind.Ssh:
                Properties.Add(new PropertyRow("Host key", e.Ssh?.AutoAcceptHostKey != false ? "auto-accept, logged" : "strict"));
                Properties.Add(new PropertyRow("Algos", e.Ssh?.AllowLegacyAlgorithms != false ? "modern, legacy fallback" : "modern only"));
                if (e.Ssh?.JumpHostId is { } jid) Properties.Add(new PropertyRow("Via", _store.Get(jid)?.Name ?? "missing jump host"));
                break;
            case EntryKind.Rdp:
                Properties.Add(new PropertyRow("Certs", e.Rdp?.IgnoreCertificateErrors != false ? "skip warnings" : "prompt"));
                Properties.Add(new PropertyRow("Display", e.Rdp is { DesktopWidth: > 0 } r ? $"{r.DesktopWidth}x{r.DesktopHeight}" : "fit to tab"));
                break;
            case EntryKind.Web:
                Properties.Add(new PropertyRow("Browser", (e.Web?.Browser ?? BrowserKind.Embedded).ToString().ToLowerInvariant()));
                break;
        }
        if (e.Kind == EntryKind.Rdp && e.Rdp?.SshTunnelId is { } tid) Properties.Add(new PropertyRow("Tunnel", _store.Get(tid)?.Name ?? "missing SSH entry"));
        if (e.IsConnectable && e.OpenIn != OpenMode.Tab) Properties.Add(new PropertyRow("Opens", e.OpenIn == OpenMode.Window ? "in its own window" : "full screen"));
        if (e.Connection is { WakeOnLan: true } wol) Properties.Add(new PropertyRow("Wake", wol.MacAddress ?? "on"));
        if (e.Ssh?.Forwards is { Count: > 0 } fw) Properties.Add(new PropertyRow("Forwards", string.Join(", ", fw.Where(f => f.Enabled))));
        if (e.Description is { Length: > 0 } desc) Properties.Add(new PropertyRow("Notes", desc));
        // Custom fields, each with a copy button; secrets stay hidden and copy like a password.
        foreach (var f in e.Fields ?? [])
        {
            var field = f;
            var copy = new CommunityToolkit.Mvvm.Input.RelayCommand(() => CopyField(field));
            Properties.Add(new PropertyRow(f.Name, f.Secret ? (f.Value is null ? "" : "••••••••") : f.Value ?? "", null, f.Value is null ? null : copy));
        }
        if (HasTotp(e)) Properties.Add(new PropertyRow("2FA", "code ready", null, new CommunityToolkit.Mvvm.Input.RelayCommand(CopyTotp)));
        var people = _store.PresenceFor(e.Id).Select(p => $"{p.User} ({p.State.ToString().ToLowerInvariant()})").ToList();
        if (people.Count > 0) Properties.Add(new PropertyRow("Users", string.Join(", ", people), "presence"));
        if (e.CreatedBy is not null) Properties.Add(new PropertyRow("Created", $"{e.CreatedAt?.ToLocalTime():g} by {e.CreatedBy}"));
        if (e.UpdatedBy is not null && e.Revision > 0 && e.UpdatedAt != e.CreatedAt)
            Properties.Add(new PropertyRow("Modified", $"{e.UpdatedAt.ToLocalTime():g} by {e.UpdatedBy}"));
    }

    public EntryNode? FindNode(Guid id) => _nodes.TryGetValue(id, out var n) ? n : null;

    /// <summary>True when <paramref name="id"/> can go into <paramref name="folder"/> (null = top level):
    /// not into itself or anything below it, and not where it already is.</summary>
    public bool CanMove(Guid id, Guid? folder)
    {
        if (_store?.Get(id) is not { } e || e.ParentId == folder) return false;
        for (var f = folder; f is { } fid; f = _store.Get(fid)?.ParentId)
            if (fid == id) return false;
        return folder is null || _store.Get(folder.Value)?.Kind == EntryKind.Folder;
    }

    public async Task MoveAsync(Guid id, Guid? folder)
    {
        if (_store is null || !CanMove(id, folder)) return;
        if (!_store.IsOnline) { Message = "Offline: entries can't be moved until the database is back"; return; }
        var copy = EntryStore.Clone(_store.Get(id)!);
        copy.ParentId = folder;
        try
        {
            await _store.SaveAsync(copy);
            var where = folder is { } f && _store.Get(f) is { } fe ? fe.Name : "the top level";
            Message = $"Moved {copy.Name} to {where}";
            if (folder is { } fid && _nodes.TryGetValue(fid, out var fn)) fn.IsExpanded = true;
        }
        catch (Exception ex) { Message = ex.Message; }
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

    /// <summary>One-off changes for "Connect with": nothing is saved.</summary>
    public sealed record ConnectOptions(bool? External = null, LoginSource? Login = null, Guid? CredentialId = null, bool? AdminSession = null);

    public async Task ConnectWithAsync(EntryNode node, ConnectOptions o)
    {
        if (node.Entry is not { IsConnectable: true } original || _store is null) return;
        var e = EntryStore.Clone(original);
        if (o.Login is { } l) { e.Login = l; e.CredentialId = o.CredentialId; }
        if (o.AdminSession is { } admin) { e.Rdp ??= new(); e.Rdp.AdminSession = admin; }
        if (o.External == true && e.Kind == EntryKind.Rdp) { await OpenInMstscAsync(e); return; }
        if (o.External is { } ext && e.Kind == EntryKind.Web)
        {
            e.Web ??= new();
            e.Web.Browser = ext ? (e.Web.Browser == BrowserKind.Embedded ? BrowserKind.Edge : e.Web.Browser) : BrowserKind.Embedded;
        }
        await ConnectEntryAsync(e);
    }

    /// <summary>Asks for a password the credential doesn't store. Null means cancelled.</summary>
    public Func<string, string?>? PromptPassword { get; set; }

    private RemoteMan.Protocols.ResolvedCredential? ResolveForConnect(Entry e)
    {
        if (_store is null || _protector is null) return RemoteMan.Protocols.ResolvedCredential.None;
        var cred = Secrets.Resolve(e, _store, _protector);
        if (cred.AskPassword && cred.Password is null)
        {
            var who = cred.Domain is { Length: > 0 } d ? $"{d}\\{cred.UserName}" : cred.UserName;
            var pw = PromptPassword?.Invoke($"Password for {who} on {e.Name}");
            if (pw is null) return null;
            cred = cred with { Password = pw };
        }
        return cred;
    }

    /// <summary>Shows a session in its own window (set by the main window). Null: everything opens in tabs.</summary>
    public Action<SessionViewModel>? OpenWindow { get; set; }

    private async Task ConnectEntryAsync(Entry e)
    {
        // Sections ticked "Folder" come from the folder.
        if (_store is not null) e = _store.Effective(e);
        var conn = e.Connection;
        var fields = _protector is null ? new Dictionary<string, string>() : Secrets.FieldValues(e, _protector);

        if (conn?.RunBefore is { Length: > 0 } before)
        {
            Message = $"{e.Name}: running \"{before}\"…";
            if (await LocalCommand.RunAsync(before, conn.WaitForRunBefore, fields) is { } err)
                Notify(new ConnectionProblem("Command before connecting failed", $"\"{before}\": {err}.", "Connecting anyway.", null), e);
        }
        if (conn is { WakeOnLan: true, MacAddress: { Length: > 0 } mac })
        {
            try { await RemoteMan.Core.Networking.WakeOnLan.SendAsync(mac, conn.WakeBroadcast); Message = $"Wake-on-LAN sent to {mac}"; }
            catch (Exception ex) { Notify(new ConnectionProblem("Wake-on-LAN failed", ex.Message, "Check the MAC address under Advanced > Connection.", null), e); }
        }

        var probe = EntryStore.Clone(e);
        if (probe.Kind == EntryKind.Web && probe.Endpoints.Count == 0 && WebSettings.NormalizeUrl(probe.Web?.Url) is { Length: > 0 } url)
            probe.Endpoints.Add(EndpointResolver.Parse(url) is var ep && ep.Port == 0
                ? ep with { Port = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 80 : 443 } : ep);

        Message = $"Connecting to {e.Name}…";

        // A serial console has no host to probe: open the COM port straight away.
        if (e.Kind == EntryKind.Serial)
        {
            if (ResolveForConnect(e) is not { } serialCred) { Message = ""; return; }
            var serial = new SshSessionViewModel(e, new HostEndpoint((e.Serial ?? new SerialSettings()).PortName), serialCred, false);
            Message = "";
            serial.CloseRequested += (_, _) => CloseSession(serial);
            TrackFailures(serial);
            Prepare(serial, e, fields);
            Show(serial);
            RememberRecent(e);
            return;
        }

        var resolver = new EndpointResolver { Timeout = TimeSpan.FromMilliseconds(_settings.FallbackTimeoutMs) };

        // Behind a jump host the target usually isn't reachable from here, so it isn't probed:
        // the SSH session tries each host in order through the tunnel instead.
        if (e.Kind == EntryKind.Ssh && e.Ssh?.JumpHostId is not null && _store is not null && _protector is not null)
        {
            if (BuildJumpChain(e) is not { } chain) return;
            var firstHop = chain[0];
            var probeHop = new Entry { Kind = EntryKind.Ssh, Endpoints = firstHop.Endpoints.ToList() };
            var hopResult = await resolver.ResolveAsync(probeHop);
            if (!hopResult.Success)
            {
                Message = $"Jump host {firstHop.Name}: no host answered ({string.Join("; ", hopResult.Attempts.Select(a => $"{a.Endpoint} {a.Error}"))})";
                return;
            }
            // The first hop starts with the host that answered, then the rest as fallbacks.
            chain[0] = firstHop with { Endpoints = firstHop.Endpoints.Where(x => x != hopResult.Endpoint).Prepend(hopResult.Endpoint!).ToList() };
            var targets = probe.Endpoints.Where(x => !string.IsNullOrWhiteSpace(x.Host)).Select(x => x.Port > 0 ? x : x with { Port = 22 }).ToList();
            if (targets.Count == 0) { Message = $"{e.Name} has no host"; return; }
            var jumped = new SshSessionViewModel(e, targets[0], Secrets.Resolve(e, _store, _protector), false)
            {
                Endpoints = targets,
                Jumps = chain,
            };
            Message = "";
            jumped.CloseRequested += (_, _) => CloseSession(jumped);
            TrackFailures(jumped);
            Prepare(jumped, e, fields);
            Show(jumped);
            RememberRecent(e);
            return;
        }

        // RDP through an SSH tunnel: the target is reached from the SSH server, so it isn't probed from here.
        if (e.Kind == EntryKind.Rdp && e.Rdp?.SshTunnelId is { } tunnelId && _store is not null && _protector is not null)
        {
            await ConnectRdpTunnelledAsync(e, tunnelId, resolver, fields);
            return;
        }

        var result = await resolver.ResolveAsync(probe);
        if (!result.Success && conn is { WaitUntilUp: true })
        {
            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(conn.WaitSeconds, 5, 3600));
            while (!result.Success && DateTime.UtcNow < deadline)
            {
                Message = $"Waiting for {e.Name} to answer… {(int)(deadline - DateTime.UtcNow).TotalSeconds} s left";
                await Task.Delay(2000);
                result = await resolver.ResolveAsync(probe);
            }
        }
        if (!result.Success)
        {
            Message = $"{e.Name}: no host answered ({string.Join("; ", result.Attempts.Select(a => $"{a.Endpoint} {a.Error}"))})";
            Notify(ConnectionProblem.FromAttempts(result.Attempts), e);
            return;
        }
        if (result.UsedFallback) Notify(ConnectionProblem.Fallback(result.Attempts[0], result.Endpoint!), e);

        if (ResolveForConnect(e) is not { } cred) { Message = ""; return; }
        var endpoint = result.Endpoint!;

        // More than one monitor: Windows' own client spans them; the embedded control can't.
        if (e.Kind == EntryKind.Rdp && e.Rdp is { Monitors: not RdpMonitors.One })
        {
            try
            {
                await RdpFiles.LaunchMstscAsync(RdpFiles.Write(e, endpoint, cred), endpoint, cred);
                Message = $"Opened {e.Name} on {(e.Rdp.Monitors == RdpMonitors.All ? "all monitors" : "the chosen monitors")} in Remote Desktop Connection";
                RememberRecent(e);
            }
            catch (Exception ex) { Message = ex.Message; }
            return;
        }

        // Chosen external browser missing: offer to install it or use the built-in one, never just fail.
        if (e.Kind == EntryKind.Web && e.Web is { Browser: BrowserKind.Chrome or BrowserKind.Edge or BrowserKind.Brave } ws
            && !RemoteMan.Protocols.Web.ExternalBrowser.IsInstalled(ws.Browser))
        {
            var name = RemoteMan.Protocols.Web.BrowserInstaller.DisplayName(ws.Browser);
            var pick = Choose?.Invoke($"{name} isn't installed",
                $"{e.Name} is set to open in {name}, which isn't installed on this PC.",
                [$"Install {name}", "Open in built-in browser", "Cancel"]) ?? 1;
            if (pick == 0)
            {
                Message = $"Installing {name}…";
                var outcome = await RemoteMan.Protocols.Web.BrowserInstaller.InstallAsync(ws.Browser);
                if (outcome != RemoteMan.Protocols.Web.BrowserInstaller.Result.Installed)
                {
                    Message = outcome == RemoteMan.Protocols.Web.BrowserInstaller.Result.DownloadPageOpened
                        ? $"Opened the {name} download page. Install it, then connect again."
                        : $"Couldn't install {name}";
                    return;
                }
                Message = $"{name} installed";
            }
            else if (pick == 1)
            {
                e = EntryStore.Clone(e);
                e.Web!.Browser = BrowserKind.Embedded;
            }
            else return;
        }

        SessionViewModel? s = e.Kind switch
        {
            EntryKind.Ssh or EntryKind.Telnet => new SshSessionViewModel(e, endpoint, cred, result.UsedFallback),
            EntryKind.Sftp or EntryKind.Ftp => new FilesSessionViewModel(e, endpoint, cred, result.UsedFallback),
            EntryKind.Proxmox => new WebSessionViewModel(e, endpoint, cred, result.UsedFallback),
            EntryKind.Rdp => new RdpSessionViewModel(e, endpoint, cred, result.UsedFallback) { GatewayCredential = GatewayLogin(e) },
            EntryKind.Web when (e.Web?.Browser ?? BrowserKind.Embedded) == BrowserKind.Embedded => new WebSessionViewModel(e, endpoint, cred, result.UsedFallback),
            _ => null,
        };

        if (s is null)
        {
            // External: VNC viewer or a chosen external browser.
            try
            {
                if (e.Kind == EntryKind.Vnc) RemoteMan.Protocols.Vnc.VncLauncher.Open(endpoint, e.Vnc ?? new VncSettings());
                else if (e.Kind == EntryKind.PowerShell) RemoteMan.Protocols.Tools.PowerShellLauncher.Open(endpoint, cred, e.PowerShell ?? new PowerShellSettings());
                else if (e.Kind == EntryKind.WinBox)
                {
                    var exe = RemoteMan.Protocols.Tools.WinBoxLauncher.FindExe();
                    if (exe is null)
                    {
                        if (Choose?.Invoke("WinBox isn't on this PC", "Download WinBox from MikroTik (about 2 MB) and open the router in it?", ["Download WinBox", "Cancel"]) != 0) { Message = ""; return; }
                        Message = "Downloading WinBox…";
                        exe = await RemoteMan.Protocols.Tools.WinBoxLauncher.DownloadAsync();
                    }
                    RemoteMan.Protocols.Tools.WinBoxLauncher.Open(exe, endpoint, cred);
                }
                else
                    RemoteMan.Protocols.Web.ExternalBrowser.Open(new WebSessionViewModel(e, endpoint, cred, false).Url, e.Web!.Browser, e.Web.IgnoreCertificateErrors);
                Message = $"Opened {e.Name} externally";
            }
            catch (Exception ex) { Message = ex.Message; Notify(ConnectionProblem.FromException(ex, e.Kind), e); }
            return;
        }

        Message = result.UsedFallback ? $"{result.Attempts[0].Endpoint} {result.Attempts[0].Error}, using fallback {endpoint}" : "";
        s.CloseRequested += (_, _) => CloseSession(s);
        TrackFailures(s);
        Prepare(s, e, fields);
        Show(s);
        RememberRecent(e);
    }

    private RemoteMan.Protocols.ResolvedCredential? GatewayLogin(Entry e) =>
        e.Rdp?.GatewayCredentialId is { } gid && _store?.Get(gid) is { } g && _protector is not null ? Secrets.Resolve(g, _store, _protector) : null;

    /// <summary>Custom fields and the "after closing" command.</summary>
    private void Prepare(SessionViewModel s, Entry e, IReadOnlyDictionary<string, string> fields)
    {
        s.FieldValues = fields;
        if (e.Connection?.RunAfter is { Length: > 0 } after)
            s.Closed += closed => { _ = LocalCommand.RunAsync(after, wait: false, fields); };
    }

    /// <summary>A tab, or its own window / full screen as the entry's Misc tab says. RDP full screen is the
    /// control's own (blue bar to restore it into the tab); other kinds get a borderless window.</summary>
    private void Show(SessionViewModel s)
    {
        // Every session opened goes in the local log behind the dashboard's connection charts.
        DashboardCharts.Record(_settings.ConnectLog, new ConnectEvent(DateTimeOffset.Now, s.KindTag));
        TrySaveSettings();
        RefreshDashboard();
        var mode = s.Entry.OpenIn;
        if (mode == OpenMode.FullScreen && s is RdpSessionViewModel) { s.FullScreen = true; mode = OpenMode.Tab; }
        if (mode != OpenMode.Tab && OpenWindow is { } open)
        {
            s.FullScreen = mode == OpenMode.FullScreen;
            open(s);
            TrackWindowSession(s);
            return;
        }
        AddSession(s);
    }

    private async Task ConnectRdpTunnelledAsync(Entry e, Guid tunnelId, EndpointResolver resolver, IReadOnlyDictionary<string, string> fields)
    {
        if (_store!.Get(tunnelId) is not { Kind: EntryKind.Ssh } hopEntry) { Message = $"{e.Name}: its SSH tunnel entry no longer exists"; return; }
        var hop = _store.Effective(hopEntry);
        var hopProbe = new Entry { Kind = EntryKind.Ssh, Endpoints = hop.Endpoints.Where(x => !string.IsNullOrWhiteSpace(x.Host)).Select(x => x.Port > 0 ? x : x with { Port = 22 }).ToList() };
        var hopResult = await resolver.ResolveAsync(hopProbe);
        if (!hopResult.Success) { Message = $"SSH tunnel {hop.Name}: no host answered"; Notify(ConnectionProblem.FromAttempts(hopResult.Attempts), hop); return; }
        var target = e.Endpoints.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Host));
        if (target is null) { Message = $"{e.Name} has no host"; return; }
        if (target.Port == 0) target = target with { Port = 3389 };
        if (ResolveForConnect(e) is not { } cred) { Message = ""; return; }

        Message = $"Opening an SSH tunnel through {hop.Name}…";
        SshTunnel tunnel;
        try
        {
            tunnel = await SshTunnel.OpenAsync(hopResult.Endpoint!, Secrets.Resolve(hop, _store, _protector!), hop.Ssh ?? new SshSettings(),
                new KnownHosts(AppPaths.KnownHosts), target.Host, target.Port);
        }
        catch (Exception ex)
        {
            Message = $"SSH tunnel {hop.Name}: {ex.Message}";
            Notify(ConnectionProblem.FromException(ex, EntryKind.Ssh), hop);
            return;
        }
        var local = new HostEndpoint("127.0.0.1", tunnel.LocalEndPoint.Port);
        var s = new RdpSessionViewModel(e, local, cred, false) { GatewayCredential = GatewayLogin(e), Via = $"{target} via {hop.Name}" };
        s.Owned.Add(tunnel);
        Message = "";
        s.CloseRequested += (_, _) => CloseSession(s);
        TrackFailures(s);
        Prepare(s, e, fields);
        Show(s);
        RememberRecent(e);
    }

    /// <summary>The entry's jump hosts, outermost first. Null (with a message) on a loop or a missing entry.</summary>
    private List<RemoteMan.Protocols.Ssh.SshHop>? BuildJumpChain(Entry target)
    {
        var hops = new List<RemoteMan.Protocols.Ssh.SshHop>();
        var seen = new HashSet<Guid> { target.Id };
        var next = target.Ssh?.JumpHostId;
        while (next is { } id)
        {
            if (!seen.Add(id) || hops.Count >= 8) { Message = $"{target.Name}: jump hosts point at each other in a loop"; return null; }
            if (_store!.Get(id) is not { Kind: EntryKind.Ssh } hop) { Message = $"{target.Name}: its jump host entry no longer exists"; return null; }
            hops.Insert(0, new RemoteMan.Protocols.Ssh.SshHop(hop.Name,
                hop.Endpoints.Where(x => !string.IsNullOrWhiteSpace(x.Host)).Select(x => x.Port > 0 ? x : x with { Port = 22 }).ToList(),
                Secrets.Resolve(hop, _store, _protector!), hop.Ssh ?? new SshSettings()));
            if (hops[0].Endpoints.Count == 0) { Message = $"Jump host {hop.Name} has no host"; return null; }
            next = hop.Ssh?.JumpHostId;
        }
        return hops;
    }

    [RelayCommand]
    private async Task QuickConnectAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;
        // A single search hit connects that entry; otherwise treat the text as a host.
        if (Roots.Count == 1 && Roots[0].Entry.IsConnectable) { await ConnectEntryAsync(Roots[0].Entry); return; }
        await ConnectHostAsync(text);
    }

    /// <summary>The "Connect" box in the status bar: always a host, never a search.</summary>
    [RelayCommand]
    private async Task QuickConnectHostAsync()
    {
        var text = QuickHost.Trim();
        if (text.Length == 0) return;
        QuickHost = "";
        await ConnectHostAsync(text);
    }

    private async Task ConnectHostAsync(string text)
    {
        var kind = text.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase) ? EntryKind.Rdp
                 : text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? EntryKind.Web
                 : EntryKind.Ssh;
        var adhoc = new Entry { Kind = kind, Name = text };
        if (kind == EntryKind.Web) adhoc.Web = new WebSettings { Url = text };
        else adhoc.Endpoints.Add(EndpointResolver.Parse(text));
        await ConnectEntryAsync(adhoc);
    }

    [RelayCommand]
    private Task NewEntryAsync(string kindName)
    {
        var parent = Selected?.Entry is { } s ? (s.Kind == EntryKind.Folder ? s.Id : s.ParentId) : null;
        return NewEntryInAsync(kindName, parent);
    }

    /// <summary>Opens the editor for a new entry inside <paramref name="parent"/> (null = top level).</summary>
    public Task NewEntryInAsync(string kindName, Guid? parent)
    {
        var kind = Enum.Parse<EntryKind>(kindName);
        if (parent == FavoritesId) parent = null;
        var e = new Entry { Kind = kind, ParentId = parent, Name = kind == EntryKind.Folder ? "New folder" : "" };
        switch (kind)
        {
            case EntryKind.Rdp: e.Rdp = new(); e.Endpoints.Add(new HostEndpoint("", 3389)); break;
            case EntryKind.Ssh: e.Ssh = new(); e.Endpoints.Add(new HostEndpoint("", 22)); break;
            case EntryKind.Vnc: e.Vnc = new(); e.Endpoints.Add(new HostEndpoint("", 5900)); break;
            case EntryKind.Web: e.Web = new(); break;
            case EntryKind.Credential: e.Credential = new(); break;
            case EntryKind.Telnet: e.Ssh = new(); e.Endpoints.Add(new HostEndpoint("", 23)); break;
            case EntryKind.Serial: e.Ssh = new(); e.Serial = new(); break;
            case EntryKind.Sftp: e.Ssh = new(); e.Endpoints.Add(new HostEndpoint("", 22)); break;
            case EntryKind.Ftp: e.Ftp = new(); e.Endpoints.Add(new HostEndpoint("", 21)); break;
            case EntryKind.Proxmox: e.Proxmox = new(); e.Endpoints.Add(new HostEndpoint("", 8006)); break;
            case EntryKind.PowerShell: e.PowerShell = new(); e.Endpoints.Add(new HostEndpoint("", 5985)); break;
            case EntryKind.WinBox: e.Endpoints.Add(new HostEndpoint("", 8291)); break;
        }
        return EditAndSaveAsync(e);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditAsync() => Selected is { } s && s.Id != FavoritesId ? EditAndSaveAsync(EntryStore.Clone(s.Entry)) : Task.CompletedTask;

    private async Task EditAndSaveAsync(Entry e)
    {
        if (_store is null || _protector is null || EditDialog is null) return;
        if (!_store.IsOnline) { Message = "Offline: entries can be opened but not changed until the database is back"; return; }
        await _store.SetMyPresenceAsync(e.Id, PresenceState.Editing);
        var toSave = EditDialog(e, _store, _protector);
        await _store.SetMyPresenceAsync(Selected?.Id, PresenceState.Viewing);
        if (toSave is null || toSave.Count == 0) return;
        try
        {
            Entry saved = null!;
            foreach (var item in toSave) saved = await _store.SaveAsync(item);
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
        if (_store is null || Selected is not { } s || s.Id == FavoritesId) return;
        var title = s.IsFolder ? $"Delete folder \"{s.Name}\"?" : $"Delete \"{s.Name}\"?";
        var text = s.IsFolder ? "The folder and everything in it is removed for everyone using this data source." : "The entry is removed for everyone using this data source.";
        if (Confirm?.Invoke(title, text, "Delete") != true) return;
        try { await _store.DeleteAsync(s.Id); }
        catch (Exception ex) { Message = ex.Message; }
    }

    /// <summary>Copies the entry and opens the editor on the copy; nothing is saved if you cancel.</summary>
    [RelayCommand]
    private Task DuplicateAsync()
    {
        if (_store is null || Selected?.Entry is not { } e || e.Kind == EntryKind.Folder) return Task.CompletedTask;
        var copy = EntryStore.Clone(e);
        copy.Id = Guid.NewGuid();
        copy.Revision = 0;
        copy.UpdatedBy = null;
        copy.Name += " (copy)";
        return EditAndSaveAsync(copy);
    }

    // ---------------- entry menu actions ----------------

    public IEnumerable<Entry> Folders => _store?.Entries.Where(x => x.Kind == EntryKind.Folder) ?? [];
    public IEnumerable<Entry> Credentials => (_store?.Entries ?? []).Where(x => x.Kind == EntryKind.Credential).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>"Datacenter / Hyper-V" style path for the Move to menu.</summary>
    public string FolderPath(Entry folder)
    {
        var parts = new List<string>();
        for (Entry? f = folder; f is not null && parts.Count < 32; f = f.ParentId is { } p ? _store?.Get(p) : null) parts.Insert(0, f.Name);
        return string.Join(" / ", parts);
    }

    // ---------------- tree: copy and paste, expand and collapse ----------------

    private Guid? _copiedId;

    /// <summary>The entry (or folder) waiting to be pasted, if it still exists.</summary>
    public Entry? CopiedEntry => _copiedId is { } id ? _store?.Get(id) : null;

    public void CopyEntry(Entry e)
    {
        _copiedId = e.Id;
        Message = $"Copied {e.Name}. Right-click a folder or empty space and choose Paste.";
    }

    /// <summary>Pastes a copy of the copied entry into <paramref name="folder"/>; a folder comes with everything in it.</summary>
    public async Task PasteAsync(Guid? folder)
    {
        if (_store is null || CopiedEntry is not { } src) return;
        if (!_store.IsOnline) { Message = "Offline: entries can't be added until the database is back"; return; }
        for (var f = folder; f is { } fid; f = _store.Get(fid)?.ParentId)
            if (fid == src.Id) { Message = "A folder can't be pasted inside itself"; return; }
        try
        {
            var n = await PasteTreeAsync(src, folder, top: true);
            Message = n == 1 ? $"Pasted {src.Name}" : $"Pasted {src.Name} ({n} entries)";
            if (folder is { } t && _nodes.TryGetValue(t, out var node)) node.IsExpanded = true;
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    private async Task<int> PasteTreeAsync(Entry src, Guid? parent, bool top)
    {
        var c = EntryStore.Clone(src);
        c.Id = Guid.NewGuid();
        c.Revision = 0;
        c.ParentId = parent;
        c.UpdatedBy = null; c.CreatedAt = null; c.CreatedBy = null;
        if (top && _store!.Entries.Any(x => x.ParentId == parent && x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) c.Name += " (copy)";
        await _store!.SaveAsync(c);
        var count = 1;
        if (src.Kind == EntryKind.Folder)
            foreach (var child in _store.Entries.Where(x => x.ParentId == src.Id).ToList())
                count += await PasteTreeAsync(child, c.Id, top: false);
        return count;
    }

    /// <summary>Expands or collapses every folder, or only <paramref name="root"/> and the folders inside it.</summary>
    public void SetExpanded(Guid? root, bool expanded)
    {
        bool Under(Entry e)
        {
            if (root is null) return true;
            for (Guid? f = e.Id; f is { } fid; f = _store?.Get(fid)?.ParentId)
                if (fid == root) return true;
            return false;
        }
        foreach (var n in _nodes.Values.Where(n => n.Entry.Kind == EntryKind.Folder && Under(n.Entry)))
            n.IsExpanded = expanded;
    }

    public async Task RefreshAsync()
    {
        if (_store is null) return;
        try { await _store.PullAsync(); Message = "Refreshed"; }
        catch (Exception ex) { Message = ex.Message; }
    }

    public async Task SaveChangeAsync(Entry original, Action<Entry> change, string done)
    {
        if (_store is null) return;
        if (!_store.IsOnline) { Message = "Offline: entries can't be changed until the database is back"; return; }
        var copy = EntryStore.Clone(original);
        change(copy);
        try { await _store.SaveAsync(copy); Message = done; }
        catch (Exception ex) { Message = ex.Message; }
    }

    /// <summary>Favorites are per user (settings), shown in the icon rail's Favorites list.</summary>
    public bool IsFavorite(Entry e) => _settings.Favorites.Contains(e.Id);

    public Task SetLoginAsync(Entry e, LoginSource source, Guid? credentialId = null) =>
        SaveChangeAsync(e, x => { x.Login = source; x.CredentialId = credentialId; if (source != LoginSource.Embedded) x.Credential = null; },
            source switch
            {
                LoginSource.Saved => $"Linked {_store?.Get(credentialId ?? Guid.Empty)?.Name} to {e.Name}",
                LoginSource.WindowsLogin => $"{e.Name} signs in with your Windows login",
                _ => $"Removed the login from {e.Name}",
            });

    public void CopyHost(Entry e)
    {
        var host = e.Kind == EntryKind.Web && e.Endpoints.Count == 0 ? WebSettings.NormalizeUrl(e.Web?.Url) : e.Endpoints.FirstOrDefault()?.ToString();
        if (string.IsNullOrEmpty(host)) { Message = $"{e.Name} has no host"; return; }
        CredentialClipboard.CopyText(host, secret: false);
        Message = "Host copied";
    }

    /// <summary>Opens an RDP entry in Windows' own Remote Desktop client. The password is handed over
    /// through Windows Credential Manager (TERMSRV/host) and removed again shortly after.</summary>
    public async Task OpenInMstscAsync(Entry e)
    {
        if (e.Endpoints.FirstOrDefault() is not { } ep) { Message = $"{e.Name} has no host"; return; }
        if (ResolveForConnect(e) is not { } cred) return;
        try
        {
            var file = RdpFiles.Write(e, ep, cred);
            await RdpFiles.LaunchMstscAsync(file, ep, cred);
            Message = $"Opened {e.Name} in Remote Desktop Connection";
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    public void SaveRdpFile(Entry e, string path)
    {
        if (e.Endpoints.FirstOrDefault() is not { } ep) { Message = $"{e.Name} has no host"; return; }
        var cred = _store is null || _protector is null ? RemoteMan.Protocols.ResolvedCredential.None : Secrets.Resolve(e, _store, _protector);
        System.IO.File.WriteAllText(path, RdpFiles.Build(e, ep, cred));
        Message = $"Saved {System.IO.Path.GetFileName(path)} (without the password)";
    }

    public void Ping(Entry e)
    {
        var host = e.Endpoints.FirstOrDefault()?.Host ?? (Uri.TryCreate(WebSettings.NormalizeUrl(e.Web?.Url), UriKind.Absolute, out var u) ? u.Host : null);
        if (host is null) { Message = $"{e.Name} has no host"; return; }
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = true };
        psi.ArgumentList.Add("/k"); psi.ArgumentList.Add("ping"); psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(host);
        System.Diagnostics.Process.Start(psi);
    }

    public async Task OpenInWinScpAsync(Entry e)
    {
        if (e.Endpoints.FirstOrDefault() is not { } ep) { Message = $"{e.Name} has no host"; return; }
        if (ResolveForConnect(e) is not { } cred) return;
        if (!WinScp.IsInstalled)
        {
            var pick = Choose?.Invoke("WinSCP isn't installed", "Browse files on this server in WinSCP? It isn't installed on this PC. RemoteMan's own Files panel (Ctrl+Shift+F in an SSH tab) works without it.",
                ["Install WinSCP", "Cancel"]) ?? 1;
            if (pick != 0) return;
            Message = "Installing WinSCP…";
            var r = await RemoteMan.Protocols.Web.WingetInstaller.InstallAsync("WinSCP.WinSCP", "https://winscp.net/eng/download.php", () => WinScp.IsInstalled);
            if (r != RemoteMan.Protocols.Web.WingetInstaller.Result.Installed)
            {
                Message = r == RemoteMan.Protocols.Web.WingetInstaller.Result.DownloadPageOpened ? "Opened the WinSCP download page" : "Couldn't install WinSCP";
                return;
            }
        }
        try { WinScp.Open(ep, cred); Message = $"Opened {e.Name} in WinSCP"; }
        catch (Exception ex) { Message = ex.Message; }
    }

    // ---------------- copy login details ----------------

    /// <summary>The credential for the selected entry: its own for credential entries, else the linked one.</summary>
    private RemoteMan.Protocols.ResolvedCredential? SelectedCredential()
    {
        if (_store is null || _protector is null || Selected?.Entry is not { } e) return null;
        var c = Secrets.Resolve(e, _store, _protector);
        if (c.UserName is null && c.Password is null) { Message = c.AskPassword ? $"{e.Name} asks for the password when connecting" : $"{e.Name} has no login"; return null; }
        return c;
    }

    [RelayCommand]
    private void CopyUserName()
    {
        if (SelectedCredential() is not { } c) return;
        var user = c.Domain is { Length: > 0 } d ? $"{d}\\{c.UserName}" : c.UserName ?? "";
        CredentialClipboard.CopyText(user, secret: false);
        Message = "User name copied";
    }

    [RelayCommand]
    private void CopyPassword()
    {
        if (SelectedCredential() is not { Password: { } pw }) { Message = "No password stored"; return; }
        CredentialClipboard.CopyText(pw, secret: true);
        Message = $"Password copied, clears in {CredentialClipboard.ClearAfter.TotalSeconds:0} s";
    }

    private void CopyField(CustomField f)
    {
        if (f.Value is null || _protector is null) return;
        try
        {
            if (f.Secret) { CredentialClipboard.CopyText(_protector.Unprotect(f.Value), secret: true); Message = $"{f.Name} copied, clears in {CredentialClipboard.ClearAfter.TotalSeconds:0} s"; }
            else { CredentialClipboard.CopyText(f.Value, secret: false); Message = $"{f.Name} copied"; }
        }
        catch (Exception ex) { Message = $"Couldn't read {f.Name}: {ex.Message}"; }
    }

    /// <summary>True when the entry's login (own, saved or from the folder) has a two-factor secret.</summary>
    public bool HasTotp(Entry e)
    {
        if (_store is null) return false;
        if (e.Kind == EntryKind.Credential) return e.Credential?.ProtectedTotpSecret is not null;
        var (_, holder) = _store.ResolveLogin(e);
        return holder?.Credential?.ProtectedTotpSecret is not null;
    }

    [RelayCommand]
    private void CopyTotp()
    {
        if (_store is null || _protector is null || Selected?.Entry is not { } e) return;
        var secret = Secrets.Resolve(e, _store, _protector).TotpSecret;
        if (secret is null || !RemoteMan.Core.Security.Totp.IsValid(secret)) { Message = $"{e.Name} has no 2FA secret"; return; }
        CredentialClipboard.CopyText(RemoteMan.Core.Security.Totp.Code(secret), secret: true);
        Message = $"2FA code copied, valid for {RemoteMan.Core.Security.Totp.SecondsLeft(secret)} s";
    }

    [RelayCommand]
    private void CopyBoth()
    {
        if (SelectedCredential() is not { } c) return;
        var user = c.Domain is { Length: > 0 } d ? $"{d}\\{c.UserName}" : c.UserName ?? "";
        CredentialClipboard.CopyBoth(user, c.Password ?? "");
        Message = "Paste once for the user name, again for the password";
    }

    // ---------------- snippets ----------------

    /// <summary>All snippets, sorted by group then name. The SSH tabs' Snippets panels show these.</summary>
    public ObservableCollection<Entry> Snippets { get; } = new();
    public event EventHandler? SnippetsChanged;

    private void RefreshSnippets()
    {
        if (_store is null) return;
        var list = _store.Entries.Where(e => e.Kind == EntryKind.Snippet)
            .OrderBy(e => e.Snippet?.Group ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Snippets.Clear();
        foreach (var e in list) Snippets.Add(e);
        SnippetsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<SshSessionViewModel> SshSessions => Sessions.OfType<SshSessionViewModel>();

    /// <summary>Sends a snippet to each session, with {host}, {user} and {name} filled in for that session.</summary>
    public int SendSnippet(Entry snippet, IEnumerable<SshSessionViewModel> targets)
    {
        if (snippet.Snippet is not { } sn) return 0;
        var n = 0;
        foreach (var t in targets)
        {
            var text = sn.Expand(t.Endpoint.Host, t.Credential.UserName, t.Entry.Name).Replace("\r\n", "\n");
            // Type only (the default): never end on a newline, so the last line waits for Enter.
            text = sn.PressEnter ? text.TrimEnd('\n') + "\n" : text.TrimEnd('\n');
            // Terminals expect CR for Enter.
            if (t.Send(text.Replace('\n', '\r'))) n++;
        }
        return n;
    }

    public async Task<bool> SaveSnippetAsync(Entry snippet)
    {
        if (_store is null) return false;
        try { await _store.SaveAsync(snippet); return true; }
        catch (Exception ex) { Message = ex is DataSourceOfflineException ? "Offline: snippets can't be changed until the database is back" : ex.Message; return false; }
    }

    public async Task DeleteSnippetAsync(Entry snippet)
    {
        if (_store is null) return;
        try { await _store.DeleteAsync(snippet.Id); }
        catch (Exception ex) { Message = ex.Message; }
    }

    /// <summary>Adds built-in snippets that aren't there yet (matched by group and name). Returns how many were added.</summary>
    public async Task<int> AddBuiltInSnippetsAsync()
    {
        if (_store is null || !_store.IsOnline) return 0;
        var have = _store.Entries.Where(e => e.Kind == EntryKind.Snippet)
            .Select(e => $"{e.Snippet?.Group}|{e.Name}".ToLowerInvariant()).ToHashSet();
        var added = 0;
        foreach (var d in BuiltInSnippets.All)
        {
            if (have.Contains($"{d.Group}|{d.Name}".ToLowerInvariant())) continue;
            try { await _store.SaveAsync(BuiltInSnippets.ToEntry(d)); added++; }
            catch (Exception ex) { Message = ex.Message; break; }
        }
        return added;
    }

    /// <summary>First run on a data source: add the starter library once (deleting them later sticks).</summary>
    private async Task SeedSnippetsAsync(string sourceName)
    {
        if (_store is null || !_store.IsOnline || _settings.SnippetsSeeded.Contains(sourceName)) return;
        if (!_store.Entries.Any(e => e.Kind == EntryKind.Snippet)) await AddBuiltInSnippetsAsync();
        _settings.SnippetsSeeded.Add(sourceName);
        _settings.Save();
    }

    // ---------------- import, export, backup ----------------

    public AppSettings Settings => _settings;

    /// <summary>Automatic backup: checks at start, then hourly, and backs up once a day.</summary>
    private async Task BackupLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            if (_store is null || !BackupService.IsDue(_settings.Backup)) continue;
            if (BackupService.LoadPassword() is not { } pw) { Message = "Automatic backup is on but has no password: set one in File > Export and backup"; continue; }
            try
            {
                var file = await BackupService.RunAsync(_settings, _store, _protector, pw);
                Message = $"Backed up to {System.IO.Path.GetFileName(file)}";
            }
            catch (Exception ex) { Message = $"Automatic backup failed: {ex.Message}"; }
        }
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>Saves planned entries one by one; returns how many were saved.</summary>
    public async Task<int> SaveImportedAsync(IReadOnlyList<Entry> entries, IProgress<int>? progress = null)
    {
        if (_store is null) return 0;
        var n = 0;
        foreach (var e in entries)
        {
            await _store.SaveAsync(e);
            progress?.Report(++n);
        }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        FreezeOpenSessions();
        foreach (var s in Sessions.ToList()) (s as IDisposable)?.Dispose();
        await StopZabbixAsync();
        StopOxidized();
        if (_store is not null) await _store.DisposeAsync();
    }
}
