using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using IrisTrackAI.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IrisTrackAI.Services;

/// <summary>
/// Port liviano del pipeline SCRFD 500M usado por UniFace/InsightFace.
/// Está implementado directamente sobre ONNX Runtime para evitar un proceso Python
/// y, sobre todo, para que cuando se use Rostros YOLO no ejecute inferencia.
/// </summary>
public sealed class ScrfdFaceDetector : IDisposable
{
    private const int InputSize = 640;
    private static readonly int[] Strides = [8, 16, 32];

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

    public IReadOnlyList<FaceDetection> Detect(Bitmap source, float threshold = 0.45f, float nmsThreshold = 0.40f)
    {
        if (_session is null) return Array.Empty<FaceDetection>();

        var prep = Preprocess(source);
        var input = NamedOnnxValue.CreateFromTensor(_inputName, prep.Tensor);
        using var results = _session.Run([input]);
        var outputs = results.Select(r => r.AsTensor<float>().ToArray()).ToArray();
        if (outputs.Length < 9) return Array.Empty<FaceDetection>();

        var candidates = new List<FaceDetection>(64);

        for (var level = 0; level < Strides.Length; level++)
        {
            var stride = Strides[level];
            var scores = outputs[level];
            var boxes = outputs[3 + level];
            var keypoints = outputs[6 + level];

            var fmWidth = InputSize / stride;
            var fmHeight = InputSize / stride;
            var anchors = fmWidth * fmHeight * 2;

            if (scores.Length < anchors || boxes.Length < anchors * 4 || keypoints.Length < anchors * 10)
                continue;

            for (var anchor = 0; anchor < anchors; anchor++)
            {
                var score = scores[anchor];
                if (score < threshold) continue;

                var cell = anchor / 2;
                var cx = (cell % fmWidth) * stride;
                var cy = (cell / fmWidth) * stride;

                var b = anchor * 4;
                var left = boxes[b] * stride;
                var top = boxes[b + 1] * stride;
                var right = boxes[b + 2] * stride;
                var bottom = boxes[b + 3] * stride;

                var x1 = (cx - left) / prep.Scale;
                var y1 = (cy - top) / prep.Scale;
                var x2 = (cx + right) / prep.Scale;
                var y2 = (cy + bottom) / prep.Scale;

                x1 = Math.Clamp(x1, 0, Math.Max(0, source.Width - 1));
                y1 = Math.Clamp(y1, 0, Math.Max(0, source.Height - 1));
                x2 = Math.Clamp(x2, 0, Math.Max(0, source.Width - 1));
                y2 = Math.Clamp(y2, 0, Math.Max(0, source.Height - 1));

                var box = RectangleF.FromLTRB(x1, y1, x2, y2);
                if (box.Width < 2 || box.Height < 2) continue;

                var landmarks = new PointF[5];
                var k = anchor * 10;
                for (var p = 0; p < 5; p++)
                {
                    var lx = (cx + keypoints[k + p * 2] * stride) / prep.Scale;
                    var ly = (cy + keypoints[k + p * 2 + 1] * stride) / prep.Scale;
                    landmarks[p] = new PointF(
                        Math.Clamp(lx, 0, Math.Max(0, source.Width - 1)),
                        Math.Clamp(ly, 0, Math.Max(0, source.Height - 1)));
                }

                candidates.Add(new FaceDetection(score, box, landmarks));
            }
        }

        return NonMaxSuppression(candidates, nmsThreshold);
    }

    private static (DenseTensor<float> Tensor, float Scale) Preprocess(Bitmap source)
    {
        var imageRatio = (float)source.Height / source.Width;
        const float modelRatio = 1f;
        int newWidth, newHeight;
        if (imageRatio > modelRatio)
        {
            newHeight = InputSize;
            newWidth = Math.Max(1, (int)(newHeight / imageRatio));
        }
        else
        {
            newWidth = InputSize;
            newHeight = Math.Max(1, (int)(newWidth * imageRatio));
        }

        var scale = (float)newHeight / source.Height;
        using var canvas = new Bitmap(InputSize, InputSize, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, newWidth, newHeight));
        }

        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        var span = tensor.Buffer.Span;
        var plane = InputSize * InputSize;
        var data = canvas.LockBits(
            new Rectangle(0, 0, InputSize, InputSize),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                var basePtr = (byte*)data.Scan0;
                for (var y = 0; y < InputSize; y++)
                {
                    var row = basePtr + y * data.Stride;
                    for (var x = 0; x < InputSize; x++)
                    {
                        var pixel = row + x * 3; // GDI+: BGR
                        var i = y * InputSize + x;
                        span[i] = (pixel[0] - 127.5f) / 127.5f;                 // B
                        span[plane + i] = (pixel[1] - 127.5f) / 127.5f;         // G
                        span[2 * plane + i] = (pixel[2] - 127.5f) / 127.5f;     // R
                    }
                }
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }

        return (tensor, scale);
    }

    private static IReadOnlyList<FaceDetection> NonMaxSuppression(List<FaceDetection> faces, float threshold)
    {
        if (faces.Count <= 1) return faces;
        var ordered = faces.OrderByDescending(f => f.Confidence).ToList();
        var kept = new List<FaceDetection>(ordered.Count);

        while (ordered.Count > 0)
        {
            var best = ordered[0];
            kept.Add(best);
            ordered.RemoveAt(0);
            ordered.RemoveAll(other => IoU(best.Box, other.Box) > threshold);
        }

        return kept;
    }

    private static float IoU(RectangleF a, RectangleF b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        var w = Math.Max(0, right - left + 1);
        var h = Math.Max(0, bottom - top + 1);
        var intersection = w * h;
        var areaA = Math.Max(0, a.Width + 1) * Math.Max(0, a.Height + 1);
        var areaB = Math.Max(0, b.Width + 1) * Math.Max(0, b.Height + 1);
        var union = areaA + areaB - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => DisposeSession();
}
