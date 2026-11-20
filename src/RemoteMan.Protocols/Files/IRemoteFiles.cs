using RemoteMan.Protocols.Ssh;

namespace RemoteMan.Protocols.Files;

/// <summary>A remote file system the Files tab can browse: SFTP or FTP.</summary>
public interface IRemoteFiles : IDisposable
{
    string CurrentDirectory { get; }
    string Parent(string path);
    string Combine(string dir, string name);
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default);
    Task UploadAsync(IEnumerable<string> localPaths, string remoteDir, IProgress<TransferProgress>? progress, CancellationToken ct = default);
    Task DownloadAsync(IEnumerable<RemoteItem> items, string localDir, IProgress<TransferProgress>? progress, CancellationToken ct = default);
    Task DeleteAsync(RemoteItem item, CancellationToken ct = default);
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
}
