using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IrisTrackAI.Models;
using IrisTrackAI.Services;

namespace IrisTrackAI;

public partial class OverlayWindow : Window
{
    private nint _hwnd;
    private string? _crossingAlert;
    private DateTime _crossingAlertUntil;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_,__) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(ex));
            NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        };
    }

    public void AlignTo(nint targetHwnd)
    {
        if (_hwnd == nint.Zero || !NativeMethods.TryGetExtendedBounds(targetHwnd, out var r)) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    public void ShowCrossingAlert(string text)
    {
        _crossingAlert = text;
        _crossingAlertUntil = DateTime.UtcNow.AddSeconds(1.6);
    }

    public void Draw(IReadOnlyList<Detection> detections, int sourceW, int sourceH, AnalysisLine? analysisLine = null, bool motionSleeping = false, IReadOnlyList<AnalysisZone>? zones = null)
    {
        OverlayCanvas.Children.Clear();
        if (sourceW <= 0 || sourceH <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        double sx = ActualWidth / sourceW, sy = ActualHeight / sourceH;

        if (zones is { Count: > 0 }) DrawZones(zones);
        if (analysisLine is not null && analysisLine.IsValid)
            DrawAnalysisLine(analysisLine, motionSleeping);

        foreach (var d in detections)
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, d.Box.Width * sx),
                Height = Math.Max(2, d.Box.Height * sy),
                Stroke = System.Windows.Media.Brushes.Cyan,
                StrokeThickness = 2
            };
            Canvas.SetLeft(rect, d.Box.Left * sx);
            Canvas.SetTop(rect, d.Box.Top * sy);
            OverlayCanvas.Children.Add(rect);

            var label = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(215, 5, 18, 28)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 50, 215, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                Child = new TextBlock
                {
                    Text = $"{d.ClassName}  {d.Confidence:P0}",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11
                }
            };
            Canvas.SetLeft(label, d.Box.Left * sx);
            Canvas.SetTop(label, Math.Max(0, d.Box.Top * sy - 23));
            OverlayCanvas.Children.Add(label);
        }

        if (!string.IsNullOrWhiteSpace(_crossingAlert) && DateTime.UtcNow <= _crossingAlertUntil)
        {
            var alert = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(232, 5, 24, 34)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 215, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 7, 12, 7),
                Child = new TextBlock
                {
                    Text = _crossingAlert,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13
                }
            };
            Canvas.SetLeft(alert, 18);
            Canvas.SetTop(alert, 18);
            OverlayCanvas.Children.Add(alert);
        }
    }

    private void DrawZones(IReadOnlyList<AnalysisZone> zones)
    {
        foreach (var zone in zones.Where(z => z.IsValid))
        {
            var ignored = zone.Type == AnalysisZoneType.Ignore;
            var strokeColor = ignored
                ? System.Windows.Media.Color.FromArgb(165, 255, 84, 108)
                : System.Windows.Media.Color.FromArgb(175, 52, 235, 181);
            var fillColor = ignored
                ? System.Windows.Media.Color.FromArgb(12, 255, 84, 108)
                : System.Windows.Media.Color.FromArgb(10, 52, 235, 181);

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, zone.Width * ActualWidth),
                Height = Math.Max(2, zone.Height * ActualHeight),
                Stroke = new SolidColorBrush(strokeColor),
                Fill = new SolidColorBrush(fillColor),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 6, 5 }
            };
            Canvas.SetLeft(rect, zone.X * ActualWidth);
            Canvas.SetTop(rect, zone.Y * ActualHeight);
            OverlayCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = ignored ? "IGNORAR" : "INTERÉS",
                Foreground = new SolidColorBrush(strokeColor),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Opacity = 0.88
            };
            Canvas.SetLeft(label, zone.X * ActualWidth + 4);
            Canvas.SetTop(label, zone.Y * ActualHeight + 3);
            OverlayCanvas.Children.Add(label);
        }
    }

    private void DrawAnalysisLine(AnalysisLine line, bool sleeping)
    {
        var x1 = line.X1 * ActualWidth;
        var y1 = line.Y1 * ActualHeight;
        var x2 = line.X2 * ActualWidth;
        var y2 = line.Y2 * ActualHeight;
        var stroke = sleeping
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(185, 93, 121, 142))
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 50, 215, 255));

        var visual = new System.Windows.Shapes.Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = stroke,
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { 7, 4 }
        };
        OverlayCanvas.Children.Add(visual);

        // A/B representan lados de la línea (no sus extremos).
        var mx = (x1 + x2) / 2.0;
        var my = (y1 + y2) / 2.0;
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
        var nx = -dy / len;
        var ny = dx / len;
        const double sideOffset = 30;
        AddSideLabel("A", mx - nx * sideOffset, my - ny * sideOffset);
        AddSideLabel("B", mx + nx * sideOffset, my + ny * sideOffset);

        if (sleeping)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(205, 7, 18, 28)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 3, 6, 3),
                Child = new TextBlock { Text = "YOLO EN ESPERA · movimiento", Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 10 }
            };
            Canvas.SetLeft(badge, Math.Max(8, (x1 + x2) / 2 - 70));
            Canvas.SetTop(badge, Math.Max(8, (y1 + y2) / 2 + 8));
            OverlayCanvas.Children.Add(badge);
        }
    }

    private void AddSideLabel(string text, double x, double y)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 5, 25, 36)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = text, Foreground = System.Windows.Media.Brushes.Cyan, FontWeight = FontWeights.Bold, FontSize = 10 }
        };
        Canvas.SetLeft(badge, Math.Max(0, x - 10));
        Canvas.SetTop(badge, Math.Max(0, y - 24));
        OverlayCanvas.Children.Add(badge);
    }
}
