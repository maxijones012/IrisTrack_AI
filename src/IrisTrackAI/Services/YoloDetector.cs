using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

/// <summary>
/// Router de inferencia de IrisTrack.
/// En objetivos COCO ejecuta únicamente YOLO. Cuando el objetivo es Rostros
/// ejecuta únicamente SCRFD (+ ArcFace bajo demanda si hay referencias cargadas).
/// Nunca corre ambos motores sobre el mismo fotograma.
/// </summary>
public sealed class YoloDetector : IDisposable
{
    public const int FaceClassId = 90;
    private const float FaceRecognitionThreshold = 0.50f;
    private const float MinRecognitionFaceSize = 42f;

    private InferenceSession? _session;
    private string _inputName = "images";
    private int _inputW = 640, _inputH = 640;

    private ScrfdFaceDetector? _faceDetector;
    private ArcFaceRecognizer? _faceRecognizer;
    private readonly SemaphoreSlim _faceLoadGate = new(1, 1);
    private readonly object _galleryLock = new();
    private readonly object _faceTrackLock = new();
    private Dictionary<string, float[]> _faceGallery = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FaceTrack> _faceTracks = new();

    private sealed class FaceTrack
    {
        public RectangleF Box;
        public DateTime LastSeen;
        public bool RecognitionAttempted;
        public string Label = "Rostro";
    }

    public string ProviderName { get; private set; } = "Sin cargar";
    public string FaceProviderName => _faceDetector?.ProviderName ?? "Bajo demanda";
    public string RecognitionProviderName => _faceRecognizer?.ProviderName ?? "En espera";
    public string FaceEngineStatus { get; private set; } = "Motor facial listo bajo demanda";
    public string KnownFacesDirectory => FaceModelStore.KnownFacesDirectory;

    public int KnownFaceCount
    {
        get { lock (_galleryLock) return _faceGallery.Count; }
    }

    public event Action<string>? FaceEngineStateChanged;

    private static readonly string[] Coco = new[] {
        "Persona","Bicicleta","Auto","Moto","Avión","Colectivo","Tren","Camión","Barco","Semáforo",
        "Hidrante","Señal stop","Parquímetro","Banco","Pájaro","Gato","Perro","Caballo","Oveja","Vaca",
        "Elefante","Oso","Cebra","Jirafa","Mochila","Paraguas","Cartera","Corbata","Valija","Frisbee",
        "Esquís","Snowboard","Pelota","Cometa","Bate","Guante","Skateboard","Tabla surf","Raqueta","Botella",
        "Copa","Taza","Tenedor","Cuchillo","Cuchara","Bowl","Banana","Manzana","Sándwich","Naranja",
        "Brócoli","Zanahoria","Panchito","Pizza","Dona","Torta","Silla","Sofá","Maceta","Cama",
        "Mesa","Inodoro","TV","Notebook","Mouse","Control remoto","Teclado","Celular","Microondas","Horno",
        "Tostadora","Pileta","Heladera","Libro","Reloj","Jarrón","Tijera","Oso de peluche","Secador","Cepillo"
    };

