namespace IrisTrackAI.Models;

public enum AnalysisZoneType
{
    Ignore = 0,
    Interest = 1
}

/// <summary>
/// Rectángulo normalizado (0..1) relativo a la ventana capturada.
/// </summary>
public sealed record AnalysisZone(
    AnalysisZoneType Type,
    double X,
    double Y,
    double Width,
    double Height)
{
    public bool IsValid => Width >= 0.01 && Height >= 0.01;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double x, double y)
        => x >= X && x <= Right && y >= Y && y <= Bottom;
}
