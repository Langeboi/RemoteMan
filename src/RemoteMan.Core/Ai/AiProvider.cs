using System.Text.Json.Serialization;

namespace RemoteMan.Core.Ai;

public enum AiProviderKind { Claude, OpenAI, Gemini, Mistral, Ollama, Custom }

/// <summary>How the user signs in. Only an API key (or a local server) lets the assistant see sessions.</summary>
public enum AiAuthMode { ApiKey, Account }

public enum ChatRole { User, Assistant }

public sealed record ChatTurn(ChatRole Role, string Text);

/// <summary>Static facts about one provider: names, defaults, and where its own chat site lives.</summary>
public sealed record AiProviderInfo(
    AiProviderKind Kind,
    string Name,
    string Vendor,
    string Monogram,
    string DefaultModel,
    string? DefaultEndpoint,
    string? AccountSite,
    bool NeedsKey,
    bool EndpointEditable)
{
    public bool SupportsAccount => AccountSite is not null;

    public static IReadOnlyList<AiProviderInfo> All { get; } =
    [
        new(AiProviderKind.Claude, "Claude", "Anthropic", "C", "claude-opus-5-5", "https://api.anthropic.com", "https://claude.ai/new", true, false),
        new(AiProviderKind.OpenAI, "ChatGPT", "OpenAI", "G", "gpt-5", "https://api.openai.com/v1", "https://chatgpt.com/", true, false),
        new(AiProviderKind.Gemini, "Gemini", "Google", "Ge", "gemini-2.5-flash", "https://generativelanguage.googleapis.com/v1beta", "https://gemini.google.com/app", true, false),
        new(AiProviderKind.Mistral, "Mistral", "Mistral AI", "M", "mistral-large-latest", "https://api.mistral.ai/v1", "https://chat.mistral.ai/chat", true, false),
        new(AiProviderKind.Ollama, "Ollama", "Local models on your own server, no account", "O", "llama3.1", "http://localhost:11434/v1", null, false, true),
        new(AiProviderKind.Custom, "Custom endpoint", "Any OpenAI-compatible URL", "{}", "", null, null, false, true),
    ];

    public static AiProviderInfo Get(AiProviderKind kind) => All.First(p => p.Kind == kind);
}

/// <summary>A signed-in provider, saved in the per-user settings. The key is stored separately under DPAPI.</summary>
public sealed class AiProviderConfig
{
    public AiProviderKind Kind { get; set; }
    public AiAuthMode AuthMode { get; set; } = AiAuthMode.ApiKey;
    public string Model { get; set; } = "";
    public string? Endpoint { get; set; }

    [JsonIgnore]
    public AiProviderInfo Info => AiProviderInfo.Get(Kind);
    [JsonIgnore]
    public string EffectiveEndpoint => string.IsNullOrWhiteSpace(Endpoint) ? Info.DefaultEndpoint ?? "" : Endpoint!.TrimEnd('/');
    [JsonIgnore]
    public string EffectiveModel => string.IsNullOrWhiteSpace(Model) ? Info.DefaultModel : Model;
}

public sealed class AiSettings
{
    public List<AiProviderConfig> Providers { get; set; } = new();
    public AiProviderKind? Active { get; set; }
    /// <summary>Whether the active session's recent output is sent along with each message.</summary>
    public bool IncludeSessionContext { get; set; } = true;

    [JsonIgnore]
    public AiProviderConfig? ActiveProvider => Providers.FirstOrDefault(p => p.Kind == Active) ?? Providers.FirstOrDefault();
}

/// <summary>Streams a reply, one text fragment at a time.</summary>
public interface IAiChatClient
{
    IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatTurn> turns, CancellationToken ct = default);

    /// <summary>Checks the key/endpoint and returns the model ids the account can use (may be empty).</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

public sealed class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);
