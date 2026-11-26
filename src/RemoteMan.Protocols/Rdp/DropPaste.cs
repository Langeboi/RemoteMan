using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteMan.Protocols.Rdp;

/// <summary>
/// Files dropped onto the remote desktop. RDP has no drag and drop of its own, so the drop is finished the way a
/// person would: the files go on the clipboard (the caller does that, as for "Send files"), then a click at the
/// spot they were dropped on and Ctrl+V. The click lands in the remote folder, desktop or window under the
/// pointer, and Explorer's paste pulls the files across RDP's clipboard channel.
/// Every step checks that the session is still the thing under the pointer and in front, so the click and the
/// keys never reach another window or app.
/// </summary>
public static class DropPaste
{
    /// <summary>True when <paramref name="screen"/> (physical pixels) is on the session and nothing covers it there.</summary>
    public static bool IsOnSession(Control rdp, Point screen)
    {
        if (!rdp.IsHandleCreated || !rdp.Visible) return false;
        if (!rdp.RectangleToScreen(rdp.ClientRectangle).Contains(screen)) return false;
        var hwnd = WindowFromPoint(screen);
        return hwnd == rdp.Handle || IsChild(rdp.Handle, hwnd);
    }

    /// <summary>A left click at <paramref name="screen"/>, sent to the session under it. False when it isn't there.</summary>
    public static bool ClickAt(Control rdp, Point screen)
    {
        if (!IsOnSession(rdp, screen)) return false;
        var (x, y) = ToAbsolute(screen, SystemInformation.VirtualScreen);
        const uint move = 0x0001, down = 0x0002, up = 0x0004, absolute = 0x8000, virtualDesk = 0x4000;
        var inputs = new[]
        {
            Mouse(x, y, move | absolute | virtualDesk),
            Mouse(x, y, down | absolute | virtualDesk),
            Mouse(x, y, up | absolute | virtualDesk),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>Ctrl+V into the session, only while RemoteMan is the active app and the session has the keyboard.</summary>
    public static bool Paste(Control rdp)
    {
        if (!rdp.IsHandleCreated || !rdp.Visible) return false;
        var root = GetAncestor(rdp.Handle, 2 /* GA_ROOT */);
        if (GetForegroundWindow() != root) return false;
        var focus = FocusedWindow();
        if (focus != rdp.Handle && !IsChild(rdp.Handle, focus)) return false;
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) }; // Ctrl down, V down, V up, Ctrl up
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>Screen pixels to SendInput's 0..65535 range over the whole virtual desktop (all monitors).</summary>
    public static (int X, int Y) ToAbsolute(Point screen, Rectangle virtualScreen)
    {
        static int Scale(int v, int origin, int size) =>
            size <= 1 ? 0 : (int)Math.Round((v - origin) * 65535.0 / (size - 1));
        return (Math.Clamp(Scale(screen.X, virtualScreen.Left, virtualScreen.Width), 0, 65535),
                Math.Clamp(Scale(screen.Y, virtualScreen.Top, virtualScreen.Height), 0, 65535));
    }

    /// <summary>The window with keyboard focus in the foreground thread (GetFocus only sees this thread's).</summary>
    private static IntPtr FocusedWindow()
    {
        var fg = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(fg, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    private static INPUT Mouse(int x, int y, uint flags) => new()
    {
        type = 0,
        u = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags } },
    };

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } },
    };

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
}
