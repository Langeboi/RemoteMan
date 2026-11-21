using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// Broadcast typing: a group of SSH tabs that all get what you type in any one of them.
/// One group per window; a tab leaves it when it closes or loses its connection.
/// </summary>
public partial class MainViewModel
{
    private readonly HashSet<SshSessionViewModel> _broadcast = new();

    [ObservableProperty] private bool _broadcastOn;

    /// <summary>The group or the on/off state changed; SSH tabs refresh their broadcast bar.</summary>
    public event EventHandler? BroadcastChanged;

    public bool InBroadcast(SshSessionViewModel s) => _broadcast.Contains(s);

    /// <summary>Connected tabs in the group, in tab order.</summary>
    public IReadOnlyList<SshSessionViewModel> BroadcastTargets => SshSessions.Where(s => _broadcast.Contains(s) && s.IsConnected).ToList();

    /// <summary>Turns broadcast on with <paramref name="from"/> in the group (and whoever was ticked before).</summary>
    public void StartBroadcast(SshSessionViewModel from)
    {
        if (!BroadcastOn) _broadcast.RemoveWhere(s => s != from);
        _broadcast.Add(from);
        BroadcastOn = true;
        ApplyBroadcast();
    }

    public void StopBroadcast()
    {
        if (!BroadcastOn) return;
        BroadcastOn = false;
        ApplyBroadcast();
        Message = "Broadcast stopped";
    }

    public void SetInBroadcast(SshSessionViewModel s, bool on)
    {
        if (on ? !_broadcast.Add(s) : !_broadcast.Remove(s)) return;
        ApplyBroadcast();
    }

    public void AddAllToBroadcast()
    {
        foreach (var s in SshSessions.Where(s => s.Entry.Kind == RemoteMan.Core.Model.EntryKind.Ssh)) _broadcast.Add(s);
        ApplyBroadcast();
    }

    /// <summary>Sends keys or pasted text to every connected tab in the group. Tabs that lost their connection leave it.</summary>
    public void BroadcastInput(SshSessionViewModel from, string data)
    {
        var dropped = false;
        foreach (var s in SshSessions.Where(_broadcast.Contains).ToList())
        {
            if (!s.Send(data) && s != from) { _broadcast.Remove(s); dropped = true; }
        }
        if (dropped) ApplyBroadcast();
    }

    private void ApplyBroadcast()
    {
        _broadcast.RemoveWhere(s => !Sessions.Contains(s));
        if (BroadcastOn && _broadcast.Count == 0) BroadcastOn = false;
        foreach (var s in SshSessions) s.IsBroadcasting = BroadcastOn && _broadcast.Contains(s);
        if (BroadcastOn)
        {
            var names = BroadcastTargets.Select(s => s.Title).ToList();
            Message = names.Count <= 1 ? "Broadcast on: tick other SSH tabs in the amber bar" : $"Broadcast on: keys go to {string.Join(", ", names)}";
        }
        BroadcastChanged?.Invoke(this, EventArgs.Empty);
    }
}
