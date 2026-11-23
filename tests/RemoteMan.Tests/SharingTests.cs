using System.Net;
using System.Net.Sockets;
using RemoteMan.Core.Sharing;
using Xunit;

namespace RemoteMan.Tests;

public class SharingTests
{
    private static ShareParticipant P(string id, bool ro = false) => new(id, id, "pc", false, ro);

    [Fact]
    public void Host_starts_with_control_and_can_hand_it_on()
    {
        var c = new ShareControl(new ShareParticipant("h", "lange", "pc"));
        c.Join(P("a")); c.Join(P("b"));
        Assert.True(c.HasControl("h"));
        Assert.True(c.TryGive("h", "a"));
        Assert.True(c.HasControl("a"));
        // The controller can pass it on; someone without control can't.
        Assert.False(c.TryGive("b", "b"));
        Assert.True(c.TryGive("a", "b"));
        Assert.True(c.HasControl("b"));
        // The host can always take it back.
        Assert.True(c.TryTakeBack("h"));
        Assert.True(c.HasControl("h"));
        Assert.False(c.TryTakeBack("a"));
    }

    [Fact]
    public void Control_returns_to_host_when_controller_leaves()
    {
        var c = new ShareControl(new ShareParticipant("h", "lange", "pc"));
        c.Join(P("a"));
        c.TryGive("h", "a");
        Assert.True(c.Leave("a"));
        Assert.True(c.HasControl("h"));
        Assert.False(c.Leave("h"));
    }

    [Fact]
    public void Read_only_logins_watch_but_never_control()
    {
        var c = new ShareControl(new ShareParticipant("h", "lange", "pc"));
        c.Join(P("ro", ro: true));
        Assert.False(c.TryGive("h", "ro"));
        Assert.False(c.MayAsk("ro"));
        c.Join(P("a"));
        Assert.True(c.MayAsk("a"));
        c.CanAskForControl = false;
        Assert.False(c.MayAsk("a"));
    }

    [Fact]
    public void Invite_round_trips_through_the_payload_column()
    {
        var i = new ShareInvite
        {
            ShareId = Guid.NewGuid(), HostUser = "lange", HostMachine = "PC1", Invitee = "mads", EntryId = Guid.NewGuid(),
            EntryName = "DC01", Kind = ShareKind.Rdp, Addresses = ["10.0.0.5:47110"], Relay = "db01:47120",
            Ticket = "T", Thumbprint = "AB", StartsWithControl = true,
        };
        var back = ShareInvite.FromRow(i.Id, i.Invitee, i.HostUser, (int)i.State, i.ExpiresAt.ToUnixTimeMilliseconds(), i.ToPayload());
        Assert.Equal(i.ShareId, back.ShareId);
        Assert.Equal(ShareKind.Rdp, back.Kind);
        Assert.Equal("db01:47120", back.Relay);
        Assert.Equal(["10.0.0.5:47110"], back.Addresses);
        Assert.True(back.StartsWithControl);
        Assert.True(back.IsLive(DateTimeOffset.UtcNow));
        Assert.True(back.IsFor("MADS"));
    }

    [Fact]
    public void Desktop_sharing_ticket_points_at_the_tunnel_first()
    {
        const string cs = "<E><A KH=\"abc\" ID=\"x\"/><C><T ID=\"1\" SID=\"0\"><L P=\"50000\" N=\"192.168.1.5\"/><L P=\"50000\" N=\"fe80::1\"/></T></C></E>";
        var l = DesktopShareTicket.Listeners(DesktopShareTicket.PointAt(cs, 61234));
        Assert.Equal(("127.0.0.1", 61234), l[0]);
        Assert.Equal(("192.168.1.5", 50000), l[1]);
        Assert.Equal(3, l.Count);
    }

