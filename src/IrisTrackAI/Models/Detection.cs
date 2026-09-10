using System.Drawing;

namespace IrisTrackAI.Models;

public sealed record Detection(int ClassId, string ClassName, float Confidence, RectangleF Box)
{
    public long TrackId { get; set; }
    public const int PlateClassId = 1000;
    public bool IsPlate => ClassId == PlateClassId;
    public string? PlateText { get; set; }
    public float? OcrConfidence { get; set; }
    public bool PlateStable { get; set; }
    public bool OcrFresh { get; set; }
    public string DisplayLabel => IsPlate
        ? string.IsNullOrWhiteSpace(PlateText) ? "Patente · leyendo…"
            : $"{PlateText} · {OcrConfidence:P0}{(PlateStable ? "" : " · por confirmar")}"
        : $"{ClassName}  {Confidence:P0}";
}
