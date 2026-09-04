using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public static class ZoneFilterService
{
    public static IReadOnlyList<Detection> Apply(
        IReadOnlyList<Detection> detections,
        IReadOnlyList<AnalysisZone> zones,
        int sourceW,
        int sourceH)
    {
        if (detections.Count == 0 || zones.Count == 0 || sourceW <= 0 || sourceH <= 0)
            return detections;

        var ignore = zones.Where(z => z.Type == AnalysisZoneType.Ignore && z.IsValid).ToArray();
        var interest = zones.Where(z => z.Type == AnalysisZoneType.Interest && z.IsValid).ToArray();
        if (ignore.Length == 0 && interest.Length == 0) return detections;

        var filtered = new List<Detection>(detections.Count);
        foreach (var d in detections)
        {
            var cx = (d.Box.Left + d.Box.Width / 2.0) / sourceW;
            var cy = (d.Box.Top + d.Box.Height / 2.0) / sourceH;

            // Si existe una zona de interés, el centro del objeto debe caer dentro de ella.
            if (interest.Length > 0 && !interest.Any(z => z.Contains(cx, cy)))
                continue;

            // Una zona ignorada siempre tiene prioridad sobre la zona de interés.
            if (ignore.Any(z => z.Contains(cx, cy)))
                continue;

            filtered.Add(d);
        }
        return filtered;
    }
}
