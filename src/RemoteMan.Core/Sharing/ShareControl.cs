namespace RemoteMan.Core.Sharing;

/// <summary>Someone in a shared session. <see cref="Id"/> is per connection, so one person on two machines is two participants.</summary>
public sealed record ShareParticipant(string Id, string User, string Machine, bool IsHost = false, bool ReadOnly = false)
{
    public string Name => User.Contains('\\') ? User[(User.LastIndexOf('\\') + 1)..] : User;

    public string Initials
    {
        get
        {
            var parts = Name.Split(['.', ' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            var s = new string(parts.Select(p => char.ToUpperInvariant(p[0])).Take(2).ToArray());
            return s.Length >= 2 || Name.Length < 2 ? s : Name[..2].ToUpperInvariant();
        }
    }
}

/// <summary>Who is in a shared session and who drives it, as everyone sees it.</summary>
public sealed record ShareSnapshot(IReadOnlyList<ShareParticipant> Participants, string ControllerId, bool CanAskForControl, bool Paused = false)
{
    public ShareParticipant? Controller => Participants.FirstOrDefault(p => p.Id == ControllerId);
    public ShareParticipant? Host => Participants.FirstOrDefault(p => p.IsHost);
}

/// <summary>
/// The rules for control in a shared session. Kept apart from the networking so they can be tested.
/// <list type="bullet">
/// <item>One participant has control at a time. The host starts with it.</item>
/// <item>The host, or whoever has control, can give it to anyone else who may control.</item>
/// <item>The host can always take it back.</item>
/// <item>When the controller leaves, control goes back to the host.</item>
/// <item>Read-only logins can watch but never get control.</item>
/// </list>
/// </summary>
public sealed class ShareControl
{
    private readonly List<ShareParticipant> _people = new();
    private readonly object _gate = new();

    public ShareControl(ShareParticipant host, bool canAskForControl = true)
    {
        if (!host.IsHost) host = host with { IsHost = true };
        Host = host;
        _people.Add(host);
        ControllerId = host.Id;
        CanAskForControl = canAskForControl;
    }

    public ShareParticipant Host { get; }
    public string ControllerId { get; private set; }
    public bool CanAskForControl { get; set; }
    public bool Paused { get; set; }

    public IReadOnlyList<ShareParticipant> Participants { get { lock (_gate) return _people.ToList(); } }

    public ShareParticipant? Find(string id) { lock (_gate) return _people.FirstOrDefault(p => p.Id == id); }

    public ShareSnapshot Snapshot() { lock (_gate) return new ShareSnapshot(_people.ToList(), ControllerId, CanAskForControl, Paused); }

    public void Join(ShareParticipant p)
    {
        lock (_gate)
        {
            if (p.IsHost) throw new ArgumentException("Only one host.", nameof(p));
            _people.RemoveAll(x => x.Id == p.Id);
            _people.Add(p);
        }
    }

    /// <summary>Returns true when control moved because the controller left.</summary>
    public bool Leave(string id)
    {
        lock (_gate)
        {
            if (id == Host.Id) return false;
            _people.RemoveAll(x => x.Id == id);
            if (ControllerId != id) return false;
            ControllerId = Host.Id;
            return true;
        }
    }

    public bool MayGive(string byId) => byId == Host.Id || byId == ControllerId;

    public bool TryGive(string byId, string toId)
    {
        lock (_gate)
        {
            if (!MayGive(byId) || toId == ControllerId) return false;
            var to = _people.FirstOrDefault(p => p.Id == toId);
            if (to is null || to.ReadOnly) return false;
            ControllerId = toId;
            return true;
        }
    }

    public bool TryTakeBack(string byId)
    {
        lock (_gate)
        {
            if (byId != Host.Id || ControllerId == Host.Id) return false;
            ControllerId = Host.Id;
            return true;
        }
    }

    /// <summary>Whether this participant may ask for control right now.</summary>
    public bool MayAsk(string id)
    {
        lock (_gate)
        {
            var p = _people.FirstOrDefault(x => x.Id == id);
            return p is { ReadOnly: false } && CanAskForControl && id != ControllerId;
        }
    }

    public bool HasControl(string id) => ControllerId == id;
}
