using System.IO;
using System.Net.Http;

namespace IrisTrackAI.Services;

public sealed class ModelManager
{
    private const string ModelUrl = "https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.onnx";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    public string ModelDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisTrackAI", "Models");
    public string ModelPath => Path.Combine(ModelDirectory, "yolo26n.onnx");

    public async Task<string> EnsureModelAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelDirectory);
        if (File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 1_000_000) return ModelPath;
        var tmp = ModelPath + ".download";
        using var resp = await _http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(tmp);
        var buffer = new byte[1024 * 128];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total is > 0) progress?.Report((double)readTotal / total.Value);
        }
        output.Close();
        File.Move(tmp, ModelPath, true);
        return ModelPath;
    }
}
