using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace RemoteMan.Protocols.Ssh;

public sealed record RemoteItem(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified)
{
    public string SizeText => IsDirectory ? "" : Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{Size / 1048576.0:0.#} MB",
        _ => $"{Size / 1073741824.0:0.##} GB",
    };
}

public sealed record TransferProgress(string File, long Done, long Total);

/// <summary>SFTP file browser and transfer engine for an SSH entry (files and whole folders, both ways).</summary>
public sealed class SftpBrowser : RemoteMan.Protocols.Files.IRemoteFiles
{
    private readonly SftpClient _sftp;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A file browser on its own SFTP connection (SFTP entries; SSH tabs reuse their session instead).</summary>
    public static async Task<SftpBrowser> ConnectAsync(RemoteMan.Core.Model.HostEndpoint endpoint, ResolvedCredential cred,
        RemoteMan.Core.Model.SshSettings settings, KnownHosts knownHosts, Action<string>? info, CancellationToken ct = default)
    {
        var ep = endpoint.Port > 0 ? endpoint : endpoint with { Port = 22 };
        var sftp = new SftpClient(SshSession.BuildConnectionInfo(ep, cred, settings)) { OperationTimeout = TimeSpan.FromSeconds(30) };
        sftp.HostKeyReceived += (_, e) =>
        {
            switch (knownHosts.Check(ep.Host, ep.Port, e.HostKeyName, e.FingerPrintSHA256))
            {
                case HostKeyVerdict.New:
                    info?.Invoke($"Host key {e.HostKeyName} SHA256:{e.FingerPrintSHA256} accepted (first use, saved).");
                    e.CanTrust = settings.AutoAcceptHostKey;
                    break;
                case HostKeyVerdict.Changed:
                    info?.Invoke($"WARNING: host key for {ep} changed, now {e.HostKeyName} SHA256:{e.FingerPrintSHA256}.");
                    e.CanTrust = settings.AutoAcceptHostKey;
                    break;
                default:
                    e.CanTrust = true;
                    break;
            }
        };
        try { await sftp.ConnectAsync(ct); }
        catch { sftp.Dispose(); throw; }
        return new SftpBrowser(sftp);
    }

    public string Separator => "/";
    string RemoteMan.Protocols.Files.IRemoteFiles.Parent(string path) => Parent(path);
    string RemoteMan.Protocols.Files.IRemoteFiles.Combine(string dir, string name) => Combine(dir, name);

    internal SftpBrowser(SftpClient sftp)
    {
        _sftp = sftp;
        CurrentDirectory = sftp.WorkingDirectory;
    }

    public string CurrentDirectory { get; private set; }

    public static string Combine(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var full = _sftp.Get(path).FullName; // canonical path, throws if missing
            var items = new List<RemoteItem>();
            await foreach (var f in _sftp.ListDirectoryAsync(full, ct))
            {
                if (f.Name is "." or "..") continue;
                items.Add(new RemoteItem(f.Name, f.FullName, IsDir(f), f.Length, f.LastWriteTime));
            }
            CurrentDirectory = full;
            return items.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { _gate.Release(); }
    }

    private bool IsDir(ISftpFile f)
    {
        if (f.IsDirectory) return true;
        if (!f.IsSymbolicLink) return false;
        try { return _sftp.GetAttributes(f.FullName).IsDirectory; } catch { return false; }
    }

    /// <summary>Uploads local files and folders (recursively) into <paramref name="remoteDir"/>.</summary>
    public Task UploadAsync(IEnumerable<string> localPaths, string remoteDir, IProgress<TransferProgress>? progress, CancellationToken ct = default) =>
        Locked(() =>
        {
            foreach (var path in localPaths)
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(path)) UploadDir(path, Combine(remoteDir, Path.GetFileName(path.TrimEnd('\\', '/'))), progress, ct);
                else UploadFile(path, Combine(remoteDir, Path.GetFileName(path)), progress);
            }
        }, ct);

    private void UploadDir(string local, string remote, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (!_sftp.Exists(remote)) _sftp.CreateDirectory(remote);
        foreach (var f in Directory.EnumerateFiles(local)) { ct.ThrowIfCancellationRequested(); UploadFile(f, Combine(remote, Path.GetFileName(f)), progress); }
        foreach (var d in Directory.EnumerateDirectories(local)) UploadDir(d, Combine(remote, Path.GetFileName(d)), progress, ct);
    }

    private void UploadFile(string local, string remote, IProgress<TransferProgress>? progress)
    {
        using var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var total = fs.Length;
        var name = Path.GetFileName(local);
        _sftp.UploadFile(fs, remote, true, done => progress?.Report(new TransferProgress(name, (long)done, total)));
    }

    /// <summary>Downloads remote files and folders (recursively) into <paramref name="localDir"/>.</summary>
    public Task DownloadAsync(IEnumerable<RemoteItem> items, string localDir, IProgress<TransferProgress>? progress, CancellationToken ct = default) =>
        Locked(() =>
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.IsDirectory) DownloadDir(item.FullPath, Path.Combine(localDir, item.Name), progress, ct);
                else DownloadFile(item.FullPath, Path.Combine(localDir, item.Name), item.Size, progress);
            }
        }, ct);

    private void DownloadDir(string remote, string local, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(local);
        foreach (var f in _sftp.ListDirectory(remote))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Name is "." or "..") continue;
            if (IsDir(f)) DownloadDir(f.FullName, Path.Combine(local, f.Name), progress, ct);
            else DownloadFile(f.FullName, Path.Combine(local, f.Name), f.Length, progress);
        }
    }

    private void DownloadFile(string remote, string local, long size, IProgress<TransferProgress>? progress)
    {
        using var fs = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var name = Path.GetFileName(local);
        _sftp.DownloadFile(remote, fs, done => progress?.Report(new TransferProgress(name, (long)done, size)));
    }

    public Task DeleteAsync(RemoteItem item, CancellationToken ct = default) =>
        Locked(() => { if (item.IsDirectory) DeleteDir(item.FullPath); else _sftp.DeleteFile(item.FullPath); }, ct);

    private void DeleteDir(string path)
    {
        foreach (var f in _sftp.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory && !f.IsSymbolicLink) DeleteDir(f.FullName); else _sftp.DeleteFile(f.FullName);
        }
        _sftp.DeleteDirectory(path);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Locked(() => _sftp.CreateDirectory(path), ct);

    private async Task Locked(Action work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await Task.Run(work, ct); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_sftp.IsConnected) _sftp.Disconnect();
        _sftp.Dispose();
    }
}
