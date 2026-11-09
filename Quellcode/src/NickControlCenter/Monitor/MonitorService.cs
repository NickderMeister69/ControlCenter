using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace NickControlCenter;

public sealed class MonitorSettings
{
    public List<string> Tiles { get; set; } = new() { "cpu.temp", "gpu.temp", "cpu.load", "gpu.load", "ram.load", "cpu.power", "gpu.power", "gpu.vram", "cpu.clock", "gpu.clock", "net.down", "net.up" };
    public int HistorySeconds { get; set; } = 120;
    public bool Overlay { get; set; }
    public int OverlayCorner { get; set; } = 0; // 0 oben links, 1 oben rechts, 2 unten links, 3 unten rechts
    public double OverlayOpacity { get; set; } = 0.85;
    public double OverlayFontSize { get; set; } = 14;
    public List<string> OverlayItems { get; set; } = new() { "cpu.temp", "cpu.load", "gpu.temp", "gpu.load", "ram.load" };
    public bool OverlayHotkey { get; set; } = true;
    /// <summary>Werte nebeneinander (von links nach rechts) statt untereinander.</summary>
    public bool OverlayHorizontal { get; set; } = true;
    public string OverlayFont { get; set; } = "Bahnschrift";
    public string OverlayTextColor { get; set; } = "#FFFFFF";
    public string OverlayLabelColor { get; set; } = "#96A0AF";
    public string OverlayBgColor { get; set; } = "#080B0F";
    public double OverlayBgOpacity { get; set; } = 0.8;
    /// <summary>Werte bei Warnung/kritisch gelb/rot färben.</summary>
    public bool OverlayAlertColors { get; set; } = true;
    public bool OverlayBorder { get; set; } = true;
    /// <summary>Einmalig: FPS vorne ins Overlay aufnehmen.</summary>
    public bool OverlayFpsAdded { get; set; }
    public double CpuAlarm { get; set; } = 90;
    public double GpuAlarm { get; set; } = 85;
    public bool Alarms { get; set; } = true;
    public int GraphStyle { get; set; } = 0; // 0 Linie, 1 Fläche
}

/// <summary>Ein Messwert im Monitor (Kachel, Overlay, Diagramm).</summary>
public sealed class Metric : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Group { get; init; }
    public string Unit { get; init; } = "";
    public double Max { get; init; } = 100;
    public double Warn { get; init; } = double.NaN;
    public double Crit { get; init; } = double.NaN;
    public bool IsTemp { get; init; }
    public bool IsRate { get; init; }
    public double[] History = new double[600];
    public int Count;
    public int Head;
    public double Min = double.NaN, Peak = double.NaN, Sum;
    public int Samples;

    private double _value = double.NaN;
    public double Value { get => _value; private set => _value = value; }
    public string Text { get; private set; } = "–";
    public string MinMaxText { get; private set; } = "";
    public Brush Brush { get; private set; } = Palette.Text;
    public double Percent { get { var v = double.IsNaN(_shown.Current) ? _value : _shown.Current; return double.IsNaN(v) ? 0 : Math.Clamp(v / Max * 100, 0, 100); } }
    public int Version;

    public void Push(double v, bool fahrenheit)
    {
        Value = v;
        History[Head] = v;
        Head = (Head + 1) % History.Length;
        Count = Math.Min(Count + 1, History.Length);
        if (!double.IsNaN(v))
        {
            Min = double.IsNaN(Min) ? v : Math.Min(Min, v);
            Peak = double.IsNaN(Peak) ? v : Math.Max(Peak, v);
            Sum += v; Samples++;
        }
        // angezeigte Zahl gleitet weich zum neuen Wert (siehe Smooth) – Diagramme/Alarme nutzen den echten Wert
        _fahr = fahrenheit;
        bool snap = double.IsNaN(_shown.Current) || double.IsNaN(v);
        _shown.Set(v);
        if (snap) Text = Format(v, fahrenheit);
        MinMaxText = double.IsNaN(Min) ? "" : $"min {Format(Min, fahrenheit)} · Ø {Format(Sum / Math.Max(1, Samples), fahrenheit)} · max {Format(Peak, fahrenheit)}";
        Brush = double.IsNaN(v) ? Palette.Muted : !double.IsNaN(Crit) && v >= Crit ? Palette.Bad : !double.IsNaN(Warn) && v >= Warn ? Palette.Warn : Palette.Text;
        Version++;
        RaiseAll(nameof(Value), nameof(Text), nameof(MinMaxText), nameof(Brush), nameof(Percent));
    }

    private readonly SmoothNumber _shown = new();
    private bool _fahr;
    /// <summary>Der gerade angezeigte (weich laufende) Wert.</summary>
    public double ShownValue => _shown.Current;

    public Metric() { Smooth.Frame += OnFrame; }

    private void OnFrame()
    {
        if (!_shown.Step()) return;
        Text = Format(_shown.Current, _fahr);
        Raise(nameof(Text));
        Raise(nameof(ShownValue));
        Raise(nameof(Percent));
    }

    public string Format(double v, bool fahrenheit)
    {
        if (double.IsNaN(v)) return "–";
        if (IsTemp) return Fmt.Temp(v, fahrenheit);
        if (IsRate) return Fmt.Rate(v);
        return Unit switch
        {
            "%" => $"{v:0} %", "MHz" => v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz", "W" => $"{v:0} W",
            "GB" => $"{v:0.0} GB", "U/min" => $"{v:0} U/min", "FPS" => $"{v:0} FPS", "ms" => $"{v:0.0} ms", _ => $"{v:0.#} {Unit}"
        };
    }

    /// <summary>Die letzten n Werte (älteste zuerst).</summary>
    public double[] Last(int n)
    {
        n = Math.Min(n, Count);
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = History[(Head - n + i + History.Length) % History.Length];
        return r;
    }

    public void ResetStats() { Min = Peak = double.NaN; Sum = 0; Samples = 0; }
}

