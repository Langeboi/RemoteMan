using System.Windows;
using RemoteMan.App.Services;

namespace RemoteMan.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DarkTitleBar.ApplyToAllWindows();
        // Warm up the shared WebView2 browser process in the background so the first
        // SSH or web tab opens instantly.
        _ = WebViewEnvironment.GetAsync();
        var settings = AppSettings.Load();
        MainWindow = new MainWindow(settings);
        MainWindow.Show();
    }
}
