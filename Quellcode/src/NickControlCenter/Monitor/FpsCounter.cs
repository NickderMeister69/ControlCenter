using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace NickControlCenter;

/// <summary>
/// Misst die FPS des Spiels im Vordergrund – ohne etwas ins Spiel einzuschleusen (Anti-Cheat-sicher).
/// Wie PresentMon/MSI Afterburner-ETW: Windows meldet über ETW jedes "Present" (fertiges Bild) von DirectX 9–12.
/// Vulkan/OpenGL-Spiele melden das nicht über DXGI → dann wird "–" angezeigt.
/// </summary>
public sealed class FpsCounter : IDisposable
{
    public static FpsCounter Instance { get; } = new();

    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const string SessionName = "NickControlCenter-FPS";

    private readonly object _lock = new();
    /// <summary>Zeitstempel (ms) der letzten Bilder je Prozess.</summary>
    private readonly Dictionary<int, Queue<double>> _frames = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private int _users;
    public string? Error { get; private set; }

    /// <summary>Messung starten (mehrere Nutzer möglich – läuft, solange einer sie braucht).</summary>
    public void Acquire()
    {
        lock (_lock)
        {
            if (++_users > 1 || _session != null) return;
            try
            {
                _session = new TraceEventSession(SessionName) { StopOnDispose = true };
                _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
                _session.EnableProvider(D3d9, TraceEventLevel.Informational, ulong.MaxValue);
                var src = _session.Source;
                src.AllEvents += OnEvent;
                _thread = new Thread(() => { try { src.Process(); } catch (Exception ex) { Error = ex.Message; } }) { IsBackground = true, Name = "FPS-ETW" };
                _thread.Start();
                Error = null;
                Log.Info(LogModule.Monitor, "FPS-Messung gestartet (ETW, DirectX 9–12)");
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _session?.Dispose(); _session = null;
                Log.Warn(LogModule.Monitor, "FPS-Messung nicht möglich: " + ex.Message);
            }
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_users > 0) _users--;
            if (_users > 0 || _session == null) return;
            try { _session.Dispose(); } catch { }
            _session = null;
            _frames.Clear();
            Log.Info(LogModule.Monitor, "FPS-Messung beendet");
        }
    }

    private void OnEvent(TraceEvent e)
    {
        // DXGI: Present_Start = 42, PresentMultiplaneOverlay_Start = 55 · D3D9: Present_Start = 1
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == Dxgi && id is 42 or 55) || (e.ProviderGuid == D3d9 && id == 1);
        if (!present) return;
        double t = e.TimeStampRelativeMSec;
        lock (_frames)
        {
            if (!_frames.TryGetValue(e.ProcessID, out var q)) _frames[e.ProcessID] = q = new Queue<double>();
            q.Enqueue(t);
            while (q.Count > 0 && t - q.Peek() > 10_000) q.Dequeue(); // 10 s Verlauf
            if (q.Count > 5000) q.Dequeue();
        }
    }

    /// <summary>FPS (letzte Sekunde) und „1 % Low“ (letzte 10 s) für einen Prozess.</summary>
    public (double Fps, double Low1, double FrameMs) Get(int pid)
    {
        lock (_frames)
        {
            if (!_frames.TryGetValue(pid, out var q) || q.Count < 2) return (double.NaN, double.NaN, double.NaN);
            var arr = q.ToArray();
            double last = arr[^1];
            // seit über 2 s keine Bilder mehr → Spiel pausiert/minimiert
            double now = _session?.Source is { } s ? s.SessionStartTimeRelativeMSec(DateTime.Now) : last;
            if (now - last > 2000) return (double.NaN, double.NaN, double.NaN);
            int inSec = arr.Count(x => last - x <= 1000);
            var deltas = new List<double>(arr.Length);
            for (int i = 1; i < arr.Length; i++) deltas.Add(arr[i] - arr[i - 1]);
            deltas.Sort();
            int n = Math.Max(1, deltas.Count / 100);
            double worst = deltas.Skip(deltas.Count - n).Average();
            double avgMs = deltas.Count > 0 ? deltas.TakeLast(Math.Min(deltas.Count, inSec)).Average() : double.NaN;
            return (inSec, worst > 0 ? 1000 / worst : double.NaN, avgMs);
        }
    }

    /// <summary>
    /// FPS des Spiels im Vordergrund. Ist gerade kein Spiel vorne (Desktop, Browser, Video …), zählt nicht „wie oft
    /// das Programm ein Bild schickt“ (Browser schicken nur bei Änderungen → springt zwischen 30 und 180),
    /// sondern es wird die Bildwiederholrate des Monitors gemeldet (Desktop = true, Hz).
    /// </summary>
    public (double Fps, double Low1, double FrameMs, int Pid, bool Desktop, double Hz) Foreground()
    {
        var h = Native.GetForegroundWindow();
        double hz = RefreshRate(h);
        if (h == IntPtr.Zero) return (double.NaN, double.NaN, double.NaN, 0, true, hz);
        Native.GetWindowThreadProcessId(h, out uint pid);
        string name = "";
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
        var boost = Services.Boost;
        bool knownGame = boost != null && name.Length > 0 && boost.IsGameProcess(name);
        bool nonGame = name.Length == 0 || BoostService.IsNonGame(name);
        var r = Get((int)pid);
        bool game = knownGame || (!nonGame && Native.IsFullscreen(h) && !double.IsNaN(r.Fps));
        if (!game) return (double.NaN, double.NaN, double.NaN, 0, true, hz);
        if (!double.IsNaN(r.Fps)) return (r.Fps, r.Low1, r.FrameMs, (int)pid, false, hz);
        // Spiel vorne, liefert aber nichts (z. B. Fenster des Launchers davor) → anderer Prozess desselben Spiels
        if (boost != null)
        {
            lock (_frames)
                foreach (var p in _frames.Keys.ToList())
                {
                    string n2 = "";
                    try { using var pr = System.Diagnostics.Process.GetProcessById(p); n2 = pr.ProcessName; } catch { continue; }
                    if (!boost.IsGameProcess(n2)) continue;
                    var g = Get(p);
                    if (!double.IsNaN(g.Fps)) return (g.Fps, g.Low1, g.FrameMs, p, false, hz);
                }
        }
        return (double.NaN, double.NaN, double.NaN, (int)pid, false, hz);
    }

    /// <summary>Bildwiederholrate (Hz) des Monitors, auf dem das Fenster liegt.</summary>
    public static double RefreshRate(IntPtr hwnd)
    {
        try
        {
            var mon = MonitorFromWindow(hwnd, 2);
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            string? dev = mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) ? mi.szDevice : null;
            var dm = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(dev, -1, ref dm) && dm.dmDisplayFrequency > 1) return dm.dmDisplayFrequency;
        }
        catch { }
        return double.NaN;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public int l1, t1, r1, b1, l2, t2, r2, b2; public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX mi);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);

    public void Dispose() { lock (_lock) { _users = 1; } Release(); }
}

internal static class SessionTimeExt
{
    public static double SessionStartTimeRelativeMSec(this TraceEventSource s, DateTime now) =>
        (now - s.SessionStartTime).TotalMilliseconds;
}
