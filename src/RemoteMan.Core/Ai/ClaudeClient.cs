using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace RemoteMan.Core.Ai;

/// <summary>Claude through the official Anthropic SDK, streamed.</summary>
public sealed class ClaudeClient(string apiKey, string model) : IAiChatClient
{
    private readonly AnthropicClient _client = new() { ApiKey = apiKey };

    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatTurn> turns, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<BetaMessageParam> messages = turns.Select(t => t.Role == ChatRole.User
            ? (BetaMessageParam)new BetaUserMessageParam { Content = t.Text }
            : new BetaAssistantMessageParam { Content = t.Text }).ToList();

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = system,
            Messages = messages,
            // If a request is declined, the API re-serves it on a fallback model in the same call.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };

        var refused = false;
        await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
            else if (ev.TryPickDelta(out var md))
                refused |= md.Delta.StopReason == BetaStopReason.Refusal;
        }
        if (refused) yield return "\n\n(Claude declined to answer this.)";
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var page = await _client.Models.List(cancellationToken: ct);
        return page.Items.Select(m => m.ID).ToList();
    }
}
