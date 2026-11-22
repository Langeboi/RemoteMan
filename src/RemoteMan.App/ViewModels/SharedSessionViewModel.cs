using RemoteMan.Core.Model;
using RemoteMan.Core.Sharing;
using RemoteMan.Protocols;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// A tab showing a teammate's shared session: their terminal or RDP screen, live. No login of ours
/// is involved and the server is never contacted directly; everything comes over the share link.
/// </summary>
public sealed class SharedSessionViewModel : SessionViewModel
{
    public SharedSessionViewModel(ShareClient client, ShareInvite invite)
        : base(new Entry
        {
            Id = invite.EntryId, Name = invite.EntryName,
            Kind = invite.Kind == ShareKind.Rdp ? EntryKind.Rdp : EntryKind.Ssh,
        }, new HostEndpoint(invite.HostMachine), ResolvedCredential.None, false)
    {
        Client = client;
        Invite = invite;
        var host = client.State.Host?.Name ?? invite.HostUser;
        Title = $"{invite.EntryName} · {host}";
        StatusText = $"Shared by {host} · {client.Via}";
    }

    public ShareClient Client { get; }
    public ShareInvite Invite { get; }
    public bool IsRdp => Client.Welcome.Kind == ShareKind.Rdp;

    /// <summary>Not your connection: no presence, not reopened at startup.</summary>
    public override bool IsConnection => false;
    public override string KindTag => IsRdp ? "RDP" : "SSH";
}
