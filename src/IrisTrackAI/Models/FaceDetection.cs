using System.Drawing;

namespace IrisTrackAI.Models;

/// <summary>
/// Detección facial nativa de IrisTrack. Conserva los 5 landmarks de SCRFD
/// para que ArcFace pueda alinear el rostro sin volver a detectar nada.
/// </summary>
public sealed record FaceDetection(float Confidence, RectangleF Box, PointF[] Landmarks);
