using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace NickControlCenter;

public sealed class CpuSettings
{
    public bool UsePlan { get; set; }          // false = normaler Windows-Energieplan
    public int Boost { get; set; } = 1;        // PERFBOOSTMODE: 0 aus, 1 normal, 2 aggressiv, 3 effizient
    public int MaxFreq { get; set; }           // MHz, 0 = kein Limit
    public int MinState { get; set; } = 5;     // %
    public int MaxState { get; set; } = 100;   // %
    public int Epp { get; set; } = -1;        // Energie-Leistungs-Präferenz 0 (Leistung) … 100 (Sparen), -1 = Windows
    public int MinCores { get; set; } = -1;   // Core Parking: min. aktive Kerne in %, -1 = Windows
    public CpuSettings Clone() => (CpuSettings)MemberwiseClone();
    public static string BoostName(int b) => b switch { 0 => "Turbo aus", 2 => "Aggressiv", 3 => "Effizient", _ => "Normal" };
    public override string ToString() => !UsePlan ? "Windows-Standard" :
        $"Turbo {BoostName(Boost)}" + (MaxFreq > 0 ? $", max. {MaxFreq} MHz" : "") + (MaxState < 100 ? $", max. {MaxState} %" : "") + $", min. {MinState} %" +
        (Epp >= 0 ? $", EPP {Epp}" : "") + (MinCores >= 0 ? $", Kerne min. {MinCores} %" : "");
}

public sealed class TweakProfile
{
    public string Name { get; set; } = "";
    public CpuSettings Cpu { get; set; } = new();
    public GpuSettings Gpu { get; set; } = new();
}

public sealed class TweakerSettings
{
    public CpuSettings Cpu { get; set; } = new();
    public GpuSettings Gpu { get; set; } = new();
    public string? HardwareId { get; set; }
    public bool ApplyOnStart { get; set; }
    public bool Stable { get; set; }
    public bool BootPending { get; set; }
    public string? OriginalScheme { get; set; }
    public string? TweakScheme { get; set; }
    public List<TweakProfile> Profiles { get; set; } = new();
}

/// <summary>
/// Tweaker: CPU über einen eigenen Windows-Energieplan (funktioniert gleich für Intel UND AMD, ohne Treiber-Risiko),
/// Grafikkarte je nach Hersteller über NVML (NVIDIA) oder ADL Overdrive 8 (AMD).
/// Sicherheit: Nach dem Anwenden 20 s Rückfrage – ohne Bestätigung wird automatisch zurückgesetzt.
/// Hat ein Start mit gespeicherten Werten nicht geklappt (Absturz), wird beim nächsten Start nichts angewendet.
/// </summary>
public sealed class TweakerService : ObservableObject
{
    private const string SubProc = "54533251-82be-4824-96c1-47b60b740d00";
    private const string BoostGuid = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string MaxStateGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string MinStateGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string FreqMax0 = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";
    private const string FreqMax1 = "75b0ae3f-bce0-45a7-8c89-c9611c25e101";
    private const string EppGuid = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string EppGuid1 = "36687f9e-e3a5-4dbf-b1dc-15eb381c6864";
    private const string MinCoresGuid = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public TweakerSettings Settings { get; } = JsonStore.Load<TweakerSettings>("tweaker.json");
    public IGpuTuner Gpu { get; private set; } = new NoGpuTuner("Wird erkannt …", "");

    public string CpuName { get; private set; } = "Unbekannte CPU";
    public int Cores { get; private set; }
    public int Threads { get; private set; }
    public int BaseClock { get; private set; }
    public bool IsIntel { get; private set; }
    public bool IsAmd { get; private set; }
    public bool Unlocked { get; private set; }
    public string CpuInfo { get; private set; } = "";
    public int MaxSeen { get; private set; }

