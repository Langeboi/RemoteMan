using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using RemoteMan.App.ViewModels;
using RemoteMan.Protocols.Rdp;

namespace RemoteMan.App.Views;

/// <summary>
/// Embedded RDP. File transfer uses RDP's own clipboard channel, which carries files both ways:
/// copy files in Explorer on either side and paste on the other. Dropping files on the side strip
/// puts them on the clipboard and focuses the session, so one Ctrl+V in the remote folder finishes it.
/// Dropping files on the remote desktop itself does the paste too: a click where they were dropped, then
/// Ctrl+V, so they land in the remote folder or on the desktop under the pointer (<see cref="DropPaste"/>).
/// </summary>
public partial class RdpSessionView : UserControl
{
    private RdpControl? _rdp;
    private CopyOnSelect? _copy;
    private RdpSessionViewModel? _vm;
    private bool _started;

    public RdpSessionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not RdpSessionViewModel vm) return;
        _started = true;
        _vm = vm;

        vm.ControlHandle = () => _rdp is { IsHandleCreated: true } r && Host.Visibility == Visibility.Visible ? r.Handle : IntPtr.Zero;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var first = NewControl(vm, (int)((ActualWidth - 22) * dpi), (int)(ActualHeight * dpi));

        vm.Disposed += (_, _) =>
        {
            // Closing the tab: each step on its own, so one failing teardown can't take the app down.
            var rdp = _rdp; _rdp = null;
            StopReconnecting();
            StopConnecting();
            try { _copy?.Dispose(); } catch (Exception) { }
            _copy = null;
            try { rdp?.Dispose(); } catch (Exception) { }
            try { Host.Child = null; Host.Dispose(); } catch (Exception) { }
        };

        // Only the tab you're looking at draws and listens to the mouse.
        vm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName != nameof(SessionViewModel.IsActive) || _rdp is null) return;
            _rdp.SetForeground(vm.IsActive);
            if (_copy is not null) _copy.Enabled = vm.IsActive;
            if (!vm.IsActive) Notice.IsOpen = false;
        };
        // Switching to this tab or back to the window: keyboard straight into the remote desktop. While it is still
        // connecting the request waits, and Reveal / EndReconnect focus it once it shows.
        vm.FocusRequested += () =>
        {
            if (_rdp is { IsHandleCreated: true } r && Host.Visibility == Visibility.Visible && vm.TakeFocus()) r.Focus();
        };

        if (!vm.FullScreen) StartConnecting(vm);

        try
        {
            vm.StatusText = $"RDP connecting to {vm.Via ?? vm.Endpoint.ToString()}…";
            if (vm.Credential.UseWindowsLogin && !SsoPolicy.AllowsDefaultCredentials(vm.Endpoint.Host))
                vm.StatusText = SsoPolicy.Hint;
            Connect(first, vm);
        }
        catch (Exception ex)
        {
            StopConnecting();
            Host.Visibility = Visibility.Collapsed;
            State.Text = $"Could not start RDP: {ex.Message}";
            vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromException(ex, RemoteMan.Core.Model.EntryKind.Rdp));
        }
    }

    /// <summary>A fresh RDP control in the host, its events wired. A reconnect gets a new one too, so nothing
    /// from the dropped connection (half-torn-down channels, the control's own error state) carries over.</summary>
    private RdpControl NewControl(RdpSessionViewModel vm, int width, int height)
    {
        var rdp = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };
        _rdp = rdp;
        Host.Child = rdp;
        rdp.Width = width;
        rdp.Height = height;

        rdp.Connected += () =>
        {
            if (!ReferenceEquals(rdp, _rdp)) return;
            if (_reconnectAttempt > 0) { OnReconnectTransportUp(vm); return; }
            OnTransportUp(vm);
            vm.StatusText = $"RDP {vm.Via ?? vm.Endpoint.ToString()}{(vm.UsedFallback ? " (fallback)" : "")}";
        };
        rdp.LoginComplete += () =>
        {
            if (!ReferenceEquals(rdp, _rdp)) return;
            _signedIn = true;
            if (_reconnectAttempt > 0) EndReconnect(vm);
            Reveal();
            vm.StatusText = $"RDP {vm.Via ?? vm.Endpoint.ToString()}  {rdp.Width}x{rdp.Height}";
        };
        rdp.Disconnected += reason =>
        {
            if (!ReferenceEquals(rdp, _rdp)) return; // tab is closing, or a reconnect replaced this control
            var ext = rdp.ExtendedDisconnectReason();
            if (_reconnectAttempt > 0) { ReconnectAttemptFailed(vm); return; }
            StopConnecting();
            if (_signedIn && WantsReconnect(vm, reason, ext)) { BeginReconnect(vm); return; }
            vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromRdpDisconnect(reason, rdp.DescribeDisconnect(reason)));
            vm.StatusText = $"RDP disconnected (code {reason})";
            Host.Visibility = Visibility.Collapsed;
            State.Text = $"Disconnected from {vm.Endpoint} (code {reason}).";
        };

        // Copy on select needs clipboard redirection to bring the text back.
        if (vm.Settings.CopyOnSelect && vm.Settings.RedirectClipboard)
        {
            try { _copy?.Dispose(); } catch (Exception) { }
            _copy = new CopyOnSelect(rdp);
            if (!vm.IsActive) _copy.Enabled = false;
        }

        // Files dragged from this PC onto the remote desktop. The ActiveX control's own windows don't take drops,
        // so Windows hands the drop to the nearest window up the chain that does: this control.
        try
        {
            rdp.AllowDrop = true;
            rdp.DragEnter += OnSessionDragOver;
            rdp.DragOver += OnSessionDragOver;
            rdp.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] files)
                    DropOnSession(files, new System.Drawing.Point(e.X, e.Y));
            };
        }
        catch (Exception) { /* no drop on this control: the side strip still sends files */ }
        return rdp;
    }

    private static void Connect(RdpControl rdp, RdpSessionViewModel vm) =>
        rdp.Connect(vm.Endpoint, vm.Credential, vm.Settings, new RdpConnectOptions(
            FullScreen: vm.FullScreen, AutoReconnect: vm.AutoReconnect, GatewayCredential: vm.GatewayCredential, Title: vm.Entry.Name));

    // ---- Reconnecting in the background ----
    // A signed-in session that drops on its own (server restart, network gone, idle limit) is tried again quietly
    // for up to a minute. It comes back without a word, or its tab closes without one. Sign-outs, "Disconnect"
    // inside the session and closing the tab here are left alone (RdpReconnect.ShouldRetry).

    private bool _signedIn;
    private int _reconnectAttempt;          // 0 = not reconnecting
    private DateTime _reconnectUntil;
    private bool _reconnectSigningIn;       // the latest try reached the server and is signing in
    private System.Windows.Threading.DispatcherTimer? _reconnectTimer;
    private System.Windows.Threading.DispatcherTimer? _reconnectDeadline;
    private System.Windows.Media.Animation.DoubleAnimation? _reconnectSpin;

    private static bool WantsReconnect(RdpSessionViewModel vm, int reason, int ext)
    {
        // The gateway switch was flipped and closed the connection on purpose: always come back on the new path.
        if (Services.GatewayService.Instance.JustRerouted && reason is not (1 or 2)) return true;
        return vm.AutoReconnect && RemoteMan.Core.Networking.RdpReconnect.ShouldRetry(reason, ext);
    }

    private void BeginReconnect(RdpSessionViewModel vm)
    {
        _reconnectUntil = DateTime.UtcNow + RemoteMan.Core.Networking.RdpReconnect.Window;
        vm.IsReconnecting = true;
        vm.StatusText = $"RDP connection to {vm.Via ?? vm.Endpoint.ToString()} lost: reconnecting…";
        Notice.IsOpen = false;
        // Hidden, not collapsed: the control keeps its size, so the desktop comes back at the same resolution.
        Host.Visibility = Visibility.Hidden;
        State.Text = "";
        ReconnText.Text = $"Reconnecting to {vm.Entry.Name}…";
        Reconnecting.Visibility = Visibility.Visible;
        _reconnectSpin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        ReconnSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, _reconnectSpin);

        _reconnectDeadline = new System.Windows.Threading.DispatcherTimer { Interval = RemoteMan.Core.Networking.RdpReconnect.Window };
        _reconnectDeadline.Tick += (_, _) =>
        {
            _reconnectDeadline?.Stop();
            // A try that is already signing in gets to finish; it closes the tab itself if it fails.
            if (!_reconnectSigningIn) GiveUp(vm);
        };
        _reconnectDeadline.Start();
        ScheduleReconnect(vm);
    }

    private void ScheduleReconnect(RdpSessionViewModel vm)
    {
        _reconnectAttempt++;
        _reconnectSigningIn = false;
        var wait = Services.GatewayService.Instance.JustRerouted ? TimeSpan.FromMilliseconds(500)
            : RemoteMan.Core.Networking.RdpReconnect.Delay(_reconnectAttempt);
        _reconnectTimer?.Stop();
        _reconnectTimer = new System.Windows.Threading.DispatcherTimer { Interval = wait };
        _reconnectTimer.Tick += async (_, _) =>
        {
            _reconnectTimer?.Stop();
            if (_rdp is null || _reconnectAttempt == 0) return;
            if (DateTime.UtcNow >= _reconnectUntil) { GiveUp(vm); return; }

            // Moved on or off site since it connected: reconnect the way that works from here now.
            if (await vm.RerouteAsync()) vm.StatusText = $"RDP connection lost: reconnecting through {vm.Via ?? vm.Endpoint.ToString()}…";
            if (_rdp is null || _reconnectAttempt == 0) return;

            // A quick knock on the port first: the RDP control takes 20 s or more to give up on a host that's down.
            var port = vm.Endpoint.Port > 0 ? vm.Endpoint.Port : 3389;
            bool up;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await RemoteMan.Core.Networking.EndpointResolver.TcpProbeAsync(vm.Endpoint.Host, port, cts.Token); up = true; }
                catch (Exception) { up = false; }
            }
            if (_rdp is null || _reconnectAttempt == 0) return;   // closed meanwhile
            if (!up) { ReconnectAttemptFailed(vm); return; }

            var old = _rdp;
            var size = (old.Width, old.Height);
            var rdp = NewControl(vm, size.Width, size.Height);
            try { old.Dispose(); } catch (Exception) { }
            try
            {
                Connect(rdp, vm);
                if (!vm.IsActive) rdp.SetForeground(false); // background tab: don't draw (as before the drop)
            }
            catch (Exception) { ReconnectAttemptFailed(vm); }
        };
        _reconnectTimer.Start();
    }

    /// <summary>The server answered: the session is coming back. With a stored login it signs in on its own and
    /// LoginComplete shows the desktop; without one, the sign-in screen needs you, so show it now.</summary>
    private void OnReconnectTransportUp(RdpSessionViewModel vm)
    {
        _reconnectSigningIn = true;
        var c = vm.Credential;
        if (string.IsNullOrEmpty(c.Password) && !c.UseWindowsLogin) { EndReconnect(vm); return; }
        // Safety net for servers that show a banner or notice before the desktop (as on the first connect).
        _revealTimer?.Stop();
        _revealTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _revealTimer.Tick += (_, _) => { _revealTimer?.Stop(); if (_reconnectAttempt > 0) EndReconnect(vm); };
        _revealTimer.Start();
    }

    private void ReconnectAttemptFailed(RdpSessionViewModel vm)
    {
        if (_rdp is null || _reconnectAttempt == 0) return;
        if (DateTime.UtcNow >= _reconnectUntil) { GiveUp(vm); return; }
        ScheduleReconnect(vm);
    }

    /// <summary>Back: drop the indicator and carry on as if nothing happened.</summary>
    private void EndReconnect(RdpSessionViewModel vm)
    {
        StopReconnecting();
        vm.IsReconnecting = false;
        Host.Visibility = Visibility.Visible;
        if (vm.IsActive) _rdp?.Focus();
    }

    /// <summary>A minute went by without the session coming back: close the tab quietly.</summary>
    private void GiveUp(RdpSessionViewModel vm)
    {
        StopReconnecting();
        vm.IsReconnecting = false;
        if (_rdp is null) return;
        vm.RequestClose();
    }

    private void StopReconnecting()
    {
        _reconnectAttempt = 0;
        _reconnectSigningIn = false;
        _reconnectTimer?.Stop(); _reconnectTimer = null;
        _reconnectDeadline?.Stop(); _reconnectDeadline = null;
        _revealTimer?.Stop(); _revealTimer = null;
        if (_reconnectSpin is not null) { ReconnSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null); _reconnectSpin = null; }
        Reconnecting.Visibility = Visibility.Collapsed;
    }

    // ---- Connecting animation ----

    private bool _spinning;
    private TimeSpan? _spinStart;
    private System.Windows.Threading.DispatcherTimer? _revealTimer;

    /// <summary>Hides the RDP control behind the animation until the desktop is ready to show.</summary>
    private void StartConnecting(RdpSessionViewModel vm)
    {
        // IP and port the session goes to (a tunnelled session shows its target and bastion).
        ConnHost.Text = vm.Via ?? $"{vm.Endpoint.Host}:{(vm.Endpoint.Port > 0 ? vm.Endpoint.Port : 3389)}";
        ConnStage.Text = vm.Settings.UseRdGateway && !string.IsNullOrEmpty(vm.Settings.GatewayHost)
            ? $"Connecting through gateway {vm.Settings.GatewayHost}…"
            : "Connecting…";
        if (vm.UsedFallback && vm.Entry.Endpoints.Count > 0)
        {
            ConnFallback.Text = $"{vm.Entry.Endpoints[0]} didn't answer, using fallback";
            ConnFallback.Visibility = Visibility.Visible;
        }
        // Hidden, not collapsed: the control keeps its size, so the session opens at the right resolution.
        Host.Visibility = Visibility.Hidden;
        Connecting.Visibility = Visibility.Visible;
        _spinStart = null;
        if (!_spinning) { _spinning = true; System.Windows.Media.CompositionTarget.Rendering += OnSpinFrame; }
    }

    /// <summary>
    /// The transport is up and the server is signing in. With a stored password the desktop follows
    /// (LoginComplete reveals it); without one the remote sign-in screen needs the user, so show it now.
    /// The timer is a safety net for servers that show a banner or notice before the desktop.
    /// </summary>
    private void OnTransportUp(RdpSessionViewModel vm)
    {
        if (Connecting.Visibility != Visibility.Visible) return;
        var c = vm.Credential;
        if (string.IsNullOrEmpty(c.Password) && !c.UseWindowsLogin) { Reveal(); return; }
        var who = string.IsNullOrEmpty(c.UserName) ? "" : string.IsNullOrEmpty(c.Domain) ? $" as {c.UserName}" : $" as {c.Domain}\\{c.UserName}";
        ConnStage.Text = $"Signing in{who}…";
        _revealTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _revealTimer.Tick += (_, _) => Reveal();
        _revealTimer.Start();
    }

    private void Reveal()
    {
        if (Connecting.Visibility != Visibility.Visible) return;
        StopConnecting();
        Host.Visibility = Visibility.Visible;
        if (_vm?.IsActive == true) _rdp?.Focus();
    }

    private void StopConnecting()
    {
        _revealTimer?.Stop();
        _revealTimer = null;
        if (_spinning) { _spinning = false; System.Windows.Media.CompositionTarget.Rendering -= OnSpinFrame; }
        Connecting.Visibility = Visibility.Collapsed;
    }

    // Ring geometry: radius 18 around (20,20). One turn every 2.2 s; the arc grows from a dot to about
    // 220 degrees and back every 2.6 s, its head running ahead while it shrinks (like the Windows 11 ring).
    private const double SpinRadius = 18, SpinTurn = 2.2, SpinBreath = 2.6;

    private void OnSpinFrame(object? sender, EventArgs e)
    {
        if (!Connecting.IsVisible || e is not System.Windows.Media.RenderingEventArgs r) return;
        _spinStart ??= r.RenderingTime;
        var t = (r.RenderingTime - _spinStart.Value).TotalSeconds;

        static double Ease(double x) => x * x * (3 - 2 * x);
        var p = t % SpinBreath / SpinBreath;
        double length, offset; // degrees
        if (p < 0.5) { var u = Ease(p * 2); length = 3 + 220 * u; offset = 64 * u; }
        else { var u = Ease((p - 0.5) * 2); length = 223 - 220 * u; offset = 64 + 293 * u; }
        var start = (t % SpinTurn / SpinTurn * 360 + offset - 90) * Math.PI / 180;
        var end = start + length * Math.PI / 180;

        var g = new System.Windows.Media.StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(20 + SpinRadius * Math.Cos(start), 20 + SpinRadius * Math.Sin(start)), false, false);
            ctx.ArcTo(new Point(20 + SpinRadius * Math.Cos(end), 20 + SpinRadius * Math.Sin(end)), new Size(SpinRadius, SpinRadius), 0,
                length > 180, System.Windows.Media.SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        Arc.Data = g;
    }

    private void OnCancelConnect(object sender, RoutedEventArgs e)
    {
        StopConnecting();
        _vm?.RequestClose();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Strip.Background = (System.Windows.Media.Brush)FindResource("Select");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => Strip.Background = (System.Windows.Media.Brush)FindResource("Panel");

    private void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Send(files);
    }

    // ---- Dropping files onto the remote desktop ----

    /// <summary>The session is showing and signed in: a drop on it can be pasted there.</summary>
    private bool CanDropOnSession =>
        _vm is not null && _rdp is { IsHandleCreated: true } && _signedIn && _reconnectAttempt == 0
        && Host.Visibility == Visibility.Visible && _vm.IsActive;

    private void OnSessionDragOver(object? sender, System.Windows.Forms.DragEventArgs e) =>
        e.Effect = CanDropOnSession && e.Data?.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop) == true
            ? System.Windows.Forms.DragDropEffects.Copy : System.Windows.Forms.DragDropEffects.None;

    // The same through WPF, for the case where Windows gives the drop to the window around the control instead.
    private void OnHostDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanDropOnSession && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnHostDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var p = Host.PointToScreen(e.GetPosition(Host));   // physical pixels, as the input calls want
        DropOnSession(files, new System.Drawing.Point((int)Math.Round(p.X), (int)Math.Round(p.Y)));
    }

    private bool _dropping;

    /// <summary>
    /// Files dropped at <paramref name="screen"/> on the remote desktop: on the clipboard, then a click on that spot
    /// and Ctrl+V, so Explorer on the other side pastes them into the folder or desktop under the pointer.
    /// The pauses let the new clipboard reach the server and the click settle before the paste.
    /// If the click or the keys can't go to the session safely, the files stay on the clipboard to paste by hand.
    /// </summary>
    private async void DropOnSession(string[] files, System.Drawing.Point screen)
    {
        if (files.Length == 0 || !CanDropOnSession || _dropping) return;
        if (!PutOnClipboard(files, out var what)) return;
        _dropping = true;
        try
        {
            var rdp = _rdp!;
            _vm!.StatusText = $"Sending {what} to {_vm.Entry.Name}…";
            await Task.Delay(150);
            if (_rdp is null) return;   // tab closed meanwhile
            if (!ReferenceEquals(rdp, _rdp) || !CanDropOnSession || !DropPaste.ClickAt(rdp, screen)) { ReadyToPaste(what); return; }
            await Task.Delay(450);
            if (_rdp is null) return;
            if (!ReferenceEquals(rdp, _rdp) || !CanDropOnSession) { ReadyToPaste(what); return; }
            rdp.Focus();
            if (!DropPaste.Paste(rdp)) { ReadyToPaste(what); return; }
            _vm.StatusText = $"{what} pasted where you dropped {(files.Length == 1 ? "it" : "them")}. Nothing there? Press Ctrl+V in a remote folder.";
        }
        finally { _dropping = false; }
    }

    private void OnSendFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send files to the remote session" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) Send(dlg.FileNames);
    }

    private void Send(IEnumerable<string> paths)
    {
        if (PutOnClipboard(paths.ToArray(), out var what)) ReadyToPaste(what);
    }

    /// <summary>
    /// The files on the local clipboard, which RDP offers to the remote side. False (with a notice) when the
    /// entry doesn't redirect the clipboard or the clipboard is busy. <paramref name="what"/> names them for messages.
    /// </summary>
    private bool PutOnClipboard(string[] files, out string what)
    {
        what = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} files";
        if (_vm is null || files.Length == 0) return false;
        if (!_vm.Settings.RedirectClipboard)
        {
            ShowNotice("Files can't be sent from this entry",
                "Clipboard redirection is off. Edit the entry, turn on \"Redirect clipboard\", and connect again.");
            return false;
        }
        var list = new StringCollection();
        list.AddRange(files);

        // Same clipboard content Explorer puts there on Ctrl+C: the file list plus "this is a copy".
        // The RDP client offers it to the remote side, where Explorer's Paste pulls the files across.
        // Both formats are native clipboard types (CF_HDROP and an HGLOBAL stream), so nothing here goes
        // through the serializer that .NET 9+ removed: only plain string, string[] and stream data on the clipboard.
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY

        // The RDP control and clipboard managers often hold the clipboard for a moment; retry instead of failing silently.
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); last = null; break; }
            catch (System.Runtime.InteropServices.COMException ex) { last = ex; Thread.Sleep(50); }
        }
        if (last is not null)
        {
            ShowNotice("Couldn't put the files on the clipboard", last.Message);
            return false;
        }
        return true;
    }

    /// <summary>The files are on the clipboard: tell the user to paste them in a remote folder.</summary>
    private void ReadyToPaste(string what)
    {
        if (_vm is null) return;
        ShowNotice($"{what} ready to paste",
            "Click into the remote session, open the folder you want them in, and press Ctrl+V (or right-click > Paste). " +
            "If Paste is greyed out, the server blocks clipboard file copy (policy \"Do not allow Clipboard redirection\").");
        _vm.StatusText = $"{what} on the clipboard: paste in a remote folder";
        _rdp?.Focus();
    }

    private bool _noticeHooked;
    private readonly System.Windows.Threading.DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private void ShowNotice(string title, string text)
    {
        NoticeTitle.Text = title;
        NoticeText.Text = text;
        Notice.HorizontalOffset = 12;
        Notice.VerticalOffset = 12;
        Notice.IsOpen = true;
        // Popups are topmost windows: hide this one when RemoteMan moves or loses focus.
        if (Window.GetWindow(this) is { } w && !_noticeHooked)
        {
            _noticeHooked = true;
            w.Deactivated += (_, _) => Notice.IsOpen = false;
            w.LocationChanged += (_, _) => Notice.IsOpen = false;
        }
        _noticeTimer.Stop();
        _noticeTimer.Tick -= OnNoticeTimeout;
        _noticeTimer.Tick += OnNoticeTimeout;
        _noticeTimer.Start();
    }

    private void OnNoticeTimeout(object? sender, EventArgs e) { _noticeTimer.Stop(); Notice.IsOpen = false; }
    private void OnCloseNotice(object sender, RoutedEventArgs e) => OnNoticeTimeout(null, EventArgs.Empty);
}
