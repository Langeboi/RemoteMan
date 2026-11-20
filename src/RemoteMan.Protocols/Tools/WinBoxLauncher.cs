using System.Diagnostics;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Tools;

/// <summary>Starts MikroTik WinBox with the address and login, and downloads WinBox on request.</summary>
public static class WinBoxLauncher
{
    private const string DownloadUrl = "https://mt.lv/winbox64";

    /// <summary>Where RemoteMan keeps a WinBox it downloaded itself.</summary>
    public static string LocalCopy => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteMan", "tools", "winbox64.exe");

    public static string? FindExe()
    {
        var names = new[] { "winbox64.exe", "winbox.exe", "WinBox.exe" };
        var dirs = new List<string>
        {
            Path.GetDirectoryName(LocalCopy)!,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MikroTik", "WinBox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinBox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinBox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinBox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d.Trim(), n); if (File.Exists(p)) return p; }
                catch (ArgumentException) { }
            }
        return null;
    }

    /// <summary>Downloads winbox64.exe from MikroTik into RemoteMan's tools folder.</summary>
    public static async Task<string> DownloadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalCopy)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var bytes = await http.GetByteArrayAsync(DownloadUrl, ct);
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new IOException("The WinBox download wasn't a program file.");
        await File.WriteAllBytesAsync(LocalCopy, bytes, ct);
        return LocalCopy;
    }

    public static void Open(string exe, HostEndpoint endpoint, ResolvedCredential cred)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add(endpoint.Port > 0 && endpoint.Port != 8291 ? $"{endpoint.Host}:{endpoint.Port}" : endpoint.Host);
        if (!string.IsNullOrEmpty(cred.UserName))
        {
            psi.ArgumentList.Add(cred.UserName);
            psi.ArgumentList.Add(cred.Password ?? "");
        }
        Process.Start(psi);
    }
}
