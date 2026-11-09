using System.Runtime.InteropServices;
using System.Text;

namespace NickControlCenter;

public sealed class GpuSettings
{
    public int PowerPct { get; set; } = 100;   // Leistungslimit in % vom Werkswert
    public int CoreOffset { get; set; }        // MHz (NVIDIA: Offset, AMD: Änderung der Max-Taktfrequenz)
    public int MemOffset { get; set; }         // MHz
    public int ClockCap { get; set; }          // MHz, 0 = kein Limit
    public int VoltOffset { get; set; }        // mV (nur AMD RDNA3+, nur negativ = Undervolt)
    public GpuSettings Clone() => (GpuSettings)MemberwiseClone();
    public bool IsDefault => PowerPct == 100 && CoreOffset == 0 && MemOffset == 0 && ClockCap == 0 && VoltOffset == 0;
    public override string ToString() =>
        IsDefault ? "Werkseinstellung" :
        $"Limit {PowerPct} %, Kern {CoreOffset:+0;-0;0} MHz, Speicher {MemOffset:+0;-0;0} MHz" + (ClockCap > 0 ? $", max. {ClockCap} MHz" : "") + (VoltOffset != 0 ? $", Spannung {VoltOffset} mV" : "");
}

/// <summary>Gemeinsame Schnittstelle für NVIDIA- und AMD-Grafikkarten im Tweaker.</summary>
public interface IGpuTuner : IDisposable
{
    bool Available { get; }
    string Name { get; }
    string Vendor { get; }
    string Info { get; }
    bool PowerAdjustable { get; }
    int PowerMinPct { get; }
    int PowerMaxPct { get; }
    bool OffsetsSupported { get; }
    int CoreMin { get; }
    int CoreMax { get; }
    int MemMin { get; }
    int MemMax { get; }
    bool ClockLockSupported { get; }
    int CapMin { get; }
    int CapMax { get; }
    bool VoltageSupported { get; }
    int VoltMin { get; }
    int VoltMax { get; }
    int SafetyTemp { get; }
    GpuSettings Clamp(GpuSettings s);
    List<string> Apply(GpuSettings s);
    List<string> Reset();
    GpuSettings ReadCurrent();
    GpuSettings PresetUndervolt();
    GpuSettings PresetLightOc();
    GpuSettings PresetMaxOc();
}

/// <summary>
/// AMD-Radeon-Grafikkarten (RX 5000 / 6000 / 7000 / 9000) über die offizielle AMD-Schnittstelle ADL "Overdrive 8"
/// (atiadlxx.dll, kommt mit jedem Adrenalin-Treiber). Leistungslimit, maximaler GPU-/Speichertakt und –
/// bei RX 7000/9000 – Spannungs-Offset (Undervolt). Grenzen kommen vom Treiber und werden zusätzlich vorsichtig begrenzt.
/// </summary>
public sealed class AmdTuner : IGpuTuner
{
    private const int GFXCLK_FMAX = 0, UCLK_FMAX = 8, POWER_PERCENTAGE = 9, OD_VOLTAGE = 37;
    private const int CAP_GFXCLK_LIMITS = 1 << 0, CAP_UCLK_MAX = 1 << 2, CAP_POWER_LIMIT = 1 << 3, CAP_ODVOLTAGE = 1 << 17;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr MallocCb(int size);
    private static readonly MallocCb Malloc = size => Marshal.AllocCoTaskMem(size);

