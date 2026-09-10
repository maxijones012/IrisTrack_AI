namespace IrisTrackAI.Models;

public sealed record PlateReading(string Text, float Confidence, float MinimumCharacterConfidence);
