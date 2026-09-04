namespace IrisTrackAI.Models;

public sealed record CaptureRecord(
    DateTime CapturedAt,
    string WindowTitle,
    string ClassName,
    float Confidence,
    long TrackId,
    string? CropPath,
    string? FramePath,
    string? LinkedVideoPath,
    string EventType = "Deteccion");
