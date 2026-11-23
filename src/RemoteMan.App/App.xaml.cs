using System.Windows;
using RemoteMan.App.Services;
using RemoteMan.App.Updates;

namespace RemoteMan.App;

public partial class App : Application
{
    private const string ImportArg = "--import=";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // No settings file yet: this is the first start on this PC (for this Windows user).
        var firstRun = !System.IO.File.Exists(AppPaths.Settings);
        var settings = AppSettings.Load();
        // Before the window opens: decides whether the What's new tab opens (never on a fresh install).
        WhatsNew.CheckOnStart(UpdateService.Instance.CurrentVersion, firstRun);
        // Before any window exists: every view looks its colours up once, as it's built.
        Themes.Apply(Resources, settings.Theme, settings.Shading);
        DarkTitleBar.ApplyToAllWindows();
        // Warm up the shared WebView2 browser process in the background so the first
        // SSH or web tab opens instantly.
        _ = WebViewEnvironment.GetAsync();

        // Setup may restart RemoteMan to apply a theme; the import it asked for comes along as an argument.
        var importAfter = e.Args.FirstOrDefault(a => a.StartsWith(ImportArg, StringComparison.Ordinal))?[ImportArg.Length..];
        if (firstRun)
        {
            // The setup window would otherwise count as the main window, and closing it would end the app.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var setup = new Views.SetupWindow(settings, rerun: false);
            setup.ShowDialog();
            importAfter = setup.ImportAfter;
            if (setup.NeedsRestart && Environment.ProcessPath is { Length: > 0 } exe)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                    if (importAfter is not null) psi.ArgumentList.Add(ImportArg + importAfter);
                    System.Diagnostics.Process.Start(psi);
                    Shutdown();
                    return;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Restart after setup failed: {ex.Message}"); }
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        var main = new MainWindow(settings);
        MainWindow = main;
        if (importAfter is "rdm" or "rdp" or "putty") main.ImportAfterLoad = importAfter;
        MainWindow.Show();
        UpdateService.Instance.Start();
    }
}
