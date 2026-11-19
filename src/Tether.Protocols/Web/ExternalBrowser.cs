using System.Diagnostics;
using Microsoft.Win32;
using Tether.Core.Model;

namespace Tether.Protocols.Web;

/// <summary>
/// Opens a web entry in a real browser. Chromium browsers get an isolated profile with
/// certificate errors ignored (that flag is refused on a normal profile). Firefox has no
/// such switch, so it opens normally.
/// </summary>
public static class ExternalBrowser
{
    public static bool IsInstalled(BrowserKind kind) => kind == BrowserKind.SystemDefault || FindExe(kind) is not null;

    public static void Open(string url, BrowserKind kind, bool ignoreCertErrors)
    {
        if (kind is BrowserKind.SystemDefault or BrowserKind.Embedded)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }
        var exe = FindExe(kind) ?? throw new FileNotFoundException($"{kind} is not installed.");
        var args = new List<string>();
        if (kind is BrowserKind.Chrome or BrowserKind.Edge)
        {
            if (ignoreCertErrors)
            {
                var profile = Path.Combine(Path.GetTempPath(), "Tether", $"profile-{kind}".ToLowerInvariant());
                args.Add($"--user-data-dir=\"{profile}\"");
                args.Add("--ignore-certificate-errors");
                args.Add("--no-first-run");
            }
            args.Add("--new-window");
        }
        else if (kind == BrowserKind.Firefox)
        {
            args.Add("-new-window");
        }
        args.Add($"\"{url}\"");
        Process.Start(new ProcessStartInfo(exe, string.Join(' ', args)) { UseShellExecute = false });
    }

    private static string? FindExe(BrowserKind kind)
    {
        var name = kind switch
        {
            BrowserKind.Chrome => "chrome.exe",
            BrowserKind.Edge => "msedge.exe",
            BrowserKind.Firefox => "firefox.exe",
            _ => null,
        };
        if (name is null || !OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }
        return null;
    }
}
