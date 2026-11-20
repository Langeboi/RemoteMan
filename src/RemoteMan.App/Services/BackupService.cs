using System.IO;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Core.Transfer;

namespace RemoteMan.App.Services;

/// <summary>
/// Daily encrypted backups of the active data source (.rmx, see <see cref="ExportFile"/>). The backup
/// password is kept on this PC under DPAPI; the files themselves open anywhere with the password.
/// </summary>
public static class BackupService
{
    public static string DefaultFolder => Path.Combine(AppPaths.Root, "backups");
    private static string PasswordFile => Path.Combine(AppPaths.Root, "backup.key");
    private const string Prefix = "RemoteMan ";

    public static string Folder(BackupSettings s) => string.IsNullOrWhiteSpace(s.Folder) ? DefaultFolder : Environment.ExpandEnvironmentVariables(s.Folder);

    public static string? LoadPassword()
    {
        try { return File.Exists(PasswordFile) ? new DpapiSecretProtector().Unprotect(File.ReadAllText(PasswordFile)) : null; }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or IOException) { return null; }
    }

    public static void SavePassword(string? password)
    {
        Directory.CreateDirectory(AppPaths.Root);
        if (string.IsNullOrEmpty(password)) { File.Delete(PasswordFile); return; }
        File.WriteAllText(PasswordFile, new DpapiSecretProtector().Protect(password));
    }

    public static bool IsDue(BackupSettings s) =>
        s.Enabled && (s.LastBackup is not { } last || DateTimeOffset.Now - last > TimeSpan.FromHours(20) || last.Date < DateTimeOffset.Now.Date);

    /// <summary>Writes one backup and prunes the oldest beyond <see cref="BackupSettings.Keep"/>. Returns the file written.</summary>
    public static async Task<string> RunAsync(AppSettings settings, EntryStore store, ISecretProtector? protector, string password)
    {
        var s = settings.Backup;
        var folder = Folder(s);
        Directory.CreateDirectory(folder);
        var source = Sanitize(settings.Active.Name);
        var path = Path.Combine(folder, $"{Prefix}{source} {DateTime.Now:yyyy-MM-dd HHmm}{ExportFile.Extension}");
        // Snapshot here, decrypt off the UI thread (shared vault keys are slow to derive on purpose).
        var snapshot = store.Entries.Select(EntryStore.Clone).ToList();
        var bundle = await Task.Run(() => ExportFile.CreateBundle(snapshot, protector, new ExportOptions { IncludeSecrets = s.IncludeSecrets }, store.User, store.Machine));
        await ExportFile.WriteFileAsync(path, bundle, password);

        foreach (var old in List(s).Where(b => b.Source == source).Skip(Math.Max(1, s.Keep)))
            try { File.Delete(old.Path); } catch (IOException) { }

        s.LastBackup = DateTimeOffset.Now;
        settings.Save();
        return path;
    }

    public sealed record BackupFile(string Path, string Source, DateTime When, long Size)
    {
        public string WhenText => When.ToString("yyyy-MM-dd HH:mm");
        public string SizeText => Size < 1024 * 1024 ? $"{Math.Max(1, Size / 1024)} KB" : $"{Size / 1048576.0:0.0} MB";
        public string Name => System.IO.Path.GetFileName(Path);
    }

    /// <summary>Backups in the folder, newest first.</summary>
    public static List<BackupFile> List(BackupSettings s)
    {
        var folder = Folder(s);
        if (!Directory.Exists(folder)) return new();
        return new DirectoryInfo(folder).GetFiles(Prefix + "*" + ExportFile.Extension)
            .Select(f =>
            {
                // "RemoteMan <source> yyyy-MM-dd HHmm.rmx"
                var stem = System.IO.Path.GetFileNameWithoutExtension(f.Name)[Prefix.Length..];
                var source = stem.Length > 16 ? stem[..^16] : stem;
                return new BackupFile(f.FullName, source, f.LastWriteTime, f.Length);
            })
            .OrderByDescending(b => b.When).ToList();
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
