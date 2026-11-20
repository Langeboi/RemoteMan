using Velopack;

namespace RemoteMan.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: Velopack uses it to handle install, update and uninstall hooks,
        // and to finish an update that was downloaded before the last exit.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
