using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using IrisTrackAI.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IrisTrackAI.Services;

/// <summary>YOLOv9 Tiny 384, matching open-image-models' RGB letterbox and [N,7] output.</summary>
public sealed class PlateDetector : IDisposable
{
    private InferenceSession? _session;
    private string _inputName = "images";
    private string? _path;
    private const int Size = 384;
    private readonly DenseTensor<float> _input = new(new[] { 1, 3, Size, Size });
    public string ProviderName { get; private set; } = "Sin cargar";
    public long InferenceCount { get; private set; }

    public void Load(string path, bool cpuOnly = false)
    {
        Dispose();
        _path = path;
        _session = OnnxSessionFactory.Create(path, out var provider, cpuOnly);
        ProviderName = provider;
        _inputName = _session.InputMetadata.Keys.Single();
        var metadata = _session.InputMetadata[_inputName];
        if (metadata.ElementType != typeof(float) || !metadata.Dimensions.Skip(1).SequenceEqual(new[] { 3, Size, Size }))
            throw new InvalidOperationException("El detector descargado no es YOLOv9 Tiny 384.");
    }

    public IReadOnlyList<Detection> Detect(Bitmap source, float threshold)
    {
        if (_session is null) throw new InvalidOperationException("El detector de patentes no está preparado.");
        var scale = Math.Min((float)Size / source.Width, (float)Size / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * (double)scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * (double)scale));
        var padX = (Size - width) / 2f;
        var padY = (Size - height) / 2f;
        using var canvas = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(114, 114, 114));
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle((int)Math.Round(padX - .1), (int)Math.Round(padY - .1), width, height),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        var data = canvas.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var span = _input.Buffer.Span;
                const int plane = Size * Size;
                for (int y = 0; y < Size; y++)
                {
                    var row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < Size; x++)
                    {
                        var i = y * Size + x;
                        span[i] = row[x * 3 + 2] / 255f;
                        span[plane + i] = row[x * 3 + 1] / 255f;
                        span[2 * plane + i] = row[x * 3] / 255f;
                    }
                }
            }
        }
        finally { canvas.UnlockBits(data); }

        try { return Run(source.Width, source.Height, threshold, scale, padX, padY); }
        catch (OnnxRuntimeException) when (ProviderName == "DirectML" && _path is not null)
        {
            // Algunos drivers cargan el grafo pero fallan con salidas NMS dinámicas.
            Load(_path, cpuOnly: true);
            return Run(source.Width, source.Height, threshold, scale, padX, padY);
        }
    }

    private IReadOnlyList<Detection> Run(int width, int height, float threshold, float scale, float padX, float padY)
    {
        using var result = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, _input) });
        InferenceCount++;
        var output = result.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        if (dims.Length is not (2 or 3) || dims[^1] != 7 || (dims.Length == 3 && dims[0] != 1))
            throw new InvalidOperationException("Formato de salida inesperado del detector de patentes.");
        var values = output.ToArray();
        var detections = new List<Detection>();
        for (int offset = 0; offset < values.Length; offset += 7)
        {
            var score = values[offset + 6];
            if (!float.IsFinite(score) || score < threshold || values[offset] != 0) continue;
            var box = MapBox(values[offset + 1], values[offset + 2], values[offset + 3], values[offset + 4],
                scale, padX, padY, width, height);
            if (box.Width >= 2 && box.Height >= 2)
                detections.Add(new Detection(Detection.PlateClassId, "Patente", score, box));
        }
        return detections;
    }

    public static RectangleF MapBox(float x1, float y1, float x2, float y2, float scale, float padX, float padY, int width, int height)
    {
        if (scale <= 0 || !float.IsFinite(x1 + y1 + x2 + y2)) return RectangleF.Empty;
        return RectangleF.FromLTRB(Math.Clamp((x1 - padX) / scale, 0, width), Math.Clamp((y1 - padY) / scale, 0, height),
            Math.Clamp((x2 - padX) / scale, 0, width), Math.Clamp((y2 - padY) / scale, 0, height));
    }

    public void Dispose() { _session?.Dispose(); _session = null; ProviderName = "Sin cargar"; }
}
