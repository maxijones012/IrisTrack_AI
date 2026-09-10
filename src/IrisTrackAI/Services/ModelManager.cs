using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace IrisTrackAI.Services;

public sealed class ModelManager : IDisposable
{
    private const string ModelUrl = "https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.onnx";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    public string ModelDirectory { get; }
    public ModelManager(string? directory = null) => ModelDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisTrackAI", "Models");
    public string ModelPath => Path.Combine(ModelDirectory, "yolo26n.onnx");

    public async Task<string> EnsureModelAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        return await EnsureFileAsync(ModelUrl, ModelPath, progress, ct);
    }

    public async Task<(string Detector, string Ocr)> EnsurePlateModelsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var folder = Path.Combine(ModelDirectory, "Patentes");
        var detector = Path.Combine(folder, "yolo-v9-t-384-license-plates-end2end.onnx");
        var ocr = Path.Combine(folder, "cct_xs_v2_global.onnx");
        progress?.Report("Preparando detector de patentes…");
        await EnsureFileAsync("https://github.com/ankandrew/open-image-models/releases/download/assets/yolo-v9-t-384-license-plates-end2end.onnx",
            detector, new InlineProgress<double>(p => progress?.Report($"Descargando detector de patentes… {p:P0}")), ct, expectedSize: 7771218);
        progress?.Report("Preparando lector de patentes…");
        await EnsureFileAsync("https://github.com/ankandrew/fast-plate-ocr/releases/download/arg-plates/cct_xs_v2_global.onnx",
            ocr, new InlineProgress<double>(p => progress?.Report($"Descargando lector de patentes… {p:P0}")), ct,
            expectedSize: 3344292, sha256: "8031afb5fdc6b4d80462c9d542f1284ebd2cfddf5dbacd62609848d7e2855f44");
        return (detector, ocr);
    }

    private async Task<string> EnsureFileAsync(string url, string path, IProgress<double>? progress, CancellationToken ct,
        long? expectedSize = null, string? sha256 = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (await IsValidAsync(path, expectedSize, sha256, ct)) return path;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[1024 * 128];
                long readTotal = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;
                    if (total is > 0) progress?.Report((double)readTotal / total.Value);
                }
            }
            if (!await IsValidAsync(tmp, expectedSize, sha256, ct))
                throw new IOException("La descarga del modelo está incompleta o no coincide con la versión esperada. Volvé a intentarlo.");
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, path, true);
            return path;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static async Task<bool> IsValidAsync(string path, long? size, string? sha256, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        if (size.HasValue ? length != size.Value : length < 1_000_000) return false;
        if (sha256 is null) return true;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return actual.Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
