using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using IrisTrackAI;
using IrisTrackAI.Models;
using IrisTrackAI.Services;

static class Checks
{
    private static int _count;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message); _count++;
    }
    static Detection Plate(long id = 1, string text = "AA001AA", float score = .90f) =>
        new(Detection.PlateClassId, "Patente", .90f, new RectangleF(10, 10, 100, 35))
        { TrackId = id, PlateText = text, OcrConfidence = score, PlateStable = true, OcrFresh = true };

    static async Task Main(string[] args)
    {
        var values = new float[370];
        const string raw = "AA001AA___";
        for (int i = 0; i < 10; i++) values[i * 37 + PlateOcrDecoder.Alphabet.IndexOf(raw[i])] = i < 7 ? .9f : .99f;
        var decoded = PlateOcrDecoder.Decode(values);
        Check(decoded.Text == "AA001AA", "OCR conserva letras repetidas y ceros, retira sólo relleno final");
        Check(Math.Abs(decoded.Confidence - .9f) < .001f, "La confianza no se infla con los espacios de relleno");
        var mapped = PlateDetector.MapBox(38.4f, 134.4f, 76.8f, 153.6f, .192f, 0, 96, 2000, 1000);
        Check(Math.Abs(mapped.X - 200) < .01 && Math.Abs(mapped.Y - 200) < .01 && Math.Abs(mapped.Width - 200) < .01,
            "El recorte vuelve a las coordenadas originales después del letterbox");

        var tracker = new DetectionTracker();
        tracker.Update(new[] { Plate() }, TimeSpan.FromSeconds(2));
        var sameFrame = new[] { Plate(), Plate() with { Box = new RectangleF(20, 10, 100, 35) } };
        tracker.Update(sameFrame, TimeSpan.FromSeconds(2));
        Check(sameFrame[0].TrackId != sameFrame[1].TrackId, "Dos cajas del mismo fotograma reciben identificadores distintos");
        Check(!tracker.HasAutoCaptured(sameFrame[0]), "Una captura pendiente puede reintentarse");
        tracker.MarkAutoCaptured(sameFrame[0]);
        Check(tracker.HasAutoCaptured(sameFrame[0]), "Sólo el guardado exitoso marca el objeto como capturado");

        var consensus = new PlateConsensus();
        var d = Plate();
        var good = new PlateReading("AA001AA", .92f, .85f);
        for (int i = 0; i < 3; i++)
        {
            var time = TimeSpan.FromMilliseconds(i * 210);
            Check(consensus.ShouldRead(d, time), "Lectura nueva disponible " + (i + 1));
            consensus.Apply(d, good, time);
            Check(d.PlateStable == (i == 2), "Estabilidad exige tres lecturas " + (i + 1));
        }
        Check(!consensus.ShouldRead(d, TimeSpan.FromMilliseconds(700)), "Una patente estable reduce la frecuencia de OCR");
        consensus.Apply(d, null, TimeSpan.FromMilliseconds(700));
        Check(!d.OcrFresh, "El overlay reutilizado no dispara una captura nueva");
        consensus.Apply(d, new PlateReading("AA001AB", .95f, .9f), TimeSpan.FromSeconds(2));
        Check(!d.PlateStable, "Una lectura contradictoria vuelve a requerir confirmación");
        consensus.Apply(d, new PlateReading("AA001AB", .99f, .2f), TimeSpan.FromSeconds(2.3));
        Check(!d.PlateStable, "Un carácter dudoso no queda oculto por una media alta");

        var temp = Path.Combine(Path.GetTempPath(), "iristrack-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var bitmap = new Bitmap(320, 200);
            using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.Silver);
            var captures = new PlateCaptureService();
            var at = new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Local);
            d = Plate();
            Check(await captures.SaveAsync(bitmap, d, temp, "demo", null, at, TimeSpan.FromSeconds(1), true, true, default), "Primera lectura estable guarda una captura");
            d.OcrConfidence = .98f;
            Check(!await captures.SaveAsync(bitmap, d, temp, "demo", null, at.AddSeconds(1), TimeSpan.FromSeconds(2), true, true, default), "Una mejora actualiza la misma aparición");
            Check(Directory.GetFiles(temp, "*.json", SearchOption.AllDirectories).Length == 1
                && Directory.GetFiles(temp, "*.jpg", SearchOption.AllDirectories).Length == 2, "No se duplican el registro ni las imágenes al mejorar");
            var record = JsonSerializer.Deserialize<CaptureRecord>(await File.ReadAllTextAsync(Directory.GetFiles(temp, "*.json", SearchOption.AllDirectories).Single()))!;
            Check(record.CapturedAt == at.AddSeconds(1) && record.PlateText == "AA001AA" && record.VideoPositionSeconds is null
                && record.AnalysisElapsedSeconds == 2, "Metadatos conservan hora real y no inventan posición del video");
            Check(!await captures.SaveAsync(bitmap, Plate(2), temp, "demo", null, at.AddSeconds(2), TimeSpan.FromSeconds(3), true, true, default), "Recuperar brevemente el seguimiento conserva la aparición");
            Check(await captures.SaveAsync(bitmap, Plate(3), temp, "demo", null, at.AddSeconds(10), TimeSpan.FromSeconds(11), true, true, default), "Una reaparición posterior tiene su propio registro");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await captures.SaveAsync(bitmap, Plate(4), temp, "demo", null, at, TimeSpan.FromSeconds(20), true, true, cancelled.Token); throw new Exception("No se canceló"); }
            catch (OperationCanceledException) { Check(true, "Cancelar no inicia un nuevo guardado"); }
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }

        if (args.Contains("--ui")) UiCheck();
        if (args.Contains("--models")) await ModelCheck();
        Console.WriteLine($"OK: {_count} comprobaciones.");
    }

    static void UiCheck()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent();
                var window = new MainWindow();
                var combo = (System.Windows.Controls.ComboBox)window.FindName("DetectionTargetCombo");
                foreach (var tag in new[] { "-1", "plates", "vehicles-plates", "0" })
                {
                    combo.SelectedItem = combo.Items.Cast<System.Windows.Controls.ComboBoxItem>().Single(i => (string)i.Tag == tag);
                    Check(combo.SelectedItem is not null, "La interfaz permite seleccionar " + tag);
                }
                var menu = new QuickEdgeMenuWindow();
                menu.SetState("plates", false, true, true, false, false);
                Check(menu.FindName("ItemPlates") is not null, "El menú del borde incluye Patentes");
                var overlay = new OverlayWindow();
                overlay.Draw(new[] { Plate() }, 640, 480);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    static async Task ModelCheck()
    {
        using var engine = new DetectionEngine();
        await engine.EnsureModeAsync(DetectionMode.Plates, new InlineProgress<string>(Console.WriteLine), default, cpuOnly: true);
        Check(engine.PlatesLoaded && !engine.GeneralLoaded, "Modo Patentes carga exclusivamente detector de patentes + OCR");
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync("https://raw.githubusercontent.com/ankandrew/fast-alpr/master/assets/test_image.png");
        using var stream = new MemoryStream(bytes);
        using var image = new Bitmap(stream);
        var watch = Stopwatch.StartNew();
        var before = engine.PlateInferenceCount;
        var detected = engine.Detect(image, .4f, null, Array.Empty<AnalysisZone>(), default);
        Check(detected.Count > 0 && detected.All(d => d.IsPlate), "Los modelos reales encuentran patentes en la imagen oficial");
        Check(engine.PlateInferenceCount == before + 1, "Una sola inferencia del detector por fotograma en modo exclusivo");
        var results = detected.Select(d => engine.ReadPlate(image, d)).ToArray();
        Check(results.Any(r => r.Text.Length >= 6 && r.Confidence >= .8f), "El OCR real produce una lectura utilizable");
        Console.WriteLine("Lecturas: " + string.Join(" | ", results.Select(r => $"{r.Text} {r.Confidence:P1}")));
        Console.WriteLine($"Demostración CPU, primera ejecución con imagen oficial: {watch.Elapsed.TotalMilliseconds:0} ms. No es un benchmark de la PC del usuario.");
        await engine.EnsureModeAsync(DetectionMode.General, null, default);
        Check(engine.GeneralLoaded && !engine.PlatesLoaded, "Al volver al modo general se descargan ambos modelos de patentes");
        await engine.EnsureModeAsync(DetectionMode.VehiclesAndPlates, null, default, cpuOnly: true);
        var combined = engine.Detect(image, .35f, null, Array.Empty<AnalysisZone>(), default);
        Check(engine.GeneralLoaded && engine.PlatesLoaded && combined.Any(d => d.IsPlate) && combined.Any(d => !d.IsPlate), "El modo combinado detecta vehículos y patentes dentro de sus recortes");
        await engine.EnsureModeAsync(DetectionMode.Plates, null, default, cpuOnly: true);
        Check(!engine.GeneralLoaded && engine.PlatesLoaded, "Cambiar de combinado a Patentes descarga el detector general");
    }
}
