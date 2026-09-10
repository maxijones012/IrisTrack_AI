using System.Drawing;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

/// <summary>Owned by the serialized analysis loop; callers must await it before switching or disposing.</summary>
public sealed class DetectionEngine : IDisposable
{
    private readonly ModelManager _models;
    private YoloDetector? _general;
    private PlateDetector? _plates;
    private PlateOcrReader? _ocr;
    private static readonly HashSet<int> Vehicles = new() { 2, 3, 5, 7 };
    public DetectionMode? LoadedMode { get; private set; }
    public bool GeneralLoaded => _general is not null;
    public bool PlatesLoaded => _plates is not null && _ocr is not null;
    public long PlateInferenceCount => _plates?.InferenceCount ?? 0;
    public long OcrInferenceCount => _ocr?.InferenceCount ?? 0;
    public string Description => LoadedMode switch
    {
        DetectionMode.General => $"YOLO26n · {_general?.ProviderName}",
        DetectionMode.Plates => $"Patentes · {_plates?.ProviderName} / OCR {_ocr?.ProviderName}",
        DetectionMode.VehiclesAndPlates => $"Vehículos + patentes · {_general?.ProviderName} / {_plates?.ProviderName} / OCR {_ocr?.ProviderName}",
        _ => "Sin motor cargado"
    };

    public DetectionEngine(ModelManager? models = null) => _models = models ?? new ModelManager();

    public async Task EnsureModeAsync(DetectionMode mode, IProgress<string>? progress, CancellationToken ct, bool cpuOnly = false)
    {
        if (LoadedMode == mode) return;
        Unload();
        try
        {
            if (mode != DetectionMode.Plates)
            {
                progress?.Report("Preparando detección general…");
                var path = await _models.EnsureModelAsync(new InlineProgress<double>(p => progress?.Report($"Descargando YOLO26n… {p:P0}")), ct);
                _general = new YoloDetector();
                await Task.Run(() => _general.Load(path, cpuOnly), ct);
            }
            if (mode != DetectionMode.General)
            {
                var paths = await _models.EnsurePlateModelsAsync(progress, ct);
                progress?.Report("Cargando modelos de patentes…");
                _plates = new PlateDetector();
                _ocr = new PlateOcrReader();
                await Task.Run(() => { _plates.Load(paths.Detector, cpuOnly); _ocr.Load(paths.Ocr, cpuOnly); }, ct);
            }
            ct.ThrowIfCancellationRequested();
            LoadedMode = mode;
            progress?.Report(Description);
        }
        catch { Unload(); throw; }
    }

    public IReadOnlyList<Detection> Detect(Bitmap frame, float threshold, IReadOnlySet<int>? classes,
        IReadOnlyList<AnalysisZone> zones, CancellationToken ct)
    {
        if (LoadedMode is null) throw new InvalidOperationException("Elegí y prepará un modo de detección.");
        if (LoadedMode == DetectionMode.Plates)
            return ZoneFilterService.Apply(_plates!.Detect(frame, threshold), zones, frame.Width, frame.Height);
        var general = ZoneFilterService.Apply(_general!.Detect(frame, threshold,
            LoadedMode == DetectionMode.VehiclesAndPlates ? Vehicles : classes), zones, frame.Width, frame.Height);
        if (LoadedMode == DetectionMode.General) return general;
        var plates = new List<Detection>();
        foreach (var vehicle in general)
        {
            ct.ThrowIfCancellationRequested();
            var rect = Rectangle.Intersect(Rectangle.Round(vehicle.Box), new Rectangle(0, 0, frame.Width, frame.Height));
            if (rect.Width < 16 || rect.Height < 16) continue;
            using var crop = frame.Clone(rect, frame.PixelFormat);
            foreach (var plate in _plates!.Detect(crop, threshold))
            {
                var box = plate.Box;
                box.Offset(rect.Left, rect.Top);
                plates.Add(plate with { Box = box });
            }
        }
        // Overlapping vehicle boxes can include the same plate: read each physical box only once.
        var unique = new List<Detection>();
        foreach (var plate in plates.OrderByDescending(p => p.Confidence))
            if (!unique.Any(p => Overlap(p.Box, plate.Box) > .5f)) unique.Add(plate);
        var filtered = ZoneFilterService.Apply(unique, zones, frame.Width, frame.Height);
        return general.Concat(filtered).ToArray();
    }

    public PlateReading ReadPlate(Bitmap frame, Detection plate) => _ocr!.Read(frame, plate.Box);

    private static float Overlap(RectangleF a, RectangleF b)
    {
        var intersection = RectangleF.Intersect(a, b);
        var area = Math.Max(0, intersection.Width) * Math.Max(0, intersection.Height);
        return area / Math.Max(1, a.Width * a.Height + b.Width * b.Height - area);
    }

    public void Unload()
    {
        _general?.Dispose(); _general = null;
        _plates?.Dispose(); _plates = null;
        _ocr?.Dispose(); _ocr = null;
        LoadedMode = null;
    }
    public void Dispose() { Unload(); _models.Dispose(); }
}
