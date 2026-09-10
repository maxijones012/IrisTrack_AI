using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

/// <summary>One JSON record and at most one crop/frame pair per appearance. Better reads replace that pair.</summary>
public sealed class PlateCaptureService
{
    private sealed record Slot(string Stem, string Text, float Confidence, float Area, int Revision, string? CropPath, string? FramePath);
    private readonly Dictionary<long, Slot> _slots = new();
    private readonly Dictionary<string, (long Track, TimeSpan LastSeen, RectangleF Box)> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<long, long> _aliases = new();
    private string _session = Guid.NewGuid().ToString("N")[..8];

    public void Reset()
    {
        _slots.Clear(); _recent.Clear(); _aliases.Clear();
        _session = Guid.NewGuid().ToString("N")[..8];
    }

    public async Task<bool> SaveAsync(Bitmap frame, Detection detection, string root, string title, string? videoPath,
        DateTime capturedAt, TimeSpan elapsed, bool saveCrop, bool saveFrame, CancellationToken ct)
    {
        if (!detection.IsPlate || !detection.PlateStable || !detection.OcrFresh || string.IsNullOrEmpty(detection.PlateText)) return false;
        if (!saveCrop && !saveFrame) return false;
        ct.ThrowIfCancellationRequested();
        var id = _aliases.GetValueOrDefault(detection.TrackId, detection.TrackId);
        // Keep one appearance if the box briefly loses tracking, but allow a later reappearance.
        if (!_slots.ContainsKey(id) && _recent.TryGetValue(detection.PlateText, out var recent)
            && elapsed - recent.LastSeen < TimeSpan.FromSeconds(4) && Nearby(recent.Box, detection.Box))
        {
            id = recent.Track;
            _aliases[detection.TrackId] = id;
        }
        _recent[detection.PlateText] = (id, elapsed, detection.Box);
        var area = detection.Box.Width * detection.Box.Height;
        var confidence = detection.OcrConfidence ?? 0;
        _slots.TryGetValue(id, out var previous);
        if (previous is not null && previous.Text == detection.PlateText
            && confidence <= previous.Confidence + .01f && !(confidence >= previous.Confidence - .01f && area > previous.Area * 1.20f)) return false;
        var folder = Path.Combine(root, "Patentes");
        Directory.CreateDirectory(folder);
        var stem = previous?.Stem ?? Path.Combine(folder, $"{capturedAt:yyyyMMdd_HHmmss_fff}_PATENTE_{_session}_ID{id}");
        var revision = (previous?.Revision ?? 0) + 1;
        var imageStem = stem + $"_r{revision}";
        string? cropPath = null, framePath = null;
        var temporary = stem + ".json.tmp";
        bool committed = false;
        try
        {
            if (saveCrop)
            {
                var rect = Rectangle.Intersect(Rectangle.Round(detection.Box), new Rectangle(0, 0, frame.Width, frame.Height));
                if (rect.Width > 1 && rect.Height > 1)
                {
                    using var crop = frame.Clone(rect, frame.PixelFormat);
                    cropPath = imageStem + "_RECORTE.jpg";
                    SaveJpeg(crop, cropPath);
                }
            }
            if (saveFrame)
            {
                framePath = imageStem + "_FOTOGRAMA.jpg";
                SaveJpeg(frame, framePath);
            }
            var record = new CaptureRecord(capturedAt, title, "Patente", detection.Confidence, id, cropPath, framePath,
                videoPath, "Patente", detection.PlateText, confidence, true, elapsed.TotalSeconds, null);
            // Stage the new images first. The previous JSON always points to its original images
            // until the new record is committed; cancellation/failure cannot mislabel an old frame.
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, stem + ".json", true);
            committed = true;
            _slots[id] = new Slot(stem, detection.PlateText, confidence, area, revision, cropPath, framePath);
            DeleteIfPresent(previous?.CropPath);
            DeleteIfPresent(previous?.FramePath);
            return previous is null;
        }
        finally
        {
            DeleteIfPresent(temporary);
            if (!committed) { DeleteIfPresent(cropPath); DeleteIfPresent(framePath); }
        }
    }

    private static bool Nearby(RectangleF a, RectangleF b)
        => Math.Abs((a.Left + a.Width / 2) - (b.Left + b.Width / 2)) <= Math.Max(40, Math.Max(a.Width, b.Width) * 1.5f)
            && Math.Abs((a.Top + a.Height / 2) - (b.Top + b.Height / 2)) <= Math.Max(40, Math.Max(a.Height, b.Height) * 1.5f);

    private static void DeleteIfPresent(string? path)
    {
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    private static void SaveJpeg(Bitmap bitmap, string path)
    {
        var temporary = path + ".tmp";
        try { bitmap.Save(temporary, ImageFormat.Jpeg); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
