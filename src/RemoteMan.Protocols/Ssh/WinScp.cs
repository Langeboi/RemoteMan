using System.Diagnostics;
using Microsoft.Win32;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Ssh;

/// <summary>"Open with > Browse files (WinSCP)" for SSH entries.</summary>
public static class WinScp
{
    public static bool IsInstalled => FindExe() is not null;

    public static void Open(HostEndpoint ep, ResolvedCredential cred)
    {
        var exe = FindExe() ?? throw new FileNotFoundException("WinSCP isn't installed.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        var user = Uri.EscapeDataString(cred.UserName ?? "");
        // The password goes on WinSCP's command line (its documented way); keys go as a temporary file.
        var auth = cred.Password is { Length: > 0 } pw && cred.PrivateKey is null ? $"{user}:{Uri.EscapeDataString(pw)}" : user;
        psi.ArgumentList.Add($"sftp://{(auth.Length > 0 ? auth + "@" : "")}{ep.Host}:{(ep.Port > 0 ? ep.Port : 22)}/");
        if (cred.PrivateKey is { Length: > 0 } key)
        {
            var dir = Path.Combine(Path.GetTempPath(), "RemoteMan");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"key-{Guid.NewGuid():N}");
            File.WriteAllText(file, key);
            psi.ArgumentList.Add($"/privatekey={file}");
            if (cred.Passphrase is { Length: > 0 } pp) psi.ArgumentList.Add($"/passphrase={pp}");
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(30)); try { File.Delete(file); } catch { } });
        }
        Process.Start(psi);
    }

    private static string? FindExe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinSCP.exe");
            if (key?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
        }
        foreach (var b in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            var basePath = Environment.GetFolderPath(b);
            if (basePath.Length == 0) continue;
            foreach (var sub in new[] { "WinSCP", @"Programs\WinSCP" })
            {
                var p = Path.Combine(basePath, sub, "WinSCP.exe");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