/// <summary>Sammelt die Werte des Sensor-Hubs für Monitor, Overlay, Dashboard und Kacheln; Aufzeichnung als CSV; Temperatur-Alarme.</summary>
public sealed class MonitorService : ObservableObject, IDisposable
{
    public MonitorSettings Settings { get; } = JsonStore.Load<MonitorSettings>("monitor.json");
    public Dictionary<string, Metric> Metrics { get; } = new();
    public ObservableCollection<Metric> TileMetrics { get; } = new();
    public ObservableCollection<SensorReading> AllSensors { get; } = new();
    public event Action? Updated;
    private StreamWriter? _csv;
    private DateTime _lastAlarm = DateTime.MinValue;
    private int _hotTicks;
    private OverlayWindow? _overlay;

    public MonitorService()
    {
        void Add(string key, string label, string group, string unit, double max, double warn = double.NaN, double crit = double.NaN, bool temp = false, bool rate = false) =>
            Metrics[key] = new Metric { Key = key, Label = label, Group = group, Unit = unit, Max = max, Warn = warn, Crit = crit, IsTemp = temp, IsRate = rate };
        Add("fps", "FPS (Spiel im Vordergrund)", "Spiel", "FPS", 360);
        Add("fps.low", "1 % Low-FPS", "Spiel", "FPS", 360);
        Add("frametime", "Frametime", "Spiel", "ms", 50, 25, 40);
        Add("cpu.temp", "CPU-Temperatur", "CPU", "°C", 100, 80, 92, temp: true);
        Add("cpu.package", "CPU-Package", "CPU", "°C", 100, 85, 95, temp: true);
        Add("cpu.load", "CPU-Auslastung", "CPU", "%", 100);
        Add("cpu.clock", "CPU-Takt", "CPU", "MHz", 6000);
        Add("cpu.power", "CPU-Leistung", "CPU", "W", 250);
        Add("gpu.temp", "GPU-Temperatur", "Grafikkarte", "°C", 100, 78, 88, temp: true);
        Add("gpu.hotspot", "GPU-Hotspot", "Grafikkarte", "°C", 110, 90, 100, temp: true);
        Add("gpu.load", "GPU-Auslastung", "Grafikkarte", "%", 100);
        Add("gpu.clock", "GPU-Takt", "Grafikkarte", "MHz", 3200);
        Add("gpu.mem", "GPU-Speichertakt", "Grafikkarte", "MHz", 12000);
        Add("gpu.power", "GPU-Leistung", "Grafikkarte", "W", 450);
        Add("gpu.fan", "GPU-Lüfter", "Grafikkarte", "U/min", 3500);
        Add("gpu.vram", "Grafikspeicher belegt", "Grafikkarte", "GB", 24);
        Add("ram.load", "RAM-Auslastung", "Arbeitsspeicher", "%", 100, 85, 95);
        Add("ram.used", "RAM belegt", "Arbeitsspeicher", "GB", 64);
        Add("board.temp", "Mainboard", "Mainboard", "°C", 80, 60, 70, temp: true);
        Add("net.down", "Download", "Netzwerk", "B/s", 12_500_000, rate: true);
        Add("net.up", "Upload", "Netzwerk", "B/s", 12_500_000, rate: true);
        if (!Settings.OverlayFpsAdded)
        {
            Settings.OverlayFpsAdded = true;
            if (!Settings.OverlayItems.Contains("fps")) Settings.OverlayItems.Insert(0, "fps");
            Save();
        }
        RebuildTiles();
        HardwareHub.Instance.Updated += OnHub;
        if (Settings.Overlay) Application.Current.Dispatcher.BeginInvoke(() => SetOverlay(true));
    }

