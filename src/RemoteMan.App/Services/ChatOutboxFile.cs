using System.IO;
using System.Text.Json;
using RemoteMan.Core.Chat;

namespace RemoteMan.App.Services;

/// <summary>Chat messages written while the database was unreachable, in %APPDATA%\RemoteMan (already encrypted).</summary>
public sealed class ChatOutboxFile(string dataSourceName) : IChatOutbox
{
    private readonly string _path = Path.Combine(AppPaths.Root,
        "chat-outbox-" + string.Concat(dataSourceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    public IReadOnlyList<StoredChatMessage> Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<StoredChatMessage>>(File.ReadAllText(_path)) ?? [] : []; }
        catch (Exception) { return []; }
    }

    public void Save(IReadOnlyList<StoredChatMessage> pending)
    {
        if (pending.Count == 0) { if (File.Exists(_path)) File.Delete(_path); return; }
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(_path, JsonSerializer.Serialize(pending));
    }
}
