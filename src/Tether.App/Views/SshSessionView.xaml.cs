using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Tether.App.Services;
using Tether.App.ViewModels;
using Tether.Protocols.Ssh;

namespace Tether.App.Views;

/// <summary>xterm.js in WebView2, fed by an SSH.NET shell. Output is batched per frame.</summary>
public partial class SshSessionView : UserControl
{
    private static readonly KnownHosts KnownHosts = new(AppPaths.KnownHosts);

    private SshSession? _ssh;
    private SshSessionViewModel? _vm;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _flush;
    private bool _started;

    public SshSessionView()
    {
        InitializeComponent();
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) => Flush(), Dispatcher);
        Loaded += OnLoaded;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Post(new { t = "focus" }); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not SshSessionViewModel vm) return;
        _started = true;
        _vm = vm;
        var env = await WebViewEnvironment.GetAsync();
        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("tether-terminal.local",
            Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"), CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://tether-terminal.local/terminal.html");
        vm.Disposed += (_, _) => Close();
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                await StartAsync((uint)m.GetProperty("cols").GetInt32(), (uint)m.GetProperty("rows").GetInt32());
                break;
            case "data":
                _ssh?.Send(m.GetProperty("d").GetString() ?? "");
                break;
            case "resize":
                _ssh?.Resize((uint)m.GetProperty("cols").GetInt32(), (uint)m.GetProperty("rows").GetInt32());
                break;
        }
    }

    private async Task StartAsync(uint cols, uint rows)
    {
        if (_vm is null || _ssh is not null) return;
        _ssh = new SshSession();
        _ssh.Output += s => { lock (_pending) _pending.Append(s); };
        _ssh.Info += s => Dispatcher.BeginInvoke(() => Post(new { t = "info", d = s }));
        _ssh.Closed += ex => Dispatcher.BeginInvoke(() =>
        {
            Post(new { t = "info", d = ex is null ? "\r\n[session closed]" : $"\r\n[disconnected: {ex.Message}]" });
            _vm.StatusText = "SSH disconnected";
        });

        var fb = _vm.UsedFallback ? " (fallback)" : "";
        Post(new { t = "info", d = $"Connecting to {_vm.Endpoint}{fb}…" });
        try
        {
            await _ssh.ConnectAsync(_vm.Endpoint, _vm.Credential, _vm.Settings, KnownHosts, cols, rows);
            Post(new { t = "info", d = _ssh.NegotiatedSummary ?? "" });
            _vm.StatusText = $"SSH {_vm.Endpoint}  {_ssh.NegotiatedSummary}";
            _flush.Start();
        }
        catch (Exception ex)
        {
            Post(new { t = "info", d = $"Connection failed: {ex.Message}" });
            _vm.StatusText = "SSH failed";
        }
    }

    private void Flush()
    {
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

    private void Close()
    {
        _flush.Stop();
        _ssh?.Dispose();
        _ssh = null;
        Web.Dispose();
    }
}
