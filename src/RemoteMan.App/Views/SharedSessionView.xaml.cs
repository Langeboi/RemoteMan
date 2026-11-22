using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RemoteMan.App.Services;
using RemoteMan.App.ViewModels;
using RemoteMan.Core.Sharing;
using RemoteMan.Protocols.Rdp;

namespace RemoteMan.App.Views;

/// <summary>
/// Shows a teammate's shared session. Terminal: output arrives over the share link and is written
/// to xterm.js at the host's size; keystrokes go back only while you have control. RDP: the Windows
/// Desktop Sharing viewer connects through a local tunnel in the share link (works the same over the relay).
/// </summary>
public partial class SharedSessionView : UserControl
{
    private SharedSessionViewModel? _vm;
    private DesktopShareViewer? _viewer;
    private bool _started, _ready, _hadControl;
    private readonly System.Text.StringBuilder _pending = new();
    private readonly DispatcherTimer _flush;

    public SharedSessionView()
    {
        InitializeComponent();
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) => Flush(), Dispatcher);
        Loaded += OnLoaded;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && _ready) Post(new { t = "focus" }); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not SharedSessionViewModel vm) return;
        _started = true;
        _vm = vm;
        var c = vm.Client;
        c.StateChanged += s => Dispatcher.BeginInvoke(() => Apply(s));
        vm.Disposed += (_, _) => Close();
        if (vm.IsRdp) StartRdp(c);
        else await StartTerminalAsync(c);
        Apply(c.State);
    }

    // ---------------- terminal ----------------

    private async Task StartTerminalAsync(ShareClient c)
    {
        WaitTitle.Text = $"Joining {c.Welcome.EntryName}…";
        await Web.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("remoteman-terminal.local",
            Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"), CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "ready":
                    _ready = true;
                    Post(new { t = "fixed", cols = c.Cols, rows = c.Rows });
                    Post(new { t = "stdin", on = c.HasControl });
                    if (c.Welcome.History.Length > 0) Post(new { t = "out", d = c.Welcome.History });
                    Waiting.Visibility = Visibility.Collapsed;
                    Web.Visibility = Visibility.Visible;
                    lock (_pending) if (_pending.Length > 0) _flush.Start();
                    break;
                case "data":
                    c.SendInput(m.GetProperty("d").GetString() ?? "");
                    break;
                case "copy":
                    if (m.GetProperty("d").GetString() is { Length: > 0 } text) { try { Clipboard.SetDataObject(text, true); } catch (Exception) { } }
                    break;
                case "paste":
                    if (c.HasControl && Clipboard.ContainsText()) c.SendInput(Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r'));
                    break;
            }
        };
        c.Output += s => { lock (_pending) _pending.Append(s); Dispatcher.BeginInvoke(() => { if (_ready) _flush.Start(); }); };
        c.SizeChanged += (cols, rows) => Dispatcher.BeginInvoke(() => Post(new { t = "fixed", cols, rows }));
        core.Navigate("https://remoteman-terminal.local/terminal.html");
    }

    private void Flush()
    {
        _flush.Stop();
        string chunk;
        lock (_pending)
        {
            if (_pending.Length == 0) return;
            chunk = _pending.ToString();
            _pending.Clear();
        }
        Post(new { t = "out", d = chunk });
    }

    private void Post(object message)
    {
        if (Web.CoreWebView2 is { } core) core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    // ---------------- RDP ----------------

    private void StartRdp(ShareClient c)
    {
        WaitTitle.Text = $"Joining {c.Welcome.EntryName}…";
        if (c.Welcome.RdpConnectionString is not { Length: > 0 } ticket)
        {
            WaitTitle.Text = "The host's screen isn't being shared";
            WaitText.Text = "Windows Desktop Sharing didn't start on their PC. Ask them to stop sharing and try again.";
            return;
        }
        try
        {
            var port = c.StartTunnel();
            _viewer = new DesktopShareViewer { Dock = System.Windows.Forms.DockStyle.Fill };
            Host.Child = _viewer;
            Host.Visibility = Visibility.Visible;
            Waiting.Visibility = Visibility.Collapsed;
            // Our participant id is the viewer's name, so the host knows who to give mouse and keyboard.
            _viewer.Connect(DesktopShareTicket.PointAt(ticket, port), c.MyId, c.Welcome.RdpPassword ?? "");
        }
        catch (Exception ex)
        {
            Host.Visibility = Visibility.Collapsed;
            Waiting.Visibility = Visibility.Visible;
            WaitTitle.Text = "Couldn't show the shared screen";
            WaitText.Text = ex.Message;
        }
    }

    // ---------------- control ----------------

    private void Apply(ShareSnapshot s)
    {
        if (_vm is null) return;
        var mine = s.ControllerId == _vm.Client.MyId;
        if (_vm.IsRdp)
        {
            if (mine != _hadControl) _viewer?.RequestControl(mine);
            // Paused: the host switched tabs; hide the frozen picture and say so.
            var paused = s.Paused && _viewer is not null;
            Host.Visibility = paused || _viewer is null ? Visibility.Collapsed : Visibility.Visible;
            if (_viewer is not null) Waiting.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
            if (paused) { WaitTitle.Text = $"{s.Host?.Name ?? "The host"} switched to another tab"; WaitText.Text = "The picture comes back when they return to it."; }
        }
        else if (_ready)
        {
            Post(new { t = "stdin", on = mine });
            if (mine && !_hadControl) Post(new { t = "focus" });
        }
        _hadControl = mine;
        _vm.StatusText = $"Shared by {s.Host?.Name} · {_vm.Client.Via} · {(mine ? "you have control" : $"{s.Controller?.Name} has control")}";
    }

    private void Close()
    {
        _flush.Stop();
        var v = _viewer; _viewer = null;
        try { v?.Dispose(); } catch (Exception) { }
        try { Host.Child = null; Host.Dispose(); } catch (Exception) { }
        try { Web.Dispose(); } catch (Exception) { }
    }
}
