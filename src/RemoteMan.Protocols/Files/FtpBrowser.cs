using FluentFTP;
using RemoteMan.Core.Model;
using RemoteMan.Protocols.Ssh;

namespace RemoteMan.Protocols.Files;

/// <summary>FTP / FTPS (explicit TLS) file browser for FTP entries.</summary>
public sealed class FtpBrowser : IRemoteFiles
{
    private readonly AsyncFtpClient _ftp;

    private FtpBrowser(AsyncFtpClient ftp, string dir)
    {
        _ftp = ftp;
        CurrentDirectory = dir;
    }

    public static async Task<FtpBrowser> ConnectAsync(HostEndpoint endpoint, ResolvedCredential cred, FtpSettings settings, CancellationToken ct = default)
    {
        var config = new FtpConfig
        {
            EncryptionMode = settings.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            ValidateAnyCertificate = settings.IgnoreCertificateErrors,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ConnectTimeout = 15000,
        };
        var user = string.IsNullOrEmpty(cred.UserName) ? "anonymous" : cred.UserName;
        var pass = cred.Password ?? (user == "anonymous" ? "anonymous@" : "");
        var ftp = new AsyncFtpClient(endpoint.Host, user, pass, endpoint.Port > 0 ? endpoint.Port : 21, config);
        try
        {
            await ftp.Connect(ct);
            var dir = string.IsNullOrWhiteSpace(settings.StartFolder) ? await ftp.GetWorkingDirectory(ct) : settings.StartFolder!;
            return new FtpBrowser(ftp, string.IsNullOrEmpty(dir) ? "/" : dir);
        }
        catch
        {
            ftp.Dispose();
            throw;
        }
    }

    public string CurrentDirectory { get; private set; }
    public string Parent(string path) => SftpBrowser.Parent(path);
    public string Combine(string dir, string name) => SftpBrowser.Combine(dir, name);

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct = default)
    {
        var list = await _ftp.GetListing(path, FtpListOption.AllFiles, ct);
        CurrentDirectory = path;
        return list.Where(f => f.Name is not ("." or ".."))
            .Select(f => new RemoteItem(f.Name, f.FullName, f.Type == FtpObjectType.Directory, f.Size < 0 ? 0 : f.Size, f.Modified))
            .OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task UploadAsync(IEnumerable<string> localPaths, string remoteDir, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        foreach (var path in localPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                var target = Combine(remoteDir, Path.GetFileName(path.TrimEnd('\\', '/')));
                await _ftp.UploadDirectory(path, target, FtpFolderSyncMode.Update, FtpRemoteExists.Overwrite, FtpVerify.None, null,
                    Report(progress, Path.GetFileName(path)), ct);
            }
            else
            {
                var name = Path.GetFileName(path);
                await _ftp.UploadFile(path, Combine(remoteDir, name), FtpRemoteExists.Overwrite, true, FtpVerify.None, Report(progress, name), ct);
            }
        }
    }

    public async Task DownloadAsync(IEnumerable<RemoteItem> items, string localDir, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
                await _ftp.DownloadDirectory(Path.Combine(localDir, item.Name), item.FullPath, FtpFolderSyncMode.Update, FtpLocalExists.Overwrite,
                    FtpVerify.None, null, Report(progress, item.Name), ct);
            else
                await _ftp.DownloadFile(Path.Combine(localDir, item.Name), item.FullPath, FtpLocalExists.Overwrite, FtpVerify.None, Report(progress, item.Name), ct);
        }
    }

    private static IProgress<FtpProgress>? Report(IProgress<TransferProgress>? progress, string name) =>
        progress is null ? null : new Progress<FtpProgress>(p => progress.Report(new TransferProgress(name, p.TransferredBytes, p.TransferredBytes + Math.Max(0, (long)(p.TransferSpeed * p.ETA.TotalSeconds)))));

    public async Task DeleteAsync(RemoteItem item, CancellationToken ct = default)
    {
        if (item.IsDirectory) await _ftp.DeleteDirectory(item.FullPath, ct);
        else await _ftp.DeleteFile(item.FullPath, ct);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => _ftp.CreateDirectory(path, ct);

    public void Dispose() => _ftp.Dispose();
}
