using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteMan.Core.Sharing;

/// <summary>What a shared session shows: a terminal (SSH, Telnet, serial) or an RDP screen.</summary>
public enum ShareKind { Terminal = 0, Rdp = 1 }

public enum ShareInviteState { Pending = 0, Accepted = 1, Declined = 2, Ended = 3 }

/// <summary>
/// One teammate invited into a shared session. Stored in the shared database (table
/// <c>remoteman_shares</c>) so the invitee's RemoteMan sees it within a moment, the same way
/// presence works. It carries what the invitee needs to reach the host: the host's addresses,
/// the relay, a one-time ticket and the fingerprint of the host's certificate (so the link
/// can't be intercepted, even through the relay).
/// </summary>
public sealed record ShareInvite
{
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(10);

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ShareId { get; init; }
    public string HostUser { get; init; } = "";
    public string HostMachine { get; init; } = "";
    public string Invitee { get; init; } = "";
    public Guid EntryId { get; init; }
    public string EntryName { get; init; } = "";
    public ShareKind Kind { get; init; }
    /// <summary>"address:port" pairs where the host listens, most likely first.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];
    /// <summary>"host:port" of the relay the host is registered with, when it has one.</summary>
    public string? Relay { get; init; }
    public string Ticket { get; init; } = "";
    /// <summary>SHA-256 of the host's TLS certificate, hex.</summary>
    public string Thumbprint { get; init; } = "";
    /// <summary>The invitee gets control as soon as they join.</summary>
    public bool StartsWithControl { get; init; }
    public bool CanAskForControl { get; init; } = true;
    public ShareInviteState State { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow + Life;

    public bool IsLive(DateTimeOffset now) => State == ShareInviteState.Pending && now < ExpiresAt;

    public bool IsFor(string user) => string.Equals(Invitee, user, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Everything but the indexed columns, as JSON for the payload column.</summary>
    public string ToPayload() => JsonSerializer.Serialize(new Payload(ShareId, HostMachine, EntryId, EntryName, Kind, Addresses, Relay, Ticket, Thumbprint,
        StartsWithControl, CanAskForControl, CreatedAt.ToUnixTimeMilliseconds()), Json);

    public static ShareInvite FromRow(Guid id, string invitee, string hostUser, int state, long expiresAt, string? payload)
    {
        var p = string.IsNullOrEmpty(payload) ? null : JsonSerializer.Deserialize<Payload>(payload, Json);
        return new ShareInvite
        {
            Id = id, Invitee = invitee, HostUser = hostUser, State = (ShareInviteState)state,
            ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt),
            ShareId = p?.ShareId ?? Guid.Empty, HostMachine = p?.HostMachine ?? "", EntryId = p?.EntryId ?? Guid.Empty,
            EntryName = p?.EntryName ?? "", Kind = p?.Kind ?? ShareKind.Terminal, Addresses = p?.Addresses ?? [],
            Relay = p?.Relay, Ticket = p?.Ticket ?? "", Thumbprint = p?.Thumbprint ?? "",
            StartsWithControl = p?.StartsWithControl ?? false, CanAskForControl = p?.CanAskForControl ?? true,
            CreatedAt = p is null ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeMilliseconds(p.CreatedAt),
        };
    }

    private sealed record Payload(Guid ShareId, string HostMachine, Guid EntryId, string EntryName, ShareKind Kind,
        IReadOnlyList<string> Addresses, string? Relay, string Ticket, string Thumbprint, bool StartsWithControl, bool CanAskForControl, long CreatedAt);
}
