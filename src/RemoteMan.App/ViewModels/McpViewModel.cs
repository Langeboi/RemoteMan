using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Services;
using RemoteMan.App.Updates;
using RemoteMan.Core.Mcp;

namespace RemoteMan.App.ViewModels;

/// <summary>One tool call, for the activity list (so it's always visible what AI apps did).</summary>
public sealed record McpActivity(DateTime At, string Client, string Tool, bool Ok, string? Detail)
{
    public string Time => At.ToString("HH:mm:ss");
    public string Title => McpCatalog.Tools.FirstOrDefault(t => t.Name == Tool)?.Title ?? Tool;
}

/// <summary>A choice in the Terminals box.</summary>
public sealed record McpTerminalOption(McpTerminalAccess Value, string Label);

/// <summary>
/// RemoteMan as an MCP server: the switch, what AI apps may do, how to connect one, and what they did.
/// The pipe only listens while switched on, and only for this Windows user.
/// </summary>
public sealed partial class McpViewModel : ObservableObject, IDisposable
{
    private const int MaxActivity = 40;
    private readonly AppSettings _settings;
    private readonly MainViewModel _main;
    private readonly Dispatcher _ui;
    private McpListener? _listener;

    public McpViewModel(AppSettings settings, MainViewModel main, Dispatcher ui)
    {
        _settings = settings;
        _main = main;
        _ui = ui;
        _enabled = settings.Mcp.Enabled;
        _terminal = TerminalOptions.First(o => o.Value == settings.Mcp.Terminal);
        _openConnections = settings.Mcp.OpenConnections;
        _deviceConfigs = settings.Mcp.DeviceConfigs;
        Apply();
    }

    public IReadOnlyList<McpTerminalOption> TerminalOptions { get; } =
    [
        new(McpTerminalAccess.Off, "Off"),
        new(McpTerminalAccess.Read, "Read output only"),
        new(McpTerminalAccess.Type, "Read and type, I press Enter"),
        new(McpTerminalAccess.Run, "Read, type and run commands"),
    ];

    public ObservableCollection<McpActivity> Activity { get; } = new();
    public bool HasActivity => Activity.Count > 0;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsLive))] private bool _enabled;
    [ObservableProperty] private McpTerminalOption _terminal;
    [ObservableProperty] private bool _openConnections;
    [ObservableProperty] private bool _deviceConfigs;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsLive))] private IReadOnlyList<string> _clients = [];
    [ObservableProperty] private string _setupResult = "";
    [ObservableProperty] private bool _setupOk = true;

    /// <summary>An AI app is connected right now (green dot on the AI button).</summary>
    public bool IsLive => Enabled && Clients.Count > 0;

    public string StatusText => !Enabled ? "Off. AI apps can't reach RemoteMan."
        : Clients.Count == 0 ? "On. No AI app connected right now."
        : $"Connected: {string.Join(", ", Clients.Select(Pretty).Distinct())}";

    public string ExePath => McpClients.ExePath;

    /// <summary>Shows a line in the main window's status bar.</summary>
    public Action<string>? StatusMessage { get; set; }

    partial void OnEnabledChanged(bool value) { _settings.Mcp.Enabled = value; Save(); Apply(); }
    partial void OnTerminalChanged(McpTerminalOption value) { _settings.Mcp.Terminal = value.Value; Save(); }
    partial void OnOpenConnectionsChanged(bool value) { _settings.Mcp.OpenConnections = value; Save(); }
    partial void OnDeviceConfigsChanged(bool value) { _settings.Mcp.DeviceConfigs = value; Save(); }

    /// <summary>Re-reads the settings (after the setup window changed them) and starts or stops listening.</summary>
    public void Reload()
    {
        Enabled = _settings.Mcp.Enabled;
        Apply();
    }

    private void Apply()
    {
        if (Enabled && _listener is null)
        {
            _listener = new McpListener(NewServer);
            _listener.ClientsChanged += () => _ui.BeginInvoke(() => Clients = _listener?.Clients ?? []);
            _listener.Problem += m => _ui.BeginInvoke(() => StatusMessage?.Invoke($"AI apps: couldn't listen for connections ({m})"));
            _listener.Start();
        }
        else if (!Enabled && _listener is not null)
        {
            _listener.Dispose();
            _listener = null;
            Clients = [];
        }
    }

    private McpServer NewServer()
    {
        McpServer? server = null;
        server = new McpServer(UpdateService.Instance.CurrentVersion, async (tool, args, ct) =>
        {
            var op = _ui.InvokeAsync(() => _main.RunMcpToolAsync(tool, args, ct));
            var result = await (await op.Task);
            _ = _ui.BeginInvoke(() => Log(new McpActivity(DateTime.Now, Pretty(server?.ClientName), tool, !result.IsError,
                result.IsError ? result.Text : null)));
            return result;
        });
        return server;
    }

    private void Log(McpActivity a)
    {
        Activity.Insert(0, a);
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
        OnPropertyChanged(nameof(HasActivity));
    }

    /// <summary>"claude-ai" → "Claude Desktop" and so on; other names as they come.</summary>
    private static string Pretty(string? client) => client switch
    {
        null or "" => "AI app",
        "claude-ai" => "Claude Desktop",
        "claude-code" => "Claude Code",
        var c when c.StartsWith("Visual Studio Code", StringComparison.OrdinalIgnoreCase) => "VS Code",
        var c => c,
    };

    // ---------------- connect an AI app ----------------

    [RelayCommand]
    private void AddToClaudeDesktop()
    {
        if (!Enabled) Enabled = true;
        var (ok, text) = McpClients.AddToClaudeDesktop();
        SetupOk = ok;
        SetupResult = text;
    }

    [RelayCommand] private void CopyClaudeCode() => Copy(McpClients.ClaudeCodeCommand, "Copied. Paste it in a terminal where Claude Code is installed.");
    [RelayCommand] private void CopyVsCode() => Copy(McpClients.VsCodeJson, "Copied. Paste it into .vscode/mcp.json (or MCP: Open User Configuration in VS Code).");
    [RelayCommand] private void CopyJson() => Copy(McpClients.GenericJson, "Copied. Paste it into your AI app's MCP config (the mcpServers section).");

    private void Copy(string text, string done)
    {
        if (!Enabled) Enabled = true;
        try
        {
            Clipboard.SetDataObject(text, true);
            SetupOk = true;
            SetupResult = done;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetupOk = false;
            SetupResult = "The clipboard is busy. Try again.";
        }
    }

    [RelayCommand]
    private void ClearActivity()
    {
        Activity.Clear();
        OnPropertyChanged(nameof(HasActivity));
    }

    private void Save()
    {
        try { _settings.Save(); } catch (Exception ex) { StatusMessage?.Invoke($"Could not save settings: {ex.Message}"); }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }
}
