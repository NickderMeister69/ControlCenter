namespace NickControlCenter;

/// <summary>Erkennt die NVIDIA-Grafikkarte, berechnet sichere Grenzen und setzt Werte über NVML.</summary>
public sealed class NvidiaTuner : IGpuTuner
{
    enum OffsetApi { None, Legacy, New }

    IntPtr _dev;
    OffsetApi _api = OffsetApi.None;
    readonly object _lock = new();

    public bool Available { get; private set; }
    public string Name { get; private set; } = "Keine NVIDIA-Grafikkarte";
    public string Vendor => "NVIDIA";
    public bool VoltageSupported => false;
    public int VoltMin => 0;
    public int VoltMax => 0;
    public string Driver { get; private set; } = "";
    public string Vram { get; private set; } = "";
    public string Info { get; private set; } = "";
    public string Family { get; private set; } = "";
    public bool Laptop { get; private set; }
    public bool HighPower { get; private set; }

    // Grenzen (bereits auf "sicher" begrenzt)
    public bool PowerAdjustable { get; private set; }
    public int PowerMinPct { get; private set; } = 100;
    public int PowerMaxPct { get; private set; } = 100;
    public int PowerDefaultW { get; private set; }
    public bool OffsetsSupported { get; private set; }
    public int CoreMin { get; private set; }
    public int CoreMax { get; private set; }
    public int MemMin { get; private set; }
    public int MemMax { get; private set; }
    public int UndervoltOffset { get; private set; }
    public bool ClockLockSupported { get; private set; } = true;
    public int CapMin { get; private set; }
    public int CapMax { get; private set; }
    public int BoostClock { get; private set; }
    public int TypicalBoost { get; private set; }
    public int LowestClock { get; private set; } = 210;
    public int SlowdownTemp { get; private set; } = 90;
    public int SafetyTemp => Math.Min(87, SlowdownTemp - 3);

    // Live-Werte
    public float Temp = -1, ClockGr = -1, ClockMem = -1, PowerW = -1, Util = -1, Fan = -1;
    public string Throttle = "";
    public bool Lost;

    public NvidiaTuner()
    {
        try { Init(); }
        catch (Exception ex) { Available = false; Info = "NVIDIA-Schnittstelle nicht verfügbar (" + ex.GetType().Name + ")."; }
    }

