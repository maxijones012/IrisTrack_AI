using System.Net.Http;
using System.Security.Cryptography;

namespace IrisTrackAI.Services;

/// <summary>
/// Descarga bajo demanda los modelos faciales ONNX usados por el modo Rostros.
/// Los pesos no se incluyen dentro del repositorio: se guardan en LocalAppData.
/// URLs y hashes tomados del registro público de modelos de UniFace.
/// </summary>
public static class FaceModelStore
{
    private const string ScrfdUrl = "https://github.com/yakhyo/uniface/releases/download/weights/scrfd_500m_kps.onnx";
    private const string ScrfdSha256 = "5e4447f50245bbd7966bd6c0fa52938c61474a04ec7def48753668a9d8b4ea3a";

    private const string ArcFaceUrl = "https://github.com/yakhyo/uniface/releases/download/weights/w600k_mbf.onnx";
    private const string ArcFaceSha256 = "9cc6e4a75f0e2bf0b1aed94578f144d15175f357bdc05e815e5c4a02b319eb4f";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string ModelDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IrisTrackAI",
        "Models");

    public static string KnownFacesDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IrisTrackAI",
        "RostrosConocidos");

    public static Task<string> EnsureScrfd500mAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => EnsureModelAsync("scrfd_500m_kps.onnx", ScrfdUrl, ScrfdSha256, progress, ct);

    public static Task<string> EnsureArcFaceMnetAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => EnsureModelAsync("w600k_mbf.onnx", ArcFaceUrl, ArcFaceSha256, progress, ct);

    private static async Task<string> EnsureModelAsync(
        string fileName,
        string url,
        string expectedSha256,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDirectory);
        var path = Path.Combine(ModelDirectory, fileName);

        if (File.Exists(path) && await HasExpectedHashAsync(path, expectedSha256, ct).ConfigureAwait(false))
            return path;

        try { if (File.Exists(path)) File.Delete(path); } catch { }

        var tmp = path + ".download";
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var output = File.Create(tmp))
        {
            var buffer = new byte[128 * 1024];
            long downloaded = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                if (total is > 0) progress?.Report((double)downloaded / total.Value);
            }
        }

        if (!await HasExpectedHashAsync(tmp, expectedSha256, ct).ConfigureAwait(false))
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidDataException($"El modelo facial descargado ({fileName}) no pasó la verificación SHA-256.");
        }

        File.Move(tmp, path, true);
        progress?.Report(1.0);
        return path;
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expectedSha256, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
