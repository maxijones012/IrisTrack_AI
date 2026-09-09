using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IrisTrackAI.Services;

/// <summary>
/// ArcFace MobileFaceNet (112x112 / embedding 512-D) ejecutado directamente en .NET.
/// Sólo se invoca para un rostro nuevo y cuando existe una galería de referencias.
/// </summary>
public sealed class ArcFaceRecognizer : IDisposable
{
    private const int Size = 112;
    private static readonly PointF[] Reference =
    [
        new(38.2946f, 51.6963f),
        new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f),
        new(70.7299f, 92.2041f)
    ];

    private InferenceSession? _session;
    private string _inputName = "input.1";

    public string ProviderName { get; private set; } = "Sin cargar";

    public void Load(string modelPath)
    {
        DisposeSession();
        SessionOptions options;
        try
        {
            options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = false
            };
            options.AppendExecutionProvider_DML(0);
            _session = new InferenceSession(modelPath, options);
            ProviderName = "DirectML";
        }
        catch
        {
            options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            options.AppendExecutionProvider_CPU();
            _session = new InferenceSession(modelPath, options);
            ProviderName = "CPU";
        }

        _inputName = _session.InputMetadata.Keys.First();
    }

    public float[] GetNormalizedEmbedding(Bitmap source, PointF[] landmarks)
    {
        if (_session is null) throw new InvalidOperationException("ArcFace todavía no está cargado.");
        if (landmarks is null || landmarks.Length != 5) throw new ArgumentException("ArcFace necesita exactamente 5 landmarks.", nameof(landmarks));

        using var aligned = AlignFace(source, landmarks);
        var tensor = Preprocess(aligned);
        var input = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
        using var results = _session.Run([input]);
        var embedding = results.First().AsTensor<float>().ToArray();

        double normSquared = 0;
        for (var i = 0; i < embedding.Length; i++) normSquared += embedding[i] * embedding[i];
        var norm = Math.Sqrt(normSquared);
        if (norm > 1e-12)
        {
            var inv = (float)(1.0 / norm);
            for (var i = 0; i < embedding.Length; i++) embedding[i] *= inv;
        }
        return embedding;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        var count = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (var i = 0; i < count; i++) dot += a[i] * b[i];
        return (float)dot;
    }

    private static Bitmap AlignFace(Bitmap source, PointF[] landmarks)
    {
        EstimateSimilarity(landmarks, Reference, out var a, out var b, out var tx, out var ty);
        var determinant = a * a + b * b;
        if (determinant < 1e-10)
            throw new InvalidOperationException("No se pudo calcular la alineación facial.");

        using var src24 = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(src24))
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));

        var output = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
        var srcData = src24.LockBits(new Rectangle(0, 0, src24.Width, src24.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var dstData = output.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                var srcBase = (byte*)srcData.Scan0;
                var dstBase = (byte*)dstData.Scan0;

                for (var v = 0; v < Size; v++)
                {
                    var dstRow = dstBase + v * dstData.Stride;
                    for (var u = 0; u < Size; u++)
                    {
                        var du = u - tx;
                        var dv = v - ty;
                        var x = (a * du + b * dv) / determinant;
                        var y = (-b * du + a * dv) / determinant;
                        var dst = dstRow + u * 3;

                        if (x < 0 || y < 0 || x > src24.Width - 1 || y > src24.Height - 1)
                        {
                            dst[0] = dst[1] = dst[2] = 0;
                            continue;
                        }

                        var x0 = Math.Clamp((int)Math.Floor(x), 0, src24.Width - 1);
                        var y0 = Math.Clamp((int)Math.Floor(y), 0, src24.Height - 1);
                        var x1 = Math.Min(x0 + 1, src24.Width - 1);
                        var y1 = Math.Min(y0 + 1, src24.Height - 1);
                        var fx = x - x0;
                        var fy = y - y0;

                        var p00 = srcBase + y0 * srcData.Stride + x0 * 3;
                        var p10 = srcBase + y0 * srcData.Stride + x1 * 3;
                        var p01 = srcBase + y1 * srcData.Stride + x0 * 3;
                        var p11 = srcBase + y1 * srcData.Stride + x1 * 3;

                        for (var c = 0; c < 3; c++)
                        {
                            var top = p00[c] + (p10[c] - p00[c]) * fx;
                            var bottom = p01[c] + (p11[c] - p01[c]) * fx;
                            var value = top + (bottom - top) * fy;
                            dst[c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                        }
                    }
                }
            }
        }
        finally
        {
            src24.UnlockBits(srcData);
            output.UnlockBits(dstData);
        }

        return output;
    }

    private static DenseTensor<float> Preprocess(Bitmap face)
    {
        var tensor = new DenseTensor<float>([1, 3, Size, Size]);
        var span = tensor.Buffer.Span;
        var plane = Size * Size;
        var data = face.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                var basePtr = (byte*)data.Scan0;
                for (var y = 0; y < Size; y++)
                {
                    var row = basePtr + y * data.Stride;
                    for (var x = 0; x < Size; x++)
                    {
                        var pixel = row + x * 3; // BGR
                        var i = y * Size + x;
                        span[i] = (pixel[2] - 127.5f) / 127.5f;             // R
                        span[plane + i] = (pixel[1] - 127.5f) / 127.5f;     // G
                        span[2 * plane + i] = (pixel[0] - 127.5f) / 127.5f; // B
                    }
                }
            }
        }
        finally
        {
            face.UnlockBits(data);
        }

        return tensor;
    }

    private static void EstimateSimilarity(PointF[] source, PointF[] target, out double a, out double b, out double tx, out double ty)
    {
        double sx = 0, sy = 0, txMean = 0, tyMean = 0;
        for (var i = 0; i < 5; i++)
        {
            sx += source[i].X;
            sy += source[i].Y;
            txMean += target[i].X;
            tyMean += target[i].Y;
        }
        sx /= 5.0;
        sy /= 5.0;
        txMean /= 5.0;
        tyMean /= 5.0;

        double numeratorA = 0, numeratorB = 0, denominator = 0;
        for (var i = 0; i < 5; i++)
        {
            var px = source[i].X - sx;
            var py = source[i].Y - sy;
            var qx = target[i].X - txMean;
            var qy = target[i].Y - tyMean;
            numeratorA += px * qx + py * qy;
            numeratorB += px * qy - py * qx;
            denominator += px * px + py * py;
        }

        if (denominator < 1e-10) throw new InvalidOperationException("Landmarks degenerados.");
        a = numeratorA / denominator;
        b = numeratorB / denominator;
        tx = txMean - a * sx + b * sy;
        ty = tyMean - b * sx - a * sy;
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => DisposeSession();
}
