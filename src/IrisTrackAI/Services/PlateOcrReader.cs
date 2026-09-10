using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using IrisTrackAI.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IrisTrackAI.Services;

public sealed class PlateOcrReader : IDisposable
{
    private InferenceSession? _session;
    private string _inputName = "input", _outputName = "plate";
    private string? _path;
    // The model includes normalization: pass RGB bytes, NHWC, never divide by 255 here.
    private readonly DenseTensor<byte> _input = new(new[] { 1, 64, 128, 3 });
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
        if (metadata.ElementType != typeof(byte) || !metadata.Dimensions.Skip(1).SequenceEqual(new[] { 64, 128, 3 }))
            throw new InvalidOperationException("El lector descargado no es CCT-XS v2 RGB 128×64.");
        _outputName = _session.OutputMetadata.Keys.FirstOrDefault(k => k == "plate")
            ?? _session.OutputMetadata.First(kv => kv.Value.Dimensions.Skip(1).Aggregate(1, (a, b) => a * b) == 370).Key;
    }

    public PlateReading Read(Bitmap source, RectangleF box)
    {
        var rect = Rectangle.Intersect(Rectangle.Round(box), new Rectangle(0, 0, source.Width, source.Height));
        if (rect.Width < 8 || rect.Height < 4) return new PlateReading("", 0, 0);
        using var canvas = new Bitmap(128, 64, PixelFormat.Format24bppRgb);
        using (var crop = source.Clone(rect, PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(crop, new Rectangle(0, 0, 128, 64), 0, 0, crop.Width, crop.Height, GraphicsUnit.Pixel, attributes);
        }
        var data = canvas.LockBits(new Rectangle(0, 0, 128, 64), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var span = _input.Buffer.Span;
                for (int y = 0; y < 64; y++)
                {
                    var row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < 128; x++)
                    {
                        var i = (y * 128 + x) * 3;
                        span[i] = row[x * 3 + 2];
                        span[i + 1] = row[x * 3 + 1];
                        span[i + 2] = row[x * 3];
                    }
                }
            }
        }
        finally { canvas.UnlockBits(data); }
        try { return Run(); }
        catch (OnnxRuntimeException) when (ProviderName == "DirectML" && _path is not null)
        {
            Load(_path, cpuOnly: true);
            return Run();
        }
    }

    private PlateReading Run()
    {
        using var output = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, _input) }, new[] { _outputName });
        InferenceCount++;
        return PlateOcrDecoder.Decode(output.First().AsTensor<float>().ToArray());
    }

    public void Dispose() { _session?.Dispose(); _session = null; ProviderName = "Sin cargar"; }
}
