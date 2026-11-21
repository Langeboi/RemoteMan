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

    /// <summary>Remote scaling in percent once logged in (0 = 100).</summary>
    private uint _scale = 100;

    public void Connect(HostEndpoint endpoint, ResolvedCredential cred, RdpSettings s, RdpConnectOptions? o = null)
    {
        o ??= new RdpConnectOptions();
        if (!IsHandleCreated) CreateControl();
        var ocx = Ocx;
        HookEvents(ocx);

        ocx.Server = endpoint.Host;
        ocx.UserName = cred.UserName ?? "";
        ocx.Domain = cred.Domain ?? s.Domain ?? "";
        var screen = Screen.FromControl(this).Bounds;
        ocx.DesktopWidth = o.FullScreen ? screen.Width : s.DesktopWidth > 0 ? s.DesktopWidth : Math.Max(Width, 800);
        ocx.DesktopHeight = o.FullScreen ? screen.Height : s.DesktopHeight > 0 ? s.DesktopHeight : Math.Max(Height, 600);
        ocx.ColorDepth = s.ColorDepth is 15 or 16 or 24 or 32 ? s.ColorDepth : 32;
        _scale = s.ScalePercent is >= 100 and <= 500 ? (uint)s.ScalePercent : 100u;

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
        adv.RedirectPrinters = s.RedirectPrinters;
        adv.ConnectToAdministerServer = s.AdminSession;
        adv.EnableAutoReconnect = o.AutoReconnect;
        try { adv.MaxReconnectAttempts = 20; } catch (COMException) { }
        adv.BitmapPersistence = 1;
        adv.Compress = 1;
        Try(() => adv.AudioCaptureRedirectionMode = s.Microphone);
        Try(() =>
        {
            var sec = ocx.SecuredSettings3;
            sec.AudioRedirectionMode = (int)s.Audio;
            // In a tab, "only in full screen" means only when the session fills the screen.
            sec.KeyboardHookMode = s.WindowsKeys == RdpKeys.FullScreenOnly && o.OwnFullScreenWindow ? (int)RdpKeys.Remote : (int)s.WindowsKeys;
        });
        RedirectDrives(adv, s);

        // Graphics pipeline. With 32-bit colour and connection-type auto-detect the client offers
        // RDP 8+ GFX, so a server that has H.264/AVC enabled streams video-coded frames, which
        // Windows decodes on the GPU (Media Foundation). That is far cheaper on the CPU than
        // classic bitmap updates, and it matters most with many sessions open.
        // Experience flags: 0x1 wallpaper off, 0x2 full-window drag off, 0x4 menu animations off,
        // 0x8 themes off, 0x20 cursor shadow off, 0x80 font smoothing on, 0x100 desktop composition on.
        var (type, flags) = s.Speed switch
        {
            RdpSpeed.Lan => (6, 0x80 | 0x100),
            RdpSpeed.Broadband => (4, 0x80 | 0x100 | 0x01 | 0x04),
            RdpSpeed.Slow => (2, 0x01 | 0x02 | 0x04 | 0x08 | 0x20),
            _ => (7, 0x100 | 0x80 | 0x01 | 0x04),  // CONNECTION_TYPE_AUTODETECT
        };
        try
        {
            adv.BandwidthDetection = s.Speed == RdpSpeed.Auto;
            adv.NetworkConnectionType = type;
        }
        catch (COMException) { /* very old mstscax: classic graphics */ }
        adv.PerformanceFlags = flags;
        adv.GrabFocusOnConnect = false;

        if (s.UseRdGateway && !string.IsNullOrEmpty(s.GatewayHost))
        {
            var t = ocx.TransportSettings2;
            t.GatewayHostname = s.GatewayHost;
            t.GatewayUsageMethod = s.GatewayBypassLocal ? 2 : 1;  // 2 = detect (direct for local addresses), 1 = always
            t.GatewayProfileUsageMethod = 1;   // use explicit settings
            if (o.GatewayCredential is { UserName: { Length: > 0 } gu } gc)
            {
                t.GatewayCredsSource = 0;      // user name and password
                t.GatewayCredSharing = 0;      // its own login
                Try(() => { t.GatewayUsername = gu; t.GatewayDomain = gc.Domain ?? ""; t.GatewayPassword = gc.Password ?? ""; });
            }
            else
            {
                t.GatewayCredsSource = 0;      // ask / reuse
                t.GatewayCredSharing = 1;      // reuse session credentials
            }
        }

        if (o.FullScreen)
        {
            // The control's own full screen, with the blue connection bar to restore or leave it.
            Try(() => { adv.DisplayConnectionBar = true; adv.PinConnectionBar = false; });
            Try(() => ocx.FullScreenTitle = o.Title ?? endpoint.Host);
            ocx.FullScreen = true;
        }

        ocx.Connect();
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or NotImplementedException) { }
    }

    /// <summary>All drives, or only the letters in <see cref="RdpSettings.Drives"/> (through the control's drive list).</summary>
    private void RedirectDrives(dynamic adv, RdpSettings s)
    {
        var letters = (s.Drives ?? "").Split(',', ';', ' ').Select(x => x.Trim().TrimEnd(':', '\\')).Where(x => x.Length == 1)
            .Select(x => char.ToUpperInvariant(x[0])).ToHashSet();
        if (!s.RedirectDrives || letters.Count == 0) { adv.RedirectDrives = s.RedirectDrives; return; }
        try
        {
            var ns = (IMsRdpClientNonScriptable3)GetOcx()!;
            dynamic drives = ns.DriveCollection();
            var count = (int)drives.DriveCount;
            for (var i = 0; i < count; i++)
            {
                dynamic d = drives.DriveByIndex[(uint)i];
                var name = ((string)d.Name).Trim();
                d.RedirectionState = name.Length > 0 && letters.Contains(char.ToUpperInvariant(name[0]));
            }
            adv.RedirectDrives = false;  // the per-drive states above decide
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            adv.RedirectDrives = true;   // can't pick single drives on this Windows: all of them
        }
    }

    /// <summary>
    /// Background tabs: hide the control's window so Windows stops drawing it, and skip
    /// resize work until the tab is shown again.
    /// </summary>
    public void SetForeground(bool foreground)
    {
        if (_closing || IsDisposed) return;
        _foreground = foreground;
        Visible = foreground;
        if (foreground && _connected && _sizeChangedWhileHidden) { _sizeChangedWhileHidden = false; _resizeDebounce.Start(); }
    }

    private bool _foreground = true;
    private bool _sizeChangedWhileHidden;

    /// <summary>The control's own text for a disconnect reason (null when it has none).</summary>
    public string? DescribeDisconnect(int reason)
    {
        if (_closing || IsDisposed || GetOcx() is not { } ocx) return null;
        try
        {
            dynamic d = ocx;
            uint ext = 0;
            try { ext = (uint)d.ExtendedDisconnectReason; } catch (Exception) { }
            return (string?)d.GetErrorDescription((uint)reason, ext);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Safe at any time, including before the control exists and while the tab is closing.</summary>
    public void Disconnect()
    {
        if (IsDisposed || GetOcx() is not { } ocx) return;
        try { dynamic d = ocx; if (d.Connected != 0) d.Disconnect(); }
        catch (Exception) { /* already gone: nothing to disconnect */ }
    }

    /// <summary>Set while the tab closes: from then on nothing touches the ActiveX control.</summary>
    private bool _closing;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_connected || _closing) return;
        if (!_foreground) { _sizeChangedWhileHidden = true; return; }
        _resizeDebounce.Stop(); _resizeDebounce.Start();
    }

    /// <summary>Dynamic resolution: resize the remote desktop to the tab (Win 8.1+ hosts).</summary>
    private void FitSessionToControl()
    {
        if (_closing || IsDisposed || Width < 200 || Height < 200 || GetOcx() is not { } ocx) return;
        try { ((dynamic)ocx).UpdateSessionDisplaySettings((uint)Width, (uint)Height, (uint)Width, (uint)Height, 0u, _scale, _scale >= 175 ? 180u : _scale >= 125 ? 140u : 100u); }
        catch (Exception) { /* older server: SmartSizing scales instead; or the session just ended */ }
    }

    private bool _hooked;

    private void HookEvents(object ocx)
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            ComEventsHelper.Combine(ocx, EventsIid, DispIdOnConnected, new Action(() => { if (!_closing) Connected?.Invoke(); }));
            ComEventsHelper.Combine(ocx, EventsIid, DispIdOnLoginComplete, new Action(() => { _connected = true; if (_scale != 100) _resizeDebounce.Start(); if (!_closing) LoginComplete?.Invoke(); }));
            ComEventsHelper.Combine(ocx, EventsIid, DispIdOnDisconnected, new Action<int>(reason => { _connected = false; if (!_closing) Disconnected?.Invoke(reason); }));
        }
        catch (Exception) { /* events are a convenience; the session still works without them */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Dispose();
            Disconnect();
            _closing = true;
            Connected = null; LoginComplete = null; Disconnected = null;
        }
        // Tearing down the ActiveX can throw when its window is already gone; closing a tab must never crash the app.
        try { base.Dispose(disposing); } catch (Exception) { }
    }
}

