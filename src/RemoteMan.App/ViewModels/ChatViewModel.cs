using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Chat;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// The chat drawer: a contact list (groups, then people) and one open conversation.
/// Links to entries are written as <see cref="ChatLink"/> tokens; the composer shows them as
/// "#Name" and swaps in the token on send.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private ChatHub? _hub;
    private EntryStore? _store;
    /// <summary>"#Label" in the draft → the link it stands for.</summary>
    private readonly List<ChatLink> _draftLinks = new();

    public ChatViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<ChatContact> Groups { get; } = new();
    public ObservableCollection<ChatContact> People { get; } = new();
    public ObservableCollection<ChatMessageItem> Messages { get; } = new();
    public ObservableCollection<ChatPickItem> PickItems { get; } = new();
    public ObservableCollection<ChatToast> Toasts { get; } = new();

    /// <summary>Chat works on this data source (a shared database).</summary>
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsListShown))] private bool _isOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsListShown), nameof(IsRoomShown))] private ChatContact? _current;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))] private int _totalUnread;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private bool _isPickerOpen;
    [ObservableProperty] private ChatPickItem? _pickSelected;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _headerText = "";
    /// <summary>"Choose who gets fw01": shown above the list when an entry was sent to chat with no conversation open.</summary>
    [ObservableProperty] private string _pendingShareText = "";

    public bool IsListShown => Current is null;
    public bool IsRoomShown => Current is not null;
    public bool HasUnread => TotalUnread > 0;
    public string UnreadText => TotalUnread > 99 ? "99+" : TotalUnread.ToString();
    public string Me => _hub?.Me ?? "";

    /// <summary>The window focuses the message box (after opening a conversation or adding a link).</summary>
    public event EventHandler? FocusComposer;
    /// <summary>The window scrolls the messages to the end.</summary>
    public event EventHandler? ScrollToEnd;
    /// <summary>Asks the window for a new group's name and members; null when cancelled.</summary>
    public Func<IReadOnlyList<string>, (string Name, IReadOnlyList<string> Members)?>? NewGroupDialog { get; set; }

    // ---------------- lifecycle ----------------

    public async Task StartAsync(EntryStore store, ChatHub hub)
    {
        _store = store;
        _hub = hub;
        hub.Changed += (_, _) => OnUi(Refresh);
        hub.MessageArrived += (_, m) => OnUi(() => Arrived(m));
        await hub.StartAsync();
        IsAvailable = true;
        Refresh();
    }

    public async Task StopAsync()
    {
        IsAvailable = false;
        if (_hub is not null) await _hub.DisposeAsync();
        _hub = null;
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    partial void OnIsOpenChanged(bool value)
    {
        _main.ChatOpenChanged(value);
        if (value) { Toasts.Clear(); if (Current is not null) MarkRead(); }
        else IsPickerOpen = false;
    }

    partial void OnFilterChanged(string value) => RefreshContacts();

    [RelayCommand] private void Toggle() => IsOpen = !IsOpen;
    [RelayCommand] private void Close() => IsOpen = false;

    // ---------------- contacts ----------------

    /// <summary>Called when presence changes, so online dots stay current.</summary>
    public void Refresh()
    {
        if (_hub is null) return;
        RefreshContacts();
        if (Current is { } c)
        {
            // The room may have been stored (or renamed) since it was opened.
            if (_hub.Room(c.Room.Id) is { } fresh) c.Room = fresh;
            RefreshMessages();
            if (IsOpen && _main.IsWindowActive) MarkRead();
        }
        TotalUnread = _hub.TotalUnread;
    }

    private bool IsOnline(string user)
    {
        if (_store is null) return false;
        var now = DateTimeOffset.UtcNow;
        return _store.Presence.Any(p => p.IsAlive(now) && ChatRoom.SameUser(p.User, user));
    }

    private void RefreshContacts()
    {
        if (_hub is null) return;
        var me = _hub.Me;
        var f = Filter.Trim();
        bool Match(string s) => f.Length == 0 || s.Contains(f, StringComparison.OrdinalIgnoreCase);

        var groups = new List<ChatContact>();
        foreach (var r in _hub.Rooms.Where(r => r.Kind != ChatRoomKind.Direct))
        {
            var name = r.Name ?? "Group";
            if (!Match(name)) continue;
            var members = r.Kind == ChatRoomKind.Everyone ? "everyone on this database" : $"{r.Members.Count} members";
            groups.Add(Contact(r, name, r.Kind == ChatRoomKind.Everyone ? "#" : name[..1].ToUpperInvariant(), null, members));
        }

        // Everyone who has used chat or is online now, each with their direct conversation (stored or not yet).
        var users = _hub.Users.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_store is not null)
            foreach (var p in _store.Presence.Where(p => p.IsAlive(DateTimeOffset.UtcNow))) users.Add(p.User);
        users.RemoveWhere(u => ChatRoom.SameUser(u, me));
        var people = users.Where(Match)
            .Select(u => Contact(_hub.DirectWith(u), u, Initials(u), u, IsOnline(u) ? "Online" : "Offline"))
            .OrderByDescending(c => c.LastAt ?? DateTimeOffset.MinValue).ThenByDescending(c => c.IsOnline).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Sync(Groups, groups);
        Sync(People, people);
        HeaderText = $"{users.Count(IsOnline) + 1} online";
    }

    private ChatContact Contact(ChatRoom room, string title, string initials, string? user, string fallback)
    {
        var last = _hub!.LastMessage(room.Id);
        var preview = last is null ? fallback
            : (last.Pending ? "Sending… " : "") + (room.Kind == ChatRoomKind.Direct || ChatRoom.SameUser(last.Sender, _hub.Me) ? (ChatRoom.SameUser(last.Sender, _hub.Me) ? "You: " : "") : Short(last.Sender) + ": ")
              + ChatFormat.Preview(last.Body);
        return new ChatContact(room, title, initials, user is null ? "" : PresenceChip.ColorFor(user))
        {
            Preview = preview,
            Unread = _hub.Unread(room.Id),
            IsOnline = user is not null && IsOnline(user),
            IsPerson = user is not null,
            LastAt = last?.SentAt,
            TimeText = last is null ? "" : TimeText(last.SentAt),
        };
    }

    /// <summary>Updates the list in place so the selection and scroll position survive.</summary>
    private static void Sync(ObservableCollection<ChatContact> target, List<ChatContact> fresh)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            var at = -1;
            for (var j = i; j < target.Count; j++) if (target[j].Room.Id == fresh[i].Room.Id) { at = j; break; }
            if (at < 0) { target.Insert(i, fresh[i]); continue; }
            if (at != i) target.Move(at, i);
            target[i].CopyFrom(fresh[i]);
        }
        while (target.Count > fresh.Count) target.RemoveAt(target.Count - 1);
    }

    public static string Initials(string user)
    {
        var name = user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
        var parts = name.Split(new[] { '.', ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0]))),
        };
    }

    /// <summary>DOMAIN\jonas → jonas.</summary>
    public static string Short(string user) => user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;

    private static string TimeText(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd");
        return local.ToString("d MMM");
    }

    // ---------------- conversations ----------------

    [RelayCommand]
    private void Open(ChatContact? c)
    {
        if (c is null) return;
        Current = c;
        Filter = "";
        RefreshMessages();
        MarkRead();
        IsOpen = true;
        FocusComposer?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void Back() { IsPickerOpen = false; Current = null; Messages.Clear(); RefreshContacts(); }

    /// <summary>Opens the direct conversation with someone (clicking their avatar in the title bar).</summary>
    public void OpenDirect(string user)
    {
        if (_hub is null || ChatRoom.SameUser(user, _hub.Me)) { IsOpen = true; return; }
        RefreshContacts();
        var c = People.FirstOrDefault(p => p.IsPerson && ChatRoom.SameUser(p.Title, user))
                ?? Contact(_hub.DirectWith(user), user, Initials(user), user, "");
        Open(c);
    }

    public void OpenRoom(Guid roomId)
    {
        RefreshContacts();
        var c = Groups.Concat(People).FirstOrDefault(x => x.Room.Id == roomId);
        if (c is not null) Open(c);
    }

    private void MarkRead()
    {
        if (_hub is null || Current is null) return;
        _ = _hub.MarkReadAsync(Current.Room.Id);
        Current.Unread = 0;
    }

    public void WindowActivated()
    {
        if (IsOpen && Current is not null) MarkRead();
    }

    private void RefreshMessages()
    {
        if (_hub is null || Current is null) return;
        var list = _hub.Messages(Current.Room.Id);
        var me = _hub.Me;
        var items = new List<ChatMessageItem>();
        ChatMessage? prev = null;
        foreach (var m in list)
        {
            var mine = ChatRoom.SameUser(m.Sender, me);
            var cont = prev is not null && ChatRoom.SameUser(prev.Sender, m.Sender) && m.SentAt - prev.SentAt < TimeSpan.FromMinutes(5);
            var dayBreak = prev is null || prev.SentAt.ToLocalTime().Date != m.SentAt.ToLocalTime().Date;
            items.Add(new ChatMessageItem(m, mine ? "You" : Short(m.Sender), Initials(m.Sender), PresenceChip.ColorFor(m.Sender), Blocks(m))
            {
                IsContinuation = cont && !dayBreak,
                DayText = dayBreak ? DayText(m.SentAt) : null,
                TimeText = m.Pending ? "sending…" : m.SentAt.ToLocalTime().ToString("HH:mm"),
            });
            prev = m;
        }
        // Rebuild only when something changed (keeps the scroll position while reading).
        var same = items.Count == Messages.Count && items.Zip(Messages).All(p => p.First.Message.Id == p.Second.Message.Id && p.First.Message.Pending == p.Second.Message.Pending);
        if (same) return;
        var appended = items.Count > Messages.Count && Messages.Zip(items).All(p => p.First.Message.Id == p.Second.Message.Id && p.First.Message.Pending == p.Second.Message.Pending);
        if (appended) foreach (var i in items.Skip(Messages.Count)) Messages.Add(i);
        else { Messages.Clear(); foreach (var i in items) Messages.Add(i); }
        ScrollToEnd?.Invoke(this, EventArgs.Empty);
    }

    private static string DayText(DateTimeOffset at)
    {
        var d = at.ToLocalTime().Date;
        if (d == DateTime.Today) return "Today";
        if (d == DateTime.Today.AddDays(-1)) return "Yesterday";
        return d.ToString("dddd d MMMM");
    }

    /// <summary>Lines of text with inline chips; a line that is only one entry link becomes a card.</summary>
    private List<object> Blocks(ChatMessage m)
    {
        var blocks = new List<object>();
        var text = new List<ChatInlinePart>();
        void FlushText()
        {
            while (text.Count > 0 && text[^1].Link is null && text[^1].Text == "\n") text.RemoveAt(text.Count - 1);
            if (text.Count > 0) blocks.Add(new ChatTextBlock(text.ToList()));
            text.Clear();
        }
        // Code (``` blocks and pasted config, logs or shell output) first; the rest is text with links and `inline code`.
        foreach (var seg in ChatFormat.Split(m.Body))
        {
            if (seg.Kind == ChatSegmentKind.Code)
            {
                FlushText();
                blocks.Add(new ChatCodeBlock(ChatLink.Plain(seg.Text), seg.Language, _main));
                continue;
            }
            var lines = seg.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = ChatLink.Parse(lines[i]);
                var only = parts.Where(p => p.Link is not null || p.Text.Trim().Length > 0).ToList();
                if (only.Count == 1 && only[0].Link is { Kind: ChatLink.Invite } inv)
                {
                    FlushText();
                    blocks.Add(new ChatInviteBlock(new ChatLinkItem(inv, null, this)));
                    continue;
                }
                if (only.Count == 1 && only[0].Link is { Kind: ChatLink.Entry } solo && Resolve(solo) is { Entry: not null } card)
                {
                    FlushText();
                    blocks.Add(new ChatCardBlock(card));
                    continue;
                }
                if (text.Count > 0) text.Add(new ChatInlinePart("\n", null));
                foreach (var p in parts)
                {
                    if (p.Link is not null) { text.Add(new ChatInlinePart(p.Text, Resolve(p.Link))); continue; }
                    foreach (var (t, code) in ChatFormat.Inline(p.Text)) text.Add(new ChatInlinePart(t, null, code));
                }
            }
        }
        FlushText();
        return blocks;
    }

    private ChatLinkItem Resolve(ChatLink link)
    {
        var e = link.EntryId is { } id ? _store?.Get(id) : null;
        return new ChatLinkItem(link, e, this);
    }

    // ---------------- links ----------------

    internal void OpenLink(ChatLinkItem l)
    {
        if (l.Link.Kind == ChatLink.Invite)
        {
            if (Guid.TryParse(l.Link.Target, out var inviteId)) _ = _main.JoinShareAsync(inviteId);
            return;
        }
        if (l.Entry is null) { _main.Message = l.Link.Kind is ChatLink.Entry or ChatLink.Backups ? $"{l.Label} isn't in your tree (deleted, or you can't see it)" : $"{l.Label} needs a newer RemoteMan"; return; }
        switch (l.Link.Kind)
        {
            case ChatLink.Backups:
                _main.RevealEntry(l.Entry.Id);
                _main.OpenConfigBackupsCommand.Execute(null);
                break;
            default:
                if (l.Entry.Kind == EntryKind.Snippet)
                {
                    try { Clipboard.SetText(l.Entry.Snippet?.Command ?? ""); _main.Message = $"Copied the {l.Entry.Name} command"; }
                    catch (Exception) { _main.Message = "Could not use the clipboard"; }
                    return;
                }
                _main.RevealEntry(l.Entry.Id);
                if (l.Entry.Kind is not (EntryKind.Folder or EntryKind.Credential)) _main.ShowOverview();
                break;
        }
    }

    internal void ConnectLink(ChatLinkItem l)
    {
        if (l.Entry is not { IsConnectable: true } e) return;
        _main.RevealEntry(e.Id);
        if (_main.FindNode(e.Id) is { } node) _main.ConnectCommand.Execute(node);
    }

    /// <summary>Posts a shared-session invite to the invitee as a direct message (a card with Join).</summary>
    public async Task PostShareInviteAsync(string user, RemoteMan.Core.Sharing.ShareInvite invite)
    {
        if (_hub is null) return;
        var what = invite.Kind == RemoteMan.Core.Sharing.ShareKind.Rdp ? "RDP session" : "terminal";
        var link = new ChatLink(ChatLink.Invite, invite.Id.ToString(), $"Join my shared {what}: {invite.EntryName}");
        try { await _hub.SendAsync(_hub.DirectWith(user), link.ToToken()); }
        catch (Exception) { /* the pop-up still reaches them */ }
    }

    /// <summary>"Send in chat" on an entry, or an entry dropped on the message box.</summary>
    public void ShareEntry(Entry e)
    {
        if (_hub is null) return;
        IsOpen = true;
        AddDraftLink(new ChatLink(ChatLink.Entry, e.Id.ToString(), e.Name), replaceQuery: false);
        PendingShareText = Current is null ? $"Choose who gets {e.Name}" : "";
    }

    private void AddDraftLink(ChatLink link, bool replaceQuery)
    {
        var tag = "#" + ChatLink.Clean(link.Label);
        if (!_draftLinks.Any(l => l.Target == link.Target && l.Kind == link.Kind)) _draftLinks.Add(link);
        if (replaceQuery && QueryStart >= 0)
        {
            var end = Math.Min(Draft.Length, QueryStart + 1 + _query.Length);
            Draft = Draft[..QueryStart] + tag + " " + Draft[end..];
            CaretAfterInsert = QueryStart + tag.Length + 1;
        }
        else
        {
            var sep = Draft.Length == 0 || char.IsWhiteSpace(Draft[^1]) ? "" : " ";
            Draft = Draft + sep + tag + " ";
            CaretAfterInsert = Draft.Length;
        }
        IsPickerOpen = false;
        QueryStart = -1;
        FocusComposer?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where the window puts the caret after a link was inserted.</summary>
    public int CaretAfterInsert { get; private set; }
    /// <summary>Index of the '#' being typed, or -1.</summary>
    public int QueryStart { get; private set; } = -1;
    private string _query = "";

    /// <summary>The window reports the text before the caret; a "#word" there opens the picker.</summary>
    public void UpdateQuery(string draft, int caret)
    {
        QueryStart = -1;
        caret = Math.Clamp(caret, 0, draft.Length);
        var hash = draft.LastIndexOf('#', Math.Max(0, caret - 1));
        if (caret == 0 || hash < 0 || (hash > 0 && !char.IsWhiteSpace(draft[hash - 1]))) { IsPickerOpen = false; return; }
        var q = draft[(hash + 1)..caret];
        if (q.Any(char.IsWhiteSpace) || q.Length > 40) { IsPickerOpen = false; return; }
        QueryStart = hash;
        _query = q;
        FillPicker(q);
    }

    private void FillPicker(string q)
    {
        PickItems.Clear();
        if (_store is null) { IsPickerOpen = false; return; }
        var matches = _store.Entries
            .Where(e => e.Kind != EntryKind.Template && (q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || e.Endpoints.Any(p => p.Host.StartsWith(q, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(e => e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(e => KindOrder(e.Kind)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8).ToList();
        foreach (var e in matches) PickItems.Add(new ChatPickItem(e, FolderPath(e), ChatLinkItem.TagFor(e.Kind), false));
        // Config backups of the best match, for devices Oxidized knows.
        if (_main.OxidizedActive && matches.FirstOrDefault(e => e.IsConnectable) is { } dev && q.Length > 0)
            PickItems.Add(new ChatPickItem(dev, "Config backups", "OX", true));
        PickSelected = PickItems.FirstOrDefault();
        IsPickerOpen = PickItems.Count > 0;
    }

    private static int KindOrder(EntryKind k) => k switch { EntryKind.Folder => 2, EntryKind.Credential => 1, EntryKind.Snippet => 3, _ => 0 };

    private string FolderPath(Entry e)
    {
        if (e.Kind == EntryKind.Snippet) return "Snippet";
        var names = new List<string>();
        for (var p = e.ParentId; p is { } id && _store?.Get(id) is { } f && names.Count < 4; p = f.ParentId) names.Insert(0, f.Name);
        return names.Count == 0 ? "" : string.Join(" › ", names);
    }

    [RelayCommand]
    public void Pick(ChatPickItem? item)
    {
        item ??= PickSelected;
        if (item is null) return;
        var link = item.IsBackups
            ? new ChatLink(ChatLink.Backups, item.Entry.Id.ToString(), item.Entry.Name + " config backups")
            : new ChatLink(ChatLink.Entry, item.Entry.Id.ToString(), item.Entry.Name);
        AddDraftLink(link, replaceQuery: true);
    }

    public void MovePick(int delta)
    {
        if (PickItems.Count == 0) return;
        var i = PickSelected is null ? -1 : PickItems.IndexOf(PickSelected);
        PickSelected = PickItems[(i + delta + PickItems.Count) % PickItems.Count];
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (_hub is null || Current is null) return;
        var text = Draft.Trim();
        if (text.Length == 0) return;
        // Longest labels first, so "#fw01-backup" isn't eaten by "#fw01".
        foreach (var l in _draftLinks.OrderByDescending(l => l.Label.Length))
        {
            var tag = "#" + ChatLink.Clean(l.Label);
            var at = text.IndexOf(tag, StringComparison.Ordinal);
            if (at >= 0) text = text[..at] + l.ToToken() + text[(at + tag.Length)..];
        }
        Draft = "";
        _draftLinks.Clear();
        IsPickerOpen = false;
        PendingShareText = "";
        try { await _hub.SendAsync(Current.Room, text); }
        catch (Exception ex) { _main.Message = $"Could not send: {ex.Message}"; }
        if (_hub.HasPending) _main.Message = "Database unreachable: the message is sent when it's back";
    }

    [RelayCommand]
    private async Task NewGroupAsync()
    {
        if (_hub is null || NewGroupDialog is null) return;
        var known = _hub.Users.ToList();
        if (_store is not null)
            foreach (var p in _store.Presence.Where(p => p.IsAlive(DateTimeOffset.UtcNow)))
                if (!known.Contains(p.User, StringComparer.OrdinalIgnoreCase) && !ChatRoom.SameUser(p.User, _hub.Me)) known.Add(p.User);
        if (NewGroupDialog(known.Order(StringComparer.OrdinalIgnoreCase).ToList()) is not { } g) return;
        try
        {
            var room = await _hub.CreateGroupAsync(g.Name, g.Members);
            OpenRoom(room.Id);
        }
        catch (Exception ex) { _main.Message = $"Could not make the group: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task LeaveGroupAsync()
    {
        if (_hub is null || Current is not { Room.Kind: ChatRoomKind.Group } c) return;
        if (_main.Confirm?.Invoke($"Leave {c.Title}?", "You stop getting its messages. The others keep the group.", "Leave") != true) return;
        try { await _hub.LeaveGroupAsync(c.Room.Id); Back(); }
        catch (Exception ex) { _main.Message = $"Could not leave the group: {ex.Message}"; }
    }

    // ---------------- notifications ----------------

    private void Arrived(ChatMessage m)
    {
        if (_hub?.Room(m.RoomId) is not { } room) return;
        var looking = IsOpen && Current?.Room.Id == m.RoomId && _main.IsWindowActive;
        if (looking) return;
        // Pop up for direct messages, groups, and Everyone messages that name you; the rest is the badge.
        var mentioned = m.Body.Contains("@" + Short(_hub.Me), StringComparison.OrdinalIgnoreCase);
        if (room.Kind == ChatRoomKind.Everyone && !mentioned) return;
        var where = room.Kind switch { ChatRoomKind.Direct => "direct message", ChatRoomKind.Group => room.Name ?? "group", _ => "mentioned you in Everyone" };
        var t = new ChatToast(m.RoomId, Short(m.Sender), Initials(m.Sender), PresenceChip.ColorFor(m.Sender), where, ChatFormat.Preview(m.Body));
        Toasts.Insert(0, t);
        while (Toasts.Count > 3) Toasts.RemoveAt(Toasts.Count - 1);
        _main.ChatToastShown();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
        timer.Start();
    }

    [RelayCommand]
    private void OpenToast(ChatToast? t)
    {
        if (t is null) return;
        Toasts.Clear();
        IsOpen = true;
        OpenRoom(t.RoomId);
    }

    [RelayCommand] private void DismissToast(ChatToast? t) { if (t is not null) Toasts.Remove(t); }
}

// ---------------- items ----------------

public sealed partial class ChatContact(ChatRoom room, string title, string initials, string color) : ObservableObject
{
    public ChatRoom Room { get; set; } = room;
    public string Title { get; } = title;
    public string ShortTitle => ChatViewModel.Short(Title);
    public string Initials { get; } = initials;
    public string Color { get; } = color;
    public bool IsGroup => Room.Kind != ChatRoomKind.Direct;
    public bool IsEveryone => Room.Kind == ChatRoomKind.Everyone;
    public bool IsPerson { get; init; }
    public DateTimeOffset? LastAt { get; set; }
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUnread))] private int _unread;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _timeText = "";
    public bool HasUnread => Unread > 0;
    public string SubTitle => IsPerson ? (IsOnline ? "Online" : "Offline") : Room.Kind == ChatRoomKind.Everyone ? "Everyone on this database" : $"{Room.Members.Count} members";

    public void CopyFrom(ChatContact o)
    {
        Room = o.Room;
        LastAt = o.LastAt;
        Preview = o.Preview;
        Unread = o.Unread;
        IsOnline = o.IsOnline;
        TimeText = o.TimeText;
        OnPropertyChanged(nameof(SubTitle));
    }
}

public sealed class ChatMessageItem(ChatMessage message, string sender, string initials, string color, IReadOnlyList<object> blocks)
{
    public ChatMessage Message { get; } = message;
    public string Sender { get; } = sender;
    public string Initials { get; } = initials;
    public string Color { get; } = color;
    public IReadOnlyList<object> Blocks { get; } = blocks;
    public bool IsContinuation { get; init; }
    public string? DayText { get; init; }
    public bool HasDay => DayText is not null;
    public string TimeText { get; init; } = "";
    public bool IsPending => Message.Pending;
    public bool IsUnreadable => Message.Unreadable;
    public string FullTime => Message.SentAt.ToLocalTime().ToString("f");
    /// <summary>The whole message as plain text (links as names), for Copy message.</summary>
    public string PlainText => ChatLink.Plain(Message.Body);
}

/// <summary>Text with inline link chips (built into a TextBlock by Views.ChatInlines).</summary>
public sealed record ChatTextBlock(IReadOnlyList<ChatInlinePart> Parts);
public sealed record ChatInlinePart(string Text, ChatLinkItem? Link, bool IsCode = false);

/// <summary>Code, config, logs or shell output: monospace, a few lines high until "Show all", with Copy.</summary>
public sealed partial class ChatCodeBlock : ObservableObject
{
    /// <summary>Lines shown before "Show all".</summary>
    public const int CollapsedLines = 10;
    private readonly MainViewModel _main;

    public ChatCodeBlock(string text, string? language, MainViewModel main)
    {
        Text = text;
        Language = language;
        _main = main;
        LineCount = text.Count(c => c == '\n') + 1;
    }

    public string Text { get; }
    public string? Language { get; }
    public int LineCount { get; }
    public string Title => (Language is { Length: > 0 } l ? l : "Code") + $" · {LineCount} line{(LineCount == 1 ? "" : "s")}";
    public bool IsLong => LineCount > CollapsedLines;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToggleText))] private bool _isExpanded;
    [ObservableProperty] private string _copyText = "Copy";
    public string ToggleText => IsExpanded ? "Show less" : $"Show all {LineCount} lines";

    [RelayCommand] private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private async Task CopyAsync()
    {
        try { Clipboard.SetText(Text); CopyText = "Copied"; }
        catch (Exception) { _main.Message = "Could not use the clipboard"; return; }
        await Task.Delay(1500);
        CopyText = "Copy";
    }
}
/// <summary>A link on a line of its own: a card with the entry's details and buttons.</summary>
public sealed record ChatCardBlock(ChatLinkItem Link);
/// <summary>A shared-session invite on a line of its own: a card with Join.</summary>
public sealed record ChatInviteBlock(ChatLinkItem Link);

public sealed class ChatLinkItem
{
    private readonly ChatViewModel _chat;

    public ChatLinkItem(ChatLink link, Entry? entry, ChatViewModel chat)
    {
        Link = link;
        Entry = entry;
        _chat = chat;
        OpenCommand = new RelayCommand(() => _chat.OpenLink(this));
        ConnectCommand = new RelayCommand(() => _chat.ConnectLink(this));
    }

    public ChatLink Link { get; }
    public Entry? Entry { get; }
    /// <summary>The entry's current name (it may have been renamed since), else the name it had when sent.</summary>
    public string Label => Link.Kind == ChatLink.Backups && Entry is not null ? $"{Entry.Name} config backups" : Entry?.Name ?? Link.Label;
    public bool Exists => Entry is not null;
    public string Tag => Link.Kind == ChatLink.Backups ? "OX" : Entry is null ? "" : TagFor(Entry.Kind);
    public bool CanConnect => Entry is { IsConnectable: true };
    public string Detail
    {
        get
        {
            if (Entry is null) return "Not in your tree";
            if (Entry.Kind == EntryKind.Credential) return "Credential" + (Entry.Credential?.UserName is { Length: > 0 } u ? $" · {u}" : "");
            if (Entry.Kind == EntryKind.Folder) return "Folder";
            if (Entry.Kind == EntryKind.Snippet) return Entry.Snippet?.Command ?? "Snippet";
            var eps = Entry.Endpoints.Select(p => p.Port > 0 ? $"{p.Host}:{p.Port}" : p.Host).ToList();
            var proto = EntryNode.KindTagFor(Entry.Kind);
            if (Entry.Web?.Url is { Length: > 0 } url && eps.Count == 0) return url;
            return string.Join(" · ", new[] { eps.FirstOrDefault(), eps.Count > 1 ? $"fallback {eps[1]}" : null, proto }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }
    public string ToolTip => Entry is null ? $"{Link.Label} (not in your tree)" : Entry.Kind == EntryKind.Snippet ? "Click to copy the command" : "Click to show it in the tree";
    public RelayCommand OpenCommand { get; }
    public RelayCommand ConnectCommand { get; }

    public static string TagFor(EntryKind k) => k switch
    {
        EntryKind.Folder => "DIR",
        EntryKind.Snippet => "SNIP",
        _ => EntryNode.KindTagFor(k),
    };
}

public sealed record ChatPickItem(Entry Entry, string Path, string Tag, bool IsBackups)
{
    public string Name => IsBackups ? $"{Entry.Name} config backups" : Entry.Name;
}

public sealed record ChatToast(Guid RoomId, string Sender, string Initials, string Color, string Where, string Text);
