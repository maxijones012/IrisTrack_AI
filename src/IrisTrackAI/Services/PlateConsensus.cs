using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

/// <summary>Requires three consecutive, spaced, high-confidence reads of exactly the same text.</summary>
public sealed class PlateConsensus
{
    private sealed class State
    {
        public TimeSpan LastRead = TimeSpan.FromDays(-1), LastSeen;
        public string Text = "";
        public float Confidence;
        public int Matches;
    }
    private readonly Dictionary<long, State> _states = new();
    public void Reset() => _states.Clear();

    public bool ShouldRead(Detection detection, TimeSpan now)
    {
        foreach (var key in _states.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromSeconds(3)).Select(kv => kv.Key).ToArray())
            _states.Remove(key);
        if (!_states.TryGetValue(detection.TrackId, out var state))
            _states[detection.TrackId] = state = new State();
        state.LastSeen = now;
        return now - state.LastRead >= TimeSpan.FromMilliseconds(state.Matches >= 3 ? 1000 : 200);
    }

    public void Apply(Detection detection, PlateReading? reading, TimeSpan now, float threshold = .80f)
    {
        if (!_states.TryGetValue(detection.TrackId, out var state))
            _states[detection.TrackId] = state = new State();
        state.LastSeen = now;
        if (reading is not null)
        {
            state.LastRead = now;
            var valid = reading.Text.Length is >= 4 and <= 10
                && reading.Text.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z')
                && reading.Confidence >= threshold && reading.MinimumCharacterConfidence >= .50f;
            state.Matches = valid ? state.Text == reading.Text ? Math.Min(3, state.Matches + 1) : 1 : 0;
            state.Text = reading.Text;
            state.Confidence = reading.Confidence;
        }
        detection.PlateText = state.Text;
        detection.OcrConfidence = state.Confidence;
        detection.PlateStable = state.Matches >= 3;
        detection.OcrFresh = reading is not null;
    }
}
