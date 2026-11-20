using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Rdp;

/// <summary>
/// Hosts the Windows Remote Desktop ActiveX control (mstscax.dll, ships with every Windows).
/// We bind late through IDispatch instead of an aximp-generated interop assembly, which keeps
/// the build simple and lets us use whatever newest control version the machine has.
/// </summary>
public sealed class RdpControl : AxHost
{
    // MsRdpClient9NotSafeForScripting: Windows 8.1 / Server 2012 R2 and newer.
    private const string Clsid = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";
    // IMsTscAxEvents dispinterface and its DISPIDs.
    private static readonly Guid EventsIid = new("336d5562-efa8-482e-8cb3-c5c0fc7a7db6");
    private const int DispIdOnConnected = 2, DispIdOnLoginComplete = 3, DispIdOnDisconnected = 4;

    private readonly System.Windows.Forms.Timer _resizeDebounce = new() { Interval = 400 };
    private bool _connected;

    public RdpControl() : base(Clsid)
    {
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); FitSessionToControl(); };
    }

    public event Action? Connected;
    public event Action? LoginComplete;
    /// <summary>Disconnect reason code from the control (see "ExtendedDisconnectReasonCode").</summary>
    public event Action<int>? Disconnected;

    private dynamic Ocx => GetOcx() ?? throw new InvalidOperationException("RDP control not created yet.");

    public void Connect(HostEndpoint endpoint, ResolvedCredential cred, RdpSettings s)
    {
        if (!IsHandleCreated) CreateControl();
        var ocx = Ocx;
        HookEvents(ocx);

        ocx.Server = endpoint.Host;
        ocx.UserName = cred.UserName ?? "";
        ocx.Domain = cred.Domain ?? s.Domain ?? "";
        ocx.DesktopWidth = s.DesktopWidth > 0 ? s.DesktopWidth : Math.Max(Width, 800);
        ocx.DesktopHeight = s.DesktopHeight > 0 ? s.DesktopHeight : Math.Max(Height, 600);
        ocx.ColorDepth = 32;

        var adv = ocx.AdvancedSettings9;
        adv.RDPPort = endpoint.Port;
        if (!string.IsNullOrEmpty(cred.Password)) adv.ClearTextPassword = cred.Password;
        // CredSSP (NLA). With no password set, CredSSP signs in with the current Windows logon when
        // the "Allow delegating default credentials" policy covers TERMSRV/<host>; otherwise Windows asks once.
        adv.EnableCredSspSupport = true;
        // 0 = connect without warning if server authentication fails: no certificate prompt.
        adv.AuthenticationLevel = s.IgnoreCertificateErrors ? 0 : 2;
        adv.SmartSizing = s.SmartSizing;
        adv.RedirectClipboard = s.RedirectClipboard;
        adv.RedirectDrives = s.RedirectDrives;
        adv.ConnectToAdministerServer = s.AdminSession;
        adv.EnableAutoReconnect = true;
        adv.BitmapPersistence = 1;
        adv.Compress = 1;

        // Graphics pipeline. With 32-bit colour and connection-type auto-detect the client offers
        // RDP 8+ GFX, so a server that has H.264/AVC enabled streams video-coded frames, which
        // Windows decodes on the GPU (Media Foundation). That is far cheaper on the CPU than
        // classic bitmap updates, and it matters most with many sessions open.
        try
        {
            adv.BandwidthDetection = true;
            adv.NetworkConnectionType = 7;  // CONNECTION_TYPE_AUTODETECT
        }
        catch (COMException) { /* very old mstscax: classic graphics */ }
        // Composition, font smoothing on; wallpaper and animations off (they only cost bandwidth).
        adv.PerformanceFlags = 0x100 | 0x80 | 0x01 | 0x04;
        adv.GrabFocusOnConnect = false;

        if (s.UseRdGateway && !string.IsNullOrEmpty(s.GatewayHost))
        {
            var t = ocx.TransportSettings2;
            t.GatewayHostname = s.GatewayHost;
            t.GatewayUsageMethod = 1;          // always use gateway
            t.GatewayProfileUsageMethod = 1;   // use explicit settings
            t.GatewayCredsSource = 0;          // ask / reuse
            t.GatewayCredSharing = 1;          // reuse session credentials
        }

        ocx.Connect();
    }

    /// <summary>
    /// Background tabs: hide the control's window so Windows stops drawing it, and skip
    /// resize work until the tab is shown again.
    /// </summary>
    public void SetForeground(bool foreground)
    {
        _foreground = foreground;
        Visible = foreground;
        if (foreground && _connected && _sizeChangedWhileHidden) { _sizeChangedWhileHidden = false; _resizeDebounce.Start(); }
    }

    private bool _foreground = true;
    private bool _sizeChangedWhileHidden;

    public void Disconnect()
    {
        try { if (Ocx.Connected != 0) Ocx.Disconnect(); } catch (COMException) { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_connected) return;
        if (!_foreground) { _sizeChangedWhileHidden = true; return; }
        _resizeDebounce.Stop(); _resizeDebounce.Start();
    }

    /// <summary>Dynamic resolution: resize the remote desktop to the tab (Win 8.1+ hosts).</summary>
    private void FitSessionToControl()
    {
        if (Width < 200 || Height < 200) return;
        try { Ocx.UpdateSessionDisplaySettings((uint)Width, (uint)Height, (uint)Width, (uint)Height, 0u, 100u, 100u); }
        catch (COMException) { /* older server: SmartSizing scales instead */ }
    }

    private bool _hooked;

    private void HookEvents(object ocx)
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            ComEventsHelper.Combine(ocx, EventsIid, DispIdOnConnected, new Action(() => Connected?.Invoke()));
            ComEventsHelper.Combine(ocx, EventsIid, DispIdOnLoginComplete, new Action(() => { _connected = true; LoginComplete?.Invoke(); }));
            ComEventsHelper.Combine(ocx, EventsIid, DispIdOnDisconnected, new Action<int>(reason => { _connected = false; Disconnected?.Invoke(reason); }));
        }
        catch (Exception) { /* events are a convenience; the session still works without them */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _resizeDebounce.Dispose(); Disconnect(); }
        base.Dispose(disposing);
    }
}
