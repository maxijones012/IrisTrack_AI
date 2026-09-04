namespace IrisTrackAI.Models;

public sealed record AnalysisLine(double X1, double Y1, double X2, double Y2)
{
    public bool IsValid => Math.Abs(X2 - X1) + Math.Abs(Y2 - Y1) > 0.02;
}
