using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.App.Services;
using RemoteMan.Core.Ai;

namespace RemoteMan.App.ViewModels;

public enum AiPage { Pick, Auth, Chat }

/// <summary>
/// The AI assistant: provider setup, then a chat that is idle (on standby) until the user
/// opens it. Nothing is sent anywhere until the user presses Send.
/// </summary>
public sealed partial class AiAssistantViewModel : ObservableObject
{
    private const int ContextLines = 50;
    private readonly AppSettings _settings;
    private readonly Func<SessionViewModel?> _activeSession;
    private CancellationTokenSource? _cts;
    private IAiChatClient? _client;

    public AiAssistantViewModel(AppSettings settings, Func<SessionViewModel?> activeSession)
    {
        _settings = settings;
        _activeSession = activeSession;
        foreach (var p in settings.Ai.Providers) Connected.Add(p);
        _includeContext = settings.Ai.IncludeSessionContext;
        Active = settings.Ai.ActiveProvider;
        Page = Active is null ? AiPage.Pick : AiPage.Chat;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public IReadOnlyList<AiProviderInfo> Catalog => AiProviderInfo.All;
    public ObservableCollection<AiProviderConfig> Connected { get; } = new();
    public ObservableCollection<AiChatMessage> Messages { get; } = new();
    public IReadOnlyList<string> Suggestions { get; } =
        ["Explain the last command's output", "Why did that fail?", "How do I check disk usage here?"];

    // ---------- pages ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickPage), nameof(IsAuthPage), nameof(IsChatPage), nameof(ShowConversation), nameof(ShowAccountView))]
    private AiPage _page;

    public bool IsPickPage => Page == AiPage.Pick;
    public bool IsAuthPage => Page == AiPage.Auth;
    public bool IsChatPage => Page == AiPage.Chat;
    public bool IsConnected => Connected.Count > 0;
    public bool CanGoBack => IsConnected || Page == AiPage.Auth;

    // ---------- setup (pick + sign in) ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupHasModes), nameof(SetupShowKeyForm), nameof(SetupShowAccountForm), nameof(SetupFieldLabel), nameof(SetupKeyNote), nameof(SetupShowKey), nameof(SetupShowEndpoint))]
    private AiProviderInfo? _setupProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupShowKeyForm), nameof(SetupShowAccountForm), nameof(IsKeyMode), nameof(IsAccountModeSelected))]
    private AiAuthMode _setupMode;

    [ObservableProperty] private string _setupEndpoint = "";
    [ObservableProperty] private string _setupModel = "";
    [ObservableProperty] private string _setupError = "";
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private IReadOnlyList<string> _setupModels = [];

    public bool SetupHasModes => SetupProvider?.SupportsAccount == true;
    public bool IsKeyMode => SetupMode == AiAuthMode.ApiKey;
    public bool IsAccountModeSelected => SetupMode == AiAuthMode.Account;
    public bool SetupShowKeyForm => SetupProvider is not null && SetupMode == AiAuthMode.ApiKey;
    public bool SetupShowAccountForm => SetupProvider is not null && SetupMode == AiAuthMode.Account;
    public bool SetupShowKey => SetupProvider?.NeedsKey == true || SetupProvider?.Kind == AiProviderKind.Custom;
    public bool SetupShowEndpoint => SetupProvider?.EndpointEditable == true;
    public string SetupFieldLabel => SetupProvider?.NeedsKey == true ? "API key" : "API key (optional)";
    public string SetupKeyNote => SetupProvider?.Kind is AiProviderKind.Ollama
        ? "Runs on your own hardware. Nothing leaves your network."
        : "Stored on this PC with Windows DPAPI. Never written to the shared database, so other users bring their own key.";

    /// <summary>Set by the view from its PasswordBox (kept out of bindings on purpose).</summary>
    public Func<string> ReadKeyInput { get; set; } = () => "";

    [RelayCommand]
    private void Pick(AiProviderInfo provider)
    {
        SetupProvider = provider;
        SetupMode = AiAuthMode.ApiKey;
        var existing = Connected.FirstOrDefault(c => c.Kind == provider.Kind);
        SetupEndpoint = existing?.Endpoint ?? provider.DefaultEndpoint ?? "";
        SetupModel = existing?.EffectiveModel ?? provider.DefaultModel;
        SetupModels = [];
        SetupError = "";
        Page = AiPage.Auth;
    }

    [RelayCommand] private void UseKeyMode() => SetupMode = AiAuthMode.ApiKey;
    [RelayCommand] private void UseAccountMode() => SetupMode = AiAuthMode.Account;

    [RelayCommand]
    private void Back() => Page = Page == AiPage.Auth ? AiPage.Pick : IsConnected ? AiPage.Chat : AiPage.Pick;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SetupProvider is not { } info) return;
        var cfg = new AiProviderConfig
        {
            Kind = info.Kind,
            AuthMode = SetupMode,
            Model = SetupModel.Trim(),
            Endpoint = info.EndpointEditable ? SetupEndpoint.Trim() : null,
        };

        if (SetupMode == AiAuthMode.ApiKey)
        {
            var key = ReadKeyInput().Trim();
            if (string.IsNullOrEmpty(key)) key = Secrets.LoadAiKey(info.Kind) ?? "";
            if (info.NeedsKey && key.Length == 0) { SetupError = "Paste your API key first."; return; }
            if (info.EndpointEditable && !Uri.TryCreate(cfg.EffectiveEndpoint, UriKind.Absolute, out _)) { SetupError = "Enter the server address, like http://10.0.10.30:11434/v1"; return; }

            IsConnecting = true;
            SetupError = "";
            try
            {
                // Listing models proves the key and endpoint work before anything is saved.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var models = await AiClientFactory.Create(cfg, key).ListModelsAsync(cts.Token);
                SetupModels = models;
                if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = models.FirstOrDefault() ?? "";
                if (string.IsNullOrWhiteSpace(cfg.EffectiveModel)) { SetupError = "Connected, but no model was found. Type a model name."; return; }
            }
            catch (Exception ex)
            {
                SetupError = ex is OperationCanceledException or TaskCanceledException ? "No answer within 15 seconds." : ex.Message;
                return;
            }
            finally { IsConnecting = false; }
            Secrets.SaveAiKey(info.Kind, key);
        }

        var old = Connected.FirstOrDefault(c => c.Kind == info.Kind);
        if (old is not null) Connected.Remove(old);
        Connected.Add(cfg);
        Active = cfg;
        Save();
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanGoBack));
        Page = AiPage.Chat;
        StatusMessage?.Invoke($"{info.Name} connected and on standby. Open it with the {info.Name} button or Ctrl+Shift+A.");
    }

    // ---------- chat ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Theme), nameof(ButtonLabel), nameof(ActiveName), nameof(ActiveModel), nameof(ActiveMonogram), nameof(IsAccountMode),
        nameof(ShowConversation), nameof(ShowAccountView), nameof(AccountSite), nameof(Placeholder))]
    private AiProviderConfig? _active;

    public AiTheme Theme => Active is { AuthMode: AiAuthMode.ApiKey } a ? AiTheme.For(a.Kind) : AiTheme.RemoteMan;
    public string ButtonLabel => Active?.Info.Name ?? "AI";
    public string ActiveName => Active?.Info.Name ?? "";
    public string ActiveModel => Active is null ? "" : Active.AuthMode == AiAuthMode.Account ? "account" : Active.EffectiveModel;
    public string ActiveMonogram => Active?.Info.Monogram ?? "";
    public bool IsAccountMode => Active?.AuthMode == AiAuthMode.Account;
    public bool ShowConversation => IsChatPage && !IsAccountMode;
    public bool ShowAccountView => IsChatPage && IsAccountMode;
    public string? AccountSite => IsAccountMode ? Active?.Info.AccountSite : null;
    public string Placeholder => Theme.Placeholder;

    [ObservableProperty] private string _draft = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ContextNote))] private bool _includeContext;
    [ObservableProperty] private bool _providerMenuOpen;
    [ObservableProperty] private string _contextLabel = "No session open";
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private bool _canPaste;
    [ObservableProperty] private string _pasteLabel = "Paste to terminal";

    public string ContextNote => IncludeContext ? "Sent only with your message" : "Context off";
    public bool IsEmpty => Messages.Count == 0;

    /// <summary>Shows a line in the main window's status bar.</summary>
    public Action<string>? StatusMessage { get; set; }

    partial void OnIncludeContextChanged(bool value) { _settings.Ai.IncludeSessionContext = value; Save(); }
    partial void OnActiveChanged(AiProviderConfig? value) { _client = null; }

    /// <summary>Re-reads the active tab; called whenever the box is shown or the tab changes.</summary>
    public void RefreshSession()
    {
        var s = _activeSession();
        HasSession = s is not null;
        ContextLabel = s switch
        {
            SshSessionViewModel ssh => $"{ssh.Title}, last {ContextLines} lines",
            null => "No session open",
            _ => $"{s.Title} ({s.KindTag})",
        };
        CanPaste = s is SshSessionViewModel { SendToTerminal: not null };
        PasteLabel = s is SshSessionViewModel ? $"Paste to {s.Title}" : "Paste to terminal";
    }

    private SessionContext? BuildContext()
    {
        if (!IncludeContext || _activeSession() is not { } s) return null;
        var output = s is SshSessionViewModel ssh ? ssh.Tail.LastLines(ContextLines) : null;
        return new SessionContext(s.Title, s.KindTag, s.Endpoint.ToString(), output);
    }

    [RelayCommand] private void Ask(string text) { if (IsBusy) return; Draft = text; _ = SendAsync(); }

    private bool CanSend() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0 || Active is not { AuthMode: AiAuthMode.ApiKey } cfg) return;
        Draft = "";

        // History goes as plain turns; only the newest message carries the session snapshot.
        var history = Messages.Where(m => !m.IsError).Select(m => new ChatTurn(m.IsUser ? ChatRole.User : ChatRole.Assistant, m.Text)).ToList();
        history.Add(new ChatTurn(ChatRole.User, AiClientFactory.WithContext(text, BuildContext())));

        Messages.Add(new AiChatMessage(true) { Text = text });
        var reply = new AiChatMessage(false);
        Messages.Add(reply);
        OnPropertyChanged(nameof(IsEmpty));

        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            _client ??= AiClientFactory.Create(cfg, Secrets.LoadAiKey(cfg.Kind));
            await foreach (var piece in _client.StreamAsync(AiClientFactory.SystemPrompt, history, _cts.Token))
                reply.Text += piece;
            if (reply.Text.Length == 0) reply.Text = "(no answer)";
        }
        catch (OperationCanceledException) { if (reply.Text.Length == 0) Messages.Remove(reply); }
        catch (Exception ex)
        {
            reply.IsError = true;
            reply.Text = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        _cts?.Cancel();
        Messages.Clear();
        ProviderMenuOpen = false;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void UseProvider(AiProviderConfig cfg)
    {
        Active = cfg;
        _settings.Ai.Active = cfg.Kind;
        Save();
        ProviderMenuOpen = false;
        Page = AiPage.Chat;
    }

    [RelayCommand]
    private void AddProvider()
    {
        ProviderMenuOpen = false;
        Page = AiPage.Pick;
    }

    [RelayCommand]
    private void SignOut()
    {
        if (Active is not { } cfg) return;
        ProviderMenuOpen = false;
        Secrets.SaveAiKey(cfg.Kind, null);
        Connected.Remove(cfg);
        Active = Connected.FirstOrDefault();
        Save();
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanGoBack));
        SignedOut?.Invoke(cfg);
        Page = Active is null ? AiPage.Pick : AiPage.Chat;
        StatusMessage?.Invoke($"Signed out of {cfg.Info.Name}");
    }

    /// <summary>Raised so the view can clear an account sign-in's cookies.</summary>
    public event Action<AiProviderConfig>? SignedOut;

    [RelayCommand]
    private void Paste(string code)
    {
        if (_activeSession() is SshSessionViewModel { SendToTerminal: { } send } s)
        {
            send(code);
            StatusMessage?.Invoke($"Pasted into {s.Title}. Review it, then press Enter in the terminal yourself.");
        }
    }

    [RelayCommand]
    private static void Copy(string code)
    {
        try { Clipboard.SetDataObject(code, true); } catch (System.Runtime.InteropServices.COMException) { }
    }

    [RelayCommand] private void ToggleProviderMenu() => ProviderMenuOpen = !ProviderMenuOpen;

    private void Save()
    {
        _settings.Ai.Providers = Connected.ToList();
        _settings.Ai.Active = Active?.Kind;
        _settings.Save();
    }
}

public sealed partial class AiChatMessage(bool isUser) : ObservableObject
{
    public bool IsUser { get; } = isUser;
    public bool IsAssistant => !IsUser;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Segments))] private string _text = "";
    [ObservableProperty] private bool _isError;

    public IReadOnlyList<ReplySegment> Segments => IsUser ? [new ReplySegment(false, Text, null)] : ReplySegment.Split(Text);
}
