using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

/// <summary>
/// Intenta resolver automáticamente el archivo que está reproduciendo la ventana seleccionada.
/// VLC se resuelve primero desde su lista local de medios recientes y luego, como respaldo,
/// desde la línea de comandos del proceso. No realiza conexiones de red.
/// </summary>
public sealed class VideoSourceResolver
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".asf", ".dav", ".ts", ".m4v",
        ".mpeg", ".mpg", ".webm", ".flv", ".3gp", ".mts", ".m2ts", ".264", ".h264", ".265", ".hevc", ".vob", ".ogv"
    };

    private readonly object _sync = new();
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private uint _lastPid;
    private string _lastTitle = string.Empty;
    private string? _lastResolved;

    public string? TryResolve(WindowTarget target, bool force = false)
    {
        var liveTitle = NativeMethods.GetWindowTitleText(target.Hwnd);
        if (string.IsNullOrWhiteSpace(liveTitle)) liveTitle = target.Title;

        lock (_sync)
        {
            if (!force && target.ProcessId == _lastPid && string.Equals(liveTitle, _lastTitle, StringComparison.Ordinal)
                && DateTime.UtcNow - _lastAttemptUtc < TimeSpan.FromSeconds(3))
            {
                return File.Exists(_lastResolved) ? _lastResolved : null;
            }

            _lastPid = target.ProcessId;
            _lastTitle = liveTitle;
            _lastAttemptUtc = DateTime.UtcNow;
        }

        var candidates = new List<Candidate>();
        var processName = GetProcessName(target.ProcessId);

        if (processName.Contains("vlc", StringComparison.OrdinalIgnoreCase))
        {
            var rank = 0;
            foreach (var p in ReadVlcRecentFiles())
                candidates.Add(new Candidate(p, 55 - Math.Min(rank++, 12)));
        }

        if (candidates.Count == 0)
        {
            var cmdRank = 0;
            foreach (var p in ReadCommandLineFiles(target.ProcessId))
                candidates.Add(new Candidate(p, 48 - Math.Min(cmdRank++, 8)));
        }

        var mediaTitle = StripPlayerSuffix(liveTitle);
        var normalizedTitle = Normalize(mediaTitle);

        var resolved = candidates
            .Where(c => IsExistingVideo(c.Path))
            .Select(c => new { c.Path, Score = c.BaseScore + ScoreTitle(c.Path, normalizedTitle) })
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .Select(x => x.Path)
            .FirstOrDefault();

        lock (_sync) _lastResolved = resolved;
        return resolved;
    }

    private static string GetProcessName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static IReadOnlyList<string> ReadVlcRecentFiles()
    {
        var results = new List<string>();
        var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlc-qt-interface.ini");
        if (!File.Exists(ini)) return results;

        string[] lines;
        try { lines = File.ReadAllLines(ini); }
        catch { return results; }

        foreach (var line in lines)
        {
            if (!line.StartsWith("list=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("@Invalid", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (Match m in Regex.Matches(value, @"file:///.+?(?=,\s*file:///|$)", RegexOptions.IgnoreCase))
            {
                var raw = m.Value.Trim();
                try
                {
                    if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsFile && IsExistingVideo(uri.LocalPath))
                        results.Add(Path.GetFullPath(uri.LocalPath));
                }
                catch { }
            }
            break;
        }
        return results;
    }

    private static IReadOnlyList<string> ReadCommandLineFiles(uint pid)
    {
        var results = new List<string>();
        string commandLine;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"$p=Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}'; if($p){{$p.CommandLine}}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            if (!p.WaitForExit(1300))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return results;
            }
            commandLine = p.StandardOutput.ReadToEnd();
        }
        catch { return results; }

        if (string.IsNullOrWhiteSpace(commandLine)) return results;

        foreach (Match m in Regex.Matches(commandLine, "\"([^\"]+)\"|([^\\s]+)"))
        {
            var value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            value = value.Trim().Trim('"');
            if (IsExistingVideo(value)) results.Add(Path.GetFullPath(value));
        }
        return results;
    }

    private static bool IsExistingVideo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) && VideoExtensions.Contains(Path.GetExtension(path)); }
        catch { return false; }
    }

    private static int ScoreTitle(string path, string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle)) return 0;
        var stem = Normalize(Path.GetFileNameWithoutExtension(path));
        if (stem == normalizedTitle) return 100;
        if (stem.Contains(normalizedTitle, StringComparison.Ordinal) || normalizedTitle.Contains(stem, StringComparison.Ordinal)) return 70;
        return 0;
    }

    private static string StripPlayerSuffix(string title)
        => Regex.Replace(title, @"\s*-\s*(VLC media player|Reproductor multimedia VLC)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record Candidate(string Path, int BaseScore);
}
