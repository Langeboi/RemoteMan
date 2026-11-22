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

        _rdp = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };
        Host.Child = _rdp;
        vm.ControlHandle = () => _rdp is { IsHandleCreated: true } r && Host.Visibility == Visibility.Visible ? r.Handle : IntPtr.Zero;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        _rdp.Width = (int)((ActualWidth - 22) * dpi);
        _rdp.Height = (int)(ActualHeight * dpi);

        _rdp.Connected += () =>
        {
            OnTransportUp(vm);
            vm.StatusText = $"RDP {vm.Via ?? vm.Endpoint.ToString()}{(vm.UsedFallback ? " (fallback)" : "")}";
        };
        _rdp.LoginComplete += () => { Reveal(); if (_rdp is { } r) vm.StatusText = $"RDP {vm.Via ?? vm.Endpoint.ToString()}  {r.Width}x{r.Height}"; };
        _rdp.Disconnected += reason =>
        {
            if (_rdp is null) return; // tab is closing
            StopConnecting();
            vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromRdpDisconnect(reason, _rdp.DescribeDisconnect(reason)));
            vm.StatusText = $"RDP disconnected (code {reason})";
            Host.Visibility = Visibility.Collapsed;
            State.Text = $"Disconnected from {vm.Endpoint} (code {reason}).";
        };
        vm.Disposed += (_, _) =>
        {
            // Closing the tab: each step on its own, so one failing teardown can't take the app down.
            var rdp = _rdp; _rdp = null;
            StopConnecting();
            try { _copy?.Dispose(); } catch (Exception) { }
            _copy = null;
            try { rdp?.Dispose(); } catch (Exception) { }
            try { Host.Child = null; Host.Dispose(); } catch (Exception) { }
        };

        // Copy on select needs clipboard redirection to bring the text back.
        if (vm.Settings.CopyOnSelect && vm.Settings.RedirectClipboard) _copy = new CopyOnSelect(_rdp);

        // Only the tab you're looking at draws and listens to the mouse.
        vm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName != nameof(SessionViewModel.IsActive) || _rdp is null) return;
            _rdp.SetForeground(vm.IsActive);
            if (_copy is not null) _copy.Enabled = vm.IsActive;
            if (!vm.IsActive) Notice.IsOpen = false;
        };

        if (!vm.FullScreen) StartConnecting(vm);

        try
        {
            vm.StatusText = $"RDP connecting to {vm.Via ?? vm.Endpoint.ToString()}…";
            if (vm.Credential.UseWindowsLogin && !SsoPolicy.AllowsDefaultCredentials(vm.Endpoint.Host))
                vm.StatusText = SsoPolicy.Hint;
            _rdp.Connect(vm.Endpoint, vm.Credential, vm.Settings, new RdpConnectOptions(
                FullScreen: vm.FullScreen, AutoReconnect: vm.AutoReconnect, GatewayCredential: vm.GatewayCredential, Title: vm.Entry.Name));
        }
        catch (Exception ex)
        {
            StopConnecting();
            Host.Visibility = Visibility.Collapsed;
            State.Text = $"Could not start RDP: {ex.Message}";
            vm.ReportProblem(RemoteMan.Core.Networking.ConnectionProblem.FromException(ex, RemoteMan.Core.Model.EntryKind.Rdp));
        }
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

    private void OnSendFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send files to the remote session" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) Send(dlg.FileNames);
    }

    private void Send(IEnumerable<string> paths)
    {
        if (_vm is null) return;
        if (!_vm.Settings.RedirectClipboard)
        {
            ShowNotice("Files can't be sent from this entry",
                "Clipboard redirection is off. Edit the entry, turn on \"Redirect clipboard\", and connect again.");
            return;
        }
        var files = paths.ToArray();
        if (files.Length == 0) return;
        var list = new StringCollection();
        list.AddRange(files);

        // Same clipboard content Explorer puts there on Ctrl+C: the file list plus "this is a copy".
        // The RDP client offers it to the remote side, where Explorer's Paste pulls the files across.
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
            return;
        }

        var what = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} files";
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