    void Init()
    {
        if (Nvml.Call(Nvml.nvmlInit_v2) != Nvml.SUCCESS) { Info = "Keine NVIDIA-Grafikkarte bzw. kein NVIDIA-Treiber gefunden."; return; }
        if (Nvml.Call(() => Nvml.nvmlDeviceGetCount_v2(out uint n) is var r && r == 0 && n > 0 ? 0 : 1) != 0) { Info = "Keine NVIDIA-Grafikkarte gefunden."; return; }
        Nvml.nvmlDeviceGetHandleByIndex_v2(0, out _dev);
        Available = true;

        var b = new byte[96];
        if (Nvml.nvmlDeviceGetName(_dev, b, 96) == 0) Name = Nvml.Str(b);
        var d = new byte[80];
        if (Nvml.Call(() => Nvml.nvmlSystemGetDriverVersion(d, 80)) == 0) Driver = Nvml.Str(d);
        if (Nvml.Call(() => Nvml.nvmlDeviceGetMemoryInfo(_dev, out var m) is var r && r == 0 ? Store(m) : r) == 0) { }
        Laptop = Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) || Name.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);

        // --- Sichere Spannen je nach Generation (bewusst vorsichtig gewählt) ---
        string n = Name.ToUpperInvariant();
        int core, mem, uv;
        if (n.Contains("RTX 50")) { Family = "RTX 50 (Blackwell)"; core = 200; mem = 1000; uv = 150; }
        else if (n.Contains("RTX 40")) { Family = "RTX 40 (Ada Lovelace)"; core = 200; mem = 1000; uv = 150; }
        else if (n.Contains("RTX 30")) { Family = "RTX 30 (Ampere)"; core = 150; mem = 800; uv = 120; }
        else if (n.Contains("RTX 20") || n.Contains("GTX 16")) { Family = "RTX 20 / GTX 16 (Turing)"; core = 100; mem = 500; uv = 100; }
        else { Family = "ältere / unbekannte Generation"; core = 75; mem = 300; uv = 75; }
        if (Laptop) { core /= 2; mem /= 2; uv = uv * 2 / 3; }

        // --- Leistungslimit ---
        if (Nvml.Call(() => Nvml.nvmlDeviceGetPowerManagementDefaultLimit(_dev, out uint def) is var r && r == 0 ? SetDef(def) : r) == 0 &&
            Nvml.Call(() => Nvml.nvmlDeviceGetPowerManagementLimitConstraints(_dev, out uint mn, out uint mx) is var r && r == 0 ? SetPow(mn, mx) : r) == 0)
            PowerAdjustable = PowerMaxPct > PowerMinPct;

        // --- Takt-Offsets: erst alte, dann neue NVML-Funktion probieren ---
        int cMin = 0, cMax = 0, mMin = 0, mMax = 0;
        if (Nvml.Call(() => Nvml.nvmlDeviceGetGpcClkVfOffset(_dev, out _)) == 0 &&
            Nvml.Call(() => Nvml.nvmlDeviceGetGpcClkMinMaxVfOffset(_dev, out cMin, out cMax)) == 0)
        {
            _api = OffsetApi.Legacy;
            if (Nvml.Call(() => Nvml.nvmlDeviceGetMemClkMinMaxVfOffset(_dev, out mMin, out mMax)) != 0) { mMin = 0; mMax = 0; }
        }
        else
        {
            var g = new Nvml.ClockOffset { Version = Nvml.ClockOffsetV1, Type = Nvml.CLOCK_GRAPHICS, PState = 0 };
            if (Nvml.Call(() => Nvml.nvmlDeviceGetClockOffsets(_dev, ref g)) == 0)
            {
                _api = OffsetApi.New; cMin = g.MinOffsetMHz; cMax = g.MaxOffsetMHz;
                var m = new Nvml.ClockOffset { Version = Nvml.ClockOffsetV1, Type = Nvml.CLOCK_MEM, PState = 0 };
                if (Nvml.Call(() => Nvml.nvmlDeviceGetClockOffsets(_dev, ref m)) == 0) { mMin = m.MinOffsetMHz; mMax = m.MaxOffsetMHz; }
            }
        }
        if (_api != OffsetApi.None && cMax > 0)
        {
            OffsetsSupported = true;
            CoreMax = Math.Min(core, cMax); CoreMin = Math.Max(-300, cMin);
            MemMax = Math.Max(0, Math.Min(mem, mMax)); MemMin = Math.Max(-500, Math.Min(0, mMin));
            UndervoltOffset = Math.Min(uv, CoreMax);
        }

        // --- Takt-Obergrenze ---
        uint maxGr = 0, boost = 0;
        Nvml.Call(() => Nvml.nvmlDeviceGetMaxClockInfo(_dev, Nvml.CLOCK_GRAPHICS, out maxGr));
        if (Nvml.Call(() => Nvml.nvmlDeviceGetMaxCustomerBoostClock(_dev, Nvml.CLOCK_GRAPHICS, out boost)) != 0 || boost == 0)
            boost = (uint)(maxGr * 0.8);
        BoostClock = (int)boost;
        // Typischer realer Boost liegt zwischen Hersteller-Boost und Maximaltakt
        TypicalBoost = maxGr > boost ? Round15(boost + (maxGr - boost) * 0.35) : Round15(boost);
        LowestClock = FindLowestClock();
        CapMax = (int)Math.Max(maxGr, boost);
        CapMin = Math.Max(LowestClock + 100, (int)(boost * 0.6) / 15 * 15);

        if (Nvml.Call(() => Nvml.nvmlDeviceGetTemperatureThreshold(_dev, Nvml.THRESH_SLOWDOWN, out uint t) is var r && r == 0 && t > 60 ? SetSlow(t) : 1) != 0)
            SlowdownTemp = 90;

        Info = $"{Family}" + (Laptop ? " · Laptop-GPU" : "") +
               (PowerAdjustable ? $" · Leistungslimit {PowerMinPct}–{PowerMaxPct} %" : " · Leistungslimit fest") +
               (OffsetsSupported ? $" · Takt-Offset bis +{CoreMax} MHz" : " · Takt-Offset vom Treiber gesperrt") +
               (HighPower ? " · Limit-Erhöhung gesperrt (Stromstecker-Schutz)" : "");
    }

    int Store(Nvml.MemInfo m) { Vram = $"{m.Total / 1024.0 / 1024 / 1024:0} GB"; return 0; }
    int SetDef(uint def) { PowerDefaultW = (int)(def / 1000); return 0; }
    int SetSlow(uint t) { SlowdownTemp = (int)t; return 0; }
    int SetPow(uint mn, uint mx)
    {
        if (PowerDefaultW <= 0) return 1;
        double d = PowerDefaultW * 1000.0;
        PowerMinPct = Math.Max(50, (int)Math.Ceiling(mn / d * 100));
        PowerMaxPct = Math.Max(PowerMinPct, (int)Math.Floor(mx / d * 100));
        // Sehr stromhungrige Karten (z. B. RTX 4090/5090 mit 12V-2x6-Stecker): Limit nie über Werkswert anheben
        if (PowerDefaultW >= 400) { HighPower = PowerMaxPct > 100; PowerMaxPct = Math.Min(PowerMaxPct, 100); }
        return 0;
    }

    int FindLowestClock()
    {
        try
        {
            uint cnt = 0;
            Nvml.Call(() => Nvml.nvmlDeviceGetSupportedMemoryClocks(_dev, ref cnt, null));
            if (cnt == 0 || cnt > 512) return 210;
            var mems = new uint[cnt];
            if (Nvml.Call(() => Nvml.nvmlDeviceGetSupportedMemoryClocks(_dev, ref cnt, mems)) != 0) return 210;
            uint mem = mems.Take((int)cnt).Max();
            uint gc = 0;
            Nvml.Call(() => Nvml.nvmlDeviceGetSupportedGraphicsClocks(_dev, mem, ref gc, null));
            if (gc == 0 || gc > 2048) return 210;
            var gr = new uint[gc];
            if (Nvml.Call(() => Nvml.nvmlDeviceGetSupportedGraphicsClocks(_dev, mem, ref gc, gr)) != 0) return 210;
            return (int)gr.Take((int)gc).Min();
        }
        catch { return 210; }
    }

    /// <summary>Begrenzt Werte auf die sicheren Spannen dieser Karte.</summary>
    public GpuSettings Clamp(GpuSettings s)
    {
        var c = s.Clone();
        c.PowerPct = PowerAdjustable ? Math.Clamp(c.PowerPct, PowerMinPct, PowerMaxPct) : 100;
        c.CoreOffset = OffsetsSupported ? Math.Clamp(c.CoreOffset, CoreMin, CoreMax) : 0;
        c.MemOffset = OffsetsSupported ? Math.Clamp(c.MemOffset, MemMin, MemMax) : 0;
        if (c.ClockCap != 0) c.ClockCap = ClockLockSupported ? Math.Clamp(c.ClockCap, CapMin, CapMax) : 0;
        return c;
    }

    // ---------- Vorschläge (auf diese Karte zugeschnitten) ----------
    public GpuSettings PresetUndervolt() => Clamp(new GpuSettings
    {
        PowerPct = 100, CoreOffset = OffsetsSupported ? UndervoltOffset : 0, MemOffset = 0,
        ClockCap = OffsetsSupported ? TypicalBoost : Round15(TypicalBoost * 0.93)
    });
    // Leichtes OC bewusst vorsichtig (40 %); Maximales OC = volles Limit (stürzt ein Spiel ab, senkt der Absturzschutz automatisch)
    public GpuSettings PresetLightOc() => Clamp(new GpuSettings
    { PowerPct = PowerMaxPct, CoreOffset = Round15(CoreMax * 0.4), MemOffset = (int)(MemMax * 0.4) / 50 * 50 });
    public GpuSettings PresetMaxOc() => Clamp(new GpuSettings
    { PowerPct = PowerMaxPct, CoreOffset = CoreMax, MemOffset = MemMax });
    static int Round15(double v) => (int)(v / 15) * 15;

    // ---------- Anwenden / Zurücksetzen ----------
    public List<string> Apply(GpuSettings s)
    {
        var msg = new List<string>();
        if (!Available) return msg;
        s = Clamp(s);
        lock (_lock)
        {
            if (PowerAdjustable)
            {
                uint mw = (uint)(PowerDefaultW * 1000L * s.PowerPct / 100);
                Check(Nvml.Call(() => Nvml.nvmlDeviceSetPowerManagementLimit(_dev, mw)), "Leistungslimit", msg);
            }
            if (OffsetsSupported)
            {
                int r1 = SetOffset(Nvml.CLOCK_GRAPHICS, s.CoreOffset);
                int r2 = MemMax > 0 || MemMin < 0 ? SetOffset(Nvml.CLOCK_MEM, s.MemOffset) : 0;
                if (r1 == Nvml.NOT_SUPPORTED)
                {
                    OffsetsSupported = false;
                    msg.Add("Takt-Offset wird von deinem Treiber unter Windows nicht freigegeben – Leistungslimit und Takt-Obergrenze funktionieren trotzdem.");
                }
                else { Check(r1, "Kern-Takt-Offset", msg); Check(r2, "Speicher-Takt-Offset", msg); }
            }
            if (ClockLockSupported)
            {
                int r = s.ClockCap > 0
                    ? Nvml.Call(() => Nvml.nvmlDeviceSetGpuLockedClocks(_dev, (uint)LowestClock, (uint)s.ClockCap))
                    : Nvml.Call(() => Nvml.nvmlDeviceResetGpuLockedClocks(_dev));
                if (r == Nvml.NOT_SUPPORTED && s.ClockCap > 0) { ClockLockSupported = false; msg.Add("Takt-Obergrenze wird von dieser Karte nicht unterstützt."); }
                else if (r != Nvml.NOT_SUPPORTED) Check(r, "Takt-Obergrenze", msg);
            }
        }
        return msg;
    }

    int SetOffset(int type, int mhz)
    {
        if (_api == OffsetApi.Legacy)
            return type == Nvml.CLOCK_GRAPHICS
                ? Nvml.Call(() => Nvml.nvmlDeviceSetGpcClkVfOffset(_dev, mhz))
                : Nvml.Call(() => Nvml.nvmlDeviceSetMemClkVfOffset(_dev, mhz));
        var o = new Nvml.ClockOffset { Version = Nvml.ClockOffsetV1, Type = type, PState = 0, OffsetMHz = mhz };
        return Nvml.Call(() => Nvml.nvmlDeviceSetClockOffsets(_dev, ref o));
    }

    static void Check(int r, string what, List<string> msg)
    {
        if (r == Nvml.SUCCESS) return;
        msg.Add(r == Nvml.NO_PERMISSION ? $"{what}: Keine Berechtigung – App bitte als Administrator starten."
                                        : $"{what}: {Nvml.Err(r)}");
    }

    public List<string> Reset() => Apply(new GpuSettings());

    /// <summary>Liest die aktuell aktiven Werte zurück (Takt-Obergrenze lässt sich nicht auslesen).</summary>
    public GpuSettings ReadCurrent()
    {
        var s = new GpuSettings();
        if (!Available) return s;
        lock (_lock)
        {
            uint lim = 0;
            if (PowerDefaultW > 0 && Nvml.Call(() => Nvml.nvmlDeviceGetPowerManagementLimit(_dev, out lim)) == 0)
                s.PowerPct = (int)Math.Round(lim / (PowerDefaultW * 10.0));
            if (_api == OffsetApi.Legacy)
            {
                if (Nvml.Call(() => Nvml.nvmlDeviceGetGpcClkVfOffset(_dev, out int c) is var r && r == 0 ? Put(s, c, true) : r) != 0) { }
                if (Nvml.Call(() => Nvml.nvmlDeviceGetMemClkVfOffset(_dev, out int m) is var r && r == 0 ? Put(s, m, false) : r) != 0) { }
            }
            else if (_api == OffsetApi.New)
            {
                var g = new Nvml.ClockOffset { Version = Nvml.ClockOffsetV1, Type = Nvml.CLOCK_GRAPHICS, PState = 0 };
                if (Nvml.Call(() => Nvml.nvmlDeviceGetClockOffsets(_dev, ref g)) == 0) s.CoreOffset = g.OffsetMHz;
                var m = new Nvml.ClockOffset { Version = Nvml.ClockOffsetV1, Type = Nvml.CLOCK_MEM, PState = 0 };
                if (Nvml.Call(() => Nvml.nvmlDeviceGetClockOffsets(_dev, ref m)) == 0) s.MemOffset = m.OffsetMHz;
            }
        }
        return s;
    }
    static int Put(GpuSettings s, int v, bool core) { if (core) s.CoreOffset = v; else s.MemOffset = v; return 0; }

    // ---------- Live-Werte ----------
    public void Poll()
    {
        if (!Available) return;
        lock (_lock)
        {
            int r = Nvml.Call(() => Nvml.nvmlDeviceGetTemperature(_dev, Nvml.TEMP_GPU, out uint t) is var x && x == 0 ? SetF(ref Temp, t) : x);
            if (r == Nvml.GPU_IS_LOST) { Lost = true; return; }
            Nvml.Call(() => Nvml.nvmlDeviceGetClockInfo(_dev, Nvml.CLOCK_GRAPHICS, out uint c) is var x && x == 0 ? SetF(ref ClockGr, c) : x);
            Nvml.Call(() => Nvml.nvmlDeviceGetClockInfo(_dev, Nvml.CLOCK_MEM, out uint c) is var x && x == 0 ? SetF(ref ClockMem, c) : x);
            Nvml.Call(() => Nvml.nvmlDeviceGetPowerUsage(_dev, out uint p) is var x && x == 0 ? SetF(ref PowerW, p / 1000f) : x);
            Nvml.Call(() => Nvml.nvmlDeviceGetUtilizationRates(_dev, out var u) is var x && x == 0 ? SetF(ref Util, u.Gpu) : x);
            Nvml.Call(() => Nvml.nvmlDeviceGetFanSpeed(_dev, out uint f) is var x && x == 0 ? SetF(ref Fan, f) : x);
            ulong reasons = 0;
            if (Nvml.Call(() => Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(_dev, out reasons)) == 0) Throttle = Reasons(reasons);
        }
    }
    static int SetF(ref float field, float v) { field = v; return 0; }

    static string Reasons(ulong r)
    {
        var l = new List<string>();
        if ((r & 0x4) != 0) l.Add("Leistungslimit");
        if ((r & 0x20) != 0 || (r & 0x40) != 0) l.Add("Temperatur");
        if ((r & 0x8) != 0) l.Add("Hardware");
        if ((r & 0x2) != 0) l.Add("Taktgrenze");
        return string.Join(", ", l);
    }

    public void Dispose() { try { if (Available) Nvml.nvmlShutdown(); } catch { } }
}
