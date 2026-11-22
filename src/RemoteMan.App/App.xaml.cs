using System.Windows;
using RemoteMan.App.Services;
using RemoteMan.App.Updates;

namespace RemoteMan.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = AppSettings.Load();
        // Before any window exists: every view looks its colours up once, as it's built.
        Themes.Apply(Resources, settings.Theme, settings.Shading);
        DarkTitleBar.ApplyToAllWindows();
        // Warm up the shared WebView2 browser process in the background so the first
        // SSH or web tab opens instantly.
        _ = WebViewEnvironment.GetAsync();
        MainWindow = new MainWindow(settings);
        MainWindow.Show();
        UpdateService.Instance.Start();
    }
}
