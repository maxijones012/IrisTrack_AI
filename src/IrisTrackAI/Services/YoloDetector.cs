using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public sealed class YoloDetector : IDisposable
{
    private InferenceSession? _session;
    private string _inputName = "images";
    private int _inputW = 640, _inputH = 640;
    public string ProviderName { get; private set; } = "Sin cargar";

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
        DisposeSession();
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

    public IReadOnlyList<Detection> Detect(Bitmap source, float threshold, IReadOnlySet<int>? allowedClassIds = null)
    {
        if (_session is null) return Array.Empty<Detection>();
        var prep = Preprocess(source);
        var input = NamedOnnxValue.CreateFromTensor(_inputName, prep.Tensor);
        using var results = _session.Run(new[] { input });
        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        var found = new List<Detection>();

        if (dims.Length == 3 && dims[^1] == 6)
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
        else if (dims.Length == 3 && dims[1] >= 84)
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

    private void DisposeSession() { _session?.Dispose(); _session = null; }
    public void Dispose() => DisposeSession();
}
