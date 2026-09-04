using System.Runtime.InteropServices;
using System.Text;

namespace IrisTrackAI.Services;

internal static class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TRANSPARENT = 0x20;
    internal const long WS_EX_TOOLWINDOW = 0x80;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    internal const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal static readonly nint HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll")] internal static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hWnd, int id);

    internal static bool TryGetExtendedBounds(nint hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0 && rect.Width > 0 && rect.Height > 0)
            return true;
        return GetWindowRect(hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }

    internal static string GetWindowTitleText(nint hwnd)
    {
        try
        {
            var len = GetWindowTextLength(hwnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString().Trim();
        }
        catch { return string.Empty; }
    }

    internal static bool IsTargetForeground(nint hwnd, uint targetPid)
    {
        var fg = GetForegroundWindow();
        if (fg == nint.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == targetPid;
    }
}