    private readonly DispatcherTimer _confirm = new() { Interval = TimeSpan.FromSeconds(1) };
    private (CpuSettings cpu, GpuSettings gpu)? _undo;
    private int _countdown;
    public int Countdown { get => _countdown; private set { Set(ref _countdown, value); Raise(nameof(Confirming)); } }
    public bool Confirming => _countdown > 0;
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public TweakerService()
    {
        DetectCpu();
        _confirm.Tick += (_, _) =>
        {
            Countdown--;
            if (Countdown <= 0) { _confirm.Stop(); Revert("Keine Bestätigung – Werte automatisch zurückgesetzt"); }
        };
        _ = Task.Run(DetectGpu);
    }

    public void Save() => JsonStore.Save("tweaker.json", Settings);

    private void DetectCpu()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    CpuName = Regex.Replace(o["Name"]?.ToString() ?? CpuName, @"\s+", " ").Trim();
                    Cores = Convert.ToInt32(o["NumberOfCores"]); Threads = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                    BaseClock = Convert.ToInt32(o["MaxClockSpeed"]);
                    break;
                }
        }
        catch { Threads = Environment.ProcessorCount; }
        IsIntel = CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        IsAmd = CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        var n = CpuName.ToUpperInvariant();
        if (IsIntel)
        {
            var m = Regex.Match(n, @"I[3579]-(\d{4,5})([A-Z]*)");
            string suffix = m.Success ? m.Groups[2].Value : "";
            Unlocked = suffix.Contains('K') || (n.Contains("ULTRA") && Regex.IsMatch(n, @"\d{3}K\b"));
            bool raptor = m.Success && m.Groups[1].Value.Length == 5 && (m.Groups[1].Value.StartsWith("13") || m.Groups[1].Value.StartsWith("14"));
            CpuInfo = Unlocked
                ? "Intel mit freiem Multiplikator (K): echtes Übertakten geht im BIOS. Hier: sichere Feinsteuerung über Windows – Turbo-Verhalten, Maximaltakt, Energie-Präferenz."
                : "Intel ohne „K“: Takt und Spannung lassen sich nicht erhöhen (Sperre der CPU). Ein niedrigerer Maximaltakt senkt automatisch die Spannung (Undervolt-Effekt): kühler und leiser.";
            if (raptor) CpuInfo += " Tipp für 13./14. Gen: BIOS aktuell halten (Microcode 0x12B oder neuer).";
        }
        else if (IsAmd)
        {
            Unlocked = n.Contains("RYZEN");
            CpuInfo = "AMD Ryzen: echtes Undervolting geht über „Curve Optimizer“ im BIOS. Hier: Turbo (Precision Boost), Maximaltakt, Energie-Präferenz und Kern-Parking über Windows – funktioniert genauso wie bei Intel.";
        }
        else CpuInfo = "Feinsteuerung von Turbo und Maximaltakt über Windows.";
        Log.Info(LogModule.Tweaker, $"CPU: {CpuName} ({Cores} Kerne/{Threads} Threads, Basis {BaseClock} MHz)");
    }

    private void DetectGpu()
    {
        IGpuTuner? t = null;
        try
        {
            var nv = new NvidiaTuner();
            if (nv.Available) t = nv; else nv.Dispose();
        }
        catch (Exception ex) { Log.Info(LogModule.Tweaker, "NVIDIA nicht verfügbar: " + ex.Message); }
        if (t == null)
        {
            try
            {
                var amd = new AmdTuner();
                if (amd.Available) t = amd; else { Log.Info(LogModule.Tweaker, "AMD: " + amd.Info); amd.Dispose(); }
            }
            catch (Exception ex) { Log.Info(LogModule.Tweaker, "AMD nicht verfügbar: " + ex.Message); }
        }
        if (t == null)
        {
            var name = GpuLoad.ListAdapters().FirstOrDefault()?.Name ?? "Keine Grafikkarte erkannt";
            var vendor = DriverCheck.VendorFromName(name);
            t = new NoGpuTuner(name, vendor == GpuVendor.Intel
                ? "Intel-Grafik: Takt/Leistung lassen sich nur mit „Intel Graphics Software“ ändern. Überwachung und Stresstest funktionieren hier trotzdem."
                : "Für diese Grafikkarte ist kein Tuning möglich (Treiber fehlt oder zu alt?). Überwachung und Stresstest funktionieren trotzdem.");
        }
        Log.Info(LogModule.Tweaker, $"Grafikkarte: {t.Name} ({t.Vendor}) – {t.Info}");
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            Gpu = t;
            // Werte von anderer Hardware nicht übernehmen
            var hw = $"{CpuName}|{t.Name}";
            if (Settings.HardwareId != null && Settings.HardwareId != hw)
            {
                Log.Warn(LogModule.Tweaker, "Andere Hardware erkannt – gespeicherte Tweaker-Werte verworfen.");
                Settings.Gpu = new GpuSettings(); Settings.Cpu = new CpuSettings(); Settings.Stable = false;
            }
            Settings.HardwareId = hw;
            Save();
            RaiseAll(nameof(Gpu), nameof(GpuAvailable));
            GpuReady?.Invoke();
        });
    }

    public event Action? GpuReady;
    public bool GpuAvailable => Gpu.Available;

    // ---------------------------------------------------------------- CPU-Werte
    public int TurboClock => Math.Max(Math.Max(MaxSeen, BaseClock), (int)(double.IsNaN(HardwareHub.Instance.Latest.CpuClock) ? 0 : HardwareHub.Instance.Latest.CpuClock));
    public int CapMin => Math.Max(800, BaseClock * 6 / 10 / 100 * 100);
    public int CapMax => Math.Max(CapMin + 100, (Math.Max(TurboClock, BaseClock) + 99) / 100 * 100 + 500);
    public int SafetyTemp => IsAmd ? 90 : 95;

    public CpuSettings PresetDefault() => new() { UsePlan = false };
    public CpuSettings PresetQuiet() { int cap = Math.Max(BaseClock, (int)(TurboClock * 0.8)) / 100 * 100; return new() { UsePlan = true, Boost = 3, MaxFreq = cap, MinState = 5, Epp = 70, MinCores = -1 }; }
    public CpuSettings PresetEfficient() { int cap = Math.Max(BaseClock, (int)(TurboClock * 0.9)) / 100 * 100; return new() { UsePlan = true, Boost = 3, MaxFreq = cap, MinState = 5, Epp = 50 }; }
    public CpuSettings PresetBalanced() => new() { UsePlan = true, Boost = 1, MinState = 5, Epp = 33 };
    public CpuSettings PresetPerformance() => new() { UsePlan = true, Boost = 2, MinState = 50, Epp = 0, MinCores = 100 };

    // ---------------------------------------------------------------- Anwenden
    /// <summary>Werte anwenden und 20 s auf Bestätigung warten (sonst automatisch zurück).</summary>
    public async Task ApplyAsync(CpuSettings cpu, GpuSettings gpu, bool askConfirm = true)
    {
        var before = (Settings.Cpu.Clone(), Gpu.ReadCurrent());
        var msgs = await Task.Run(() =>
        {
            var m = new List<string>();
            m.AddRange(ApplyCpu(cpu));
            if (Gpu.Available) m.AddRange(Gpu.Apply(gpu));
            return m;
        });
        Settings.Cpu = cpu.Clone();
        Settings.Gpu = Gpu.Clamp(gpu);
        Settings.Stable = false;
        Save();
        Log.Info(LogModule.Tweaker, $"Angewendet – CPU: {cpu} · GPU: {Settings.Gpu}");
        foreach (var m in msgs) Log.Warn(LogModule.Tweaker, m);
        Status = msgs.Count == 0 ? "Angewendet." : string.Join("\n", msgs);
        if (askConfirm && (!Settings.Gpu.IsDefault || cpu.UsePlan))
        {
            _undo = before;
            Countdown = 20;
            _confirm.Start();
        }
    }

    public void Keep()
    {
        _confirm.Stop();
        Countdown = 0;
        _undo = null;
        Status = "Übernommen. Tipp: Mit dem Stabilitätstest prüfen (2 Minuten).";
        Log.Ok(LogModule.Tweaker, "Werte bestätigt.");
    }

    public void Revert(string why)
    {
        _confirm.Stop();
        Countdown = 0;
        if (_undo is { } u)
        {
            ApplyCpu(u.cpu);
            if (Gpu.Available) Gpu.Apply(u.gpu);
            Settings.Cpu = u.cpu; Settings.Gpu = u.gpu;
            Save();
        }
        _undo = null;
        Status = why;
        Log.Warn(LogModule.Tweaker, why);
        Ui.Toast(why, true);
    }

    public async Task ResetAllAsync()
    {
        _confirm.Stop(); Countdown = 0; _undo = null;
        var msgs = await Task.Run(() =>
        {
            var m = ResetCpu();
            if (Gpu.Available) m.AddRange(Gpu.Reset());
            return m;
        });
        Settings.Cpu = new CpuSettings(); Settings.Gpu = new GpuSettings(); Settings.Stable = false;
        Save();
        Status = msgs.Count == 0 ? "Alles auf Werkseinstellung." : string.Join("\n", msgs);
        Log.Ok(LogModule.Tweaker, "Auf Werkseinstellung zurückgesetzt.");
    }

    /// <summary>Beim Start gespeicherte Werte anwenden – außer der letzte Start mit Werten ist abgestürzt.</summary>
    public void ApplyOnStartup(bool autostart)
    {
        if (!Settings.ApplyOnStart || (Settings.Gpu.IsDefault && !Settings.Cpu.UsePlan)) return;
        if (Settings.BootPending)
        {
            Settings.BootPending = false;
            Settings.ApplyOnStart = false;
            Save();
            Log.Warn(LogModule.Tweaker, "Der letzte Start mit Tweaker-Werten wurde nicht sauber beendet (Absturz?) – Werte werden diesmal NICHT angewendet, „Beim Start anwenden“ ist aus.");
            App.Instance.Balloon("Tweaker-Werte nicht angewendet", "Beim letzten Mal ist der PC nach dem Anwenden wohl abgestürzt. Bitte Werte im Tweaker prüfen.", true);
            return;
        }
        Settings.BootPending = true;
        Save();
        GpuReady += async () =>
        {
            await Task.Delay(autostart ? 20000 : 3000);
            await ApplyAsync(Settings.Cpu, Settings.Gpu, askConfirm: false);
            Log.Info(LogModule.Tweaker, "Gespeicherte Werte beim Start angewendet.");
            await Task.Delay(90000);
            Settings.BootPending = false;
            Save();
        };
    }

    public void SaveProfile(string name)
    {
        Settings.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Settings.Profiles.Insert(0, new TweakProfile { Name = name, Cpu = Settings.Cpu.Clone(), Gpu = Settings.Gpu.Clone() });
        Save();
        Raise(nameof(Settings));
    }

    // ---------------------------------------------------------------- Energieplan
    private List<string> ApplyCpu(CpuSettings s)
    {
        var msg = new List<string>();
        if (!s.UsePlan) return ResetCpu();
        string? active = ActiveScheme();
        if (Settings.TweakScheme == null || !SchemeExists(Settings.TweakScheme))
        {
            if (active != null && !active.Equals(Settings.TweakScheme, StringComparison.OrdinalIgnoreCase)) Settings.OriginalScheme = active;
            string src = Settings.OriginalScheme ?? Balanced;
            var m = Regex.Match(Env.Run("powercfg.exe", "/duplicatescheme " + src).Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (!m.Success) { msg.Add("Energieplan konnte nicht angelegt werden."); return msg; }
            Settings.TweakScheme = m.Value;
            Env.Run("powercfg.exe", $"/changename {Settings.TweakScheme} \"Control Center Tweaker\" \"Erstellt vom Control Center\"");
        }
        else if (active != null && !active.Equals(Settings.TweakScheme, StringComparison.OrdinalIgnoreCase)) Settings.OriginalScheme = active;

        string g = Settings.TweakScheme;
        // versteckte Optionen sichtbar machen (sonst ignoriert Windows manche Werte)
        foreach (var hidden in new[] { BoostGuid, EppGuid, EppGuid1, MinCoresGuid, FreqMax0, FreqMax1 }) Env.Run("powercfg.exe", $"-attributes {SubProc} {hidden} -ATTRIB_HIDE");
        SetPlan(g, BoostGuid, Math.Clamp(s.Boost, 0, 3));
        SetPlan(g, MaxStateGuid, Math.Clamp(s.MaxState, 20, 100));
        SetPlan(g, MinStateGuid, Math.Clamp(s.MinState, 1, Math.Clamp(s.MaxState, 20, 100)));
        int f = s.MaxFreq <= 0 ? 0 : Math.Clamp(s.MaxFreq, CapMin, CapMax);
        SetPlan(g, FreqMax0, f);
        SetPlan(g, FreqMax1, f);
        if (s.Epp >= 0) { SetPlan(g, EppGuid, Math.Clamp(s.Epp, 0, 100)); SetPlan(g, EppGuid1, Math.Clamp(s.Epp, 0, 100)); }
        if (s.MinCores >= 0) SetPlan(g, MinCoresGuid, Math.Clamp(s.MinCores, 0, 100));
        Env.Run("powercfg.exe", "/setactive " + g);
        if (!g.Equals(ActiveScheme(), StringComparison.OrdinalIgnoreCase)) msg.Add("Energieplan konnte nicht aktiviert werden (vom Hersteller gesperrt?).");
        return msg;
    }

    private List<string> ResetCpu()
    {
        var back = Settings.OriginalScheme ?? Balanced;
        if (!SchemeExists(back)) back = Balanced;
        Env.Run("powercfg.exe", "/setactive " + back);
        if (Settings.TweakScheme != null) { Env.Run("powercfg.exe", "/delete " + Settings.TweakScheme); Settings.TweakScheme = null; }
        return new List<string>();
    }

    private static void SetPlan(string scheme, string setting, int v)
    {
        Env.Run("powercfg.exe", $"/setacvalueindex {scheme} {SubProc} {setting} {v}");
        Env.Run("powercfg.exe", $"/setdcvalueindex {scheme} {SubProc} {setting} {v}");
    }

    private static string? ActiveScheme()
    {
        var m = Regex.Match(Env.Run("powercfg.exe", "/getactivescheme").Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static bool SchemeExists(string guid) => Env.Run("powercfg.exe", "/list").Output.Contains(guid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Kurz Last erzeugen, um den echten Turbo-Takt zu messen.</summary>
    /// <summary>Belastet einen Kern 5 s und misst den höchsten Takt. Rückgabe: gemessener Höchsttakt (0 = keine Messwerte).</summary>
    public async Task<int> ProbeTurboAsync()
    {
        var load = new CpuLoad();
        load.Start(CpuStressMode.Float, 1);
        int seen = 0;
        try
        {
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(450);
                var c = HardwareHub.Instance.Latest.CpuClock;
                if (!double.IsNaN(c) && c is > 300 and < 7000) seen = Math.Max(seen, (int)c);
            }
        }
        finally { load.Stop(); }
        if (seen > 0) MaxSeen = Math.Max(MaxSeen, seen);
        Raise(nameof(TurboClock)); Raise(nameof(CapMax));
        Log.Info(LogModule.Tweaker, seen > 0 ? $"Gemessener Turbo-Takt: {seen} MHz (bisher höchster: {MaxSeen} MHz)" : "Turbo-Takt: keine Taktwerte von den Sensoren");
        return seen;
    }
}
