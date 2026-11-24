using System.Net.Http;
using System.Text.Json;

namespace RemoteMan.Core.Chat;

/// <summary>A GIF from GIPHY: a small animated preview for the picker and the version that gets sent.</summary>
public sealed record GiphyGif(string Id, string Title, string PreviewUrl, string SendUrl, long SendBytes)
{
    /// <summary>The file name it is sent under ("happy-dance.gif").</summary>
    public string FileName
    {
        get
        {
            var t = new string(Title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            while (t.Contains("--")) t = t.Replace("--", "-");
            t = t.Trim('-');
            if (t.EndsWith("-gif")) t = t[..^4];
            return (t.Length == 0 ? "giphy" : t.Length > 60 ? t[..60].Trim('-') : t) + ".gif";
        }
    }
}

/// <summary>
/// GIPHY search for the chat GIF picker. The picked GIF is downloaded and sent as a file like any other,
/// so the people reading it never contact GIPHY. Needs an API key from developers.giphy.com (kept in the
/// data source's shared settings, encrypted with the vault key).
/// </summary>
public sealed class GiphyClient(string apiKey, string rating = "g", HttpClient? http = null)
{
    public const int PageSize = 24;
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _http = http ?? Shared;

    /// <summary>GIFs for <paramref name="query"/>, or trending ones when it is empty.</summary>
    public async Task<IReadOnlyList<GiphyGif>> SearchAsync(string query, int offset = 0, CancellationToken ct = default)
    {
        var q = query.Trim();
        var url = q.Length == 0
            ? $"https://api.giphy.com/v1/gifs/trending?api_key={Uri.EscapeDataString(apiKey)}&limit={PageSize}&offset={offset}&rating={Uri.EscapeDataString(rating)}"
            : $"https://api.giphy.com/v1/gifs/search?api_key={Uri.EscapeDataString(apiKey)}&q={Uri.EscapeDataString(q)}&limit={PageSize}&offset={offset}&rating={Uri.EscapeDataString(rating)}&lang=en";
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException((int)resp.StatusCode switch
            {
                401 or 403 => "GIPHY didn't accept the API key. Check it under More, Chat GIFs (GIPHY).",
                429 => "GIPHY's limit for this API key is used up for now. Try again in a while.",
                var code => $"GIPHY answered {code}.",
            });
        return Parse(body);
    }

    /// <summary>The GIF to send, at most <see cref="ChatFileRef.MaxBytes"/>.</summary>
    public async Task<byte[]> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.EndsWith("giphy.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Not a GIPHY address.");
        using var resp = await _http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > ChatFileRef.MaxBytes) throw new InvalidOperationException("That GIF is too big to send.");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > ChatFileRef.MaxBytes) throw new InvalidOperationException("That GIF is too big to send.");
        }
        return ms.ToArray();
    }

    /// <summary>Reads GIPHY's search answer: a small preview and the "downsized" version (under 2 MB) to send.</summary>
    public static IReadOnlyList<GiphyGif> Parse(string json)
    {
        var list = new List<GiphyGif>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in data.EnumerateArray())
        {
            if (!g.TryGetProperty("images", out var img)) continue;
            string? Url(string name) => img.TryGetProperty(name, out var v) && v.TryGetProperty("url", out var u) ? u.GetString() : null;
            long Size(string name) => img.TryGetProperty(name, out var v) && v.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var n) ? n : 0;
            var preview = Url("fixed_width_small") ?? Url("fixed_width_downsampled") ?? Url("fixed_width");
            var sendName = new[] { "downsized", "fixed_height", "original" }.FirstOrDefault(n => Url(n) is not null && Size(n) is var s && s is > 0 and <= ChatFileRef.MaxBytes)
                           ?? new[] { "downsized", "fixed_height" }.FirstOrDefault(n => Url(n) is not null);
            if (preview is null || sendName is null) continue;
            list.Add(new GiphyGif(g.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                g.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "", preview, Url(sendName)!, Size(sendName)));
        }
        return list;
    }
}
