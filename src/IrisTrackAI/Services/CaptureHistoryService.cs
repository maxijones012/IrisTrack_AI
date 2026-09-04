using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public sealed class CaptureHistoryService
{
    public string? LinkedVideoPath { get; set; }
    public bool SaveCrop { get; set; } = true;
    public bool SaveFullFrame { get; set; } = true;

    public string ResolveOutputFolder(string windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(LinkedVideoPath) && File.Exists(LinkedVideoPath))
        {
            var dir = Path.GetDirectoryName(LinkedVideoPath)!;
            var stem = Path.GetFileNameWithoutExtension(LinkedVideoPath);
            return Path.Combine(dir, stem + "_Detecciones");
        }
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pics, "IrisTrack AI", Sanitize(windowTitle));
    }

    public async Task<CaptureRecord> SaveAsync(Bitmap frame, Detection d, string windowTitle, CancellationToken ct = default, string eventType = "Deteccion")
    {
        var root = ResolveOutputFolder(windowTitle);
        var classDir = Path.Combine(root, Sanitize(d.ClassName));
        Directory.CreateDirectory(classDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var prefix = eventType.Equals("CruceLinea", StringComparison.OrdinalIgnoreCase) ? "CRUCE_" : "";
        var stem = $"{prefix}{stamp}_{Sanitize(d.ClassName)}_{Math.Round(d.Confidence*100)}pct_ID{d.TrackId}";
        string? cropPath=null, framePath=null;
        if (SaveCrop)
        {
            var rect = Rectangle.Round(d.Box); rect.Intersect(new Rectangle(0,0,frame.Width,frame.Height));
            if (rect.Width > 1 && rect.Height > 1)
            {
                using var crop = frame.Clone(rect, frame.PixelFormat);
                cropPath = Path.Combine(classDir, stem + "_RECORTE.jpg"); crop.Save(cropPath, ImageFormat.Jpeg);
            }
        }
        if (SaveFullFrame)
        {
            framePath = Path.Combine(classDir, stem + "_FOTOGRAMA.jpg"); frame.Save(framePath, ImageFormat.Jpeg);
        }
        var record = new CaptureRecord(DateTime.Now, windowTitle, d.ClassName, d.Confidence, d.TrackId, cropPath, framePath, LinkedVideoPath, eventType);
        var json = JsonSerializer.Serialize(record);
        await File.AppendAllTextAsync(Path.Combine(root, "historial.jsonl"), json + Environment.NewLine, ct);
        return record;
    }

    public async Task<string> SaveManualFrameAsync(Bitmap frame, string windowTitle, CancellationToken ct = default)
    {
        var root=ResolveOutputFolder(windowTitle); Directory.CreateDirectory(root);
        var p=Path.Combine(root,$"MANUAL_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg"); frame.Save(p,ImageFormat.Jpeg);
        await Task.CompletedTask; return p;
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value=value.Replace(c,'_');
        return string.IsNullOrWhiteSpace(value) ? "Sin_nombre" : value.Trim();
    }
}
