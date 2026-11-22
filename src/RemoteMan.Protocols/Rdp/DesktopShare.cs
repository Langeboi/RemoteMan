using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteMan.Protocols.Rdp;

/// <summary>
/// The sharer's side of Windows Desktop Sharing (rdpencomapi.dll, the engine behind Windows Remote
/// Assistance; part of every Windows 10/11). Only RemoteMan's own windows are shared, and only the
/// rectangle of the RDP tab, so the tree, other tabs and other programs never show. Guests reach the
/// sharing port through the shared-session link, never directly. Bound late through IDispatch, like
/// <see cref="RdpControl"/>.
/// </summary>
public sealed class DesktopShare : IDisposable
{
    private const string SessionClsid = "9B78F0E6-3E05-4A5B-B2E8-E743A8956B65";
    // _IRDPSessionEvents and the DISPIDs we use (rdpencomapi.h).
    private static readonly Guid EventsIid = new("98a97042-6698-40e9-8efd-b3200990004b");
    private const int DispIdAttendeeConnected = 301, DispIdAttendeeDisconnected = 302, DispIdCtrlLevelChangeRequest = 309;
    // CTRL_LEVEL
    private const int CtrlView = 2, CtrlInteractive = 3;

    private dynamic? _session;
    private readonly Dictionary<string, dynamic> _attendees = new(StringComparer.Ordinal);
    private string? _controller;
    private bool _paused;

    /// <summary>The invitation the guests' viewers open (sent to them over the shared-session link).</summary>
    public string ConnectionString { get; private set; } = "";
    public string Password { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    /// <summary>Local port the sharer listens on; shared-session tunnels connect here.</summary>
    public int Port { get; private set; }

    public static bool IsAvailable => Type.GetTypeFromCLSID(new Guid(SessionClsid), false) is not null;

    /// <summary>Starts sharing. <paramref name="area"/> is the RDP tab's rectangle on screen, in pixels.</summary>
    public void Start(System.Drawing.Rectangle area)
    {
        var type = Type.GetTypeFromCLSID(new Guid(SessionClsid), true)!;
        _session = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Desktop Sharing isn't available on this PC.");
        var s = _session!;
        Port = FreeLocalPort();
        Try(() => s.Properties["PortId"] = Port);
        Try(() => s.Properties["PortProtocol"] = 2);                       // IPv4
        Try(() => s.Properties["DefaultAttendeeControlLevel"] = CtrlView);   // everyone watches until given control
        HookEvents(s);

        // Only RemoteMan's own windows; everything else shows as a hatched area to the guests.
        Try(() =>
        {
            var filter = s.ApplicationFilter;
            var pid = Environment.ProcessId;
            foreach (dynamic app in (System.Collections.IEnumerable)filter.Applications)
            {
                bool ours = (int)app.Id == pid;
                app.Shared = ours;
                if (!ours) continue;
                foreach (dynamic w in (System.Collections.IEnumerable)app.Windows) Try(() => w.Shared = true);
            }
            filter.Enabled = true;
        });
        SetArea(area);
        s.Open();
        // No area yet (the session is still connecting or hidden): show nothing until there is one.
        if (area.Width < 16 || area.Height < 16) SetPaused(true);
        var invitation = s.Invitations.CreateInvitation(null, "remoteman", Password, 16);
        ConnectionString = (string)invitation.ConnectionString;
    }

    /// <summary>Follows the RDP tab when the window moves or resizes.</summary>
    public void SetArea(System.Drawing.Rectangle r)
    {
        if (_session is not { } s || r.Width < 16 || r.Height < 16) return;
        Try(() => s.SetDesktopSharedRect(r.Left, r.Top, r.Right, r.Bottom));
    }

    /// <summary>Pauses the picture while the shared tab isn't on screen.</summary>
    public void SetPaused(bool paused)
    {
        if (_session is not { } s || paused == _paused) return;
        _paused = paused;
        Try(() => { if (paused) s.Pause(); else s.Resume(); });
    }

    /// <summary>
    /// Gives mouse and keyboard to the guest whose viewer connected under <paramref name="remoteName"/>
    /// (their shared-session participant id); null leaves everyone watching (the host drives).
    /// </summary>
    public void SetController(string? remoteName)
    {
        _controller = remoteName;
        foreach (var (name, a) in _attendees.ToList())
            Try(() => a.ControlLevel = name == remoteName ? CtrlInteractive : CtrlView);
    }

    private void HookEvents(object session)
    {
        try
        {
            ComEventsHelper.Combine(session, EventsIid, DispIdAttendeeConnected, new Action<object>(OnAttendeeConnected));
            ComEventsHelper.Combine(session, EventsIid, DispIdAttendeeDisconnected, new Action<object>(OnAttendeeDisconnected));
            ComEventsHelper.Combine(session, EventsIid, DispIdCtrlLevelChangeRequest, new Action<object, int>(OnControlRequest));
        }
        catch (Exception) { /* without events nobody gets control, but watching still works */ }
    }

    private void OnAttendeeConnected(object attendee)
    {
        dynamic a = attendee;
        string name = "";
        Try(() => name = (string)a.RemoteName);
        _attendees[name] = a;
        Try(() => a.ControlLevel = name == _controller ? CtrlInteractive : CtrlView);
    }

    private void OnAttendeeDisconnected(object info)
    {
        // The argument is disconnect info, not the attendee: drop entries whose attendee no longer answers.
        foreach (var (name, a) in _attendees.ToList())
        {
            try { _ = a.Id; } catch (Exception) { _attendees.Remove(name); }
        }
    }

    /// <summary>A viewer asks for a control level: only the participant who has control in the shared session gets it.</summary>
    private void OnControlRequest(object attendee, int level)
    {
        dynamic a = attendee;
        string name = "";
        Try(() => name = (string)a.RemoteName);
        Try(() => a.ControlLevel = name == _controller && level >= CtrlInteractive ? CtrlInteractive : CtrlView);
    }

    private static int FreeLocalPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void Try(Action a) { try { a(); } catch (Exception) { } }

    public void Dispose()
    {
        if (_session is not { } s) return;
        _session = null;
        Try(() => s.Close());
        Try(() => Marshal.FinalReleaseComObject(s));
        _attendees.Clear();
    }
}

/// <summary>
/// The guest's side: the Windows Desktop Sharing viewer ActiveX (RDPViewer), scaled to the tab.
/// </summary>
public sealed class DesktopShareViewer : AxHost
{
    private const string Clsid = "32be5ed2-5c86-480f-a914-0ff8885a1b3f";
    private const int CtrlInteractive = 3;

    public DesktopShareViewer() : base(Clsid) { }

    private dynamic Ocx => GetOcx() ?? throw new InvalidOperationException("Viewer not created yet.");

    /// <summary>Connects with the host's invitation; <paramref name="name"/> is our participant id (how the host knows who to give control).</summary>
    public void Connect(string connectionString, string name, string password)
    {
        if (!IsHandleCreated) CreateControl();
        var ocx = Ocx;
        try { ocx.SmartSizing = true; } catch (Exception) { }
        ocx.Connect(connectionString, name, password);
    }

    /// <summary>Asks for mouse and keyboard once the shared session says we have control.</summary>
    public void RequestControl(bool interactive)
    {
        try { Ocx.RequestControl(interactive ? CtrlInteractive : 2); } catch (Exception) { }
    }

    public void Disconnect()
    {
        try { if (IsHandleCreated) Ocx.Disconnect(); } catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Disconnect();
        try { base.Dispose(disposing); } catch (Exception) { }
    }
}
