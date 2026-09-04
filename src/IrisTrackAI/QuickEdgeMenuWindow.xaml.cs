using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IrisTrackAI.Services;

namespace IrisTrackAI;

public partial class QuickEdgeMenuWindow : Window
{
    private const int CollapsedWidth = 5;
    private const int CollapsedHeight = 165;
    private const int ExpandedWidth = 214;
    private const int ExpandedHeight = 430;
    private const int TopOffset = 48;

    private nint _hwnd;
    private nint _targetHwnd;
    private NativeMethods.RECT _targetBounds;
    private bool _expanded;
    private CancellationTokenSource? _collapseCts;
    private string _objectiveTag = "-1";
    private bool _lineMode;
    private bool _yoloEnabled = true;
    private bool _capturesEnabled;
    private bool _hasIgnoreZones;
    private bool _hasInterestZone;

    public event Action<QuickEdgeCommand>? CommandRequested;

    public QuickEdgeMenuWindow()
    {
        InitializeComponent();
        SourceInitialized += (_,__) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(ex));
            NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            ApplyBounds();
        };
    }

    public void AlignTo(nint targetHwnd)
    {
        _targetHwnd = targetHwnd;
        if (!NativeMethods.TryGetExtendedBounds(targetHwnd, out _targetBounds)) return;
        ApplyBounds();
    }

    public void SetState(string? objectiveTag, bool lineMode, bool yoloEnabled, bool capturesEnabled, bool hasIgnoreZones, bool hasInterestZone)
    {
        _objectiveTag = string.IsNullOrWhiteSpace(objectiveTag) ? "-1" : objectiveTag;
        _lineMode = lineMode;
        _yoloEnabled = yoloEnabled;
        _capturesEnabled = capturesEnabled;
        _hasIgnoreZones = hasIgnoreZones;
        _hasInterestZone = hasInterestZone;
        ItemYolo.Text = yoloEnabled ? "YOLO ON" : "YOLO OFF";
        ItemCaptures.Text = capturesEnabled ? "CAPTURAS ON" : "CAPTURAS OFF";
        RefreshColors();
    }

    public void ForceCollapse()
    {
        _collapseCts?.Cancel();
        if (!_expanded)
        {
            MenuPanel.Visibility = Visibility.Collapsed;
            ApplyBounds();
            return;
        }
        CollapseAnimated(immediate: true);
    }

    private void Root_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseCts?.Cancel();
        if (!_expanded) ExpandAnimated();
    }

    private async void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_expanded) return;
        _collapseCts?.Cancel();
        _collapseCts = new CancellationTokenSource();
        var token = _collapseCts.Token;
        try
        {
            await Task.Delay(430, token);
            if (!token.IsCancellationRequested && !IsMouseOver)
                CollapseAnimated(immediate: false);
        }
        catch (TaskCanceledException) { }
    }

    private void ExpandAnimated()
    {
        _expanded = true;
        ApplyBounds();
        MenuPanel.Visibility = Visibility.Visible;
        RefreshColors();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MenuPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(165)) { EasingFunction = ease });
        if (MenuPanel.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private void CollapseAnimated(bool immediate)
    {
        if (!_expanded)
        {
            ApplyBounds();
            return;
        }

        if (immediate)
        {
            _expanded = false;
            MenuPanel.BeginAnimation(OpacityProperty, null);
            if (MenuPanel.RenderTransform is TranslateTransform tt0) tt0.BeginAnimation(TranslateTransform.XProperty, null);
            MenuPanel.Opacity = 0;
            MenuPanel.Visibility = Visibility.Collapsed;
            ApplyBounds();
            return;
        }

        var fade = new DoubleAnimation(MenuPanel.Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_,__) =>
        {
            _expanded = false;
            MenuPanel.Visibility = Visibility.Collapsed;
            ApplyBounds();
        };
        MenuPanel.BeginAnimation(OpacityProperty, fade);
        if (MenuPanel.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 18, TimeSpan.FromMilliseconds(120)));
    }

    private void ApplyBounds()
    {
        if (_hwnd == nint.Zero || _targetHwnd == nint.Zero || _targetBounds.Width <= 0 || _targetBounds.Height <= 0) return;

        var width = _expanded ? ExpandedWidth : CollapsedWidth;
        var height = _expanded ? Math.Min(ExpandedHeight, Math.Max(140, _targetBounds.Height - TopOffset - 8)) : Math.Min(CollapsedHeight, Math.Max(40, _targetBounds.Height - TopOffset - 8));
        var x = _targetBounds.Right - width;
        var y = _targetBounds.Top + Math.Min(TopOffset, Math.Max(0, _targetBounds.Height - height));

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void Item_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseCts?.Cancel();
        if (sender is TextBlock tb)
        {
            tb.Foreground = AccentBrush;
            tb.Opacity = 1;
        }
    }

    private void Item_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is TextBlock) RefreshColors();
    }

    private void Item_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        var tag = tb.Tag?.ToString();
        var command = tag switch
        {
            "all" => QuickEdgeCommand.DetectAll,
            "person" => QuickEdgeCommand.DetectPerson,
            "bicycle" => QuickEdgeCommand.DetectBicycle,
            "motorcycle" => QuickEdgeCommand.DetectMotorcycle,
            "vehicles" => QuickEdgeCommand.DetectVehicles,
            "drawline" => QuickEdgeCommand.DrawLine,
            "normal" => QuickEdgeCommand.NormalMode,
            "ignorezone" => QuickEdgeCommand.AddIgnoreZone,
            "interestzone" => QuickEdgeCommand.SetInterestZone,
            "clearzones" => QuickEdgeCommand.ClearZones,
            "captures" => QuickEdgeCommand.ToggleCaptures,
            "manualcapture" => QuickEdgeCommand.ManualCapture,
            "yolo" => QuickEdgeCommand.ToggleYolo,
            _ => (QuickEdgeCommand?)null
        };
        if (command is null) return;

        ForceCollapse();
        CommandRequested?.Invoke(command.Value);
        e.Handled = true;
    }

    private static readonly SolidColorBrush AccentBrush = new(System.Windows.Media.Color.FromRgb(50, 215, 255));
    private static readonly SolidColorBrush NormalBrush = new(System.Windows.Media.Color.FromRgb(234, 248, 255));

    private void RefreshColors()
    {
        SetItem(ItemAll, _objectiveTag == "-1");
        SetItem(ItemPerson, _objectiveTag == "0");
        SetItem(ItemBicycle, _objectiveTag == "1");
        SetItem(ItemMotorcycle, _objectiveTag == "3");
        SetItem(ItemVehicles, _objectiveTag == "2,3,5,7");
        SetItem(ItemNormal, !_lineMode);
        SetItem(ItemIgnoreZone, _hasIgnoreZones);
        SetItem(ItemInterestZone, _hasInterestZone);
        SetItem(ItemCaptures, _capturesEnabled);
        SetItem(ItemYolo, _yoloEnabled);
    }

    private static void SetItem(TextBlock tb, bool active)
    {
        tb.Foreground = active ? AccentBrush : NormalBrush;
        tb.Opacity = active ? 1.0 : 0.88;
        tb.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
    }
}
