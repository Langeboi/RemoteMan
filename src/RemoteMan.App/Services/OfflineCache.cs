using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Services;

/// <summary>
/// Last known entries of a shared data source, so RemoteMan opens (offline) when the
/// database is down. Encrypted with DPAPI for the current Windows user; passwords inside
/// are additionally still encrypted with the shared vault key.
/// </summary>
public sealed class OfflineCache(string dataSourceName) : IEntryCache
{
    private static readonly byte[] Entropy = "RemoteMan offline cache v1"u8.ToArray();

    private readonly string _path = Path.Combine(AppPaths.Root, "cache",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataSourceName)))[..16] + ".bin");

    private sealed record Row(Guid Id, Guid? ParentId, EntryKind Kind, string Name, string? Payload, long Revision,
        DateTimeOffset UpdatedAt, string? UpdatedBy, DateTimeOffset? CreatedAt, string? CreatedBy);

    public IReadOnlyList<Entry>? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<Row>>(json)?.Select(r =>
            {
                var e = new Entry
                {
                    Id = r.Id, ParentId = r.ParentId, Kind = r.Kind, Name = r.Name, Revision = r.Revision,
                    UpdatedAt = r.UpdatedAt, UpdatedBy = r.UpdatedBy, CreatedAt = r.CreatedAt, CreatedBy = r.CreatedBy,
                };
                EntryJson.ApplyPayload(e, r.Payload);
                return e;
            }).ToList();
        }
        catch (Exception) { return null; } // unreadable cache = no cache
    }

    /// <summary>Deletes this PC's copy (a hosted workspace you were taken out of, or signed out of).</summary>
    public void Delete()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    public void Save(IReadOnlyList<Entry> entries)
    {
        var rows = entries.Select(e => new Row(e.Id, e.ParentId, e.Kind, e.Name, EntryJson.ToPayload(e), e.Revision,
            e.UpdatedAt, e.UpdatedBy, e.CreatedAt, e.CreatedBy)).ToList();
        var blob = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(rows), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, _path, overwrite: true);
    }
}
