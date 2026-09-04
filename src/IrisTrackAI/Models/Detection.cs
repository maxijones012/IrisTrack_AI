using System.Drawing;

namespace IrisTrackAI.Models;

public sealed record Detection(int ClassId, string ClassName, float Confidence, RectangleF Box)
{
    public long TrackId { get; set; }
}
