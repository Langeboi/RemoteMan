using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteMan.App.Services;

/// <summary>
/// How AI apps start RemoteMan's MCP server: <c>RemoteMan.exe --mcp</c> as a stdio server. The installed exe lives in
/// RemoteMan's "current" folder, which stays put across updates, so the config written once keeps working.
/// </summary>
public static class McpClients
{
    public const string ServerName = "remoteman";
    public const string Arg = "--mcp";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RemoteMan.exe");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonObject StdioServer(bool withType = false)
    {
        var o = new JsonObject();
        if (withType) o["type"] = "stdio";
        o["command"] = ExePath;
        o["args"] = new JsonArray(Arg);
        return o;
    }

    /// <summary>The usual mcpServers block (Claude Desktop, Cursor, Windsurf, LM Studio and most others).</summary>
    public static string GenericJson =>
        new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = StdioServer() } }.ToJsonString(Indented);

    /// <summary>For .vscode/mcp.json or the user's VS Code MCP settings.</summary>
    public static string VsCodeJson =>
        new JsonObject { ["servers"] = new JsonObject { [ServerName] = StdioServer(withType: true) } }.ToJsonString(Indented);

    /// <summary>One command that adds RemoteMan to Claude Code for every project.</summary>
    public static string ClaudeCodeCommand => $"claude mcp add {ServerName} --scope user -- \"{ExePath}\" {Arg}";

    /// <summary>Claude Desktop's config files: the normal install, and the Microsoft Store one if it's there.</summary>
    private static IEnumerable<string> ClaudeDesktopConfigs()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(roaming, "Claude", "claude_desktop_config.json");
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        string[] store = [];
        try { if (Directory.Exists(packages)) store = Directory.GetDirectories(packages, "Claude_*"); } catch (Exception) { }
        foreach (var p in store) yield return Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
    }

    public static bool HasClaudeDesktop => ClaudeDesktopConfigs().Any(f => Directory.Exists(Path.GetDirectoryName(f)));

    /// <summary>Adds (or updates) RemoteMan in Claude Desktop's config, keeping everything else in it. Returns what happened.</summary>
    public static (bool Ok, string Text) AddToClaudeDesktop()
    {
        var targets = ClaudeDesktopConfigs().Where(f => Directory.Exists(Path.GetDirectoryName(f))).ToList();
        if (targets.Count == 0)
            return (false, "Claude Desktop isn't installed for this Windows user. Install it from claude.ai/download, open it once, then try again.");
        foreach (var file in targets)
        {
            JsonObject root;
            try
            {
                var text = File.Exists(file) ? File.ReadAllText(file) : "";
                root = text.Trim().Length == 0 ? new JsonObject()
                    : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                      ?? throw new JsonException("not an object");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return (false, $"Couldn't read {file}: {ex.Message}. Fix or remove it, or use Copy JSON and paste it in yourself.");
            }
            if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
            servers[ServerName] = StdioServer();
            try
            {
                if (File.Exists(file)) File.Copy(file, file + ".bak", overwrite: true);
                File.WriteAllText(file, root.ToJsonString(Indented));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (false, $"Couldn't write {file}: {ex.Message}");
            }
        }
        return (true, "Added to Claude Desktop. Quit Claude Desktop fully (tray icon > Quit) and open it again to load RemoteMan.");
    }
}
