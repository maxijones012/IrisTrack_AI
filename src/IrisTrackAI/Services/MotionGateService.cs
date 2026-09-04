using System.Drawing;
using System.Drawing.Imaging;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

/// <summary>
/// Detector barato de cambio visual cerca de la línea. No reemplaza a YOLO:
/// sólo sirve para dormir/despertar la inferencia cuando la escena está quieta.
/// </summary>
public sealed class MotionGateService
{
    private byte[]? _previous;
    private int _sampleCount;

    public void Reset()
    {
        _previous = null;
        _sampleCount = 0;
    }

    public unsafe bool HasMotion(Bitmap frame, AnalysisLine line, double band = 0.14)
    {
        if (!line.IsValid || frame.Width < 16 || frame.Height < 16) return true;

        Bitmap? converted = null;
        var bitmap = frame;
        if (frame.PixelFormat != PixelFormat.Format24bppRgb)
        {
            converted = Ensure24Bpp(frame);
            bitmap = converted;
        }

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            const int step = 10; // Muestreo liviano: ~1/100 de los píxeles.
            var values = new List<byte>(Math.Max(128, bitmap.Width * bitmap.Height / (step * step * 5)));
            var basePtr = (byte*)data.Scan0;

            for (int y = step / 2; y < bitmap.Height; y += step)
            {
                var ny = (double)y / bitmap.Height;
                var row = basePtr + y * data.Stride;
                for (int x = step / 2; x < bitmap.Width; x += step)
                {
                    var nx = (double)x / bitmap.Width;
                    if (LineCrossingService.DistanceToSegment(nx, ny, line.X1, line.Y1, line.X2, line.Y2) > band) continue;
                    var p = row + x * 3;
                    // BGR -> luminancia aproximada con enteros.
                    values.Add((byte)((p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8));
                }
            }

            if (values.Count < 20) return true;
            if (_previous is null || _sampleCount != values.Count)
            {
                _previous = values.ToArray();
                _sampleCount = values.Count;
                return true; // Primera muestra: despertamos para no perder el inicio.
            }

            var changed = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (Math.Abs(values[i] - _previous[i]) >= 20) changed++;
                _previous[i] = values[i];
            }

            // Con CCTV comprimido toleramos ruido leve. Aproximadamente 2,2% de muestras cambiadas.
            return changed >= Math.Max(4, (int)(values.Count * 0.022));
        }
        finally
        {
            bitmap.UnlockBits(data);
            converted?.Dispose();
        }
    }

    private static Bitmap Ensure24Bpp(Bitmap source)
    {
        var clone = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(clone);
        g.DrawImageUnscaled(source, 0, 0);
        return clone;
    }
}
