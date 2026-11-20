using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace RemoteMan.Core.Ai;

/// <summary>Google Gemini through the Generative Language API (streamGenerateContent over SSE).</summary>
public sealed class GeminiClient(HttpClient http, string endpoint, string model, string apiKey) : IAiChatClient
{
    public static JsonObject BuildBody(string system, IReadOnlyList<ChatTurn> turns)
    {
        var contents = new JsonArray();
        foreach (var t in turns)
            contents.Add(new JsonObject
            {
                ["role"] = t.Role == ChatRole.User ? "user" : "model",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = t.Text } },
            });
        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } },
            ["contents"] = contents,
        };
    }

    public static string ParseDelta(string data)
    {
        var node = JsonNode.Parse(data);
        if (node?["error"] is { } err) throw new AiProviderException(err["message"]?.GetValue<string>() ?? err.ToJsonString());
        var parts = node?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        return parts is null ? "" : string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? ""));
    }

    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatTurn> turns, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse");
        req.Headers.Add("x-goog-api-key", apiKey);
        req.Content = new StringContent(BuildBody(system, turns).ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await AiHttp.EnsureOkAsync(res, ct);
        await foreach (var data in Sse.ReadDataAsync(await res.Content.ReadAsStreamAsync(ct), ct))
            if (ParseDelta(data) is { Length: > 0 } text) yield return text;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models?pageSize=200");
        req.Headers.Add("x-goog-api-key", apiKey);
        using var res = await http.SendAsync(req, ct);
        await AiHttp.EnsureOkAsync(res, ct);
        var node = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return node?["models"]?.AsArray()
            .Where(m => m?["supportedGenerationMethods"]?.AsArray().Any(x => x?.GetValue<string>() == "generateContent") == true)
            .Select(m => m?["name"]?.GetValue<string>()?.Replace("models/", ""))
            .OfType<string>().Order().ToList() ?? [];
    }
}
