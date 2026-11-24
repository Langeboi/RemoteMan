using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteMan.App.Services;

/// <summary>Terminal look for this user: theme, font, size and scrollback. Shared by every SSH, Telnet and serial tab.</summary>
public static class TerminalPrefs
{
    private static string FilePath => Path.Combine(AppPaths.Root, "terminal.json");

    public static readonly string[] Themes = ["RemoteMan", "RemoteMan Soft", "Campbell", "Tango", "Monokai", "Tokyo Night", "Dracula", "One Dark", "Solarized Dark", "Nord", "Gruvbox", "Light"];
    public static readonly string[] Fonts = ["Courier New", "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console"];

    /// <summary>Font for a user who hasn't picked one. An entry's or the user's own choice wins.</summary>
    public const string DefaultFont = "Courier New";

    // Version 1 files saved the old default font ("Cascadia Mono") whenever any other setting changed,
    // so that value there means "never picked". Version 2 files only hold what the user chose.
    private const int FileVersion = 2;
    public static readonly int[] Scrollbacks = [1000, 5000, 10000, 50000, 100000];

    /// <summary>Raised after a change so every open terminal restyles.</summary>
    public static event Action? Changed;

    /// <summary>The user's choices with the defaults filled in.</summary>
    public static JsonObject Load()
    {
        var o = Chosen();
        o.Remove("v");
        o["theme"] ??= "RemoteMan";
        o["fontFamily"] ??= DefaultFont;
        o["fontSize"] ??= 13;
        o["scrollback"] ??= 10000;
        return o;
    }

    /// <summary>Only what the user picked, so a later change of a default reaches them.</summary>
    private static JsonObject Chosen()
    {
        JsonObject o;
        try { o = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new() : new(); }
        catch (Exception) { o = new(); }
        return Upgrade(o);
    }

    internal static JsonObject Upgrade(JsonObject o)
    {
        var version = o["v"] is JsonValue jv && jv.TryGetValue<int>(out var n) ? n : 1;
        if (version < 2 && o["fontFamily"]?.ToString() == "Cascadia Mono") o.Remove("fontFamily");
        return o;
    }

    public static string Get(string key) => Load()[key]?.ToString() ?? "";

    public static void Set(string key, JsonNode value)
    {
        var o = Chosen();
        o[key] = value;
        Write(o);
    }

    /// <summary>From the terminal page (Ctrl+wheel changes the font size there).</summary>
    public static void Save(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject changes) return;
            var o = Chosen();
            foreach (var (k, v) in changes) if (k is "theme" or "fontFamily" or "fontSize" or "scrollback") o[k] = v?.DeepClone();
            Write(o);
        }
        catch (JsonException) { }
    }

    private static void Write(JsonObject o)
    {
        try
        {
            o["v"] = FileVersion;
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(FilePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        Changed?.Invoke();
    }
}
