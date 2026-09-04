using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public sealed class ZoneProfileService
{
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IrisTrackAI", "zones");

    public string BuildKey(string? videoPath, string windowTitle)
        => !string.IsNullOrWhiteSpace(videoPath)
            ? "file:" + Path.GetFullPath(videoPath).Trim().ToLowerInvariant()
            : "window:" + (windowTitle ?? string.Empty).Trim().ToLowerInvariant();

    public IReadOnlyList<AnalysisZone> Load(string key)
    {
        try
        {
            var path = GetProfilePath(key);
            if (!File.Exists(path)) return Array.Empty<AnalysisZone>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<AnalysisZone>>(json)
                   ?.Where(z => z.IsValid).ToArray()
                   ?? Array.Empty<AnalysisZone>();
        }
        catch
        {
            return Array.Empty<AnalysisZone>();
        }
    }

    public void Save(string key, IReadOnlyList<AnalysisZone> zones)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var json = JsonSerializer.Serialize(zones.Where(z => z.IsValid), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetProfilePath(key), json);
        }
        catch { }
    }

    private string GetProfilePath(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var name = Convert.ToHexString(bytes).ToLowerInvariant();
        return Path.Combine(_folder, name + ".json");
    }
}
