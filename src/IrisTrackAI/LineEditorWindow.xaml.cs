using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using IrisTrackAI.Models;
using IrisTrackAI.Services;

namespace IrisTrackAI;

public partial class LineEditorWindow : Window
{
    private readonly nint _targetHwnd;
    private System.Windows.Point? _start;
    private readonly Line _preview = new()
    {
        Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 215, 255)),
        StrokeThickness = 4,
        StrokeDashArray = new DoubleCollection { 8, 4 }
    };

    public AnalysisLine? Result { get; private set; }

    public LineEditorWindow(nint targetHwnd, AnalysisLine? existing)
    {
        InitializeComponent();
        _targetHwnd = targetHwnd;
        Loaded += (_,__) =>
        {
            AlignToTarget();
            if (existing is not null && existing.IsValid)
            {
                DrawPreview(
                    new System.Windows.Point(existing.X1 * ActualWidth, existing.Y1 * ActualHeight),
                    new System.Windows.Point(existing.X2 * ActualWidth, existing.Y2 * ActualHeight));
            }
            Focus();
        };
        SourceInitialized += (_,__) => AlignToTarget();
    }

    private void AlignToTarget()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero || !NativeMethods.TryGetExtendedBounds(_targetHwnd, out var r)) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        var end = e.GetPosition(EditorCanvas);
        EditorCanvas.ReleaseMouseCapture();
        var dx = end.X - _start.Value.X;
        var dy = end.Y - _start.Value.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 30)
        {
            _start = null;
            return;
        }

        var w = Math.Max(1, EditorCanvas.ActualWidth);
        var h = Math.Max(1, EditorCanvas.ActualHeight);
        Result = new AnalysisLine(
            Math.Clamp(_start.Value.X / w, 0, 1),
            Math.Clamp(_start.Value.Y / h, 0, 1),
            Math.Clamp(end.X / w, 0, 1),
            Math.Clamp(end.Y / h, 0, 1));
        DialogResult = true;
        Close();
    }

    private void DrawPreview(System.Windows.Point a, System.Windows.Point b)
    {
        if (!EditorCanvas.Children.Contains(_preview)) EditorCanvas.Children.Add(_preview);
        _preview.X1 = a.X; _preview.Y1 = a.Y; _preview.X2 = b.X; _preview.Y2 = b.Y;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        Close();
    }
}
