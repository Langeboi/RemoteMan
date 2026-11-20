using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteMan.Core.Ai;

/// <summary>Chat Completions over HTTP: OpenAI, Mistral, Ollama and any OpenAI-compatible server.</summary>
public sealed class OpenAiCompatibleClient(HttpClient http, string endpoint, string model, string? apiKey) : IAiChatClient
{
    public static JsonObject BuildBody(string model, string system, IReadOnlyList<ChatTurn> turns)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var t in turns)
            messages.Add(new JsonObject { ["role"] = t.Role == ChatRole.User ? "user" : "assistant", ["content"] = t.Text });
        return new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = messages };
    }

    /// <summary>Text in one streamed chunk, or null.</summary>
    public static string? ParseDelta(string data)
    {
        if (data == "[DONE]") return null;
        var node = JsonNode.Parse(data);
        if (node?["error"] is { } err) throw new AiProviderException(err["message"]?.GetValue<string>() ?? err.ToJsonString());
        return node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
    }

    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatTurn> turns, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "/chat/completions");
        req.Content = new StringContent(BuildBody(model, system, turns).ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await AiHttp.EnsureOkAsync(res, ct);
        await foreach (var data in Sse.ReadDataAsync(await res.Content.ReadAsStreamAsync(ct), ct))
        {
            if (data == "[DONE]") yield break;
            if (ParseDelta(data) is { Length: > 0 } text) yield return text;
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, "/models");
        using var res = await http.SendAsync(req, ct);
        await AiHttp.EnsureOkAsync(res, ct);
        var node = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return node?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Order().ToList() ?? [];
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, endpoint.TrimEnd('/') + path);
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return req;
    }
}

internal static class AiHttp
{
    public static async Task EnsureOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        string? detail = null;
        try
        {
            var n = JsonNode.Parse(body);
            detail = n?["error"]?["message"]?.GetValue<string>() ?? (n?["error"] is JsonValue v ? v.GetValue<string>() : null);
        }
        catch (JsonException) { }
        var reason = (int)res.StatusCode switch
        {
            401 or 403 => "The key was refused",
            404 => "Not found (check the model or endpoint)",
            429 => "Rate limited or out of credit",
            _ => $"HTTP {(int)res.StatusCode}",
        };
        throw new AiProviderException(detail is null ? reason : $"{reason}: {detail}");
    }
}
