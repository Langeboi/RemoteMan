using System.Data.Common;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Sharing;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using RemoteMan.Protocols.Rdp;

namespace RemoteMan.App.ViewModels;

/// <summary>What the invite dialog returns.</summary>
public sealed record ShareInviteChoice(IReadOnlyList<string> Users, string? StartsWithControl, bool CanAskForControl, bool PostInChat);

/// <summary>
/// Shared sessions: one person opens an RDP or terminal session and invites teammates to watch it
/// live; one person has control at a time. Invites travel through the shared database (like presence);
/// the session itself goes host ↔ guest directly, or through the relay on the database server.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<SessionViewModel, HostedShare> _hosted = new();
    private readonly Dictionary<Guid, Notice> _inviteNotices = new();
    private readonly HashSet<Guid> _seenInvites = new();
    private IReadOnlyList<ShareInvite> _invites = [];
    private DispatcherTimer? _shareTimer;
    private int _checkingInvites;

    /// <summary>Set by the window: asks who to invite (online teammates, entry name). Null = cancelled.</summary>
    public Func<IReadOnlyList<string>, string, bool, ShareInviteChoice?>? PickInvitees { get; set; }

    public bool CanShareSessions => IsSharedSource && _store?.Source.SupportsSharing == true;

    private void StartSharingWatch(EntryStore store)
    {
        if (!store.Source.SupportsSharing) return;
        store.SharesChanged += (_, _) => OnUi(() => _ = CheckInvitesAsync());
        // A slow safety net in case a signal is missed (and to expire old pop-ups).
        _shareTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => _ = CheckInvitesAsync(),
            System.Windows.Application.Current.Dispatcher);
        _shareTimer.Start();
        _ = CheckInvitesAsync();
    }

    /// <summary>The data source is closing: stop listening for invites and take their pop-ups away.</summary>
    private void StopSharingWatch()
    {
        _shareTimer?.Stop();
        _shareTimer = null;
        foreach (var n in _inviteNotices.Values) Notices.Remove(n);
        _inviteNotices.Clear();
        _seenInvites.Clear();
        _invites = [];
        HasNotices = Notices.Count > 0;
    }

    private bool SharingReady()
    {
        if (CanShareSessions) return true;
        Message = !IsSharedSource
            ? "Shared sessions need a shared database (PostgreSQL, SQL Server or MySQL), so teammates can get your invite."
            : "Shared sessions aren't set up on this database yet. Someone with edit rights has to open it once with this version.";
        return false;
    }

    // ---------------- hosting ----------------

    /// <summary>Entry menu > Shared session…: connects (or uses the open tab), then invites.</summary>
    public async Task ShareEntryAsync(EntryNode node)
    {
        if (!SharingReady() || node.Entry is not { IsConnectable: true } e) return;
        if (e.Kind is not (EntryKind.Rdp or EntryKind.Ssh or EntryKind.Telnet or EntryKind.Serial))
        {
            Message = "Only RDP, SSH, Telnet and serial sessions can be shared.";
            return;
        }
        var open = OpenSessionFor(e.Id);
        if (open is null)
        {
            await ConnectEntryAsync(e);
            open = OpenSessionFor(e.Id);
            if (open is null) return;   // connecting failed or was cancelled; the notice says why
        }
        await ShareSessionAsync(open);
    }

    private SessionViewModel? OpenSessionFor(Guid entryId) =>
        Sessions.Concat(_windowSessions).LastOrDefault(s => s.Entry.Id == entryId && s.CanShare && s is not SharedSessionViewModel);

    /// <summary>Tab menu > Share this session…, and Invite… on the share bar.</summary>
    [RelayCommand]
    public async Task ShareSessionAsync(SessionViewModel? s)
    {
        if (s is null || !s.CanShare || s is SharedSessionViewModel || _store is not { } store || !SharingReady()) return;
        var hosting = _hosted.TryGetValue(s, out var existing);
        var online = OnlineTeammates();
        var choice = PickInvitees?.Invoke(online, s.Entry.Name, Chat.IsAvailable);
        if (choice is null || choice.Users.Count == 0) return;
        try
        {
            var h = hosting ? existing! : await HostAsync(s, store, choice.CanAskForControl);
            await InviteAsync(h, store, choice);
        }
        catch (Exception ex)
        {
            Notify(new ConnectionProblem("Couldn't start sharing", ShareError(ex), s is RdpSessionViewModel ? "Sharing the RDP screen uses Windows Desktop Sharing. Use Details to copy the error and send it over." : "Check that Windows Firewall allows RemoteMan, then try again.", ex.ToString()), s.Entry);
        }
    }

    private IReadOnlyList<string> OnlineTeammates()
    {
        if (_store is not { } store) return [];
        var now = DateTimeOffset.UtcNow;
        return store.Presence.Where(p => p.IsAlive(now) && !string.Equals(p.User, store.User, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.User).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<HostedShare> HostAsync(SessionViewModel s, EntryStore store, bool canAsk)
    {
        var kind = s is RdpSessionViewModel ? ShareKind.Rdp : ShareKind.Terminal;
        var host = new ShareHost(kind, s.Entry.Name, store.User, store.Machine, canAsk);
        var h = new HostedShare(s, host);

        if (s is RdpSessionViewModel rdp)
        {
            if (!DesktopShare.IsAvailable) throw new InvalidOperationException("Windows Desktop Sharing isn't available on this PC.");
            var desk = h.Desktop = new DesktopShare();
            desk.Start(ScreenRect(rdp.ControlHandle?.Invoke() ?? IntPtr.Zero));
            host.RdpConnectionString = desk.ConnectionString;
            host.RdpPassword = desk.Password;
            host.RdpPort = desk.Port;
            host.RdpListeners = desk.Listeners;
            string Who(string id) => host.Control.Participants.FirstOrDefault(p => p.Id == id)?.Name ?? "A teammate";
            desk.ViewerConnected += id => OnUi(() => Message = $"{Who(id)} now sees your {s.Entry.Name} screen");
            // Follow the tab as the window moves; pause the picture while the tab isn't on screen.
            h.Follow = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
            {
                var hwnd = rdp.ControlHandle?.Invoke() ?? IntPtr.Zero;
                var hidden = hwnd == IntPtr.Zero || !rdp.IsActive || !IsWindowVisible(hwnd) || IsIconic(GetAncestor(hwnd, 2));
                if (!hidden) desk.SetArea(ScreenRect(hwnd));   // area first, so un-pausing never shows the whole window
                desk.SetPaused(hidden);
                host.SetPaused(hidden);
            }, System.Windows.Application.Current.Dispatcher);
            h.Follow.Start();
        }
        else if (s is SshSessionViewModel ssh)
        {
            // Late joiners first see the recent screen as text, then everything live.
            var recent = ssh.Tail.LastLines(ssh.Size.Rows);
            if (recent.Length > 0) host.SendOutput(recent.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Action<string> tap = host.SendOutput;
            Action<int, int> size = host.SetSize;
            ssh.OutputTapped += tap;
            ssh.SizeChanged += size;
            host.SetSize(ssh.Size.Cols, ssh.Size.Rows);
            host.Input += text => OnUi(() => ssh.Send(text));
            h.Cleanup += () => { ssh.OutputTapped -= tap; ssh.SizeChanged -= size; ssh.InputLocked = false; };
        }

        var bar = new ShareBarViewModel(true, host.HostId, give: host.Give, end: () => _ = StopSharingAsync(s),
            takeBack: host.TakeBack, invite: () => _ = ShareSessionAsync(s));
        h.Bar = bar;
        host.StateChanged += snap => OnUi(() => ApplyHostState(h, snap));
        host.ControlRequested += p => OnUi(() =>
        {
            bar.Asking(p);
            NotifyWithActions(new ConnectionProblem($"{p.Name} asks for control", $"Of {s.Entry.Name}. They'll drive until you take it back.", null, null, IsWarning: true),
                null, [new NoticeAction("Give control", () => host.Give(p.Id), Primary: true), new NoticeAction("Not now", () => bar.DismissRequestCommand.Execute(null))],
                TimeSpan.FromSeconds(30));
        });
        host.Joined += p => OnUi(() => Message = $"{p.Name} joined your shared session of {s.Entry.Name}");
        host.Left += p => OnUi(() => Message = $"{p.Name} left your shared session of {s.Entry.Name}");

        await host.StartAsync(RelayCandidates());
        _hosted[s] = h;
        s.Share = bar;
        bar.Apply(host.Control.Snapshot());
        s.Disposed += (_, _) => _ = StopSharingAsync(s);
        return h;
    }

    private void ApplyHostState(HostedShare h, ShareSnapshot snap)
    {
        h.Bar?.Apply(snap);
        var hostDrives = snap.ControllerId == h.Host.HostId;
        if (h.Session is SshSessionViewModel ssh) ssh.InputLocked = !hostDrives;
        h.Desktop?.SetController(hostDrives ? null : snap.ControllerId);
        var guests = snap.Participants.Count(p => !p.IsHost);
        h.Session.StatusText = $"Sharing with {guests} · {(hostDrives ? "you have" : $"{snap.Controller?.Name} has")} control" + (h.Host.Relay is { } r ? $" · relay {r}" : "");
        if (h.Session == ActiveSession) SessionStatus = h.Session.StatusText;
    }

    private async Task InviteAsync(HostedShare h, EntryStore store, ShareInviteChoice choice)
    {
        var host = h.Host;
        var names = new List<string>();
        foreach (var user in choice.Users.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var starts = string.Equals(choice.StartsWithControl, user, StringComparison.OrdinalIgnoreCase);
            host.Invite(user, starts);
            var invite = new ShareInvite
            {
                ShareId = host.ShareId, HostUser = store.User, HostMachine = store.Machine, Invitee = user,
                EntryId = h.Session.Entry.Id, EntryName = h.Session.Entry.Name, Kind = host.Kind,
                Addresses = host.Addresses, Relay = host.Relay, Ticket = host.Ticket, Thumbprint = host.Thumbprint,
                StartsWithControl = starts, CanAskForControl = choice.CanAskForControl,
            };
            await store.Source.PutShareInviteAsync(invite);
            h.Invites.Add(invite);
            names.Add(ShortName(user));
            if (choice.PostInChat) await Chat.PostShareInviteAsync(user, invite);
        }
        host.SetCanAsk(choice.CanAskForControl);
        Message = $"Invited {string.Join(", ", names)} to {h.Session.Entry.Name}" + (host.Relay is null ? " (no relay found: teammates need to reach this PC directly)" : "");
    }

    [RelayCommand]
    public async Task StopSharingAsync(SessionViewModel? s)
    {
        if (s is null || !_hosted.Remove(s, out var h)) return;
        s.Share = null;
        h.Follow?.Stop();
        h.Cleanup?.Invoke();
        try { h.Desktop?.Dispose(); } catch (Exception) { }
        await h.Host.DisposeAsync();
        if (_store is { } store)
            foreach (var i in h.Invites)
            {
                try { await store.Source.SetShareInviteStateAsync(i.Id, ShareInviteState.Ended); } catch (Exception) { }
            }
        s.StatusText = "";
        if (s == ActiveSession) SessionStatus = "";
        Message = $"Stopped sharing {s.Entry.Name}";
    }

    public bool IsHosting(SessionViewModel s) => _hosted.ContainsKey(s);

    // ---------------- invites for me ----------------

    private async Task CheckInvitesAsync()
    {
        if (_store is not { IsOnline: true } store || !store.Source.SupportsSharing) return;
        if (Interlocked.Exchange(ref _checkingInvites, 1) == 1) return;
        try
        {
            var all = _invites = await store.Source.GetShareInvitesAsync();
            var now = DateTimeOffset.UtcNow;
            foreach (var inv in all.Where(i => i.IsFor(store.User)))
            {
                if (inv.IsLive(now) && _seenInvites.Add(inv.Id)) ShowInvite(inv);
                else if (!inv.IsLive(now) && _inviteNotices.Remove(inv.Id, out var n)) Notices.Remove(n);
            }
            // The host hears when someone says not now.
            foreach (var h in _hosted.Values)
                for (var k = 0; k < h.Invites.Count; k++)
                {
                    var mine = h.Invites[k];
                    if (all.FirstOrDefault(x => x.Id == mine.Id) is { State: ShareInviteState.Declined } d && mine.State != ShareInviteState.Declined)
                    {
                        h.Invites[k] = d;
                        Message = $"{ShortName(d.Invitee)} can't join {d.EntryName} right now";
                    }
                }
        }
        catch (Exception) { /* next signal or tick tries again */ }
        finally { Interlocked.Exchange(ref _checkingInvites, 0); }
    }

    private void ShowInvite(ShareInvite inv)
    {
        var what = inv.Kind == ShareKind.Rdp ? "RDP session" : "terminal";
        var n = NotifyWithActions(
            new ConnectionProblem($"{ShortName(inv.HostUser)} is sharing {inv.EntryName}",
                $"Join to watch their {what} live{(inv.StartsWithControl ? "; you'll start with control" : "")}.",
                "You never get their password, and you don't need your own route to the server.", null, IsWarning: true),
            null,
            [new NoticeAction("Join", () => _ = JoinShareAsync(inv.Id), Primary: true), new NoticeAction("Not now", () => _ = DeclineShareAsync(inv.Id))],
            inv.ExpiresAt - DateTimeOffset.UtcNow);
        _inviteNotices[inv.Id] = n;
        ChatToastShown();
    }

    private async Task DeclineShareAsync(Guid inviteId)
    {
        _inviteNotices.Remove(inviteId);
        if (_store is { } store) { try { await store.Source.SetShareInviteStateAsync(inviteId, ShareInviteState.Declined); } catch (Exception) { } }
    }

    /// <summary>Join from the pop-up or the chat card.</summary>
    public async Task JoinShareAsync(Guid inviteId)
    {
        if (_store is not { } store) return;
        if (_inviteNotices.Remove(inviteId, out var notice)) Notices.Remove(notice);
        if (Sessions.OfType<SharedSessionViewModel>().FirstOrDefault(t => t.Invite.Id == inviteId) is { } already) { ActiveSession = already; return; }
        await CheckInvitesAsync();
        var inv = _invites.FirstOrDefault(i => i.Id == inviteId);
        if (inv is null || !inv.IsFor(store.User))
        {
            Message = "That invite isn't for you, or it's gone.";
            return;
        }
        if (inv.State == ShareInviteState.Ended || DateTimeOffset.UtcNow >= inv.ExpiresAt)
        {
            Message = $"{ShortName(inv.HostUser)} isn't sharing {inv.EntryName} anymore.";
            return;
        }
        Message = $"Joining {ShortName(inv.HostUser)}'s {inv.EntryName}…";
        ShareClient client;
        try { client = await ShareClient.ConnectAsync(inv, store.User, store.Machine, IsReadOnlyLogin(store.Source), RelayCandidates()); }
        catch (Exception ex)
        {
            Message = "";
            Notify(new ConnectionProblem("Couldn't join the shared session", ShareError(ex),
                ex is ShareUnreachableException ? "Ask whoever runs the database to install the relay (remoteman-db.sh relay install), or use the same LAN/VPN." : null,
                ex.ToString()), null);
            return;
        }
        try { await store.Source.SetShareInviteStateAsync(inv.Id, ShareInviteState.Accepted); } catch (Exception) { }

        var tab = new SharedSessionViewModel(client, inv);
        var bar = new ShareBarViewModel(false, client.MyId, give: client.Give, end: () => CloseSession(tab), ask: client.RequestControl);
        bar.Apply(client.State);
        tab.Share = bar;
        client.StateChanged += snap => OnUi(() => bar.Apply(snap));
        client.ControlRequested += p => OnUi(() => bar.Asking(p));
        var ending = false;
        client.Ended += reason => OnUi(() =>
        {
            if (ending || !Sessions.Contains(tab)) return;
            ending = true;
            CloseSession(tab);
            Notify(new ConnectionProblem("Shared session ended", reason ?? $"The link to {ShortName(inv.HostUser)} dropped.", null, null, IsWarning: true), null);
        });
        tab.Disposed += (_, _) => { ending = true; _ = client.DisposeAsync().AsTask(); };
        tab.CloseRequested += (_, _) => CloseSession(tab);
        Message = "";
        AddSession(tab);
    }

    // ---------------- helpers ----------------

    /// <summary>Where the relay may be: the setting, else the database server(s) on the relay port.</summary>
    private IEnumerable<string> RelayCandidates()
    {
        if (_settings.ShareRelay is { Length: > 0 } r) { yield return r; yield break; }
        foreach (var cs in _settings.Active.ConnectionStrings)
            foreach (var h in DatabaseHosts(cs)) yield return $"{h}:{ShareNet.RelayPort}";
    }

    /// <summary>The server names in a connection string (PostgreSQL can list several: "a:5432,b:5432").</summary>
    private static IEnumerable<string> DatabaseHosts(string connectionString)
    {
        var found = new List<string>();
        try
        {
            var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in new[] { "host", "server", "data source", "address", "addr" })
            {
                if (!b.TryGetValue(key, out var v) || v?.ToString() is not { Length: > 0 } text) continue;
                // SQL Server: "tcp:host,1433" is one host with a port.
                var items = text.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) || key == "data source" ? [text.Split(',')[0]] : text.Split(',');
                foreach (var raw in items)
                {
                    var h = raw.Trim();
                    if (h.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) h = h[4..];
                    if (h.Contains('\\')) h = h[..h.IndexOf('\\')];   // SQL Server named instance
                    if (h.Count(c => c == ':') == 1) h = h[..h.IndexOf(':')];
                    if (h.Length > 0 && !h.All(char.IsDigit) && h is not ("." or "(local)" or "localhost")) found.Add(h);   // "host,1433" is SQL Server's port
                }
                break;
            }
        }
        catch (ArgumentException) { }
        return found;
    }

    private static bool IsReadOnlyLogin(IDataSource s) => s switch
    {
        SqlDataSource sql => sql.IsReadOnly,
        RedundantDataSource r => r.Active is SqlDataSource a && a.IsReadOnly,
        _ => false,
    };

    private static string ShortName(string user) => user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;

    private static string ShareError(Exception ex) => ex switch
    {
        ShareRefusedException r => r.Message,
        ShareUnreachableException u => u.Message,
        _ => StoreError(ex),
    };

    private static System.Drawing.Rectangle ScreenRect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : System.Drawing.Rectangle.Empty;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private sealed class HostedShare(SessionViewModel session, ShareHost host)
    {
        public SessionViewModel Session { get; } = session;
        public ShareHost Host { get; } = host;
        public DesktopShare? Desktop { get; set; }
        public DispatcherTimer? Follow { get; set; }
        public ShareBarViewModel? Bar { get; set; }
        public List<ShareInvite> Invites { get; } = new();
        public Action? Cleanup { get; set; }
    }
}
