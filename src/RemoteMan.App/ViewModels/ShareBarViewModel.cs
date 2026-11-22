using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Sharing;

namespace RemoteMan.App.ViewModels;

/// <summary>One person in the share bar: initials, name, and whether they have control.</summary>
public sealed class SharePersonChip(ShareParticipant p, bool isMe, bool hasControl, bool canGive)
{
    public string Id { get; } = p.Id;
    public string Initials { get; } = p.Initials;
    public string Name { get; } = isMe ? $"{p.Name} (you)" : p.Name;
    public string Color { get; } = PresenceChip.ColorFor(p.User);
    public bool HasControl { get; } = hasControl;
    public string Role { get; } = hasControl ? "has control" : p.IsHost ? "host" : p.ReadOnly ? "watching (read-only)" : "watching";
    /// <summary>Clicking gives this person control.</summary>
    public bool CanGive { get; } = canGive;
    public string ToolTip { get; } = canGive ? $"Give control to {p.Name}" : hasControl ? $"{p.Name} has control" : p.Name;
}

/// <summary>
/// The amber bar above a shared session: who's in it, who drives, and the buttons that fit
/// (give, take back, ask, give back, invite, stop or leave). Used by the host's tab and the guests'.
/// </summary>
public sealed partial class ShareBarViewModel : ObservableObject
{
    private readonly Action<string> _give;
    private readonly Action? _takeBack, _ask, _invite;
    private readonly Action _end;
    private string _myId;
    private string? _hostId;

    public ShareBarViewModel(bool isHost, string myId, Action<string> give, Action end, Action? takeBack = null, Action? ask = null, Action? invite = null)
    {
        IsHost = isHost;
        _myId = myId;
        _give = give;
        _end = end;
        _takeBack = takeBack;
        _ask = ask;
        _invite = invite;
    }

    public bool IsHost { get; }
    public bool IsGuest => !IsHost;
    public ObservableCollection<SharePersonChip> People { get; } = new();

    [ObservableProperty] private string _liveText = "Shared session";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _iHaveControl = true;
    [ObservableProperty] private bool _canTakeBack;
    [ObservableProperty] private bool _canAsk;
    [ObservableProperty] private bool _canGiveBack;
    [ObservableProperty] private bool _asked;
    [ObservableProperty] private string _controllerName = "";
    [ObservableProperty] private bool _paused;
    /// <summary>Someone is waiting for an answer to "may I have control?" (host or controller).</summary>
    [ObservableProperty] private SharePersonChip? _request;

    public string EndText => IsHost ? "Stop sharing" : "Leave";

    public void SetMyId(string id) => _myId = id;

    public void Apply(ShareSnapshot s)
    {
        _hostId = s.Host?.Id;
        var mayGive = IsHost || s.ControllerId == _myId;
        People.Clear();
        foreach (var p in s.Participants.OrderBy(p => p.IsHost ? 0 : 1))
            People.Add(new SharePersonChip(p, p.Id == _myId, p.Id == s.ControllerId, mayGive && p.Id != s.ControllerId && !p.ReadOnly));
        var ctl = s.Controller;
        ControllerName = ctl is null ? "" : ctl.Id == _myId ? "You" : ctl.Name;
        IHaveControl = s.ControllerId == _myId;
        CanTakeBack = IsHost && !IHaveControl;
        CanGiveBack = !IsHost && IHaveControl;
        var me = s.Participants.FirstOrDefault(p => p.Id == _myId);
        CanAsk = !IsHost && !IHaveControl && s.CanAskForControl && me is { ReadOnly: false };
        if (IHaveControl) Asked = false;
        Paused = s.Paused;
        if (Request is { } r && (s.ControllerId == r.Id || s.Participants.All(p => p.Id != r.Id))) Request = null;
        var guests = s.Participants.Count(p => !p.IsHost);
        LiveText = IsHost
            ? guests == 0 ? "Shared session · waiting for teammates" : "Shared session"
            : $"{s.Host?.Name ?? "Host"} is sharing";
        Hint = s.Paused && !IsHost ? $"{s.Host?.Name ?? "The host"} switched to another tab"
            : IHaveControl ? (guests > 0 && mayGive ? "You have control. Click a name to hand it over." : "You have control.")
            : $"{ControllerName} has control. You're watching.";
    }

    /// <summary>A teammate asks for control (shown on the bar with Give / Not now).</summary>
    public void Asking(ShareParticipant p) => Request = new SharePersonChip(p, false, false, true);

    [RelayCommand] private void Give(SharePersonChip? p) { if (p is { CanGive: true }) _give(p.Id); }
    [RelayCommand] private void GiveRequested() { if (Request is { } r) { _give(r.Id); Request = null; } }
    [RelayCommand] private void DismissRequest() => Request = null;
    [RelayCommand] private void TakeBack() => _takeBack?.Invoke();
    [RelayCommand] private void Ask() { Asked = true; CanAsk = false; Hint = "Asked for control…"; _ask?.Invoke(); }
    [RelayCommand] private void GiveBack() { if (_hostId is { } h) _give(h); }
    [RelayCommand] private void Invite() => _invite?.Invoke();
    [RelayCommand] private void End() => _end();
    public bool CanInvite => _invite is not null;
}
