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
