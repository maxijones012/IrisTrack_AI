using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace IrisTrackAI;

/// <summary>
/// Extensión de UI para el motor facial. Se mantiene separada de MainWindow.xaml.cs
/// para no tocar el loop estable de IrisTrack: el enrutamiento se resuelve dentro
/// de YoloDetector, que ejecuta YOLO o SCRFD de forma excluyente.
/// </summary>
public partial class MainWindow
{
    private TextBlock? _faceRuntimeStatus;
    private bool _faceUiInstalled;

    public void InstallFaceEngineOption()
    {
        if (_faceUiInstalled || DetectionTargetCombo is null) return;
        _faceUiInstalled = true;

        var faceItem = new ComboBoxItem
        {
            Content = "Rostros / identificación",
            Tag = IrisTrackAI.Services.YoloDetector.FaceClassId.ToString(),
            ToolTip = "Usa SCRFD 500M para rostros. En este modo YOLO no procesa el fotograma. ArcFace sólo trabaja si cargaste referencias."
        };

        var insertAt = DetectionTargetCombo.Items.Count;
        for (var i = 0; i < DetectionTargetCombo.Items.Count; i++)
        {
            if (DetectionTargetCombo.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), "0", StringComparison.Ordinal))
            {
                insertAt = i + 1;
                break;
            }
        }
        DetectionTargetCombo.Items.Insert(insertAt, faceItem);
        DetectionTargetCombo.SelectionChanged += FaceEngineSelectionChanged;

        if (DetectionEnabled is not null)
        {
            DetectionEnabled.Content = "ANÁLISIS ACTIVO";
            DetectionEnabled.ToolTip = "Prende o apaga el motor seleccionado. F8. En Rostros se usa SCRFD; en los demás objetivos se usa YOLO.";
        }

        if (ModelStatus is not null)
            ModelStatus.ToolTip = "Muestra qué motor está trabajando. YOLO y el motor facial nunca hacen inferencia juntos en modo Rostros.";

        InstallFaceReferenceControls();
        _detector.FaceEngineStateChanged += FaceEngineStateChanged;
    }

    private void InstallFaceReferenceControls()
    {
        if (DetectionTargetCombo.Parent is not StackPanel parent) return;

        var row = new WrapPanel { Margin = new Thickness(3, 7, 0, 0) };
        var openFolder = new Button
        {
            Content = "CARPETA ROSTROS",
            ToolTip = "Abrí la carpeta de referencias. El nombre del archivo se usa como nombre de la referencia."
        };
        var reload = new Button
        {
            Content = "RECARGAR",
            ToolTip = "Vuelve a analizar las imágenes de referencia y reconstruye la galería ArcFace."
        };

        if (TryFindResource("GhostButton") is Style ghost)
        {
            openFolder.Style = ghost;
            reload.Style = ghost;
        }

        openFolder.Click += OpenKnownFaces_Click;
        reload.Click += ReloadKnownFaces_Click;
        row.Children.Add(openFolder);
        row.Children.Add(reload);

        _faceRuntimeStatus = new TextBlock
        {
            Text = "Rostros: SCRFD bajo demanda · ArcFace apagado sin referencias",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(119, 150, 170)),
            FontSize = 10,
            Margin = new Thickness(4, 3, 4, 0),
            TextWrapping = TextWrapping.Wrap
        };

        parent.Children.Add(row);
        parent.Children.Add(_faceRuntimeStatus);
    }

    private bool IsFaceEngineSelected()
        => DetectionTargetCombo?.SelectedItem is ComboBoxItem item
           && string.Equals(item.Tag?.ToString(), IrisTrackAI.Services.YoloDetector.FaceClassId.ToString(), StringComparison.Ordinal);

    private async void FaceEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _tracker.Reset();
        _lineCrossing.Reset();

        if (!IsFaceEngineSelected())
        {
            if (ModelStatus is not null)
                ModelStatus.Text = $"Motor: YOLO26n · {_detector.ProviderName} · Rostros en pausa";
            if (_faceRuntimeStatus is not null)
                _faceRuntimeStatus.Text = _detector.KnownFaceCount > 0
                    ? $"Galería facial preparada: {_detector.KnownFaceCount} referencia(s). ArcFace no trabaja mientras uses YOLO."
                    : "Motor facial en pausa · sin referencias cargadas";
            return;
        }

        if (DetectionModeBadge is not null)
            DetectionModeBadge.Text = "ROSTROS · SCRFD 500M";
        if (ModelStatus is not null)
            ModelStatus.Text = "Motor: preparando SCRFD 500M… · YOLO en pausa";
        if (_faceRuntimeStatus is not null)
            _faceRuntimeStatus.Text = "Preparando motor facial…";

        var progress = new Progress<double>(p =>
        {
            if (!IsFaceEngineSelected()) return;
            if (ModelStatus is not null)
                ModelStatus.Text = $"Motor facial: preparando modelos… {p:P0} · YOLO en pausa";
        });

        try
        {
            await _detector.PrepareFaceEngineAsync(progress);
            if (!IsFaceEngineSelected()) return;
            UpdateFaceEngineStatus(_detector.FaceEngineStatus);
        }
        catch (Exception ex)
        {
            if (ModelStatus is not null)
                ModelStatus.Text = "Motor facial: error · " + ex.Message;
            if (_faceRuntimeStatus is not null)
                _faceRuntimeStatus.Text = "No se pudo preparar el motor facial. YOLO no fue ejecutado por esta selección.";
        }
    }

    private void FaceEngineStateChanged(string status)
    {
        try
        {
            Dispatcher.InvokeAsync(() => UpdateFaceEngineStatus(status));
        }
        catch { }
    }

    private void UpdateFaceEngineStatus(string status)
    {
        if (_faceRuntimeStatus is not null)
            _faceRuntimeStatus.Text = status;

        if (IsFaceEngineSelected() && ModelStatus is not null)
            ModelStatus.Text = $"Motor: SCRFD 500M · {_detector.FaceProviderName} · ArcFace {_detector.RecognitionProviderName} · YOLO en pausa";
    }

    private void OpenKnownFaces_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_detector.KnownFacesDirectory);
            var readme = Path.Combine(_detector.KnownFacesDirectory, "LEEME.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme,
                    "IRISTRACK AI - ROSTROS CONOCIDOS\r\n\r\n" +
                    "1) Copiá una imagen clara por persona en esta carpeta.\r\n" +
                    "2) El nombre del archivo será la etiqueta mostrada por IrisTrack.\r\n" +
                    "   Ejemplo: Juan_Perez.jpg\r\n" +
                    "3) Volvé a IrisTrack y presioná RECARGAR.\r\n\r\n" +
                    "El resultado de ArcFace se muestra como POSIBLE coincidencia y un valor de similitud.\r\n" +
                    "No debe interpretarse por sí solo como identificación concluyente.\r\n");
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_detector.KnownFacesDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (_faceRuntimeStatus is not null) _faceRuntimeStatus.Text = "No se pudo abrir la carpeta: " + ex.Message;
        }
    }

    private async void ReloadKnownFaces_Click(object sender, RoutedEventArgs e)
    {
        if (_faceRuntimeStatus is not null) _faceRuntimeStatus.Text = "Reindexando referencias faciales…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (_faceRuntimeStatus is not null) _faceRuntimeStatus.Text = $"Preparando referencias… {p:P0}";
            });
            await _detector.ReloadFaceGalleryAsync(progress);
            UpdateFaceEngineStatus(_detector.FaceEngineStatus);
        }
        catch (Exception ex)
        {
            if (_faceRuntimeStatus is not null) _faceRuntimeStatus.Text = "Error al recargar referencias: " + ex.Message;
        }
    }
}
