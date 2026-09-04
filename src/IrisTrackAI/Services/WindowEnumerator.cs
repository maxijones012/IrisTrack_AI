using System.Text;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public static class WindowEnumerator
{
    public static IReadOnlyList<WindowTarget> GetWindows()
    {
        var list = new List<WindowTarget>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            var len = NativeMethods.GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return true;
            list.Add(new WindowTarget(hwnd, title, pid));
            return true;
        }, nint.Zero);
        return list.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
