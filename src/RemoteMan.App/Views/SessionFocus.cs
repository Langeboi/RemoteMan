using System.Windows;
using System.Windows.Input;

namespace RemoteMan.App.Views;

internal static class SessionFocus
{
    /// <summary>Puts the keyboard in an embedded browser (WebView2). WPF can still think the control has focus after
    /// the window lost and regained it, while Windows has it on the main window: Focus() then does nothing. Clearing
    /// first makes WebView2 hand the focus to the page again.</summary>
    public static void Into(UIElement web)
    {
        if (web.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        web.Focus();
    }
}