    public void Save() => JsonStore.Save("monitor.json", Settings);

    public void RebuildTiles()
    {
        TileMetrics.Clear();
        foreach (var k in Settings.Tiles)
            if (Metrics.TryGetValue(k, out var m)) TileMetrics.Add(m);
            else if (k.StartsWith("sensor:")) TileMetrics.Add(CustomMetric(k));
    }

    /// <summary>Beliebigen Sensor (aus der Liste "Alle Sensoren") als Kachel.</summary>
    private Metric CustomMetric(string key)
    {
        if (Metrics.TryGetValue(key, out var m)) return m;
        var id = key["sensor:".Length..];
        var r = HardwareHub.Instance.Latest.All.FirstOrDefault(x => x.Id == id);
        m = new Metric
        {
            Key = key, Label = r?.Name ?? id, Group = r?.Group ?? "Sensor", Unit = r?.Unit ?? "",
            IsTemp = r?.Kind == SensorKind.Temperature, Max = r?.Kind switch { SensorKind.Temperature => 100, SensorKind.Load or SensorKind.Control => 100, SensorKind.Fan => 3000, _ => Math.Max(1, (r?.Max ?? 100) * 1.2) },
            Warn = r?.Kind == SensorKind.Temperature ? 80 : double.NaN
        };
        Metrics[key] = m;
        return m;
    }

    public void AddTile(string key)
    {
        if (Settings.Tiles.Contains(key)) return;
        Settings.Tiles.Add(key);
        Save();
        RebuildTiles();
    }

    public void RemoveTile(string key)
    {
        Settings.Tiles.Remove(key);
        Save();
        RebuildTiles();
    }

    public void MoveTile(string key, int delta)
    {
        int i = Settings.Tiles.IndexOf(key), j = i + delta;
        if (i < 0 || j < 0 || j >= Settings.Tiles.Count) return;
        (Settings.Tiles[i], Settings.Tiles[j]) = (Settings.Tiles[j], Settings.Tiles[i]);
        Save();
        RebuildTiles();
    }

    private void OnHub(SensorSnapshot s) => Application.Current?.Dispatcher.BeginInvoke(() => Apply(s));

    private void Apply(SensorSnapshot s)
    {
        bool f = AppSettings.Current.Fahrenheit;
        void P(string k, double v) { if (Metrics.TryGetValue(k, out var m)) m.Push(v, f); }
        P("cpu.temp", s.CpuTemp); P("cpu.package", s.CpuPackage); P("cpu.load", s.CpuLoad); P("cpu.clock", s.CpuClock); P("cpu.power", s.CpuPower);
        P("gpu.temp", s.GpuTemp); P("gpu.hotspot", s.GpuHotSpot); P("gpu.load", s.GpuLoad); P("gpu.clock", s.GpuClock); P("gpu.mem", s.GpuMemClock);
        P("gpu.power", s.GpuPower); P("gpu.fan", s.GpuFanRpm); P("gpu.vram", s.GpuVramUsedGb); P("ram.load", s.RamLoad); P("ram.used", s.RamUsedGb);
        P("board.temp", s.BoardTemp); P("net.down", s.NetDownBps); P("net.up", s.NetUpBps);
        if (_fpsOn)
        {
            var fr = FpsCounter.Instance.Foreground();
            P("fps", fr.Fps); P("fps.low", fr.Low1); P("frametime", fr.FrameMs);
        }
        foreach (var m in Metrics.Values.Where(m => m.Key.StartsWith("sensor:")))
        {
            var id = m.Key["sensor:".Length..];
            var r = s.All.FirstOrDefault(x => x.Id == id);
            m.Push(r?.Value ?? double.NaN, f);
        }
        _latestAll = s.All;
        Record(s);
        CheckAlarms(s);
        Updated?.Invoke();
    }

