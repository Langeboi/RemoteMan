namespace RemoteMan.App.Updates;

/// <summary>Set from Velopack's startup hooks, before WPF starts.</summary>
public static class LaunchInfo
{
    /// <summary>True when Setup.exe just installed the app and started it.</summary>
    public static bool FirstRunAfterInstall { get; set; }
}
