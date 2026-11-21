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
        VelopackApp.Build()
            .OnFirstRun(_ => Updates.LaunchInfo.FirstRunAfterInstall = true)
            .Run();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        try
        {
            var app = new App();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // An error inside the UI shouldn't take open sessions down with it: log, tell, carry on.
                WriteLog(e.Exception);
                MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to {CrashLog}",
                    "RemoteMan", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
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
        try
        {
            MessageBox.Show($"RemoteMan couldn't start:\n\n{ex.GetBaseException().Message}\n\nDetails were saved to {CrashLog}",
                "RemoteMan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { }
        Environment.Exit(1);
    }

    private static void WriteLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(CrashLog, $"[{DateTimeOffset.Now:O}] {typeof(Program).Assembly.GetName().Version}\n{ex}\n\n");
        }
        catch (Exception) { }
    }
}
