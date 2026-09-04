using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfCanvas = System.Windows.Controls.Canvas;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using IrisTrackAI.Models;
using IrisTrackAI.Services;

namespace IrisTrackAI;

public partial class ZoneEditorWindow : Window
{
    private readonly nint _targetHwnd;
    private readonly AnalysisZoneType _zoneType;
    private readonly IReadOnlyList<AnalysisZone> _existing;
    private System.Windows.Point? _start;
    private readonly WpfRectangle _preview = new() { StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 7, 4 } };

    public AnalysisZone? Result { get; private set; }

    public ZoneEditorWindow(nint targetHwnd, AnalysisZoneType zoneType, IReadOnlyList<AnalysisZone> existing)
    {
        InitializeComponent();
        _targetHwnd = targetHwnd;
        _zoneType = zoneType;
        _existing = existing;
        _preview.Stroke = zoneType == AnalysisZoneType.Ignore
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 84, 108))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 235, 181));

        Loaded += (_,__) =>
        {
            AlignToTarget();
            EditorTitle.Text = zoneType == AnalysisZoneType.Ignore ? "ZONA IGNORADA" : "ZONA DE INTERÉS";
            EditorHelp.Text = zoneType == AnalysisZoneType.Ignore
                ? "Marcá el sector que IrisTrack debe ignorar · ESC cancela"
                : "Marcá el sector donde sí querés detectar · ESC cancela";
            DrawExisting();
            Focus();
        };
        SourceInitialized += (_,__) => AlignToTarget();
    }

    private void AlignToTarget()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero || !NativeMethods.TryGetExtendedBounds(_targetHwnd, out var r)) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, NativeMethods.SWP_SHOWWINDOW);
    }

    private void DrawExisting()
    {
        foreach (var zone in _existing.Where(z => z.IsValid))
        {
            var rect = new WpfRectangle
            {
                Width = zone.Width * ActualWidth,
                Height = zone.Height * ActualHeight,
                Stroke = zone.Type == AnalysisZoneType.Ignore
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 84, 108))
                    : new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 52, 235, 181)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 6, 4 },
                Fill = new SolidColorBrush(zone.Type == AnalysisZoneType.Ignore
                    ? System.Windows.Media.Color.FromArgb(18, 255, 84, 108)
                    : System.Windows.Media.Color.FromArgb(14, 52, 235, 181)),
                IsHitTestVisible = false
            };
            WpfCanvas.SetLeft(rect, zone.X * ActualWidth);
            WpfCanvas.SetTop(rect, zone.Y * ActualHeight);
            EditorCanvas.Children.Add(rect);
        }
    }

    private void Canvas_MouseLeftButtonDown(object sender, WpfMouseButtonEventArgs e)
    {
        _start = e.GetPosition(EditorCanvas);
        EditorCanvas.CaptureMouse();
        DrawPreview(_start.Value, _start.Value);
    }

    private void Canvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_start is null || e.LeftButton != MouseButtonState.Pressed) return;
        DrawPreview(_start.Value, e.GetPosition(EditorCanvas));
    }

    private void Canvas_MouseLeftButtonUp(object sender, WpfMouseButtonEventArgs e)
    {
        if (_start is null) return;
        var end = e.GetPosition(EditorCanvas);
        EditorCanvas.ReleaseMouseCapture();

        var left = Math.Min(_start.Value.X, end.X);
        var top = Math.Min(_start.Value.Y, end.Y);
        var width = Math.Abs(end.X - _start.Value.X);
        var height = Math.Abs(end.Y - _start.Value.Y);
        if (width < 28 || height < 28)
        {
            _start = null;
            return;
        }

        var w = Math.Max(1, EditorCanvas.ActualWidth);
        var h = Math.Max(1, EditorCanvas.ActualHeight);
        Result = new AnalysisZone(
            _zoneType,
            Math.Clamp(left / w, 0, 1),
            Math.Clamp(top / h, 0, 1),
            Math.Clamp(width / w, 0, 1),
            Math.Clamp(height / h, 0, 1));
        DialogResult = true;
        Close();
    }

    private void DrawPreview(System.Windows.Point a, System.Windows.Point b)
    {
        if (!EditorCanvas.Children.Contains(_preview)) EditorCanvas.Children.Add(_preview);
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        _preview.Width = Math.Abs(b.X - a.X);
        _preview.Height = Math.Abs(b.Y - a.Y);
        _preview.Fill = new SolidColorBrush(_zoneType == AnalysisZoneType.Ignore
            ? System.Windows.Media.Color.FromArgb(28, 255, 84, 108)
            : System.Windows.Media.Color.FromArgb(24, 52, 235, 181));
        WpfCanvas.SetLeft(_preview, left);
        WpfCanvas.SetTop(_preview, top);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        Close();
    }
}
