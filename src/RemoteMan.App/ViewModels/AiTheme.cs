using System.Windows;
using System.Windows.Media;
using RemoteMan.Core.Ai;

namespace RemoteMan.App.ViewModels;

/// <summary>
/// The chat box takes on the colour mood and shapes of the chosen provider (no logos or copied
/// layouts). Local and custom endpoints keep RemoteMan's own flat look.
/// </summary>
public sealed class AiTheme
{
    public required Brush Background { get; init; }
    public required Brush Header { get; init; }
    public required Brush Line { get; init; }
    public required Brush Text { get; init; }
    public required Brush Muted { get; init; }
    public required Brush Accent { get; init; }
    public required Brush AccentText { get; init; }
    public required Brush UserBubble { get; init; }
    public required Brush UserText { get; init; }
    public Brush Code { get; init; } = B("#0F0F10");
    public CornerRadius Radius { get; init; }
    public CornerRadius SmallRadius { get; init; }
    public CornerRadius InputRadius { get; init; }
    public CornerRadius MarkRadius { get; init; }
    public FontFamily ReplyFont { get; init; } = new("Segoe UI Variable Text, Segoe UI");
    public double ReplySize { get; init; } = 13;
    public FontFamily TitleFont { get; init; } = new("Segoe UI Variable Text, Segoe UI");
    public bool ShowAvatar { get; init; }
    public bool Rounded { get; init; }
    public string Greeting { get; init; } = "Ask about this session";
    public Brush? GreetingBrush { get; init; }
    public string Placeholder { get; init; } = "Ask about this session…";
    public string Footnote { get; init; } = "";
    public CornerRadius SendRadius => Rounded ? new(15) : new(0);
    public Brush ComposerBackground => Rounded ? UserBubble : Brushes.Transparent;
    public Brush GreetingForeground => GreetingBrush ?? Text;

    private static SolidColorBrush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static readonly FontFamily Serif = new("Georgia, Cambria, Times New Roman");

    public static AiTheme RemoteMan { get; } = new()
    {
        Background = B("#202226"), Header = B("#232529"), Line = B("#34373C"), Text = B("#D4D7DC"), Muted = B("#9DA2A9"),
        Accent = B("#3A4E63"), AccentText = B("#EEF0F2"), UserBubble = B("#33414F"), UserText = B("#EEF0F2"),
        ReplySize = 12, Greeting = "Try", Placeholder = "Ask about this session…  (Enter to send)",
        Footnote = "Runs on your own server. Nothing leaves your network.",
    };

    private static readonly Dictionary<AiProviderKind, AiTheme> Cache = new();

    public static AiTheme For(AiProviderKind kind)
    {
        if (!Cache.TryGetValue(kind, out var t)) Cache[kind] = t = Create(kind);
        return t;
    }

    private static AiTheme Create(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Claude => new()
        {
            Background = B("#262624"), Header = B("#1F1E1D"), Line = B("#3A3936"), Text = B("#ECE9E1"), Muted = B("#A39E93"),
            Accent = B("#D97757"), AccentText = B("#1F1E1D"), UserBubble = B("#141413"), UserText = B("#ECE9E1"),
            Radius = new(14), SmallRadius = new(8), InputRadius = new(16), MarkRadius = new(9), Rounded = true,
            ReplyFont = Serif, ReplySize = 14, TitleFont = Serif, ShowAvatar = true,
            Greeting = "How can I help with this session?", Placeholder = "Reply to Claude…",
            Footnote = "Claude can make mistakes. Check commands before running them.",
        },
        AiProviderKind.OpenAI => new()
        {
            Background = B("#212121"), Header = B("#212121"), Line = B("#303030"), Text = B("#ECECEC"), Muted = B("#9B9B9B"),
            Accent = B("#ECECEC"), AccentText = B("#0D0D0D"), UserBubble = B("#303030"), UserText = B("#ECECEC"),
            Radius = new(16), SmallRadius = new(8), InputRadius = new(24), MarkRadius = new(9), Rounded = true,
            Greeting = "What can I help with?", Placeholder = "Ask anything",
            Footnote = "ChatGPT can make mistakes. Check important info.",
        },
        AiProviderKind.Gemini => new()
        {
            Background = B("#1B1C1D"), Header = B("#1B1C1D"), Line = B("#2D2F31"), Text = B("#E3E3E3"), Muted = B("#A8ABAF"),
            Accent = B("#A8C7FA"), AccentText = B("#062E6F"), UserBubble = B("#2B2D30"), UserText = B("#E3E3E3"),
            Radius = new(20), SmallRadius = new(10), InputRadius = new(24), MarkRadius = new(9), Rounded = true, ShowAvatar = true,
            Greeting = "Hello there", GreetingBrush = B("#A8C7FA"), Placeholder = "Ask Gemini",
            Footnote = "Gemini can make mistakes, so double-check it.",
        },
        AiProviderKind.Mistral => new()
        {
            Background = B("#1A1918"), Header = B("#141312"), Line = B("#33302C"), Text = B("#EDE8E1"), Muted = B("#A7A096"),
            Accent = B("#FA7A35"), AccentText = B("#1A1918"), UserBubble = B("#2A2724"), UserText = B("#EDE8E1"),
            Radius = new(6), SmallRadius = new(4), InputRadius = new(8), MarkRadius = new(3), Rounded = true, ShowAvatar = true,
            Greeting = "Ask le Chat", Placeholder = "Ask le Chat",
            Footnote = "Le Chat can make mistakes.",
        },
        _ => RemoteMan,
    };
}