    [DllImport("atiadlxx.dll")] private static extern int ADL2_Main_Control_Create(MallocCb cb, int enumConnected, out IntPtr ctx);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Main_Control_Destroy(IntPtr ctx);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int n);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr ctx, IntPtr info, int size);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_Active_Get(IntPtr ctx, int idx, out int active);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive_Caps(IntPtr ctx, int idx, out int supported, out int enabled, out int version);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Init_SettingX2_Get(IntPtr ctx, int idx, out int caps, out int num, out IntPtr list);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Current_SettingX2_Get(IntPtr ctx, int idx, out int num, out IntPtr list);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Setting_Set(IntPtr ctx, int idx, IntPtr set, IntPtr current);

    private const int AdapterInfoSize = 1572; // Windows-Layout laut ADL-SDK (siehe Test)
    private IntPtr _ctx;
    private int _idx = -1;
    private int _caps;
    private int _count;
    private readonly Dictionary<int, (int min, int max, int def)> _init = new();
    private readonly object _lock = new();

    public bool Available { get; private set; }
    public string Name { get; private set; } = "Keine AMD-Grafikkarte";
    public string Vendor => "AMD";
    public string Info { get; private set; } = "";
    public bool PowerAdjustable { get; private set; }
    public int PowerMinPct { get; private set; } = 100;
    public int PowerMaxPct { get; private set; } = 100;
    public bool OffsetsSupported { get; private set; }
    public int CoreMin { get; private set; }
    public int CoreMax { get; private set; }
    public int MemMin { get; private set; }
    public int MemMax { get; private set; }
    public bool ClockLockSupported => false;
    public int CapMin => 0;
    public int CapMax => 0;
    public bool VoltageSupported { get; private set; }
    public int VoltMin { get; private set; }
    public int VoltMax { get; private set; }
    public int SafetyTemp => 95;
    private int _defGfx, _defMem, _defPow;

    public AmdTuner()
    {
        try { Init(); }
        catch (DllNotFoundException) { Info = "Kein AMD-Treiber (atiadlxx.dll) gefunden."; }
        catch (EntryPointNotFoundException) { Info = "AMD-Treiber zu alt für Overdrive 8 – bitte Adrenalin aktualisieren."; }
        catch (Exception ex) { Info = "AMD-Schnittstelle nicht verfügbar: " + ex.Message; Log.Warn(LogModule.Tweaker, "AMD ADL: " + ex); }
    }

    private void Init()
    {
        if (ADL2_Main_Control_Create(Malloc, 1, out _ctx) != 0) { Info = "AMD-Treiber antwortet nicht."; return; }
        ADL2_Adapter_NumberOfAdapters_Get(_ctx, out int n);
        if (n <= 0) { Info = "Keine AMD-Grafikkarte gefunden."; return; }
        var buf = Marshal.AllocHGlobal(AdapterInfoSize * n);
        try
        {
            for (int i = 0; i < n; i++) Marshal.WriteInt32(buf, i * AdapterInfoSize, AdapterInfoSize);
            if (ADL2_Adapter_AdapterInfo_Get(_ctx, buf, AdapterInfoSize * n) != 0) { Info = "AMD-Grafikkarte nicht lesbar."; return; }
            for (int i = 0; i < n; i++)
            {
                IntPtr p = buf + i * AdapterInfoSize;
                int adapterIndex = Marshal.ReadInt32(p, 4);
                int vendor = Marshal.ReadInt32(p, 8 + 256 + 12);
                if (vendor != 0x1002) continue;
                if (ADL2_Adapter_Active_Get(_ctx, adapterIndex, out int act) != 0 || act == 0) continue;
                if (ADL2_Overdrive_Caps(_ctx, adapterIndex, out int sup, out _, out int ver) != 0 || sup == 0 || ver < 8) continue;
                _idx = adapterIndex;
                Name = Marshal.PtrToStringAnsi(p + 8 + 256 + 16) ?? "AMD Radeon";
                break;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        if (_idx < 0) { Info = "AMD-Grafikkarte gefunden, aber ohne Overdrive 8 (vor RX 5000) – Tuning hier nicht möglich."; return; }

        if (ADL2_Overdrive8_Init_SettingX2_Get(_ctx, _idx, out _caps, out _count, out var list) != 0 || _count <= 0) { Info = "Overdrive-Werte nicht lesbar."; return; }
        try
        {
            for (int i = 0; i < _count; i++)
            {
                IntPtr e = list + i * 16;
                int id = Marshal.ReadInt32(e, 0), min = Marshal.ReadInt32(e, 4), max = Marshal.ReadInt32(e, 8), def = Marshal.ReadInt32(e, 12);
                _init[id] = (min, max, def);
            }
        }
        finally { Marshal.FreeCoTaskMem(list); }
        Available = true;

        if ((_caps & CAP_POWER_LIMIT) != 0 && _init.TryGetValue(POWER_PERCENTAGE, out var pw) && pw.max > pw.min)
        {
            // ADL: Prozent als Abweichung (z. B. -10 … +15) → in "Prozent vom Werkswert" umrechnen
            PowerAdjustable = true;
            _defPow = pw.def;
            PowerMinPct = 100 + Math.Max(pw.min - pw.def, -30);
            PowerMaxPct = 100 + Math.Min(pw.max - pw.def, 15);
        }
        if ((_caps & CAP_GFXCLK_LIMITS) != 0 && _init.TryGetValue(GFXCLK_FMAX, out var g) && g.def > 0)
        {
            _defGfx = CurrentOrDefault(GFXCLK_FMAX, g.def);
            OffsetsSupported = true;
            CoreMin = -Math.Min(500, _defGfx - g.min);
            CoreMax = Math.Min(Math.Max(0, g.max - _defGfx), (int)(_defGfx * 0.05)); // höchstens +5 %
        }
        if ((_caps & CAP_UCLK_MAX) != 0 && _init.TryGetValue(UCLK_FMAX, out var m) && m.def > 0)
        {
            _defMem = m.def;
            MemMin = 0;
            MemMax = Math.Min(Math.Max(0, m.max - m.def), (int)(m.def * 0.04));
        }
        if ((_caps & CAP_ODVOLTAGE) != 0 && _init.TryGetValue(OD_VOLTAGE, out var v))
        {
            VoltageSupported = true;
            VoltMin = Math.Max(v.min, -100); // nur vorsichtiges Undervolting
            VoltMax = 0;
        }
        Info = $"Overdrive 8" + (PowerAdjustable ? $" · Leistungslimit {PowerMinPct}–{PowerMaxPct} %" : "") +
               (OffsetsSupported ? $" · Max-Takt {_defGfx} MHz ({CoreMin:+0;-0} … +{CoreMax} MHz)" : "") +
               (VoltageSupported ? $" · Undervolt bis {VoltMin} mV" : "");
        Log.Info(LogModule.Tweaker, $"AMD: {Name} – {Info} – Funktionen 0x{_caps:X}, {_count} Werte");
    }

    private int[]? ReadCurrentRaw()
    {
        if (ADL2_Overdrive8_Current_SettingX2_Get(_ctx, _idx, out int n, out var list) != 0 || n <= 0) return null;
        try
        {
            var r = new int[n];
            Marshal.Copy(list, r, 0, n);
            return r;
        }
        finally { Marshal.FreeCoTaskMem(list); }
    }

    private int CurrentOrDefault(int id, int def)
    {
        var cur = ReadCurrentRaw();
        return cur != null && id < cur.Length && cur[id] > 0 ? cur[id] : def;
    }

    public GpuSettings Clamp(GpuSettings s)
    {
        var c = s.Clone();
        c.PowerPct = PowerAdjustable ? Math.Clamp(c.PowerPct, PowerMinPct, PowerMaxPct) : 100;
        c.CoreOffset = OffsetsSupported ? Math.Clamp(c.CoreOffset, CoreMin, CoreMax) : 0;
        c.MemOffset = MemMax > 0 ? Math.Clamp(c.MemOffset, MemMin, MemMax) : 0;
        c.ClockCap = 0;
        c.VoltOffset = VoltageSupported ? Math.Clamp(c.VoltOffset, VoltMin, VoltMax) : 0;
        return c;
    }

    public GpuSettings PresetUndervolt() => Clamp(new GpuSettings { PowerPct = 100, VoltOffset = VoltageSupported ? Math.Max(VoltMin, -50) : 0, CoreOffset = VoltageSupported ? 0 : Math.Max(CoreMin, -(int)(_defGfx * 0.05)) });
    public GpuSettings PresetLightOc() => Clamp(new GpuSettings { PowerPct = PowerMaxPct, CoreOffset = CoreMax / 2, MemOffset = MemMax / 2 });
    public GpuSettings PresetMaxOc() => Clamp(new GpuSettings { PowerPct = PowerMaxPct, CoreOffset = CoreMax, MemOffset = MemMax });

    public List<string> Apply(GpuSettings s)
    {
        var msg = new List<string>();
        if (!Available) return msg;
        s = Clamp(s);
        lock (_lock)
        {
            var cur = ReadCurrentRaw();
            if (cur == null) { msg.Add("AMD: aktuelle Werte nicht lesbar."); return msg; }
            var want = new Dictionary<int, int>();
            if (PowerAdjustable) want[POWER_PERCENTAGE] = _defPow + s.PowerPct - 100;
            if (OffsetsSupported) want[GFXCLK_FMAX] = _defGfx + s.CoreOffset;
            if (MemMax > 0) want[UCLK_FMAX] = _defMem + s.MemOffset;
            if (VoltageSupported) want[OD_VOLTAGE] = s.VoltOffset;
            int r = Write(cur, want, reset: false);
            if (r != 0) msg.Add($"AMD-Treiber hat die Werte abgelehnt (Code {r}). In Adrenalin unter „Leistung → Optimierung“ auf „Manuell“/„Standard“ stellen und erneut versuchen.");
        }
        return msg;
    }

    public List<string> Reset()
    {
        var msg = new List<string>();
        if (!Available) return msg;
        lock (_lock)
        {
            var cur = ReadCurrentRaw();
            if (cur == null) return msg;
            var want = new Dictionary<int, int>();
            foreach (var (id, cap) in new[] { (POWER_PERCENTAGE, CAP_POWER_LIMIT), (GFXCLK_FMAX, CAP_GFXCLK_LIMITS), (UCLK_FMAX, CAP_UCLK_MAX), (OD_VOLTAGE, CAP_ODVOLTAGE) })
                if ((_caps & cap) != 0 && _init.TryGetValue(id, out var v)) want[id] = v.def;
            int r = Write(cur, want, reset: true);
            if (r != 0) msg.Add($"AMD-Werkseinstellung: Treiber meldet Code {r}.");
        }
        return msg;
    }

    private int Write(int[] cur, Dictionary<int, int> want, bool reset)
    {
        int n = Math.Max(_count, cur.Length);
        // ADLOD8SetSetting: int count + n × (value, requested, reset); ADLOD8CurrentSetting: int count + n × int
        var set = Marshal.AllocHGlobal(4 + n * 12);
        var curBuf = Marshal.AllocHGlobal(4 + n * 4);
        try
        {
            Marshal.WriteInt32(set, 0, n);
            for (int i = 0; i < n; i++)
            {
                bool req = want.TryGetValue(i, out var v);
                Marshal.WriteInt32(set, 4 + i * 12, req ? v : (i < cur.Length ? cur[i] : 0));
                Marshal.WriteInt32(set, 4 + i * 12 + 4, req ? 1 : 0);
                Marshal.WriteInt32(set, 4 + i * 12 + 8, req && reset ? 1 : 0);
            }
            Marshal.WriteInt32(curBuf, 0, n);
            return ADL2_Overdrive8_Setting_Set(_ctx, _idx, set, curBuf);
        }
        finally { Marshal.FreeHGlobal(set); Marshal.FreeHGlobal(curBuf); }
    }

    public GpuSettings ReadCurrent()
    {
        var s = new GpuSettings();
        if (!Available) return s;
        var cur = ReadCurrentRaw();
        if (cur == null) return s;
        if (PowerAdjustable && POWER_PERCENTAGE < cur.Length) s.PowerPct = 100 + cur[POWER_PERCENTAGE] - _defPow;
        if (OffsetsSupported && GFXCLK_FMAX < cur.Length && cur[GFXCLK_FMAX] > 0 && _init.TryGetValue(GFXCLK_FMAX, out var g)) s.CoreOffset = cur[GFXCLK_FMAX] - g.def;
        if (MemMax > 0 && UCLK_FMAX < cur.Length && cur[UCLK_FMAX] > 0) s.MemOffset = cur[UCLK_FMAX] - _defMem;
        if (VoltageSupported && OD_VOLTAGE < cur.Length) s.VoltOffset = cur[OD_VOLTAGE];
        return s;
    }

    public void Dispose()
    {
        try { if (_ctx != IntPtr.Zero) ADL2_Main_Control_Destroy(_ctx); } catch { }
        _ctx = IntPtr.Zero;
    }
}

/// <summary>Platzhalter, wenn keine einstellbare Grafikkarte da ist (z. B. Intel Arc / iGPU).</summary>
public sealed class NoGpuTuner : IGpuTuner
{
    public NoGpuTuner(string name, string info) { Name = name; Info = info; }
    public bool Available => false;
    public string Name { get; }
    public string Vendor => "";
    public string Info { get; }
    public bool PowerAdjustable => false; public int PowerMinPct => 100; public int PowerMaxPct => 100;
    public bool OffsetsSupported => false; public int CoreMin => 0; public int CoreMax => 0; public int MemMin => 0; public int MemMax => 0;
    public bool ClockLockSupported => false; public int CapMin => 0; public int CapMax => 0;
    public bool VoltageSupported => false; public int VoltMin => 0; public int VoltMax => 0;
    public int SafetyTemp => 90;
    public GpuSettings Clamp(GpuSettings s) => new();
    public List<string> Apply(GpuSettings s) => new();
    public List<string> Reset() => new();
    public GpuSettings ReadCurrent() => new();
    public GpuSettings PresetUndervolt() => new();
    public GpuSettings PresetLightOc() => new();
    public GpuSettings PresetMaxOc() => new();
    public void Dispose() { }
}
