using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

public enum CrossingDirection
{
    Any = 0,
    AtoB = 1,
    BtoA = 2
}

public sealed class LineCrossingService
{
    private sealed class State
    {
        public int StableSide;
        public DateTime LastSeen;
        public DateTime LastCrossing;
    }

    private readonly Dictionary<long, State> _states = new();

    public void Reset() => _states.Clear();

    public bool TryRegisterCrossing(
        Detection detection,
        AnalysisLine line,
        int sourceW,
        int sourceH,
        CrossingDirection direction,
        out string directionText)
    {
        directionText = string.Empty;
        if (sourceW <= 0 || sourceH <= 0 || !line.IsValid || detection.TrackId <= 0) return false;

        var now = DateTime.UtcNow;
        foreach (var stale in _states.Where(x => now - x.Value.LastSeen > TimeSpan.FromSeconds(4)).Select(x => x.Key).ToArray())
            _states.Remove(stale);

        var cx = (detection.Box.Left + detection.Box.Width / 2f) / sourceW;
        var cy = (detection.Box.Top + detection.Box.Height / 2f) / sourceH;
        var signed = SignedSide(line, cx, cy);

        // Zona muerta mínima para evitar falsos cruces por vibración de la caja YOLO.
        const double epsilon = 0.004;
        var currentSide = signed > epsilon ? 1 : signed < -epsilon ? -1 : 0;

        if (!_states.TryGetValue(detection.TrackId, out var state))
        {
            _states[detection.TrackId] = new State { StableSide = currentSide, LastSeen = now };
            return false;
        }

        state.LastSeen = now;
        if (currentSide == 0) return false;
        if (state.StableSide == 0)
        {
            state.StableSide = currentSide;
            return false;
        }
        if (currentSide == state.StableSide) return false;

        var from = state.StableSide;
        var to = currentSide;
        state.StableSide = currentSide;

        // Un mismo track no puede disparar varias veces por oscilación cerca de la línea.
        if (now - state.LastCrossing < TimeSpan.FromSeconds(1.25)) return false;

        var actualDirection = from < to ? CrossingDirection.AtoB : CrossingDirection.BtoA;
        if (direction != CrossingDirection.Any && direction != actualDirection) return false;

        // El centro debe estar razonablemente cerca del segmento real y no de su prolongación infinita.
        if (!IsNearLine(detection, line, sourceW, sourceH, 0.16)) return false;

        state.LastCrossing = now;
        directionText = actualDirection == CrossingDirection.AtoB ? "A → B" : "B → A";
        return true;
    }

    public static bool IsNearLine(Detection detection, AnalysisLine line, int sourceW, int sourceH, double band)
    {
        if (sourceW <= 0 || sourceH <= 0 || !line.IsValid) return false;
        var px = (detection.Box.Left + detection.Box.Width / 2f) / sourceW;
        var py = (detection.Box.Top + detection.Box.Height / 2f) / sourceH;
        return DistanceToSegment(px, py, line.X1, line.Y1, line.X2, line.Y2) <= band;
    }

    public static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
    {
        var vx = x2 - x1;
        var vy = y2 - y1;
        var wx = px - x1;
        var wy = py - y1;
        var vv = vx * vx + vy * vy;
        if (vv <= 1e-9) return Math.Sqrt(wx * wx + wy * wy);
        var t = Math.Clamp((wx * vx + wy * vy) / vv, 0.0, 1.0);
        var dx = px - (x1 + t * vx);
        var dy = py - (y1 + t * vy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SignedSide(AnalysisLine line, double px, double py)
        => (line.X2 - line.X1) * (py - line.Y1) - (line.Y2 - line.Y1) * (px - line.X1);
}
