using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace NickControlCenter;

/// <summary>Ein Anschluss im Effekt-Thread (unveränderlich bis auf Laufzeitwerte).</summary>
public sealed class EngineZone
{
    public required RgbZone Target { get; init; }
    public bool Enabled { get; init; }
    public int LedCount { get; init; }
    /// <summary>Effekt dieses Anschlusses (eigener oder der gemeinsame).</summary>
    public required EffectSnap Effect { get; init; }
    public bool FlickerGuard { get; init; }
    /// <summary>Zuletzt berechnete Farben (für die Live-Vorschau).</summary>
    public volatile RgbF[] Current = Array.Empty<RgbF>();
    public RgbF[]? Smoothed;
    public byte[]? LastSent;
    public DateTime LastSendTime;
}

public sealed class EngineState
{
    public bool Enabled { get; init; }
    public double Brightness { get; init; } = 1;
    public EffectSnap Global { get; init; } = new();
    public EngineZone[] Zones { get; init; } = Array.Empty<EngineZone>();
    public bool NightMode { get; init; }
    public int NightFrom { get; init; } = 23;
    public int NightTo { get; init; } = 7;
    public int KeepAliveSeconds { get; init; }
}

/// <summary>Was die Oberfläche über eine gefundene Zone wissen muss.</summary>
public sealed record ZoneDescriptor(RgbZone Zone, string Key, string DeviceName, string Vendor, string ZoneName, string Kind,
                                    int LedCount, bool Adjustable, int MaxLeds, bool PerLed, int LedsPerFan, int DefaultFans);

/// <summary>Laufzeit-Statistik je Gerät (für Warnungen und Fehlersuche).</summary>
public sealed class DeviceStats
{
    public string Name = "";
    public int Sends, Errors, ConsecutiveErrors;
    public string LastError = "";
    public DateTime LastErrorTime;
}

/// <summary>
/// Effekt-Thread: findet die RGB-Hardware (eingebaute Treiber, kein Zusatzprogramm nötig),
/// berechnet ~30× pro Sekunde alle Farben und schickt sie an die Geräte.
///
/// Gegen Flackern:
///  • Anschlüsse bekommen nur Daten, wenn sich die Farbe wirklich ändert (kein Dauerfeuer)
///  • Einzelfarben-Anschlüsse (12 V RGB, Mainboard-LEDs) höchstens 4× pro Sekunde, mit Flacker-Schutz 2×
///  • Gigabyte: "Übernehmen" nur noch bei echten Effektänderungen (das war die Hauptursache)
///  • Fehler werden pro Gerät gezählt und als Warnung angezeigt
/// </summary>
public sealed class RgbEngine : IDisposable
{
    private const int PreviewLeds = 24;

    /// <summary>Gamma 2.2 – LEDs sind linear, sonst wirken Farben blass/weißlich.</summary>
    private static readonly byte[] Gamma = Enumerable.Range(0, 256)
        .Select(i => (byte)Math.Round(255 * Math.Pow(i / 255.0, 2.2))).ToArray();

    private readonly Thread _thread;
    private readonly SynchronizationContext _ui;
    private readonly ScreenSampler _screen = new();
    private readonly ConcurrentQueue<Action> _actions = new();
    private List<RgbDevice> _devices = new();
    private readonly Dictionary<RgbDevice, int> _errors = new();
    private volatile EngineState _state = new();
    private volatile bool _running = true;
    private RgbF[]? _globalSmoothed;
    private (RgbZone zone, DateTime until)? _identify;

    public volatile RgbF[] GlobalPreview = new RgbF[PreviewLeds];
    public ConcurrentDictionary<string, DeviceStats> Stats { get; } = new();

    public ScreenColors Screen => _screen.Colors;
    public bool ScreenActive => _screen.Active;
    public bool NightActive { get; private set; }

    public event Action<List<ZoneDescriptor>, List<DetectedHardware>>? DevicesChanged;
    public event Action<string, bool>? StatusChanged;
    /// <summary>Ein Gerät meldet wiederholt Fehler (Name, Meldung) – im UI-Thread.</summary>
    public event Action<string, string>? DeviceProblem;

    public string HardwareReportText { get; private set; } = "";

    public RgbEngine(SynchronizationContext ui)
    {
        _ui = ui;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RgbEngine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        Rescan();
    }

    public void SetState(EngineState s) => _state = s;
    public void Rescan() => _actions.Enqueue(DoScan);

    /// <summary>Lässt einen Anschluss 3 s weiß blinken (zum Finden, welcher Lüfter welcher ist).</summary>
    public void Identify(RgbZone zone) => _actions.Enqueue(() => _identify = (zone, DateTime.Now.AddSeconds(3)));

