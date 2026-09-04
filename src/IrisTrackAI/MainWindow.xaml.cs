using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using IrisTrackAI.Models;
using IrisTrackAI.Services;
using Forms = System.Windows.Forms;

namespace IrisTrackAI;

public partial class MainWindow : Window
{
    private readonly ScreenCaptureService _capture = new();
    private readonly ModelManager _models = new();
    private readonly YoloDetector _detector = new();
    private readonly DetectionTracker _tracker = new();
    private readonly CaptureHistoryService _history = new();
    private readonly LineCrossingService _lineCrossing = new();
    private readonly MotionGateService _motionGate = new();
    private readonly VideoSourceResolver _videoResolver = new();
    private readonly ZoneProfileService _zoneProfiles = new();
    private readonly List<AnalysisZone> _analysisZones = new();

    private OverlayWindow? _overlay;
    private QuickEdgeMenuWindow? _quickMenu;
    private WindowTarget? _target;
    private CancellationTokenSource? _loopCts;
    private HotKeyManager? _hotkeys;
    private Forms.NotifyIcon? _tray;
    private Bitmap? _lastFrame;
    private IReadOnlyList<Detection> _lastDetections = Array.Empty<Detection>();
    private AnalysisLine? _analysisLine;
    private DateTime _motionAwakeUntil = DateTime.MinValue;
    private int _captureCount;
    private int _crossingCount;
    private bool _modelReady;
    private DateTime _nextVideoResolveUtc = DateTime.MinValue;
    private int _videoResolveBusy;
    private string? _zoneProfileKey;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        ConfidenceSlider.ValueChanged += (_,__) => ConfidenceText.Text = $"{ConfidenceSlider.Value:P0}";
        RefreshWindows();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        UpdateDetectionTargetUi();
        UpdateAnalysisRateUi();
        UpdateAnalysisModeUi();
        UpdateZoneStatus();
        try
        {
            ModelStatus.Text = "Modelo: preparando YOLO26n ONNX…";
            var progress = new Progress<double>(p => ModelStatus.Text = $"Modelo: descargando YOLO26n… {p:P0}");
            var path = await _models.EnsureModelAsync(progress);
            await Task.Run(() => _detector.Load(path));
            _modelReady = true;
            ModelStatus.Text = $"Modelo: YOLO26n · {_detector.ProviderName}";
        }
        catch (Exception ex)
        {
            ModelStatus.Text = "No se pudo preparar el modelo: " + ex.Message;
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotkeys = new HotKeyManager(new WindowInteropHelper(this).Handle);
        _hotkeys.Pressed += id => Dispatcher.Invoke(() =>
        {
            if (id == 8) DetectionEnabled.IsChecked = !(DetectionEnabled.IsChecked == true);
            else if (id == 9) _ = ManualCaptureAsync();
            else if (id == 10) AutoCaptureEnabled.IsChecked = !(AutoCaptureEnabled.IsChecked == true);
        });
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon { Text = "IrisTrack AI", Visible = true, Icon = GetAppIcon() };
        _tray.DoubleClick += (_,__) => Dispatcher.Invoke(RestoreFromTray);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_,__) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Activar/Desactivar YOLO (F8)", null, (_,__) => Dispatcher.Invoke(() => DetectionEnabled.IsChecked = !(DetectionEnabled.IsChecked == true)));
        menu.Items.Add("Salir", null, (_,__) => Dispatcher.Invoke(() => { _reallyClose = true; Close(); }));
        _tray.ContextMenuStrip = menu;
    }

    private static System.Drawing.Icon GetAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
                return System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private bool _reallyClose;
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        StopLoop();
        _overlay?.Close();
        _quickMenu?.Close();
        _hotkeys?.Dispose();
        _detector.Dispose();
        _lastFrame?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var previous = (WindowCombo.SelectedItem as WindowTarget)?.Hwnd;
        var list = WindowEnumerator.GetWindows();
        WindowCombo.ItemsSource = list;
        WindowCombo.SelectedItem = previous is null
            ? list.FirstOrDefault()
            : list.FirstOrDefault(x => x.Hwnd == previous) ?? list.FirstOrDefault();
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCombo.SelectedItem is not WindowTarget t)
        {
            System.Windows.MessageBox.Show("Elegí una ventana primero.");
            return;
        }
        _target = t;
        _history.LinkedVideoPath = null;
        _nextVideoResolveUtc = DateTime.MinValue;
        LoadZonesForSource(null, t.Title, replaceExisting: true);
        VideoPathText.Text = "AUTO · detectando archivo…";
        _ = RefreshAutoVideoPathAsync(t, force: true);
        _overlay ??= new OverlayWindow();
        if (!_overlay.IsVisible) _overlay.Show();
        _overlay.AlignTo(t.Hwnd);
        EnsureQuickMenu(t.Hwnd);
        TargetStatus.Text = $"● ACOPLADO · {t.Title}";
        TargetStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(182, 238, 255));
        StartLoop();
        Hide();
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        StopLoop();
        _target = null;
        _history.LinkedVideoPath = null;
        lock (_analysisZones) _analysisZones.Clear();
        _zoneProfileKey = null;
        UpdateZoneStatus();
        VideoPathText.Text = "AUTO · esperando video";
        _overlay?.Hide();
        _quickMenu?.Hide();
        TargetStatus.Text = "SIN ACOPLAR";
        TargetStatus.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        PerfStatus.Text = "En espera";
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(97, 117, 138));
    }

    private void StartLoop()
    {
        StopLoop();
        _loopCts = new CancellationTokenSource();
        _ = AnalyzeLoopAsync(_loopCts.Token);
    }

    private void StopLoop()
    {
        try { _loopCts?.Cancel(); } catch { }
        _loopCts?.Dispose();
        _loopCts = null;
    }

    private async Task AnalyzeLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int analyses = 0;

        while (!ct.IsCancellationRequested)
        {
            var iteration = Stopwatch.StartNew();
            var t = _target;

            if (t is null || !_modelReady)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.Hide();
                    _quickMenu?.Hide();
                });
                await Task.Delay(150, ct);
                continue;
            }

            bool foreground = NativeMethods.IsTargetForeground(t.Hwnd, t.ProcessId);
            if (OnlyForeground.IsChecked == true && !foreground)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.Hide();
                    _quickMenu?.Hide();
                });
                await Task.Delay(120, ct);
                continue;
            }

            if (NativeMethods.IsIconic(t.Hwnd) || !NativeMethods.IsWindowVisible(t.Hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.Hide();
                    _quickMenu?.Hide();
                });
                await Task.Delay(200, ct);
                continue;
            }

            await Dispatcher.InvokeAsync(() => EnsureQuickMenu(t.Hwnd));

            if (DateTime.UtcNow >= _nextVideoResolveUtc)
            {
                _nextVideoResolveUtc = DateTime.UtcNow.AddSeconds(5);
                _ = RefreshAutoVideoPathAsync(t);
            }

            if (DetectionEnabled.IsChecked != true)
            {
                await Dispatcher.InvokeAsync(() => _overlay?.Hide());
                await Task.Delay(120, ct);
                continue;
            }

            using var frame = _capture.Capture(t);
            if (frame is null)
            {
                await Task.Delay(80, ct);
                continue;
            }

            // La captura manual siempre conserva el fotograma más reciente, incluso si YOLO está dormido.
            lock (this)
            {
                _lastFrame?.Dispose();
                _lastFrame = (Bitmap)frame.Clone();
            }

            var activeLine = _analysisLine;
            var lineMode = IsLineMode() && activeLine is { IsValid: true };
            var motionSleeping = false;

            if (lineMode && MotionWakeEnabled.IsChecked == true && activeLine is not null)
            {
                var motion = await Task.Run(() => _motionGate.HasMotion(frame, activeLine), ct);
                if (motion) _motionAwakeUntil = DateTime.UtcNow.AddSeconds(2.5);

                if (DateTime.UtcNow > _motionAwakeUntil)
                {
                    motionSleeping = true;
                    _lastDetections = Array.Empty<Detection>();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _overlay ??= new OverlayWindow();
                        if (!_overlay.IsVisible) _overlay.Show();
                        _overlay.AlignTo(t.Hwnd);
                        _overlay.Draw(Array.Empty<Detection>(), frame.Width, frame.Height, activeLine, true, GetZoneSnapshot());
                        PerfStatus.Text = $"En espera · sin movimiento cerca de la línea · objetivo: {GetObjectiveStatusText()}";
                        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(93, 121, 142));
                    });
                    await Task.Delay(100, ct);
                    continue;
                }
            }

            var threshold = (float)ConfidenceSlider.Value;
            var allowedClasses = GetSelectedDetectionClassIds();
            var detections = await Task.Run(() => _detector.Detect(frame, threshold, allowedClasses), ct);

            // Las zonas se aplican antes del tracking/overlay/capturas para no gastar trabajo
            // secundario en objetos que el usuario decidió ignorar.
            var zones = GetZoneSnapshot();
            detections = ZoneFilterService.Apply(detections, zones, frame.Width, frame.Height);
            detections = _tracker.Update(detections, TimeSpan.FromSeconds(1.5));
            analyses++;

            lock (this)
            {
                _lastDetections = detections.ToArray();
            }

            IReadOnlyList<Detection> overlayDetections = detections;
            if (lineMode && activeLine is not null && LineNearOnly.IsChecked == true)
            {
                overlayDetections = detections
                    .Where(d => LineCrossingService.IsNearLine(d, activeLine, frame.Width, frame.Height, 0.18))
                    .ToArray();
            }

            if (lineMode && activeLine is not null)
            {
                var direction = GetCrossingDirection();
                foreach (var d in detections)
                {
                    if (!_lineCrossing.TryRegisterCrossing(d, activeLine, frame.Width, frame.Height, direction, out var directionText))
                        continue;

                    _crossingCount++;
                    var alert = $"CRUCE · {d.ClassName.ToUpperInvariant()} · {directionText} · {d.Confidence:P0}";
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _overlay?.ShowCrossingAlert(alert);
                        LineStatus.Text = $"Línea activa · {_crossingCount} cruce(s) detectado(s) · último: {d.ClassName} {directionText}";
                    });

                    if (CaptureOnCrossing.IsChecked == true)
                    {
                        _history.SaveCrop = SaveCrop.IsChecked == true;
                        _history.SaveFullFrame = SaveFrame.IsChecked == true;
                        try
                        {
                            await _history.SaveAsync(frame, d, t.Title, ct, "CruceLinea");
                            _captureCount++;
                        }
                        catch { }
                    }
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _overlay ??= new OverlayWindow();
                if (!_overlay.IsVisible) _overlay.Show();
                _overlay.AlignTo(t.Hwnd);
                _overlay.Draw(overlayDetections, frame.Width, frame.Height, lineMode ? activeLine : null, motionSleeping, GetZoneSnapshot());
            });

            // Las capturas automáticas originales siguen funcionando de forma independiente al modo línea.
            if (AutoCaptureEnabled.IsChecked == true)
            {
                _history.SaveCrop = SaveCrop.IsChecked == true;
                _history.SaveFullFrame = SaveFrame.IsChecked == true;
                foreach (var d in detections.Where(IsAutoCaptureClass))
                {
                    if (!_tracker.ShouldAutoCapture(d)) continue;
                    try
                    {
                        await _history.SaveAsync(frame, d, t.Title, ct);
                        _captureCount++;
                    }
                    catch { }
                }
            }

            if (sw.ElapsedMilliseconds >= 1000)
            {
                var aps = analyses / (sw.ElapsedMilliseconds / 1000.0);
                analyses = 0;
                sw.Restart();
                await Dispatcher.InvokeAsync(() =>
                {
                    var modeText = lineMode ? $"línea · cruces {_crossingCount}" : "normal";
                    PerfStatus.Text = $"Activo · {aps:0.0} análisis/s · {detections.Count} detecciones · objetivo: {GetObjectiveStatusText()} · {modeText}";
                    CaptureCountText.Text = $"Capturas: {_captureCount}";
                    StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 215, 255));
                });
            }

            var periodMs = GetSelectedAnalysisPeriodMs();
            var delayMs = periodMs > 0
                ? Math.Max(0, periodMs - (int)iteration.ElapsedMilliseconds)
                : 20;
            if (delayMs > 0) await Task.Delay(delayMs, ct);
        }
    }

    private HashSet<int>? GetSelectedDetectionClassIds()
    {
        if (DetectionTargetCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return null;
        var raw = item.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw == "-1") return null;

        var ids = new HashSet<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var id) && id >= 0) ids.Add(id);
        return ids.Count == 0 ? null : ids;
    }

    private string? GetSelectedDetectionClassName()
    {
        if (DetectionTargetCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return null;
        var raw = item.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw == "-1") return null;
        return (item.Content?.ToString() ?? "Objeto").Replace("Sólo ", "", StringComparison.OrdinalIgnoreCase);
    }

    private string GetObjectiveStatusText() => GetSelectedDetectionClassName()?.ToLowerInvariant() ?? "todas";

    private int GetSelectedAnalysisPeriodMs()
    {
        if (AnalysisRateCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return 0;
        if (!int.TryParse(item.Tag?.ToString(), out var rate) || rate <= 0) return 0;
        return (int)Math.Round(1000.0 / rate);
    }

    private CrossingDirection GetCrossingDirection()
    {
        if (CrossingDirectionCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return CrossingDirection.Any;
        return int.TryParse(item.Tag?.ToString(), out var value) && Enum.IsDefined(typeof(CrossingDirection), value)
            ? (CrossingDirection)value
            : CrossingDirection.Any;
    }

    private bool IsLineMode()
        => AnalysisModeCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item
           && string.Equals(item.Tag?.ToString(), "line", StringComparison.OrdinalIgnoreCase);

    private void DetectionTargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _lineCrossing.Reset();
        UpdateDetectionTargetUi();
    }

    private void UpdateDetectionTargetUi()
    {
        if (DetectionModeBadge is null || DetectionTargetStatus is null || PerformanceHint is null) return;
        var name = GetSelectedDetectionClassName();
        if (name is null)
        {
            DetectionModeBadge.Text = "TODAS LAS CLASES";
            DetectionTargetStatus.Text = "Detectando todas las clases disponibles.";
            PerformanceHint.Text = "El filtro por clase reduce overlay, tracking y postprocesado. Para bajar CPU de verdad, usá un objetivo concreto o el modo de cruce con reposo por movimiento.";
        }
        else
        {
            DetectionModeBadge.Text = $"OBJETIVO · {name.ToUpperInvariant()}";
            DetectionTargetStatus.Text = $"Mostrando y siguiendo únicamente: {name}.";
            PerformanceHint.Text = $"IrisTrack descarta las demás clases apenas salen del modelo. En cruce de línea, este mismo objetivo define qué puede disparar el evento.";
        }
        UpdateQuickMenuState();
    }

    private void AnalysisRateCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateAnalysisRateUi();

    private void UpdateAnalysisRateUi()
    {
        if (PerformanceHint is null || AnalysisRateCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var rateText = item.Content?.ToString() ?? "Máximo";
        PerformanceHint.ToolTip = $"Ritmo actual: {rateText}";
    }

    private void AnalysisModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _lineCrossing.Reset();
        _motionGate.Reset();
        _motionAwakeUntil = DateTime.MinValue;
        UpdateAnalysisModeUi();
    }

    private void UpdateAnalysisModeUi()
    {
        if (LineModeBadge is null || LineStatus is null) return;
        if (IsLineMode())
        {
            LineModeBadge.Text = "CRUCE DE LÍNEA";
            LineStatus.Text = _analysisLine is { IsValid: true }
                ? $"Línea activa · {_crossingCount} cruce(s) detectado(s)."
                : "Modo cruce activo, pero todavía no dibujaste una línea.";
        }
        else
        {
            LineModeBadge.Text = "MODO NORMAL";
            LineStatus.Text = _analysisLine is { IsValid: true }
                ? "Línea guardada. Cambiá a 'Cruce de línea' cuando quieras usarla."
                : "No hay línea definida.";
        }
        UpdateQuickMenuState();
    }

    private void DrawLine_Click(object sender, RoutedEventArgs e)
    {
        if (_target is null)
        {
            System.Windows.MessageBox.Show("Primero acoplá IrisTrack a la ventana del video y después dibujá la línea.");
            return;
        }

        var wasRunning = _loopCts is not null;
        StopLoop();
        _overlay?.Hide();
        _quickMenu?.ForceCollapse();
        _quickMenu?.Hide();

        var editor = new LineEditorWindow(_target.Hwnd, _analysisLine);
        var ok = editor.ShowDialog();
        if (ok == true && editor.Result is { IsValid: true } line)
        {
            _analysisLine = line;
            _lineCrossing.Reset();
            _motionGate.Reset();
            _motionAwakeUntil = DateTime.UtcNow.AddSeconds(2.5);
            if (AnalysisModeCombo.SelectedIndex == 0) AnalysisModeCombo.SelectedIndex = 1;
            UpdateAnalysisModeUi();
        }

        if (wasRunning) StartLoop();
        if (_target is not null)
        {
            Hide();
            try { NativeMethods.SetForegroundWindow(_target.Hwnd); } catch { }
        }
    }

    private void ClearLine_Click(object sender, RoutedEventArgs e)
    {
        _analysisLine = null;
        _lineCrossing.Reset();
        _motionGate.Reset();
        _motionAwakeUntil = DateTime.MinValue;
        UpdateAnalysisModeUi();
    }

    private bool IsAutoCaptureClass(Detection d)
    {
        // Con objetivo específico, CAPTURAS ON sigue automáticamente ese objetivo.
        // Los checks por clase quedan para el modo "Todos".
        var selected = GetSelectedDetectionClassIds();
        if (selected is not null) return selected.Contains(d.ClassId);

        return d.ClassId switch
        {
            0 => CapPerson.IsChecked == true,
            1 => CapBicycle.IsChecked == true,
            2 => CapCar.IsChecked == true,
            3 => CapMotorcycle.IsChecked == true,
            5 => CapBus.IsChecked == true,
            7 => CapTruck.IsChecked == true,
            16 => CapDog.IsChecked == true,
            _ => false
        };
    }

    private async Task ManualCaptureAsync()
    {
        if (_target is null) return;
        await RefreshAutoVideoPathAsync(_target);
        Bitmap? clone = null;
        lock (this)
        {
            if (_lastFrame is not null) clone = (Bitmap)_lastFrame.Clone();
        }
        if (clone is null) return;

        using (clone)
        {
            try
            {
                var p = await _history.SaveManualFrameAsync(clone, _target.Title);
                _captureCount++;
                CaptureCountText.Text = $"Capturas: {_captureCount}";
                PerfStatus.Text = "Captura manual guardada: " + Path.GetFileName(p);
            }
            catch (Exception ex)
            {
                PerfStatus.Text = "Error al guardar captura: " + ex.Message;
            }
        }
    }

    private async Task RefreshAutoVideoPathAsync(WindowTarget target, bool force = false)
    {
        if (Interlocked.CompareExchange(ref _videoResolveBusy, 1, 0) != 0) return;
        try
        {
            var path = await Task.Run(() => _videoResolver.TryResolve(target, force));
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                _history.LinkedVideoPath = path;
                await Dispatcher.InvokeAsync(() =>
                {
                    LoadZonesForSource(path, target.Title, replaceExisting: false);
                    VideoPathText.Text = "AUTO · " + path;
                    VideoPathText.ToolTip = $"Video detectado automáticamente.\nLas capturas se guardan en: {_history.ResolveOutputFolder(target.Title)}";
                });
            }
            else if (string.IsNullOrWhiteSpace(_history.LinkedVideoPath))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    VideoPathText.Text = "AUTO · buscando ruta del video…";
                    VideoPathText.ToolTip = "IrisTrack todavía no pudo identificar el archivo abierto. Seguirá intentando en segundo plano; si no lo resuelve, usa la carpeta de Imágenes como respaldo.";
                });
            }
        }
        catch { }
        finally
        {
            Volatile.Write(ref _videoResolveBusy, 0);
        }
    }

    private IReadOnlyList<AnalysisZone> GetZoneSnapshot()
    {
        lock (_analysisZones) return _analysisZones.ToArray();
    }

    private void LoadZonesForSource(string? videoPath, string windowTitle, bool replaceExisting)
    {
        var key = _zoneProfiles.BuildKey(videoPath, windowTitle);
        if (string.Equals(key, _zoneProfileKey, StringComparison.Ordinal)) return;

        var loaded = _zoneProfiles.Load(key);
        lock (_analysisZones)
        {
            if (replaceExisting || loaded.Count > 0 || _analysisZones.Count == 0)
            {
                _analysisZones.Clear();
                _analysisZones.AddRange(loaded);
            }
            else if (_analysisZones.Count > 0 && loaded.Count == 0)
            {
                // El usuario pudo dibujar zonas antes de que VLC revelara la ruta real.
                // En ese caso migramos el perfil temporal al perfil específico del archivo.
                _zoneProfiles.Save(key, _analysisZones.ToArray());
            }
        }
        _zoneProfileKey = key;
        UpdateZoneStatus();
    }

    private void PersistZones()
    {
        var t = _target;
        if (t is null) return;
        var key = _zoneProfiles.BuildKey(_history.LinkedVideoPath, t.Title);
        var snapshot = GetZoneSnapshot();
        _zoneProfiles.Save(key, snapshot);
        // También guardamos un perfil por título como respaldo si el reproductor no expone la ruta.
        _zoneProfiles.Save(_zoneProfiles.BuildKey(null, t.Title), snapshot);
        _zoneProfileKey = key;
        UpdateZoneStatus();
    }

    private void UpdateZoneStatus()
    {
        if (ZoneStatus is null) return;
        var zones = GetZoneSnapshot();
        var ignored = zones.Count(z => z.Type == AnalysisZoneType.Ignore);
        var interest = zones.Count(z => z.Type == AnalysisZoneType.Interest);
        ZoneStatus.Text = zones.Count == 0
            ? "Sin zonas activas"
            : $"Zonas · ignoradas {ignored} · interés {interest}";
        UpdateQuickMenuState();
    }

    private void AddIgnoreZone_Click(object sender, RoutedEventArgs e)
        => EditZone(AnalysisZoneType.Ignore);

    private void SetInterestZone_Click(object sender, RoutedEventArgs e)
        => EditZone(AnalysisZoneType.Interest);

    private void EditZone(AnalysisZoneType type)
    {
        if (_target is null)
        {
            System.Windows.MessageBox.Show("Primero acoplá IrisTrack a la ventana del video.");
            return;
        }

        var wasRunning = _loopCts is not null;
        StopLoop();
        _overlay?.Hide();
        _quickMenu?.ForceCollapse();
        _quickMenu?.Hide();

        var editor = new ZoneEditorWindow(_target.Hwnd, type, GetZoneSnapshot());
        var ok = editor.ShowDialog();
        if (ok == true && editor.Result is { IsValid: true } zone)
        {
            lock (_analysisZones)
            {
                if (type == AnalysisZoneType.Interest)
                    _analysisZones.RemoveAll(z => z.Type == AnalysisZoneType.Interest);
                _analysisZones.Add(zone);
            }
            _tracker.Reset();
            _lineCrossing.Reset();
            PersistZones();
        }

        if (wasRunning) StartLoop();
        if (_target is not null)
        {
            Hide();
            try { NativeMethods.SetForegroundWindow(_target.Hwnd); } catch { }
        }
    }

    private void ClearZones_Click(object sender, RoutedEventArgs e)
    {
        lock (_analysisZones) _analysisZones.Clear();
        _tracker.Reset();
        _lineCrossing.Reset();
        PersistZones();
    }

    private void OpenCaptures_Click(object sender, RoutedEventArgs e)
    {
        var title = _target?.Title ?? "Sin ventana";
        var folder = _history.ResolveOutputFolder(title);
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/maxijones012/IrisTrack_AI")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            PerfStatus.Text = "No se pudo abrir el repositorio: " + ex.Message;
        }
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (StatusDot is null) return;
        if (DetectionEnabled?.IsChecked == true)
        {
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 215, 255));
        }
        else
        {
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(97, 117, 138));
        }
        UpdateQuickMenuState();
    }

    private void CaptureSettingChanged(object sender, RoutedEventArgs e)
    {
        if (DetectionTargetCombo is null || CapPerson is null || CapBicycle is null || CapCar is null || CapMotorcycle is null
            || CapBus is null || CapTruck is null || CapDog is null) return;

        if (AutoCaptureEnabled?.IsChecked == true)
        {
            // En modo Todos, si el usuario prende CAPTURAS desde cero, habilitamos las
            // clases visibles para que el estado ON tenga efecto inmediato.
            if (GetSelectedDetectionClassIds() is null && !AnyCaptureClassSelected())
            {
                CapPerson.IsChecked = true;
                CapBicycle.IsChecked = true;
                CapCar.IsChecked = true;
                CapMotorcycle.IsChecked = true;
                CapBus.IsChecked = true;
                CapTruck.IsChecked = true;
                CapDog.IsChecked = true;
            }

            if (_target is not null) _ = RefreshAutoVideoPathAsync(_target, force: true);
        }
        UpdateQuickMenuState();
    }

    private bool AnyCaptureClassSelected()
        => CapPerson.IsChecked == true || CapBicycle.IsChecked == true || CapCar.IsChecked == true
           || CapMotorcycle.IsChecked == true || CapBus.IsChecked == true || CapTruck.IsChecked == true || CapDog.IsChecked == true;

    private void EnsureQuickMenu(nint targetHwnd)
    {
        if (_quickMenu is null)
        {
            _quickMenu = new QuickEdgeMenuWindow();
            _quickMenu.CommandRequested += QuickMenu_CommandRequested;
        }
        if (!_quickMenu.IsVisible) _quickMenu.Show();
        _quickMenu.AlignTo(targetHwnd);
        UpdateQuickMenuState();
    }

    private void UpdateQuickMenuState()
    {
        if (_quickMenu is null) return;
        string tag = "-1";
        if (DetectionTargetCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            tag = item.Tag?.ToString() ?? "-1";
        var zones = GetZoneSnapshot();
        _quickMenu.SetState(tag, IsLineMode(), DetectionEnabled?.IsChecked == true, AutoCaptureEnabled?.IsChecked == true,
            zones.Any(z => z.Type == AnalysisZoneType.Ignore), zones.Any(z => z.Type == AnalysisZoneType.Interest));
    }

    private void QuickMenu_CommandRequested(QuickEdgeCommand command)
    {
        switch (command)
        {
            case QuickEdgeCommand.DetectAll:
                SelectDetectionTargetByTag("-1");
                break;
            case QuickEdgeCommand.DetectPerson:
                SelectDetectionTargetByTag("0");
                break;
            case QuickEdgeCommand.DetectBicycle:
                SelectDetectionTargetByTag("1");
                break;
            case QuickEdgeCommand.DetectMotorcycle:
                SelectDetectionTargetByTag("3");
                break;
            case QuickEdgeCommand.DetectVehicles:
                SelectDetectionTargetByTag("2,3,5,7");
                break;
            case QuickEdgeCommand.DrawLine:
                DrawLine_Click(this, new RoutedEventArgs());
                break;
            case QuickEdgeCommand.NormalMode:
                AnalysisModeCombo.SelectedIndex = 0;
                break;
            case QuickEdgeCommand.AddIgnoreZone:
                AddIgnoreZone_Click(this, new RoutedEventArgs());
                break;
            case QuickEdgeCommand.SetInterestZone:
                SetInterestZone_Click(this, new RoutedEventArgs());
                break;
            case QuickEdgeCommand.ClearZones:
                ClearZones_Click(this, new RoutedEventArgs());
                break;
            case QuickEdgeCommand.ToggleCaptures:
                AutoCaptureEnabled.IsChecked = !(AutoCaptureEnabled.IsChecked == true);
                break;
            case QuickEdgeCommand.ManualCapture:
                _ = ManualCaptureAsync();
                break;
            case QuickEdgeCommand.ToggleYolo:
                DetectionEnabled.IsChecked = !(DetectionEnabled.IsChecked == true);
                break;
        }

        UpdateQuickMenuState();
        if (_target is not null)
        {
            try { NativeMethods.SetForegroundWindow(_target.Hwnd); } catch { }
        }
    }

    private void SelectDetectionTargetByTag(string tag)
    {
        foreach (var obj in DetectionTargetCombo.Items)
        {
            if (obj is System.Windows.Controls.ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                DetectionTargetCombo.SelectedItem = item;
                return;
            }
        }
    }
}
