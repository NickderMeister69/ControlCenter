using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace NickControlCenter;

/// <summary>
/// Zentrale Sensor-Quelle für die ganze App. EINE LibreHardwareMonitor-Instanz (nicht pro Modul eine –
/// das war vorher doppelte Last und konnte Werte springen lassen) plus optional HWiNFO.
/// Liest im Hintergrund-Thread und meldet neue Werte über <see cref="Updated"/> (Hintergrund-Thread!).
/// </summary>
public sealed class HardwareHub : IDisposable
{
    public static HardwareHub Instance { get; } = new();

    private readonly Computer _pc = new()
    {
        IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsMemoryEnabled = true,
        IsStorageEnabled = true, IsNetworkEnabled = true, IsControllerEnabled = false, IsPsuEnabled = false, IsBatteryEnabled = true
    };

    /// <summary>Schützt jeden Hardware-Zugriff (Lesen im Hub, Lüfter setzen im UI-Thread).</summary>
    public readonly object Lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly Dictionary<string, (double min, double max)> _minMax = new();

    public Computer Computer => _pc;
    public bool Opened { get; private set; }
    public string? OpenError { get; private set; }
    public int IntervalMs { get; set; } = 1000;
    public SourcePreference Preference { get; set; } = SourcePreference.Auto;

    public SensorSnapshot Latest { get; private set; } = new();
    public event Action<SensorSnapshot>? Updated;

    /// <summary>Ist HWiNFO mit aktivem Shared Memory gerade die Quelle?</summary>
    public bool HwinfoActive { get; private set; }
    public HwinfoSource.Status HwinfoStatus => HwinfoSource.LastStatus;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HardwareHub", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Open()
    {
        try
        {
            lock (Lock) _pc.Open();
            Opened = true;
            Log.Info(LogModule.Monitor, "Sensoren geöffnet: " + string.Join(", ", _pc.Hardware.Select(h => $"{h.HardwareType} \"{h.Name}\"")));
        }
        catch (Exception ex)
        {
            OpenError = ex.Message;
            Log.Error(LogModule.Monitor, "Sensoren konnten nicht geöffnet werden (fehlt der Treiber PawnIO?)", ex);
        }
    }

    private void Loop()
    {
        Open();
        var sw = Stopwatch.StartNew();
        HwinfoSource.Status lastHw = HwinfoSource.Status.NotRunning;
        while (_running)
        {
            var t0 = sw.ElapsedMilliseconds;
            try
            {
                UpdateNow(force: true);
                var snap = Build();
                Latest = snap;
                if (lastHw != HwinfoSource.LastStatus)
                {
                    Log.Info(LogModule.Monitor, "HWiNFO-Status: " + HwinfoText(HwinfoSource.LastStatus));
                    lastHw = HwinfoSource.LastStatus;
                }
                Updated?.Invoke(snap);
            }
            catch (Exception ex) { Log.Error(LogModule.Monitor, "Sensor-Durchlauf fehlgeschlagen", ex); }
            int wait = (int)Math.Max(100, IntervalMs - (sw.ElapsedMilliseconds - t0));
            Thread.Sleep(wait);
        }
    }

    public static string HwinfoText(HwinfoSource.Status s) => s switch
    {
        HwinfoSource.Status.Ok => "aktiv – Temperaturen kommen von HWiNFO",
        HwinfoSource.Status.Stale => "HWiNFO liefert keine neuen Werte (Shared Memory nach 12 h abgelaufen?) – nutze LibreHardwareMonitor",
        HwinfoSource.Status.Inactive => "HWiNFO läuft, aber „Shared Memory Support“ ist aus",
        _ => "HWiNFO läuft nicht – nutze LibreHardwareMonitor"
    };

    /// <summary>Sensoren aktualisieren (wenn force=false nur, falls älter als 400 ms).</summary>
    public void UpdateNow(bool force = false)
    {
        if (!Opened) return;
        if (!force && (DateTime.Now - _lastUpdate).TotalMilliseconds < 400) return;
        lock (Lock)
        {
            foreach (var hw in _pc.Hardware)
                foreach (var h in Flatten(hw))
                {
                    try { h.Update(); } catch { /* einzelne Sensoren dürfen ausfallen */ }
                }
            _lastUpdate = DateTime.Now;
        }
    }

    public static IEnumerable<IHardware> Flatten(IHardware hw)
    {
        yield return hw;
        foreach (var sub in hw.SubHardware)
            foreach (var x in Flatten(sub))
                yield return x;
    }

    public IEnumerable<IHardware> AllHardware()
    {
        foreach (var hw in _pc.Hardware) foreach (var h in Flatten(hw)) yield return h;
    }

    public ISensor? FindSensor(string id)
    {
        lock (Lock)
            foreach (var h in AllHardware())
                foreach (var s in h.Sensors)
                    if (s.Identifier.ToString() == id) return s;
        return null;
    }

