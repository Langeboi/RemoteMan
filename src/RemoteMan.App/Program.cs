using System.IO;
using System.Windows;
using Velopack;

namespace RemoteMan.App;

public static class Program
{
    private static string LogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteMan");
    public static string CrashLog => Path.Combine(LogDir, "crash.log");

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: Velopack uses it to handle install, update and uninstall hooks,
        // and to finish an update that was downloaded before the last exit.
        VelopackApp.Build().Run();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        // A task nobody awaited failed: it doesn't end the app, but keep it for the log.
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteLog(e.Exception, "unobserved task"); e.SetObserved(); };
        RecordLastRun();
        // Runs on a normal exit and on Environment.Exit (an update restart), not when the process is torn down.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EndRun();
        try
        {
            var app = new App();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // An error inside the UI shouldn't take open sessions down with it: log, tell, carry on.
                WriteLog(e.Exception);
                e.Handled = true;
                ShowError("Something went wrong", e.Exception.Message);
            };
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
        }
    }

    /// <summary>A startup failure would otherwise exit silently: save it and show it.</summary>
    private static void ReportCrash(Exception? ex)
    {
        if (ex is null) return;
        WriteLog(ex);
        ShowError("RemoteMan couldn't start", ex.GetBaseException().Message);
        Environment.Exit(1);
    }

    /// <summary>The themed error box, with a button to open the log. Falls back to the plain Windows box
    /// if WPF itself is what broke.</summary>
    private static void ShowError(string title, string message)
    {
        var text = $"{message}\n\nDetails were saved to {CrashLog}";
        try
        {
            if (Views.ChoiceDialog.Show(null, title, text, ["OK", "Open log"], Views.DialogIcon.Error) == 1)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CrashLog) { UseShellExecute = true });
        }
        catch (Exception)
        {
            try { MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Error); } catch (Exception) { }
        }
    }

    private static void WriteLog(Exception ex, string? what = null) =>
        WriteLog($"{(what is null ? "" : what + ": ")}{ex}");

    private static void WriteLog(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(CrashLog, $"[{DateTimeOffset.Now:O}] {typeof(Program).Assembly.GetName().Version}\n{text}\n\n");
        }
        catch (Exception) { }
    }

    // ── Crashes that skip the handlers above ─────────────────────────────────────────────────────────────────
    // A crash inside native code (the RDP control, WebView2, a driver) or a stack overflow ends the process
    // before any .NET handler runs. A marker file is written at start and removed on a normal exit; if it's
    // still there at the next start, the last run died that way, and Windows' own record of it (Event Viewer,
    // Application log) is copied into crash.log so it can be sent along like any other crash.

    private static string RunMarker => Path.Combine(LogDir, "running");

    private static void RecordLastRun()
    {
        try
        {
            if (File.Exists(RunMarker))
            {
                var started = File.GetLastWriteTime(RunMarker);
                var other = File.ReadAllText(RunMarker).Trim();
                // Another copy still running (a second window) isn't a crash.
                if (!(int.TryParse(other, out var pid) && IsRemoteMan(pid)) && WindowsCrashRecord(started) is { } record)
                    WriteLog($"The previous run (started {started:yyyy-MM-dd HH:mm:ss}) crashed outside .NET; Windows recorded:\n{record}");
            }
            Directory.CreateDirectory(LogDir);
            File.WriteAllText(RunMarker, Environment.ProcessId.ToString());
        }
        catch (Exception) { }
    }

    private static void EndRun()
    {
        try
        {
            if (File.Exists(RunMarker) && File.ReadAllText(RunMarker).Trim() == Environment.ProcessId.ToString()) File.Delete(RunMarker);
        }
        catch (Exception) { }
    }

    private static bool IsRemoteMan(int pid)
    {
        if (pid == Environment.ProcessId) return false;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>Windows' Application log entries about RemoteMan since <paramref name="since"/> (the crash itself).</summary>
    private static string? WindowsCrashRecord(DateTime since)
    {
        try
        {
            using var log = new System.Diagnostics.EventLog("Application");
            var exe = Path.GetFileName(Environment.ProcessPath ?? "RemoteMan.exe");
            var hits = new List<string>();
            for (var i = log.Entries.Count - 1; i >= 0 && hits.Count < 3; i--)
            {
                var e = log.Entries[i];
                if (e.TimeGenerated < since) break;
                if (e.EntryType != System.Diagnostics.EventLogEntryType.Error) continue;
                if (e.Source is not ("Application Error" or ".NET Runtime" or "Application Hang")) continue;
                if (!e.Message.Contains(exe, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add($"Windows Event Log, {e.Source}, {e.TimeGenerated:yyyy-MM-dd HH:mm:ss}:\n{e.Message.Trim()}");
            }
            // Nothing recorded: ended from Task Manager, a shutdown, or a crash already in the log above.
            return hits.Count == 0 ? null : string.Join("\n\n", hits);
        }
        catch (Exception ex) { return $"Couldn't read the Windows event log: {ex.Message}"; }
    }
}
