using System.Windows;
using Tether.App.Services;

namespace Tether.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Warm up the shared WebView2 browser process in the background so the first
        // SSH or web tab opens instantly.
        _ = WebViewEnvironment.GetAsync();
        var settings = AppSettings.Load();
        MainWindow = new MainWindow(settings);
        MainWindow.Show();
    }
}
