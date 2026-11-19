using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tether.Protocols.Rdp;

/// <summary>
/// Copy-on-select for RDP. The selection lives on the remote machine, so after a mouse drag
/// (or a double-click) inside the session we send Ctrl+C to the remote; RDP clipboard
/// redirection then brings the text to the local clipboard. A low-level mouse hook is used
/// because the ActiveX window swallows mouse messages before WinForms sees them.
/// </summary>
public sealed class CopyOnSelect : IDisposable
{
    private const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int DragThreshold = 5;

    private readonly Control _target;
    private readonly HookProc _proc;
    private IntPtr _hook;
    private Point _down;
    private bool _pressedInside;
    private long _lastUpTicks;
    private readonly System.Windows.Forms.Timer _send = new() { Interval = 60 };

    public CopyOnSelect(Control target)
    {
        _target = target;
        _proc = HookCallback;
        _send.Tick += (_, _) => { _send.Stop(); SendCtrlC(); };
        using var p = Process.GetCurrentProcess();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(p.MainModule?.ModuleName), 0);
    }

    public bool Enabled { get; set; } = true;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled && _target.IsHandleCreated && _target.Visible)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var pt = new Point(info.x, info.y);
            var inside = _target.RectangleToScreen(_target.ClientRectangle).Contains(pt) && IsOurWindowForeground();
            if (wParam == WM_LBUTTONDOWN) { _down = pt; _pressedInside = inside; }
            else if (wParam == WM_LBUTTONUP && _pressedInside && inside)
            {
                var dragged = Math.Abs(pt.X - _down.X) > DragThreshold || Math.Abs(pt.Y - _down.Y) > DragThreshold;
                var now = Environment.TickCount64;
                var doubleClick = now - _lastUpTicks < SystemInformation.DoubleClickTime;
                _lastUpTicks = now;
                // Let the remote finish its selection first, then copy.
                if (dragged || doubleClick) { _send.Stop(); _send.Start(); }
                _pressedInside = false;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool IsOurWindowForeground()
    {
        var fg = GetForegroundWindow();
        return fg == _target.TopLevelControl?.Handle || GetAncestor(_target.Handle, 2 /* GA_ROOT */) == fg;
    }

    private static void SendCtrlC()
    {
        var inputs = new[]
        {
            Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true), // Ctrl down, C down, C up, Ctrl up
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } },
    };

    public void Dispose()
    {
        _send.Dispose();
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    // Sequential layout puts the union at the right offset on both 32- and 64-bit.
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