    public void SetLedCount(RgbZone zone, int count) => _actions.Enqueue(() =>
    {
        zone.LedCount = Math.Clamp(count, 0, zone.MaxLeds);
        try { zone.Device.ApplyLedCount(zone); } catch (Exception ex) { Log.Warn(LogModule.Rgb, "LED-Anzahl setzen fehlgeschlagen: " + ex.Message); }
    });

    private void DoScan()
    {
        foreach (var d in _devices) { try { d.Dispose(); } catch { } }
        _devices = new List<RgbDevice>();
        _errors.Clear();
        Stats.Clear();
        _state = new EngineState { Enabled = _state.Enabled, Brightness = _state.Brightness, Global = _state.Global };

        Post(() => StatusChanged?.Invoke("Suche Geräte …", true));
        var (devices, report, all) = RgbScanner.Scan();
        _devices = devices;
        foreach (var d in devices) Stats[d.Key] = new DeviceStats { Name = d.Name };
        HardwareReportText = RgbScanner.HardwareReport(all, report);
        try { File.WriteAllText(Path.Combine(Log.Folder, "RGB-Hardware.txt"), HardwareReportText); } catch { }

        var zones = devices.SelectMany(d => d.Zones.Select(z =>
            new ZoneDescriptor(z, z.Key, d.Name, d.Vendor, z.Name, z.Kind, z.LedCount, z.Adjustable, z.MaxLeds, z.PerLed,
                               z.LedsPerFan, z.DefaultFans))).ToList();
        Post(() => DevicesChanged?.Invoke(zones, report));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        int ok = _devices.Count(d => !_errors.TryGetValue(d, out var e) || e < 5);
        string text = _devices.Count == 0
            ? "Keine unterstützten RGB-Geräte gefunden"
            : $"{ok} Gerät{(ok == 1 ? "" : "e")} · {_devices.Sum(d => d.Zones.Count)} Anschlüsse" + (ok < _devices.Count ? $" · {_devices.Count - ok} mit Fehlern" : "");
        Post(() => StatusChanged?.Invoke(text, _devices.Count > 0 && ok == _devices.Count));
    }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    // ------------------------------------------------------------------ Hauptschleife

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        while (_running)
        {
            var frameStart = sw.Elapsed;
            while (_actions.TryDequeue(out var a))
            {
                try { a(); } catch (Exception ex) { Log.Error(LogModule.Rgb, "RGB-Aktion fehlgeschlagen", ex); }
            }

            var st = _state;
            double t = sw.Elapsed.TotalSeconds;
            NightActive = st.NightMode && RgbConfig.IsNight(st.NightFrom, st.NightTo, DateTime.Now.Hour);

            _screen.Active = st.Enabled && !NightActive && (st.Global.Type == LightEffect.Screen || st.Zones.Any(z => z.Effect.Type == LightEffect.Screen));
            var snap = HardwareHub.Instance.Latest;
            var input = new EffectInputs { Screen = _screen.Active ? _screen.Colors : null, CpuTemp = snap.CpuTemp, GpuTemp = snap.GpuTemp };

            // Vorschau des gemeinsamen Effekts (auch ohne Geräte)
            var prev = new RgbF[PreviewLeds];
            for (int i = 0; i < PreviewLeds; i++) prev[i] = EffectMath.Eval(st.Global, t, i / (double)PreviewLeds, input);
            if (st.Global.Type == LightEffect.Screen) prev = SmoothArr(ref _globalSmoothed, prev, st.Global.Smooth);
            GlobalPreview = prev;

            // Farben je Anschluss
            var ident = _identify;
            if (ident != null && DateTime.Now > ident.Value.until) { _identify = null; ident = null; }
            foreach (var z in st.Zones)
            {
                int n = Math.Max(1, z.LedCount);
                var arr = new RgbF[n];
                if (ident != null && ident.Value.zone == z.Target)
                {
                    bool on = ((int)(t * 4)) % 2 == 0;
                    for (int i = 0; i < n; i++) arr[i] = on ? RgbF.White : RgbF.Black;
                }
                else
                {
                    for (int i = 0; i < n; i++) arr[i] = EffectMath.Eval(z.Effect, t, n == 1 ? 0.5 : i / (double)n, input);
                    if (z.Effect.Type == LightEffect.Screen) arr = SmoothArr(ref z.Smoothed, arr, z.Effect.Smooth);
                }
                z.Current = arr;
            }

            if (st.Enabled || NightActive) SendToDevices(st, ident?.zone);
            var elapsed = (sw.Elapsed - frameStart).TotalMilliseconds;
            Thread.Sleep((int)Math.Max(5, 33 - elapsed));
        }
    }

    private void SendToDevices(EngineState st, RgbZone? identZone)
    {
        var now = DateTime.Now;
        double bright = NightActive ? 0 : st.Brightness;
        foreach (var dev in _devices)
        {
            if (_errors.TryGetValue(dev, out var errs) && errs >= 5)
            {
                // Gerät hat mehrfach nicht reagiert → 15 s Pause, dann neuer Versuch
                if ((now - dev.LastUpdate).TotalSeconds < 15) continue;
                _errors[dev] = 3;
            }
            if ((now - dev.LastUpdate).TotalMilliseconds < dev.MinIntervalMs) continue;

            bool any = false;
            var stats = Stats.GetOrAdd(dev.Key, _ => new DeviceStats { Name = dev.Name });
            try
            {
                foreach (var z in st.Zones)
                {
                    if (z.Target.Device != dev || !z.Enabled) continue;
                    int leds = Math.Max(1, z.Target.LedCount);
                    var cur = z.Current;
                    var buf = new byte[leds * 3];
                    for (int i = 0; i < leds && cur.Length > 0; i++)
                    {
                        var c = cur[Math.Min(i, cur.Length - 1)].Scale(z.Target == identZone ? 1 : bright);
                        int o = i * 3;
                        buf[o] = Gamma[c.R8]; buf[o + 1] = Gamma[c.G8]; buf[o + 2] = Gamma[c.B8];
                    }

                    bool changed = z.LastSent == null || !buf.AsSpan().SequenceEqual(z.LastSent);
                    int keep = Math.Max(dev.KeepAliveSeconds > 0 ? (int)dev.KeepAliveSeconds : 0, st.KeepAliveSeconds);
                    bool keepAlive = keep > 0 && (now - z.LastSendTime).TotalSeconds > keep;
                    bool single = !z.Target.PerLed || leds == 1;

                    if (changed && !keepAlive && z.LastSent != null)
                    {
                        // Einzelfarben-Anschlüsse setzen bei jedem Befehl ihren Effekt neu → selten senden.
                        double minMs = single ? (z.FlickerGuard ? 500 : 250) : (z.FlickerGuard ? 100 : 0);
                        int minDiff = single ? (z.FlickerGuard ? 10 : 6) : (z.FlickerGuard ? 4 : 0);
                        if ((now - z.LastSendTime).TotalMilliseconds < minMs) changed = false;
                        else if (minDiff > 0)
                        {
                            int diff = 0;
                            for (int k = 0; k < buf.Length && k < z.LastSent.Length; k++) diff = Math.Max(diff, Math.Abs(buf[k] - z.LastSent[k]));
                            if (diff < minDiff) changed = false;
                        }
                    }
                    if (changed || keepAlive)
                    {
                        dev.SetZone(z.Target, buf);
                        z.LastSent = buf;
                        z.LastSendTime = now;
                        any = true;
                        stats.Sends++;
                    }
                }
                if (any) dev.Commit();
                dev.LastUpdate = now;
                stats.ConsecutiveErrors = 0;
                if (_errors.Remove(dev)) UpdateStatus();
            }
            catch (Exception ex)
            {
                int n = _errors.TryGetValue(dev, out var e) ? e + 1 : 1;
                _errors[dev] = n;
                stats.Errors++; stats.ConsecutiveErrors++;
                stats.LastError = ex.Message; stats.LastErrorTime = now;
                dev.LastUpdate = now.AddSeconds(n);
                if (n == 1 || n == 5) Log.Warn(LogModule.Rgb, $"{dev.Name}: Fehler beim Senden ({n}×): {ex.Message}");
                if (n == 5)
                {
                    UpdateStatus();
                    var msg = ex.Message;
                    Post(() => DeviceProblem?.Invoke(dev.Name, msg));
                }
            }
        }
    }

    private static RgbF[] SmoothArr(ref RgbF[]? state, RgbF[] next, double smooth)
    {
        if (state == null || state.Length != next.Length) { state = next; return next; }
        var res = new RgbF[next.Length];
        double a = 1 - smooth;
        for (int i = 0; i < next.Length; i++) res[i] = RgbF.Lerp(state[i], next[i], a);
        state = res;
        return res;
    }

    public void Dispose()
    {
        _running = false;
        try { _thread.Join(800); } catch { }
        _screen.Dispose();
        foreach (var d in _devices) { try { d.Dispose(); } catch { } }
    }
}