    [Theory]
    [InlineData("db01", "db01", 47120)]
    [InlineData("db01:5000", "db01", 5000)]
    [InlineData("10.1.2.3:47110", "10.1.2.3", 47110)]
    [InlineData("[fe80::1]:99", "fe80::1", 99)]
    public void Endpoints_parse(string text, string host, int port)
    {
        Assert.True(ShareNet.TryParseEndpoint(text, ShareNet.RelayPort, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    private static ShareInvite InviteFor(ShareHost host, string user, IReadOnlyList<string>? addresses = null, string? relay = null) => new()
    {
        ShareId = host.ShareId, HostUser = "lange", Invitee = user, Kind = host.Kind, EntryName = host.EntryName,
        Addresses = addresses ?? [$"127.0.0.1:{host.Port}"], Relay = relay, Ticket = host.Ticket, Thumbprint = host.Thumbprint,
    };

    private static async Task Until(Func<bool> ok)
    {
        for (var i = 0; i < 200 && !ok(); i++) await Task.Delay(25);
        Assert.True(ok());
    }

    [Fact]
    public async Task Terminal_session_is_mirrored_and_only_the_controller_types()
    {
        await using var host = new ShareHost(ShareKind.Terminal, "pve01", "lange", "PC1");
        await host.StartAsync([], preferredPort: 0);
        host.Invite("mads");
        host.SendOutput("root@pve01:~# ");
        host.SetSize(120, 40);
        var typed = new List<string>();
        host.Input += s => { lock (typed) typed.Add(s); };

        await using var guest = await ShareClient.ConnectAsync(InviteFor(host, "mads"), "mads", "PC2", false, []);
        Assert.Equal("root@pve01:~# ", guest.Welcome.History);
        Assert.Equal(120, guest.Cols);
        Assert.Equal("direct", guest.Via);

        var seen = "";
        guest.Output += s => seen += s;
        host.SendOutput("qm list\r\n");
        await Until(() => seen == "qm list\r\n");

        // Without control, keystrokes don't go anywhere.
        guest.SendInput("rm -rf /");
        await Task.Delay(100);
        lock (typed) Assert.Empty(typed);

        await Until(() => host.Control.Participants.Count == 2);
        var madsId = guest.MyId;
        host.Give(madsId);
        await Until(() => guest.HasControl);
        guest.SendInput("ls\r");
        await Until(() => { lock (typed) return typed.Count == 1; });

        guest.GiveBack();
        await Until(() => host.HostHasControl);
    }

    [Fact]
    public async Task Asking_for_control_reaches_the_host()
    {
        await using var host = new ShareHost(ShareKind.Terminal, "pve01", "lange", "PC1");
        await host.StartAsync([], preferredPort: 0);
        host.Invite("mads");
        ShareParticipant? asked = null;
        host.ControlRequested += p => asked = p;
        await using var guest = await ShareClient.ConnectAsync(InviteFor(host, "mads"), "mads", "PC2", false, []);
        guest.RequestControl();
        await Until(() => asked?.User == "mads");
    }

    [Fact]
    public async Task Wrong_ticket_or_uninvited_user_is_refused()
    {
        await using var host = new ShareHost(ShareKind.Terminal, "pve01", "lange", "PC1");
        await host.StartAsync([], preferredPort: 0);
        host.Invite("mads");
        await Assert.ThrowsAsync<ShareRefusedException>(() => ShareClient.ConnectAsync(InviteFor(host, "eve"), "eve", "PC3", false, []));
        await Assert.ThrowsAsync<ShareRefusedException>(() => ShareClient.ConnectAsync(InviteFor(host, "mads") with { Ticket = "nope" }, "mads", "PC2", false, []));
    }

    [Fact]
    public async Task Invitee_who_starts_with_control_gets_it_on_join()
    {
        await using var host = new ShareHost(ShareKind.Terminal, "pve01", "lange", "PC1");
        await host.StartAsync([], preferredPort: 0);
        host.Invite("mads", startsWithControl: true);
        await using var guest = await ShareClient.ConnectAsync(InviteFor(host, "mads"), "mads", "PC2", false, []);
        Assert.True(guest.HasControl);
    }

    [Fact]
    public async Task Guest_gets_in_through_the_relay_when_the_host_is_unreachable()
    {
        await using var relay = new ShareRelay();
        await using var host = new ShareHost(ShareKind.Terminal, "pve01", "lange", "PC1");
        await host.StartAsync([$"127.0.0.1:{relay.Port}"], preferredPort: 0);
        await Until(() => host.RelayConnected);
        host.Invite("mads");
        host.SendOutput("hello");

        // An address nothing listens on stands in for "a different network".
        var invite = InviteFor(host, "mads", addresses: ["127.0.0.1:1"], relay: host.Relay);
        await using var guest = await ShareClient.ConnectAsync(invite, "mads", "PC2", false, []);
        Assert.Equal("relay", guest.Via);
        Assert.Equal("hello", guest.Welcome.History);
    }

    /// <summary>Same as above against deploy/relay/remoteman-relay.py, when one is running (REMOTEMAN_TEST_RELAY=host:port).</summary>
    [Fact]
    public async Task Python_relay_works_like_the_built_in_one()
    {
        if (Environment.GetEnvironmentVariable("REMOTEMAN_TEST_RELAY") is not { Length: > 0 } relay) return;
        await using var host = new ShareHost(ShareKind.Terminal, "pve01", "lange", "PC1");
        await host.StartAsync([relay], preferredPort: 0);
        await Until(() => host.RelayConnected);
        host.Invite("mads");
        host.SendOutput("via python");
        var invite = InviteFor(host, "mads", addresses: ["127.0.0.1:1"], relay: host.Relay);
        await using var guest = await ShareClient.ConnectAsync(invite, "mads", "PC2", false, []);
        Assert.Equal("relay", guest.Via);
        Assert.Equal("via python", guest.Welcome.History);
        var seen = "";
        guest.Output += s => seen += s;
        host.SendOutput("!");
        await Until(() => seen == "!");
    }

    [Fact]
    public async Task Rdp_tunnel_falls_back_to_the_port_the_invitation_lists()
    {
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        _ = Task.Run(async () =>
        {
            using var c = await echo.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[1024];
            int n;
            while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
        });
        // Windows ignored the port we asked for: nothing listens there, the invitation names the real one.
        var real = ((IPEndPoint)echo.LocalEndpoint).Port;
        await using var host = new ShareHost(ShareKind.Rdp, "DC01", "lange", "PC1") { RdpPort = 1, RdpListeners = [("127.0.0.1", real)], RdpConnectionString = "<E/>" };
        await host.StartAsync([], preferredPort: 0);
        host.Invite("mads");
        await using var guest = await ShareClient.ConnectAsync(InviteFor(host, "mads"), "mads", "PC2", false, []);
        using var viewer = new TcpClient();
        await viewer.ConnectAsync(IPAddress.Loopback, guest.StartTunnel());
        var vs = viewer.GetStream();
        await vs.WriteAsync("pong"u8.ToArray());
        var got = new byte[4];
        var read = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (read < 4) read += await vs.ReadAsync(got.AsMemory(read), timeout.Token);
        Assert.Equal("pong", System.Text.Encoding.ASCII.GetString(got));
        echo.Stop();
    }

    [Fact]
    public async Task Rdp_tunnel_carries_bytes_both_ways()
    {
        // Stand-in for the Windows Desktop Sharing port: echoes what it gets.
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        _ = Task.Run(async () =>
        {
            using var c = await echo.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[1024];
            int n;
            while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
        });

        await using var host = new ShareHost(ShareKind.Rdp, "DC01", "lange", "PC1") { RdpPort = ((IPEndPoint)echo.LocalEndpoint).Port, RdpConnectionString = "<E/>" };
        await host.StartAsync([], preferredPort: 0);
        host.Invite("mads");
        await using var guest = await ShareClient.ConnectAsync(InviteFor(host, "mads"), "mads", "PC2", false, []);
        Assert.Equal("<E/>", guest.Welcome.RdpConnectionString);

        var port = guest.StartTunnel();
        using var viewer = new TcpClient();
        await viewer.ConnectAsync(IPAddress.Loopback, port);
        var vs = viewer.GetStream();
        await vs.WriteAsync("ping"u8.ToArray());
        var got = new byte[4];
        var read = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (read < 4) read += await vs.ReadAsync(got.AsMemory(read), timeout.Token);
        Assert.Equal("ping", System.Text.Encoding.ASCII.GetString(got));
        echo.Stop();
    }
}