    public void Load(string modelPath)
    {
        DisposeYoloSession();
        SessionOptions opts;
        try
        {
            opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableMemoryPattern = false };
            opts.AppendExecutionProvider_DML(0);
            _session = new InferenceSession(modelPath, opts);
            ProviderName = "DirectML (GPU/NPU compatible)";
        }
        catch
        {
            opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            opts.AppendExecutionProvider_CPU();
            _session = new InferenceSession(modelPath, opts);
            ProviderName = "CPU";
        }
        _inputName = _session.InputMetadata.Keys.First();
        var dims = _session.InputMetadata[_inputName].Dimensions;
        if (dims.Length >= 4)
        {
            if (dims[^1] > 0) _inputW = dims[^1];
            if (dims[^2] > 0) _inputH = dims[^2];
        }
    }

    /// <summary>
    /// Prepara SCRFD y, solamente si existen imágenes de referencia,
    /// prepara ArcFace e indexa la galería. Se llama al elegir Rostros.
    /// </summary>
    public async Task PrepareFaceEngineAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _faceLoadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureFaceDetectorCoreAsync(progress, ct).ConfigureAwait(false);
            await ReloadFaceGalleryCoreAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _faceLoadGate.Release();
        }
    }

    public async Task ReloadFaceGalleryAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _faceLoadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureFaceDetectorCoreAsync(progress, ct).ConfigureAwait(false);
            await ReloadFaceGalleryCoreAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _faceLoadGate.Release();
        }
    }

    private async Task EnsureFaceDetectorCoreAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (_faceDetector is not null) return;
        SetFaceStatus("Preparando SCRFD 500M…");
        var path = await FaceModelStore.EnsureScrfd500mAsync(progress, ct).ConfigureAwait(false);
        var detector = new ScrfdFaceDetector();
        detector.Load(path);
        _faceDetector = detector;
        SetFaceStatus($"SCRFD 500M · {detector.ProviderName} · YOLO en pausa cuando se usa Rostros");
    }

    private async Task ReloadFaceGalleryCoreAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(KnownFacesDirectory);
        var files = Directory.EnumerateFiles(KnownFacesDirectory)
            .Where(IsSupportedFaceImage)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            lock (_galleryLock) _faceGallery = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            lock (_faceTrackLock) _faceTracks.Clear();
            SetFaceStatus($"SCRFD 500M · {_faceDetector?.ProviderName} · sin rostros de referencia · ArcFace apagado");
            return;
        }

        if (_faceRecognizer is null)
        {
            SetFaceStatus("Preparando ArcFace MobileNet para las referencias…");
            var arcPath = await FaceModelStore.EnsureArcFaceMnetAsync(progress, ct).ConfigureAwait(false);
            var recognizer = new ArcFaceRecognizer();
            recognizer.Load(arcPath);
            _faceRecognizer = recognizer;
        }

        var gallery = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var image = new Bitmap(file);
                var faces = _faceDetector!.Detect(image, 0.45f);
                var face = faces
                    .OrderByDescending(f => f.Confidence * MathF.Sqrt(Math.Max(1, f.Box.Width * f.Box.Height)))
                    .FirstOrDefault();
                if (face is null)
                {
                    skipped++;
                    continue;
                }

                var embedding = _faceRecognizer!.GetNormalizedEmbedding(image, face.Landmarks);
                var name = Path.GetFileNameWithoutExtension(file).Trim();
                if (string.IsNullOrWhiteSpace(name)) name = "Referencia";
                gallery[name] = embedding;
            }
            catch
            {
                skipped++;
            }
        }

        lock (_galleryLock) _faceGallery = gallery;
        lock (_faceTrackLock) _faceTracks.Clear();

        var suffix = skipped > 0 ? $" · {skipped} omitida(s)" : string.Empty;
        SetFaceStatus($"SCRFD 500M · {_faceDetector?.ProviderName} + ArcFace · {_faceRecognizer?.ProviderName} · referencias {gallery.Count}{suffix}");
    }

    public IReadOnlyList<Detection> Detect(Bitmap source, float threshold, IReadOnlySet<int>? allowedClassIds = null)
    {
        // El selector Rostros es excluyente a propósito: si está activo no se invoca YOLO.
        if (allowedClassIds?.Contains(FaceClassId) == true)
            return DetectFaces(source, threshold);

        if (_session is null) return Array.Empty<Detection>();
        var prep = Preprocess(source);
        var input = NamedOnnxValue.CreateFromTensor(_inputName, prep.Tensor);
        using var results = _session.Run(new[] { input });
        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        var found = new List<Detection>();

        if (dims.Length == 3 && dims[^1] == 6) // YOLO26 end-to-end: [1, N, 6]
        {
            int n = dims[^2];
            for (int i = 0; i < n; i++)
            {
                float x1 = output[0, i, 0], y1 = output[0, i, 1], x2 = output[0, i, 2], y2 = output[0, i, 3];
                float score = output[0, i, 4];
                int cls = (int)MathF.Round(output[0, i, 5]);
                if (score < threshold || cls < 0 || cls >= Coco.Length) continue;
                if (allowedClassIds is not null && !allowedClassIds.Contains(cls)) continue;
                var box = Unletterbox(x1, y1, x2, y2, prep.Scale, prep.PadX, prep.PadY, source.Width, source.Height);
                if (box.Width >= 2 && box.Height >= 2) found.Add(new Detection(cls, Coco[cls], score, box));
            }
        }
        else if (dims.Length == 3 && dims[1] >= 84) // fallback tradicional [1, 84, 8400]
        {
            int attrs = dims[1], n = dims[2], classes = attrs - 4;
            for (int i = 0; i < n; i++)
            {
                float best = 0; int cls = -1;
                if (allowedClassIds is null)
                {
                    for (int c = 0; c < classes && c < Coco.Length; c++)
                    {
                        var s = output[0, 4 + c, i];
                        if (s > best) { best = s; cls = c; }
                    }
                }
                else
                {
                    // Cuando el modelo usa la salida tradicional evitamos recorrer las 80 clases:
                    // sólo puntuamos las clases que el usuario pidió ver.
                    foreach (var c in allowedClassIds)
                    {
                        if (c < 0 || c >= classes || c >= Coco.Length) continue;
                        var s = output[0, 4 + c, i];
                        if (s > best) { best = s; cls = c; }
                    }
                }
                if (best < threshold || cls < 0) continue;
                float cx = output[0, 0, i], cy = output[0, 1, i], w = output[0, 2, i], h = output[0, 3, i];
                var box = Unletterbox(cx - w/2, cy - h/2, cx + w/2, cy + h/2, prep.Scale, prep.PadX, prep.PadY, source.Width, source.Height);
                found.Add(new Detection(cls, Coco[cls], best, box));
            }
        }
        return found.OrderByDescending(x => x.Confidence).ToList();
    }

    private IReadOnlyList<Detection> DetectFaces(Bitmap source, float threshold)
    {
        try
        {
            if (_faceDetector is null)
                PrepareFaceEngineAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            SetFaceStatus("Error al preparar motor facial: " + ex.Message);
            return Array.Empty<Detection>();
        }

        var faces = _faceDetector!.Detect(source, threshold);
        if (faces.Count == 0)
        {
            lock (_faceTrackLock)
                _faceTracks.RemoveAll(t => DateTime.UtcNow - t.LastSeen > TimeSpan.FromSeconds(1.8));
            return Array.Empty<Detection>();
        }

        KeyValuePair<string, float[]>[] gallery;
        lock (_galleryLock) gallery = _faceGallery.ToArray();
        var recognizer = _faceRecognizer;
        var now = DateTime.UtcNow;
        var detections = new List<Detection>(faces.Count);

        lock (_faceTrackLock)
        {
            _faceTracks.RemoveAll(t => now - t.LastSeen > TimeSpan.FromSeconds(1.8));
            var used = new HashSet<FaceTrack>();

            foreach (var face in faces.OrderByDescending(f => f.Confidence))
            {
                FaceTrack? track = null;
                var bestIou = 0f;
                foreach (var candidate in _faceTracks)
                {
                    if (used.Contains(candidate)) continue;
                    var iou = IoU(candidate.Box, face.Box);
                    if (iou > bestIou) { bestIou = iou; track = candidate; }
                }

                if (track is null || bestIou < 0.28f)
                {
                    track = new FaceTrack { Box = face.Box, LastSeen = now };
                    _faceTracks.Add(track);
                }
                else
                {
                    track.Box = face.Box;
                    track.LastSeen = now;
                }
                used.Add(track);

                // ArcFace no se ejecuta en cada frame. Sólo una vez por track y únicamente
                // cuando el rostro ya tiene un tamaño razonable y existe una galería.
                if (!track.RecognitionAttempted && recognizer is not null && gallery.Length > 0
                    && Math.Min(face.Box.Width, face.Box.Height) >= MinRecognitionFaceSize)
                {
                    try
                    {
                        var embedding = recognizer.GetNormalizedEmbedding(source, face.Landmarks);
                        string? bestName = null;
                        var bestSimilarity = float.NegativeInfinity;
                        foreach (var item in gallery)
                        {
                            var similarity = ArcFaceRecognizer.CosineSimilarity(embedding, item.Value);
                            if (similarity > bestSimilarity)
                            {
                                bestSimilarity = similarity;
                                bestName = item.Key;
                            }
                        }

                        track.RecognitionAttempted = true;
                        track.Label = bestName is not null && bestSimilarity >= FaceRecognitionThreshold
                            ? $"Rostro · POSIBLE {bestName} · sim {bestSimilarity:0.00}"
                            : "Rostro · sin coincidencia";
                    }
                    catch
                    {
                        // Si el frame justo vino mal no condenamos el track: se podrá intentar
                        // otra vez cuando vuelva a aparecer con mejor calidad.
                    }
                }

                detections.Add(new Detection(FaceClassId, track.Label, face.Confidence, face.Box));
            }
        }

        return detections;
    }

    private (DenseTensor<float> Tensor, float Scale, float PadX, float PadY) Preprocess(Bitmap src)
    {
        float scale = Math.Min((float)_inputW / src.Width, (float)_inputH / src.Height);
        int nw = Math.Max(1, (int)MathF.Round(src.Width * scale));
        int nh = Math.Max(1, (int)MathF.Round(src.Height * scale));
        int px = (_inputW - nw) / 2, py = (_inputH - nh) / 2;
        using var canvas = new Bitmap(_inputW, _inputH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black); g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(src, new Rectangle(px, py, nw, nh));
        }
        var tensor = new DenseTensor<float>(new[] { 1, 3, _inputH, _inputW });
        var span = tensor.Buffer.Span;
        int plane = _inputW * _inputH;
        var data = canvas.LockBits(new Rectangle(0, 0, _inputW, _inputH), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < _inputH; y++)
                {
                    byte* row = basePtr + y * data.Stride;
                    for (int x = 0; x < _inputW; x++)
                    {
                        byte* p = row + x * 3;
                        int i = y * _inputW + x;
                        span[i] = p[2] / 255f;
                        span[plane + i] = p[1] / 255f;
                        span[2 * plane + i] = p[0] / 255f;
                    }
                }
            }
        }
        finally { canvas.UnlockBits(data); }
        return (tensor, scale, px, py);
    }

    private static RectangleF Unletterbox(float x1,float y1,float x2,float y2,float scale,float px,float py,int sw,int sh)
    {
        x1=(x1-px)/scale; y1=(y1-py)/scale; x2=(x2-px)/scale; y2=(y2-py)/scale;
        x1=Math.Clamp(x1,0,sw-1); y1=Math.Clamp(y1,0,sh-1); x2=Math.Clamp(x2,0,sw-1); y2=Math.Clamp(y2,0,sh-1);
        return RectangleF.FromLTRB(x1,y1,x2,y2);
    }

    private static bool IsSupportedFaceImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private void SetFaceStatus(string status)
    {
        FaceEngineStatus = status;
        try { FaceEngineStateChanged?.Invoke(status); } catch { }
    }

    private static float IoU(RectangleF a, RectangleF b)
    {
        var l = Math.Max(a.Left, b.Left);
        var t = Math.Max(a.Top, b.Top);
        var r = Math.Min(a.Right, b.Right);
        var bt = Math.Min(a.Bottom, b.Bottom);
        var inter = Math.Max(0, r - l) * Math.Max(0, bt - t);
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private void DisposeYoloSession() { _session?.Dispose(); _session = null; }

    public void Dispose()
    {
        DisposeYoloSession();
        _faceDetector?.Dispose();
        _faceDetector = null;
        _faceRecognizer?.Dispose();
        _faceRecognizer = null;
        _faceLoadGate.Dispose();
    }
}
