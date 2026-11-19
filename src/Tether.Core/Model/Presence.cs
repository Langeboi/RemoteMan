namespace Tether.Core.Model;

public enum PresenceState { Viewing = 0, Editing = 1, Connected = 2 }

/// <summary>"Who is looking at / connected to what", heartbeat based.</summary>
public sealed record Presence(
    string User,
    string Machine,
    Guid EntryId,
    PresenceState State,
    DateTimeOffset LastSeen)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    public bool IsAlive(DateTimeOffset now) => now - LastSeen < Ttl;
    public string Initials => string.Concat(User.Split(' ', '.', '_', '\\').Where(s => s.Length > 0).Take(2).Select(s => char.ToUpperInvariant(s[0])));
}
