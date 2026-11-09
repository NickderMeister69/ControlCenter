using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NickControlCenter;

public sealed record GpuAdapter(int Index, string Name, string Vendor, double VramGb);

public sealed class StressSettings
{
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Ram { get; set; }
    public CpuStressMode CpuMode { get; set; } = CpuStressMode.Mixed;
    public int Threads { get; set; }          // 0 = alle
    public int GpuAdapter { get; set; } = -1; // -1 = automatisch (stärkste Karte)
    public int GpuIntensity { get; set; } = 100; // %
    public int RamPercent { get; set; } = 50;
    public int Minutes { get; set; } = 10;    // 0 = bis Stopp
    public double CpuLimit { get; set; } = 0; // 0 = automatisch je nach CPU
    public double GpuLimit { get; set; } = 90;
}

/// <summary>CPU-Last (alle/gewählte Threads) mit Rechenkontrolle. Läuft auf jeder x64-CPU (Intel, AMD).</summary>
public sealed class CpuLoad
{
    private volatile bool _run;
    private readonly List<Thread> _threads = new();
    private long _chunks;
    private int _errors;
    public int Errors => Volatile.Read(ref _errors);
    public long Chunks => Interlocked.Read(ref _chunks);

    public void Start(CpuStressMode mode, int threads)
    {
        Stop();
        _run = true; _errors = 0; _chunks = 0;
        int n = threads <= 0 ? Environment.ProcessorCount : Math.Min(threads, Environment.ProcessorCount);
        for (int t = 0; t < n; t++)
        {
            int seed = t;
            var th = new Thread(() =>
            {
                var refs = new ulong[3];
                for (int r = 0; r < 3; r++) refs[r] = StressKernels.Run(mode, seed, r);
                int round = 0;
                while (_run)
                {
                    var v = StressKernels.Run(mode, seed, round);
                    if (v != refs[round % 3]) Interlocked.Increment(ref _errors);
                    Interlocked.Increment(ref _chunks);
                    round = (round + 1) % 3;
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "CPU-Last " + t };
            _threads.Add(th);
            th.Start();
        }
    }

    public void Stop()
    {
        _run = false;
        foreach (var t in _threads) t.Join(3000);
        _threads.Clear();
    }
}

/// <summary>
/// GPU-Last per Direct3D-11-Compute-Shader – funktioniert mit JEDER DirectX-11-Grafikkarte (NVIDIA, AMD, Intel),
/// ohne Hersteller-Bibliothek (der alte Test hing an NVIDIA-Funktionen und lief deshalb beim Freund mit AMD-Karte nicht).
/// Prüft regelmäßig, ob die GPU bei gleicher Rechnung immer exakt das gleiche Ergebnis liefert.
/// </summary>
public sealed class GpuLoad
{
    private const int WG = 256, GROUPS = 8192, N = WG * GROUPS;
    private const int VGROUPS = 256, VN = WG * VGROUPS, VITERS = 96;
    private const int PER_BATCH = 4;

    private volatile bool _run;
    private Thread? _thread;
    private int _errors, _checks;
    private long _work;
    public string? Error { get; private set; }
    public bool DriverCrashed { get; private set; }
    public string AdapterName { get; private set; } = "";
    public int Errors => Volatile.Read(ref _errors);
    public int Checks => Volatile.Read(ref _checks);
    public long Work => Interlocked.Read(ref _work);
    public bool Running => _run;

    private const string HLSL = @"
cbuffer P : register(b0) { uint iters; uint3 pad; };
RWStructuredBuffer<float4> buf : register(u0);
[numthreads(256,1,1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float f = id.x * 0.000001;
    float4 a = float4(f, f+0.1, f+0.2, f+0.3);
    float4 b = float4(0.5, 0.25, 0.75, 0.125);
    float4 c = float4(0.3, 0.6, 0.9, 0.1);
    float4 d = float4(0.7, 0.2, 0.4, 0.8);
    [loop] for (uint i = 0; i < iters; i++)
    {
        a = frac(mad(a, b, c));
        b = frac(mad(b, c, d));
        c = frac(mad(c, d, a));
        d = frac(mad(d, a, b));
    }
    buf[id.x] = a + b + c + d;
}";

    public static List<GpuAdapter> ListAdapters()
    {
        var list = new List<GpuAdapter>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
            {
                using (a)
                {
                    var d = a.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    string vendor = d.VendorId switch { 0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", _ => $"0x{d.VendorId:X4}" };
                    list.Add(new GpuAdapter((int)i, d.Description.Trim(), vendor, (ulong)d.DedicatedVideoMemory / 1073741824.0));
                }
            }
        }
        catch (Exception ex) { Log.Warn(LogModule.Stress, "Grafikkarten konnten nicht aufgelistet werden: " + ex.Message); }
        return list;
    }

    public void Start(int adapterIndex, int intensityPct)
    {
        Stop();
        Error = null; DriverCrashed = false; _errors = 0; _checks = 0; _work = 0;
        _run = true;
        _thread = new Thread(() => Loop(adapterIndex, Math.Clamp(intensityPct, 10, 100))) { IsBackground = true, Name = "GPU-Last" };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(5000);
        _thread = null;
    }

    private static IDXGIAdapter1? PickAdapter(int index)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (index >= 0 && factory.EnumAdapters1((uint)index, out IDXGIAdapter1 chosen).Success) return chosen;
        IDXGIAdapter1? best = null; ulong bestMem = 0;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
        {
            var d = a.Description1;
            if ((d.Flags & AdapterFlags.Software) == 0 && (ulong)d.DedicatedVideoMemory > bestMem)
            { best?.Dispose(); best = a; bestMem = (ulong)d.DedicatedVideoMemory; }
            else a.Dispose();
        }
        return best;
    }

    private void Loop(int adapterIndex, int intensity)
    {
        ID3D11Device? device = null; ID3D11DeviceContext? ctx = null;
        double targetMs = 6 + 24 * intensity / 100.0; // kurze Dispatches → kein Treiber-Timeout (TDR) bei keinem Hersteller
        try
        {
            using var adapter = PickAdapter(adapterIndex);
            AdapterName = adapter?.Description1.Description.Trim() ?? "Standard-Grafikkarte";
            var res = D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown,
                DeviceCreationFlags.None, new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out device, out ctx);
            if (res.Failure || device == null || ctx == null)
                throw new InvalidOperationException($"DirectX 11 konnte auf „{AdapterName}“ nicht gestartet werden (0x{res.Code:X8}). Grafiktreiber aktuell?");

            ReadOnlyMemory<byte> bytecode;
            try { bytecode = Compiler.Compile(HLSL, "main", "test.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3); }
            catch (Exception ex) { throw new InvalidOperationException("Shader-Compiler (d3dcompiler_47.dll) fehlt oder ist defekt: " + ex.Message); }
            using var cs = device.CreateComputeShader(bytecode.Span);

            using var buf = device.CreateBuffer(new BufferDescription((uint)(N * 16), BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
            using var uav = device.CreateUnorderedAccessView(buf, new UnorderedAccessViewDescription(buf, Format.Unknown, 0, (uint)N));
            using var vbuf = device.CreateBuffer(new BufferDescription((uint)(VN * 16), BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
            using var vuav = device.CreateUnorderedAccessView(vbuf, new UnorderedAccessViewDescription(vbuf, Format.Unknown, 0, (uint)VN));
            using var staging = device.CreateBuffer(new BufferDescription((uint)(VN * 16), BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
            using var cb = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
            using var vcb = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
            ctx.UpdateSubresource(new uint[] { VITERS, 0, 0, 0 }, vcb);

            var queries = new[] { device.CreateQuery(new QueryDescription(QueryType.Event)), device.CreateQuery(new QueryDescription(QueryType.Event)) };
            bool[] inFlight = new bool[2];
            ctx.CSSetShader(cs);

            uint iters = 32;
            var sw = Stopwatch.StartNew();
            double tPrev = 0, lastCheck = 0;
            int slot = 0;

            byte[] Verify()
            {
                ctx.CSSetConstantBuffer(0, vcb);
                ctx.CSSetUnorderedAccessView(0, vuav);
                ctx.Dispatch(VGROUPS, 1, 1);
                ctx.CopyResource(staging, vbuf);
                var map = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                var arr = new byte[VN * 16];
                Marshal.Copy(map.DataPointer, arr, 0, arr.Length);
                ctx.Unmap(staging, 0);
                return arr;
            }

            var reference = Verify();
            if (!Verify().AsSpan().SequenceEqual(reference)) Interlocked.Increment(ref _errors);
            Log.Info(LogModule.Stress, $"GPU-Last läuft auf „{AdapterName}“ (Intensität {intensity} %)");

            while (_run)
            {
                ctx.CSSetConstantBuffer(0, cb);
                ctx.CSSetUnorderedAccessView(0, uav);
                ctx.UpdateSubresource(new uint[] { iters, 0, 0, 0 }, cb);
                for (int k = 0; k < PER_BATCH; k++) ctx.Dispatch(GROUPS, 1, 1);
                ctx.End(queries[slot]);
                inFlight[slot] = true;
                ctx.Flush();
                Interlocked.Add(ref _work, (long)iters * PER_BATCH);

                int prev = slot ^ 1;
                if (inFlight[prev])
                {
                    var wait = Stopwatch.StartNew();
                    while (!ctx.IsDataAvailable(queries[prev]) && _run)
                    {
                        Thread.Sleep(0);
                        if (wait.ElapsedMilliseconds > 5000) throw new TimeoutException("GPU antwortet nicht mehr");
                    }
                    inFlight[prev] = false;
                    double now = sw.Elapsed.TotalMilliseconds;
                    double per = (now - tPrev) / PER_BATCH;
                    tPrev = now;
                    if (per < targetMs * 0.7) iters = Math.Min(iters * 3 / 2 + 1, 1u << 16);
                    else if (per > targetMs * 1.4) iters = Math.Max(iters * 7 / 10, 8u);

                    if (now - lastCheck > 1000)
                    {
                        lastCheck = now;
                        if (!Verify().AsSpan().SequenceEqual(reference)) Interlocked.Increment(ref _errors);
                        Interlocked.Increment(ref _checks);
                    }
                    if (intensity < 100) Thread.Sleep((int)(targetMs * (100 - intensity) / 100));
                }
                else tPrev = sw.Elapsed.TotalMilliseconds;
                slot = prev;
            }
            foreach (var q in queries) q.Dispose();
            ctx.ClearState();
        }
        catch (Exception ex)
        {
            try { DriverCrashed = device != null && device.DeviceRemovedReason.Failure; } catch { DriverCrashed = true; }
            if (ex is TimeoutException) DriverCrashed = true;
            Error = DriverCrashed ? "Der Grafiktreiber ist abgestürzt bzw. hat sich zurückgesetzt (instabile Übertaktung oder Überhitzung)." : ex.Message;
            Log.Error(LogModule.Stress, "GPU-Last abgebrochen: " + Error, DriverCrashed ? null : ex);
            _run = false;
        }
        finally
        {
            ctx?.Dispose();
            device?.Dispose();
        }
    }
}

public sealed class StressService : ObservableObject
{
    public StressSettings Settings { get; } = JsonStore.Load<StressSettings>("stresstest.json");
    public List<GpuAdapter> Adapters { get; private set; } = new();
    public ObservableCollection<string> Events { get; } = new();
    private readonly CpuLoad _cpu = new();
    private readonly GpuLoad _gpu = new();
    private MemoryPattern? _mem;
    private Thread? _memThread;
    private volatile bool _memRun;
    private long _memErrors, _memPasses;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _start;
    private long _lastChunks, _lastWork;
    private double _maxCpuT = double.NaN, _maxGpuT = double.NaN, _maxCpuW, _maxGpuW;
    private readonly List<double> _cpuScores = new(), _gpuScores = new();
    private int _throttleTicks;

    public StressService()
    {
        _timer.Tick += (_, _) => Tick();
        Adapters = GpuLoad.ListAdapters();
        Log.Info(LogModule.Stress, "Grafikkarten: " + (Adapters.Count == 0 ? "keine gefunden" : string.Join(", ", Adapters.Select(a => $"{a.Name} ({a.Vendor}, {a.VramGb:0.#} GB)"))));
    }

    public void Save() => JsonStore.Save("stresstest.json", Settings);

    public string CpuName => HardwareHub.Instance.Latest.CpuName is { Length: > 0 } n ? n : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
    public int LogicalCores => Environment.ProcessorCount;
    public bool IsAmdCpu => CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    /// <summary>Sichere Abbruchgrenze: Intel ~ TjMax 100 → 95 °C, AMD Ryzen ~ 95 °C Tjmax → 90 °C.</summary>
    public double CpuLimitEffective => Settings.CpuLimit > 0 ? Settings.CpuLimit : IsAmdCpu ? 90 : 95;

    private bool _running;
    public bool Running { get => _running; private set { Set(ref _running, value); Raise(nameof(NotRunning)); } }
    public bool NotRunning => !_running;
    private string _elapsed = "00:00";
    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private string _cpuScore = "–", _gpuScore = "–", _errors = "0", _state = "Bereit", _summary = "";
    public string CpuScore { get => _cpuScore; private set => Set(ref _cpuScore, value); }
    public string GpuScore { get => _gpuScore; private set => Set(ref _gpuScore, value); }
    public string ErrorsText { get => _errors; private set => Set(ref _errors, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Summary { get => _summary; private set { Set(ref _summary, value); Raise(nameof(HasSummary)); } }
    public bool HasSummary => _summary.Length > 0;

    private void Event(string msg, LogLevel lvl = LogLevel.Info)
    {
        Log.Write(lvl, LogModule.Stress, msg);
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {msg}");
        while (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
    }

    public void Start()
    {
        if (Running) return;
        if (!Settings.Cpu && !Settings.Gpu && !Settings.Ram) { Ui.Toast("Bitte mindestens CPU, Grafikkarte oder RAM auswählen.", true); return; }
        Save();
        Summary = "";
        _maxCpuT = _maxGpuT = double.NaN; _maxCpuW = _maxGpuW = 0;
        _cpuScores.Clear(); _gpuScores.Clear(); _throttleTicks = 0;
        _lastChunks = 0; _lastWork = 0;
        var parts = new List<string>();
        if (Settings.Cpu) { _cpu.Start(Settings.CpuMode, Settings.Threads); parts.Add($"CPU ({ModeName(Settings.CpuMode)}, {(Settings.Threads <= 0 ? LogicalCores : Settings.Threads)} Threads)"); }
        if (Settings.Gpu) { _gpu.Start(Settings.GpuAdapter, Settings.GpuIntensity); parts.Add("Grafikkarte"); }
        if (Settings.Ram) StartRam();
        if (Settings.Ram) parts.Add($"RAM ({Settings.RamPercent} % frei)");
        _start = DateTime.Now;
        Running = true;
        State = "Läuft";
        Event($"Stresstest gestartet: {string.Join(", ", parts)} · Dauer: {(Settings.Minutes == 0 ? "bis Stopp" : Settings.Minutes + " min")} · Abbruch bei CPU {CpuLimitEffective:0} °C / GPU {Settings.GpuLimit:0} °C", LogLevel.Info);
        _timer.Start();
    }

    private static string ModeName(CpuStressMode m) => m switch { CpuStressMode.Float => "Gleitkomma", CpuStressMode.Vector => "AVX/Vektor", CpuStressMode.Integer => "Ganzzahl", _ => "Gemischt" };

    private void StartRam()
    {
        var mem = Native.Memory();
        double freeGb = mem.TotalGb - mem.UsedGb;
        long bytes = (long)(Math.Max(0.25, freeGb * Settings.RamPercent / 100.0 - 0.5) * 1073741824L);
        _mem = new MemoryPattern(bytes);
        _memRun = true; _memErrors = 0; _memPasses = 0;
        _memThread = new Thread(() =>
        {
            ulong seed = 1;
            var cts = new CancellationTokenSource();
            while (_memRun)
            {
                var e = _mem.Pass(seed++, cts.Token);
                Interlocked.Add(ref _memErrors, e);
                Interlocked.Increment(ref _memPasses);
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "RAM-Test" };
        _memThread.Start();
        Event($"RAM-Test: {bytes / 1073741824.0:0.0} GB werden geschrieben und geprüft");
    }

    public void StopAll(string reason)
    {
        if (!Running) return;
        _timer.Stop();
        _cpu.Stop();
        _gpu.Stop();
        _memRun = false;
        _memThread?.Join(5000);
        _mem?.Dispose(); _mem = null;
        Running = false;
        State = reason;
        MakeReport(reason);
    }

    private void Tick()
    {
        var el = DateTime.Now - _start;
        Elapsed = $"{(int)el.TotalMinutes:00}:{el.Seconds:00}";
        Progress = Settings.Minutes == 0 ? 0 : Math.Min(100, el.TotalMinutes / Settings.Minutes * 100);

        var s = HardwareHub.Instance.Latest;
        if (SensorSnapshot.Plausible(s.CpuTemp)) _maxCpuT = double.IsNaN(_maxCpuT) ? s.CpuTemp : Math.Max(_maxCpuT, s.CpuTemp);
        if (SensorSnapshot.Plausible(s.GpuTemp)) _maxGpuT = double.IsNaN(_maxGpuT) ? s.GpuTemp : Math.Max(_maxGpuT, s.GpuTemp);
        if (!double.IsNaN(s.CpuPower)) _maxCpuW = Math.Max(_maxCpuW, s.CpuPower);
        if (!double.IsNaN(s.GpuPower)) _maxGpuW = Math.Max(_maxGpuW, s.GpuPower);

        if (Settings.Cpu)
        {
            long c = _cpu.Chunks, d = c - _lastChunks; _lastChunks = c;
            if (el.TotalSeconds > 3) _cpuScores.Add(d);
            CpuScore = $"{d * 10:N0} Punkte/s";
        }
        if (Settings.Gpu)
        {
            long w = _gpu.Work, d = w - _lastWork; _lastWork = w;
            if (el.TotalSeconds > 3) _gpuScores.Add(d);
            GpuScore = $"{d / 100.0:N0} Punkte/s";
        }
        int errs = _cpu.Errors + _gpu.Errors + (int)Interlocked.Read(ref _memErrors);
        ErrorsText = errs.ToString();

        // Abbruchgründe
        if (SensorSnapshot.Plausible(s.CpuTemp) && s.CpuTemp >= CpuLimitEffective) { Event($"CPU erreicht {s.CpuTemp:0} °C – Sicherheitsabbruch", LogLevel.Warn); StopAll("Abbruch: CPU zu heiß"); return; }
        if (SensorSnapshot.Plausible(s.GpuTemp) && s.GpuTemp >= Settings.GpuLimit) { Event($"Grafikkarte erreicht {s.GpuTemp:0} °C – Sicherheitsabbruch", LogLevel.Warn); StopAll("Abbruch: Grafikkarte zu heiß"); return; }
        if (Settings.Gpu && _gpu.Error != null) { Event("Grafikkarte: " + _gpu.Error, LogLevel.Error); StopAll(_gpu.DriverCrashed ? "Abbruch: Grafiktreiber abgestürzt" : "Abbruch: GPU-Fehler"); return; }
        if (errs > 0 && errs != _lastErrors) { Event($"Rechenfehler erkannt (gesamt {errs}) – System ist unter Last NICHT stabil", LogLevel.Error); _lastErrors = errs; }

        // Drosselung: hohe Temperatur + Takt fällt deutlich unter Spitzenwert
        if (SensorSnapshot.Plausible(s.CpuTemp) && s.CpuTemp > CpuLimitEffective - 8 && _cpuScores.Count > 10 && _cpuScores[^1] < _cpuScores.Take(10).Average() * 0.85)
        {
            if (++_throttleTicks == 5) Event($"CPU drosselt wegen Temperatur ({s.CpuTemp:0} °C) – Kühlung prüfen", LogLevel.Warn);
        }

        if (Settings.Minutes > 0 && el.TotalMinutes >= Settings.Minutes) StopAll("Fertig");
    }
    private int _lastErrors;

    private void MakeReport(string reason)
    {
        var el = DateTime.Now - _start;
        int errs = _cpu.Errors + _gpu.Errors + (int)Interlocked.Read(ref _memErrors);
        bool ok = errs == 0 && reason == "Fertig";
        var sb = new StringBuilder();
        sb.AppendLine(ok ? "✔ BESTANDEN – keine Rechenfehler, keine Abstürze." :
                      errs > 0 ? $"✖ NICHT STABIL – {errs} Rechenfehler." : $"■ {reason}.");
        sb.AppendLine($"Dauer: {(int)el.TotalMinutes} min {el.Seconds} s");
        if (Settings.Cpu) sb.AppendLine($"CPU: {CpuName} · Ø {(_cpuScores.Count > 0 ? _cpuScores.Average() * 10 : 0):N0} Punkte/s · max. {Fmt.Temp(_maxCpuT, false)} · max. {_maxCpuW:0} W · Fehler {_cpu.Errors}");
        if (Settings.Gpu) sb.AppendLine($"Grafikkarte: {_gpu.AdapterName} · Ø {(_gpuScores.Count > 0 ? _gpuScores.Average() / 100 : 0):N0} Punkte/s · max. {Fmt.Temp(_maxGpuT, false)} · max. {_maxGpuW:0} W · Fehler {_gpu.Errors}");
        if (Settings.Ram) sb.AppendLine($"RAM: {Interlocked.Read(ref _memPasses)} Durchläufe · Fehler {Interlocked.Read(ref _memErrors)}");
        if (_throttleTicks >= 5) sb.AppendLine("Hinweis: Die CPU hat wegen Temperatur gedrosselt – Kühlung/Lüfterkurve prüfen.");
        if (!ok && errs > 0) sb.AppendLine("Tipp: Übertaktung/Undervolt im Tweaker zurücknehmen (Werkseinstellung) und erneut testen.");
        Summary = sb.ToString().Trim();
        Event("Ergebnis: " + Summary.Replace(Environment.NewLine, " | "), ok ? LogLevel.Ok : LogLevel.Warn);
        try
        {
            var file = Path.Combine(Log.Folder, $"Stresstest-Bericht_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
            File.WriteAllText(file, $"Nick Control Center – Stresstest vom {DateTime.Now:dd.MM.yyyy HH:mm}\r\n\r\n{Summary}\r\n\r\nVerlauf:\r\n{string.Join("\r\n", Events.Reverse())}\r\n");
        }
        catch { }
        App.Instance.Balloon("Stresstest " + (ok ? "bestanden" : "beendet"), Summary.Split('\n')[0], !ok);
    }

    /// <summary>Kurzer Stabilitätstest (für den Tweaker): 2 Minuten CPU + GPU.</summary>
    public void QuickTest()
    {
        Settings.Cpu = true; Settings.Gpu = true; Settings.Ram = false; Settings.Minutes = 2;
        Raise(nameof(Settings));
        Start();
    }
}
