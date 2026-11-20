namespace RemoteMan.Core.Model;

public enum PresenceState { Viewing = 0, Editing = 1, Connected = 2 }

/// <summary>An open connection tab: which entry, and since when.</summary>
public sealed record PresenceSession(Guid EntryId, DateTimeOffset Since);

/// <summary>
/// "Who is looking at / connected to what", heartbeat based. <see cref="EntryId"/> and
/// <see cref="State"/> are what the user has in front of them (the selected entry or the active
/// tab); <see cref="Sessions"/> lists every connection they have open, including hidden tabs.
/// </summary>
public sealed record Presence(
    string User,
    string Machine,
    Guid EntryId,
    PresenceState State,
    DateTimeOffset LastSeen,
    IReadOnlyList<PresenceSession>? Sessions = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    public bool IsAlive(DateTimeOffset now) => now - LastSeen < Ttl;
    public string Initials => InitialsOf(User);

    /// <summary>When this person connected to the entry this record is about, if they are connected.</summary>
    public DateTimeOffset? ConnectedSince => Sessions?.FirstOrDefault(s => s.EntryId == EntryId)?.Since;

    public static string InitialsOf(string user)
    {
        // DOMAIN\user: initials of the user part.
        var name = user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;
        var parts = name.Split(' ', '.', '_', '-').Where(s => s.Length > 0).Take(2).ToList();
        if (parts.Count == 1 && parts[0].Length > 1) return parts[0][..2].ToUpperInvariant();
        return string.Concat(parts.Select(s => char.ToUpperInvariant(s[0])));
    }
}
