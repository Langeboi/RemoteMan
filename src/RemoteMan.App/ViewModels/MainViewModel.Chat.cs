using RemoteMan.App.Services;
using RemoteMan.Core.Chat;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.ViewModels;

/// <summary>Team chat: only on shared databases, started once the entries are open.</summary>
public sealed partial class MainViewModel
{
    private ChatViewModel? _chat;
    public ChatViewModel Chat => _chat ??= new ChatViewModel(this);

    /// <summary>Set by the window: whether RemoteMan is the active window (no pop-ups for a conversation you're looking at).</summary>
    public bool IsWindowActive { get; set; } = true;

    /// <summary>The window flashes the taskbar button when a message pops up while it's in the background.</summary>
    public event EventHandler? ChatAttention;

    private async Task StartChatAsync(EntryStore store)
    {
        if (!IsSharedSource || store.Source is not IChatSource { SupportsChat: true }) return;
        try
        {
            var hub = new ChatHub(store.Source, () => store.User, _protector, new ChatOutboxFile(_settings.Active.Name))
            {
                Retention = _settings.ChatRetentionDays > 0 ? TimeSpan.FromDays(_settings.ChatRetentionDays) : null,
            };
            store.PresenceChanged += (_, _) => OnUi(Chat.Refresh);
            await Chat.StartAsync(store, hub);
            if (_settings.ChatOpen) Chat.IsOpen = true;
        }
        catch (Exception ex)
        {
            // Entries keep working; chat tries again on the next start.
            Message = $"Chat is unavailable: {StoreError(ex)}";
        }
    }

    private static string StoreError(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }

    internal void ChatOpenChanged(bool open)
    {
        if (_settings.ChatOpen == open) return;
        _settings.ChatOpen = open;
        TrySaveSettings();
    }

    internal void ChatToastShown() { if (!IsWindowActive) ChatAttention?.Invoke(this, EventArgs.Empty); }

    /// <summary>More > Chat history: how long messages are kept.</summary>
    public int ChatRetentionDays
    {
        get => _settings.ChatRetentionDays;
        set { _settings.ChatRetentionDays = value; TrySaveSettings(); OnPropertyChanged(); }
    }
}
