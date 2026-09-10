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
    string EventType = "Deteccion",
    string? PlateText = null,
    float? OcrConfidence = null,
    bool? PlateStable = null,
    double? AnalysisElapsedSeconds = null,
    double? VideoPositionSeconds = null);