    private static bool IsRealTemp(ISensor s) =>
        s.SensorType == SensorType.Temperature &&
        !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase) &&
        !s.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase) &&
        !s.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase);

    private static SensorKind Kind(SensorType t) => t switch
    {
        SensorType.Temperature => SensorKind.Temperature, SensorType.Load => SensorKind.Load, SensorType.Clock => SensorKind.Clock,
        SensorType.Power => SensorKind.Power, SensorType.Fan => SensorKind.Fan, SensorType.Control => SensorKind.Control,
        SensorType.Voltage => SensorKind.Voltage, SensorType.Data or SensorType.SmallData => SensorKind.Data,
        SensorType.Throughput => SensorKind.Throughput, _ => SensorKind.Other
    };

    private static string Unit(SensorType t) => t switch
    {
        SensorType.Temperature => "°C", SensorType.Load or SensorType.Control or SensorType.Level => "%", SensorType.Clock => "MHz",
        SensorType.Power => "W", SensorType.Fan => "U/min", SensorType.Voltage => "V", SensorType.Data => "GB", SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s", SensorType.Current => "A", SensorType.Frequency => "Hz", SensorType.Energy => "mWh", _ => ""
    };

    private static string GroupKind(HardwareType t) => t switch
    {
        HardwareType.Cpu => "Cpu", HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Gpu",
        HardwareType.Storage => "Storage", HardwareType.Memory => "Memory", HardwareType.Network => "Network", _ => "Board"
    };

    /// <summary>Werte aus LibreHardwareMonitor auswerten, danach – falls aktiv – HWiNFO drüberlegen.</summary>
    private SensorSnapshot Build()
    {
        var s = new SensorSnapshot { Time = DateTime.Now, TempSourceText = "LibreHardwareMonitor" };
        if (Opened)
        {
            lock (Lock)
            {
                foreach (var hw in _pc.Hardware)
                {
                    foreach (var h in Flatten(hw))
                        foreach (var x in h.Sensors)
                        {
                            if (x.Value is not float v || float.IsNaN(v)) continue;
                            var id = x.Identifier.ToString();
                            if (!_minMax.TryGetValue(id, out var mm)) mm = (v, v);
                            mm = (Math.Min(mm.min, v), Math.Max(mm.max, v));
                            _minMax[id] = mm;
                            s.All.Add(new SensorReading
                            {
                                Id = id, Group = h.Name, Name = x.Name, Kind = Kind(x.SensorType), Unit = Unit(x.SensorType),
                                Value = v, Min = mm.min, Max = mm.max, Source = SensorSource.Lhm, GroupKind = GroupKind(hw.HardwareType),
                                OriginalName = x.Name, OriginalGroup = h.Name
                            });
                        }
                }
                FillFromLhm(s);
            }
        }

        var mem = Native.Memory();
        s.RamLoad = mem.LoadPct; s.RamTotalGb = mem.TotalGb; s.RamUsedGb = mem.UsedGb;

        HwinfoActive = false;
        if (Preference != SourcePreference.LhmOnly)
        {
            var hw = HwinfoSource.Read();
            if (hw != null && hw.Count > 0)
            {
                var h = new SensorSnapshot();
                HwinfoSource.Fill(h, hw);
                // HWiNFO-Werte haben Vorrang, fehlende Werte bleiben von LHM
                Overlay(s, h);
                s.All.AddRange(hw);
                HwinfoActive = true;
            }
        }
        return s;
    }

    private static void Overlay(SensorSnapshot s, SensorSnapshot h)
    {
        static double Pick(double a, double b) => double.IsNaN(b) ? a : b;
        if (!string.IsNullOrEmpty(h.CpuName)) s.CpuName = h.CpuName;
        if (!string.IsNullOrEmpty(h.GpuName)) s.GpuName = h.GpuName;
        if (SensorSnapshot.Plausible(h.CpuTemp)) { s.CpuTemp = h.CpuTemp; s.CpuTempLabel = h.CpuTempLabel; }
        s.CpuPackage = Pick(s.CpuPackage, h.CpuPackage);
        s.CpuLoad = Pick(s.CpuLoad, h.CpuLoad);
        s.CpuClock = Pick(s.CpuClock, h.CpuClock);
        s.CpuPower = Pick(s.CpuPower, h.CpuPower);
        if (SensorSnapshot.Plausible(h.GpuTemp)) s.GpuTemp = h.GpuTemp;
        s.GpuHotSpot = Pick(s.GpuHotSpot, h.GpuHotSpot);
        s.GpuLoad = Pick(s.GpuLoad, h.GpuLoad);
        s.GpuClock = Pick(s.GpuClock, h.GpuClock);
        s.GpuMemClock = Pick(s.GpuMemClock, h.GpuMemClock);
        s.GpuPower = Pick(s.GpuPower, h.GpuPower);
        s.GpuFanRpm = Pick(s.GpuFanRpm, h.GpuFanRpm);
        s.GpuVramUsedGb = Pick(s.GpuVramUsedGb, h.GpuVramUsedGb);
        s.BoardTemp = Pick(s.BoardTemp, h.BoardTemp);
        if (h.Fans.Count > 0) { s.Fans.Clear(); s.Fans.AddRange(h.Fans); }
        if (h.DriveTemps.Count > 0) { s.DriveTemps.Clear(); s.DriveTemps.AddRange(h.DriveTemps); }
        s.FromHwinfo = true;
        s.TempSourceText = "HWiNFO";
    }

    private void FillFromLhm(SensorSnapshot s)
    {
        float? Val(IHardware h, SensorType t, params string[] names)
        {
            foreach (var n in names)
            {
                var m = h.Sensors.FirstOrDefault(x => x.SensorType == t && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && x.Value.HasValue);
                if (m != null) return m.Value;
            }
            foreach (var n in names)
            {
                var m = h.Sensors.FirstOrDefault(x => x.SensorType == t && x.Name.Contains(n, StringComparison.OrdinalIgnoreCase) && x.Value.HasValue);
                if (m != null) return m.Value;
            }
            return null;
        }
        static double D(float? f) => f ?? double.NaN;

        var cpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu != null)
        {
            s.CpuName = cpu.Name;
            var (t, lbl) = SensorSnapshot.PickCpuTemp(cpu.Sensors.Where(IsRealTemp).Where(x => x.Value.HasValue).Select(x => (x.Name, (double)x.Value!.Value)));
            s.CpuTemp = t; s.CpuTempLabel = lbl;
            s.CpuPackage = D(Val(cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Package"));
            s.CpuLoad = D(Val(cpu, SensorType.Load, "CPU Total"));
            var clocks = cpu.Sensors.Where(x => x.SensorType == SensorType.Clock && x.Value is > 0 &&
                                               (x.Name.StartsWith("P-Core #") || x.Name.StartsWith("Core #") || x.Name.StartsWith("CPU Core #")))
                                    .Select(x => (double)x.Value!.Value).ToList();
            if (clocks.Count > 0) s.CpuClock = clocks.Max();
            s.CpuPower = D(Val(cpu, SensorType.Power, "CPU Package", "Package"));
        }

        var gpus = _pc.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                              .OrderBy(h => h.HardwareType == HardwareType.GpuIntel ? 1 : 0).ToList();
        var gpu = gpus.FirstOrDefault();
        if (gpu != null)
        {
            s.GpuName = gpu.Name;
            s.GpuTemp = D(Val(gpu, SensorType.Temperature, "GPU Core", "GPU Temperature", "GPU"));
            s.GpuHotSpot = D(Val(gpu, SensorType.Temperature, "GPU Hot Spot", "GPU Hotspot", "Hot Spot"));
            s.GpuLoad = D(Val(gpu, SensorType.Load, "GPU Core", "D3D 3D", "GPU"));
            s.GpuClock = D(Val(gpu, SensorType.Clock, "GPU Core", "GPU Shader"));
            s.GpuMemClock = D(Val(gpu, SensorType.Clock, "GPU Memory"));
            s.GpuPower = D(Val(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core", "GPU"));
            s.GpuFanRpm = D(Val(gpu, SensorType.Fan, "GPU Fan", "GPU"));
            var used = Val(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            var total = Val(gpu, SensorType.SmallData, "GPU Memory Total");
            if (used.HasValue) s.GpuVramUsedGb = used.Value / 1024.0;
            if (total.HasValue) s.GpuVramTotalGb = total.Value / 1024.0;
        }

        var board = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        if (board != null)
        {
            foreach (var h in Flatten(board))
            {
                foreach (var f in h.Sensors.Where(x => x.SensorType == SensorType.Fan && x.Value.HasValue))
                    s.Fans.Add((f.Name, f.Value!.Value));
                if (double.IsNaN(s.BoardTemp))
                {
                    var t = h.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value is > 5 and < 110 &&
                                                          (x.Name.Contains("System") || x.Name.Contains("Motherboard") || x.Name.Contains("Temperature #1")));
                    if (t != null) s.BoardTemp = t.Value!.Value;
                }
            }
        }
        if (gpu != null && !double.IsNaN(s.GpuFanRpm)) s.Fans.Add(("GPU-Lüfter", s.GpuFanRpm));

        foreach (var st in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            var t = st.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value is > 0 and < 120);
            if (t != null) s.DriveTemps.Add((st.Name, t.Value!.Value));
        }

        double down = 0, up = 0;
        foreach (var n in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Network))
        {
            down += Val(n, SensorType.Throughput, "Download Speed") ?? 0;
            up += Val(n, SensorType.Throughput, "Upload Speed") ?? 0;
        }
        s.NetDownBps = down; s.NetUpBps = up;
    }

    public void ResetMinMax() { lock (Lock) _minMax.Clear(); }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(1500); } catch { }
        try { lock (Lock) _pc.Close(); } catch { }
    }
}
