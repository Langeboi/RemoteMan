using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteMan.App.Services;

/// <summary>
/// Dialogs keep the standard Windows frame; this makes that frame dark (Windows 10 20H1+)
/// and, on Windows 11, paints the caption in the app's own header color so it blends in.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Applies to every window the app opens, dialogs included.</summary>
    public static void ApplyToAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((Window)s)));

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));
        // COLORREF is 0x00BBGGRR. Header #16191C, border #2A2F35, text #E3E6EA. Ignored before Windows 11.
        var caption = 0x001C1916; var border = 0x00352F2A; var text = 0x00EAE6E3;
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }
}