    private List<SensorReading> _latestAll = new();
    public List<SensorReading> LatestAll => _latestAll;

    public void ResetStats()
    {
        foreach (var m in Metrics.Values) m.ResetStats();
        HardwareHub.Instance.ResetMinMax();
        Log.Info(LogModule.Monitor, "Min/Max zurückgesetzt");
    }

    // ---------------------------------------------------------------- Alarme
    private void CheckAlarms(SensorSnapshot s)
    {
        if (!Settings.Alarms) return;
        bool hot = (SensorSnapshot.Plausible(s.CpuTemp) && s.CpuTemp >= Settings.CpuAlarm) || (SensorSnapshot.Plausible(s.GpuTemp) && s.GpuTemp >= Settings.GpuAlarm);
        _hotTicks = hot ? _hotTicks + 1 : 0;
        if (_hotTicks == 5 && (DateTime.Now - _lastAlarm).TotalMinutes > 5)
        {
            _lastAlarm = DateTime.Now;
            var msg = $"CPU {s.CpuTemp:0} °C · GPU {s.GpuTemp:0} °C – über deiner Alarmgrenze.";
            Log.Warn(LogModule.Monitor, "Temperatur-Alarm: " + msg);
            App.Instance.Balloon("Temperatur-Alarm", msg, true);
        }
    }

    // ---------------------------------------------------------------- Aufzeichnung
    public bool Recording => _csv != null;
    public string RecordFile { get; private set; } = "";

    public void ToggleRecording()
    {
        if (_csv != null)
        {
            _csv.Dispose(); _csv = null;
            Log.Ok(LogModule.Monitor, "Aufzeichnung gespeichert: " + RecordFile);
            Ui.Toast("Aufzeichnung gespeichert: " + Path.GetFileName(RecordFile));
        }
        else
        {
            RecordFile = Path.Combine(Log.Folder, $"Monitor-Aufzeichnung_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            _csv = new StreamWriter(RecordFile, false, new UTF8Encoding(true)) { AutoFlush = true };
            _csv.WriteLine("Zeit;" + string.Join(";", Metrics.Values.Where(m => !m.Key.StartsWith("sensor:")).Select(m => $"{m.Label} ({m.Unit})")));
            Log.Info(LogModule.Monitor, "Aufzeichnung gestartet: " + RecordFile);
        }
        RaiseAll(nameof(Recording), nameof(RecordFile));
    }

    private void Record(SensorSnapshot s)
    {
        if (_csv == null) return;
        try
        {
            var vals = Metrics.Values.Where(m => !m.Key.StartsWith("sensor:"))
                              .Select(m => double.IsNaN(m.Value) ? "" : m.Value.ToString("0.##", CultureInfo.GetCultureInfo("de-DE")));
            _csv.WriteLine($"{s.Time:HH:mm:ss};" + string.Join(";", vals));
        }
        catch { }
    }

    // ---------------------------------------------------------------- Overlay
    public bool OverlayOn => _overlay != null;

    private bool _fpsOn;
    /// <summary>FPS-Messung nur, solange sie gebraucht wird (Overlay an oder Monitor-Seite offen).</summary>
    public void UseFps(bool on)
    {
        if (on == _fpsOn) return;
        _fpsOn = on;
        if (on) FpsCounter.Instance.Acquire(); else FpsCounter.Instance.Release();
    }

    public void SetOverlay(bool on)
    {
        if (on && _overlay == null) { _overlay = new OverlayWindow(this); _overlay.Show(); UseFps(true); }
        else if (!on && _overlay != null) { _overlay.Close(); _overlay = null; UseFps(false); }
        Settings.Overlay = on;
        Save();
        Raise(nameof(OverlayOn));
    }

    public void RefreshOverlay() => _overlay?.Rebuild();

    public void Dispose()
    {
        HardwareHub.Instance.Updated -= OnHub;
        _csv?.Dispose();
        _overlay?.Close();
        try { FpsCounter.Instance.Dispose(); } catch { }
    }
}
