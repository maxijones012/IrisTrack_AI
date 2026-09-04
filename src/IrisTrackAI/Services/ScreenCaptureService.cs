using System.Drawing;
using System.Drawing.Imaging;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public sealed class ScreenCaptureService
{
    public Bitmap? Capture(WindowTarget target)
    {
        if (!NativeMethods.IsWindowVisible(target.Hwnd) || NativeMethods.IsIconic(target.Hwnd)) return null;
        if (!NativeMethods.TryGetExtendedBounds(target.Hwnd, out var r) || r.Width < 10 || r.Height < 10) return null;
        try
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { return null; }
    }
}
