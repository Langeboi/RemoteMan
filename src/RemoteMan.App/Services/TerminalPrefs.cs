using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteMan.App.Services;

/// <summary>Terminal look for this user: theme, font, size and scrollback. Shared by every SSH, Telnet and serial tab.</summary>
public static class TerminalPrefs
{
    private static string FilePath => Path.Combine(AppPaths.Root, "terminal.json");

    public static readonly string[] Themes = ["RemoteMan", "RemoteMan Soft", "Campbell", "Tango", "Monokai", "Tokyo Night", "Dracula", "One Dark", "Solarized Dark", "Nord", "Gruvbox", "Light"];
    public static readonly string[] Fonts = ["Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New"];
    public static readonly int[] Scrollbacks = [1000, 5000, 10000, 50000, 100000];

    /// <summary>Raised after a change so every open terminal restyles.</summary>
    public static event Action? Changed;

    public static JsonObject Load()
    {
        JsonObject o;
        try { o = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new() : new(); }
        catch (Exception) { o = new(); }
        o["theme"] ??= "RemoteMan";
        o["fontFamily"] ??= "Cascadia Mono";
        o["fontSize"] ??= 13;
        o["scrollback"] ??= 10000;
        return o;
    }

    public static string Get(string key) => Load()[key]?.ToString() ?? "";

    public static void Set(string key, JsonNode value)
    {
        var o = Load();
        o[key] = value;
        Write(o);
    }

    /// <summary>From the terminal page (Ctrl+wheel changes the font size there).</summary>
    public static void Save(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject changes) return;
            var o = Load();
            foreach (var (k, v) in changes) if (k is "theme" or "fontFamily" or "fontSize" or "scrollback") o[k] = v?.DeepClone();
            Write(o);
        }
        catch (JsonException) { }
    }

    private static void Write(JsonObject o)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(FilePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        Changed?.Invoke();
    }
}
