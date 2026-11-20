using System.Diagnostics;
using Microsoft.Win32;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Web;

/// <summary>
/// Opens a web entry in a real browser. Chromium browsers get an isolated profile with
/// certificate errors ignored (that flag is refused on a normal profile). Firefox has no
/// such switch, so it opens normally.
/// </summary>
public static class ExternalBrowser
{
    public static bool IsInstalled(BrowserKind kind) => kind == BrowserKind.SystemDefault || FindExe(kind) is not null;

    /// <summary>Opens the URL in the chosen browser. Throws <see cref="BrowserNotInstalledException"/>
    /// when it isn't installed, so the caller can offer to install it.</summary>
    public static void Open(string url, BrowserKind kind, bool ignoreCertErrors)
    {
        url = WebSettings.NormalizeUrl(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"\"{url}\" is not a web address.");

        if (kind is BrowserKind.SystemDefault or BrowserKind.Embedded)
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        var exe = FindExe(kind) ?? throw new BrowserNotInstalledException(kind);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (kind is BrowserKind.Chrome or BrowserKind.Edge)
        {
            if (ignoreCertErrors)
            {
                psi.ArgumentList.Add("--user-data-dir=" + Path.Combine(Path.GetTempPath(), "RemoteMan", $"profile-{kind}".ToLowerInvariant()));
                psi.ArgumentList.Add("--ignore-certificate-errors");
                psi.ArgumentList.Add("--no-first-run");
            }
            psi.ArgumentList.Add("--new-window");
        }
        else if (kind == BrowserKind.Firefox)
        {
            psi.ArgumentList.Add("-new-window");
        }
        psi.ArgumentList.Add(uri.AbsoluteUri);
        Process.Start(psi);
    }

    private static string? FindExe(BrowserKind kind)
    {
        var (name, folders) = kind switch
        {
            BrowserKind.Chrome => ("chrome.exe", new[] { @"Google\Chrome\Application" }),
            BrowserKind.Edge => ("msedge.exe", new[] { @"Microsoft\Edge\Application" }),
            BrowserKind.Firefox => ("firefox.exe", new[] { "Mozilla Firefox", @"Firefox Developer Edition", @"Mozilla Firefox ESR" }),
            _ => (null, Array.Empty<string>()),
        };
        if (name is null || !OperatingSystem.IsWindows()) return null;

        // 1. App Paths (where installers normally register).
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
            if (Exe(key?.GetValue(null) as string) is { } p) return p;
        }
        // 2. Registered browsers (StartMenuInternet), including 32-bit and per-user installs.
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var path in new[] { @"SOFTWARE\Clients\StartMenuInternet", @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet" })
            {
                using var clients = root.OpenSubKey(path);
                if (clients is null) continue;
                foreach (var sub in clients.GetSubKeyNames())
                {
                    using var cmd = clients.OpenSubKey(sub + @"\shell\open\command");
                    if (Exe(cmd?.GetValue(null) as string) is { } p && Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)) return p;
                }
            }
        // 3. Usual install folders.
        var bases = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var b in bases.Where(b => b.Length > 0))
            foreach (var f in folders)
                if (Exe(Path.Combine(b, f, name)) is { } p) return p;
        // 4. PATH.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Exe(Path.Combine(dir.Trim(), name)) is { } p) return p;
        return null;
    }

    /// <summary>Path from a registry value such as "C:\\x\\firefox.exe" -osint -url "%1"; null when missing.</summary>
    private static string? Exe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        string path;
        if (v.StartsWith('"')) { var end = v.IndexOf('"', 1); path = end > 1 ? v[1..end] : v.Trim('"'); }
        else { var i = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); path = i > 0 ? v[..(i + 4)] : v; }
        path = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(path) ? path : null;
    }
}

public sealed class BrowserNotInstalledException(BrowserKind kind) : Exception($"{BrowserInstaller.DisplayName(kind)} isn't installed on this PC.")
{
    public BrowserKind Browser { get; } = kind;
}

/// <summary>
/// Installs a missing browser on request: winget (silent, per machine) when it's available,
/// otherwise the vendor's download page opens.
/// </summary>
public static class BrowserInstaller
{
    public static string DisplayName(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        BrowserKind.Firefox => "Firefox",
        _ => kind.ToString(),
    };

    private static (string WingetId, string DownloadPage) Source(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => ("Google.Chrome", "https://www.google.com/chrome/"),
        BrowserKind.Edge => ("Microsoft.Edge", "https://www.microsoft.com/edge/download"),
        BrowserKind.Firefox => ("Mozilla.Firefox", "https://www.mozilla.org/firefox/new/"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public enum Result { Installed, DownloadPageOpened, Failed }

    public static async Task<Result> InstallAsync(BrowserKind kind, CancellationToken ct = default)
    {
        var (id, page) = Source(kind);
        try
        {
            var psi = new ProcessStartInfo("winget") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "install", "--exact", "--id", id, "--silent", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is not null)
            {
                await p.WaitForExitAsync(ct);
                if (p.ExitCode == 0 && ExternalBrowser.IsInstalled(kind)) return Result.Installed;
            }
        }
        catch (System.ComponentModel.Win32Exception) { /* no winget on this machine */ }

        try
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
            return Result.DownloadPageOpened;
        }
        catch (Exception) { return Result.Failed; }
    }
}