/// <summary>How a session connects beyond the entry's own settings.</summary>
public sealed record RdpConnectOptions(bool FullScreen = false, bool AutoReconnect = true, ResolvedCredential? GatewayCredential = null,
    string? Title = null, bool OwnFullScreenWindow = false);

/// <summary>
/// Not reachable through IDispatch: used only for the drive list. The methods before DriveCollection only
/// keep the vtable order (IMsTscNonScriptable, IMsRdpClientNonScriptable, ...2, ...3) and are never called.
/// </summary>
[ComImport, Guid("B3378D90-0728-45C7-8ED7-B6159FB92219"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable3
{
    void _ClearTextPassword(); void _PutPortablePassword(); void _GetPortablePassword(); void _PutPortableSalt(); void _GetPortableSalt();
    void _PutBinaryPassword(); void _GetBinaryPassword(); void _PutBinarySalt(); void _GetBinarySalt(); void _ResetPassword();
    void _NotifyRedirectDeviceChange(); void _SendKeys();
    void _PutUIParentWindowHandle(); void _GetUIParentWindowHandle();
    void _PutShowRedirectionWarningDialog(); void _GetShowRedirectionWarningDialog();
    void _PutPromptForCredentials(); void _GetPromptForCredentials();
    void _PutNegotiateSecurityLayer(); void _GetNegotiateSecurityLayer();
    void _PutEnableCredSspSupport(); void _GetEnableCredSspSupport();
    void _PutRedirectDynamicDrives(); void _GetRedirectDynamicDrives();
    void _PutRedirectDynamicDevices(); void _GetRedirectDynamicDevices();
    void _GetDeviceCollection();
    [return: MarshalAs(UnmanagedType.IDispatch)] object DriveCollection();
}
