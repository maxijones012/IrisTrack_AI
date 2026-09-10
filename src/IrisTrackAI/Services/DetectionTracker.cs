using System.Drawing;
using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public sealed class DetectionTracker
{
    private sealed class Track { public long Id; public int ClassId; public RectangleF Box; public DateTime LastSeen; public bool AutoCaptured; }
    private readonly List<Track> _tracks = new();
    private long _nextId = 1;

    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
    }

    public IReadOnlyList<Detection> Update(IReadOnlyList<Detection> detections, TimeSpan ttl)
    {
        var now = DateTime.UtcNow;
        _tracks.RemoveAll(t => now - t.LastSeen > ttl);
        var candidates = _tracks.ToArray();
        var matched = new HashSet<long>();
        foreach (var d in detections)
        {
            Track? best = null; float bestIou = 0;
            foreach (var t in candidates.Where(t => t.ClassId == d.ClassId && !matched.Contains(t.Id)))
            {
                var iou = IoU(t.Box, d.Box);
                if (iou > bestIou) { bestIou = iou; best = t; }
            }
            if (best is not null && bestIou >= 0.30f)
            {
                best.Box = d.Box; best.LastSeen = now; d.TrackId = best.Id;
                matched.Add(best.Id);
            }
            else
            {
                var t = new Track { Id = _nextId++, ClassId = d.ClassId, Box = d.Box, LastSeen = now };
                _tracks.Add(t); d.TrackId = t.Id;
            }
        }
        return detections;
    }

    public bool ShouldAutoCapture(Detection d)
    {
        var t = _tracks.FirstOrDefault(x => x.Id == d.TrackId);
        if (t is null || t.AutoCaptured) return false;
        t.AutoCaptured = true; return true;
    }

    public bool HasAutoCaptured(Detection d) => _tracks.FirstOrDefault(t => t.Id == d.TrackId)?.AutoCaptured == true;
    public void MarkAutoCaptured(Detection d)
    {
        var track = _tracks.FirstOrDefault(t => t.Id == d.TrackId);
        if (track is not null) track.AutoCaptured = true;
    }

    private static float IoU(RectangleF a, RectangleF b)
    {
        var l=Math.Max(a.Left,b.Left); var t=Math.Max(a.Top,b.Top); var r=Math.Min(a.Right,b.Right); var bt=Math.Min(a.Bottom,b.Bottom);
        var inter=Math.Max(0,r-l)*Math.Max(0,bt-t); var uni=a.Width*a.Height+b.Width*b.Height-inter;
        return uni <= 0 ? 0 : inter/uni;
    }
}
