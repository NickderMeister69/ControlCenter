namespace NickControlCenter;

/// <summary>
/// Reserve-Messung für NVIDIA-Karten direkt über den Treiber (NVML).
/// Greift, wenn LibreHardwareMonitor/HWiNFO keine GPU-Temperatur liefern
/// (z. B. GPU nicht erkannt, iGPU statt Grafikkarte gewählt, Treiber während des Betriebs neu installiert).
/// </summary>
internal static class NvmlSensor
{
    private static readonly object _lock = new();
    private static IntPtr _dev;
    private static bool _ok;
    private static DateTime _nextTry = DateTime.MinValue;
    private static bool _logged;
    public static string Name { get; private set; } = "";

    private static bool Ensure()
    {
        if (_ok) return true;
        if (DateTime.Now < _nextTry) return false;
        _nextTry = DateTime.Now.AddSeconds(30);
        try
        {
            if (Nvml.Call(Nvml.nvmlInit_v2) != Nvml.SUCCESS) return false;
            if (Nvml.Call(() => Nvml.nvmlDeviceGetCount_v2(out uint n) is var r && r == 0 && n > 0 ? 0 : 1) != 0) return false;
            if (Nvml.Call(() => Nvml.nvmlDeviceGetHandleByIndex_v2(0, out _dev)) != 0) return false;
            var b = new byte[96];
            if (Nvml.Call(() => Nvml.nvmlDeviceGetName(_dev, b, 96)) == 0) Name = Nvml.Str(b);
            _ok = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Fehlende GPU-Werte im Snapshot aus NVML ergänzen.</summary>
    public static void Fill(SensorSnapshot s)
    {
        bool needPower = double.IsNaN(s.GpuPower) || s.GpuPower <= 0.5;
        if (SensorSnapshot.Plausible(s.GpuTemp) && !double.IsNaN(s.GpuLoad) && !needPower) return;
        // nur NVIDIA-Karten (oder Karte unbekannt)
        if (!string.IsNullOrEmpty(s.GpuName) && DriverCheck.VendorFromName(s.GpuName) is GpuVendor.Amd) return;
        lock (_lock)
        {
            if (!Ensure()) return;
            try
            {
                int r = Nvml.Call(() => Nvml.nvmlDeviceGetTemperature(_dev, Nvml.TEMP_GPU, out uint t) is var x && x == 0 ? (SensorSnapshot.Plausible(s.GpuTemp) ? 0 : Put(s, t)) : x);
                if (r == Nvml.GPU_IS_LOST || r == 999) { _ok = false; return; }
                if (r != 0) return;
                if (string.IsNullOrEmpty(s.GpuName) || DriverCheck.VendorFromName(s.GpuName) == GpuVendor.Intel) s.GpuName = Name;
                if (double.IsNaN(s.GpuLoad) && Nvml.nvmlDeviceGetUtilizationRates(_dev, out var u) == 0) s.GpuLoad = u.Gpu;
                if (needPower)
                {
                    int pr = Nvml.Call(() => Nvml.nvmlDeviceGetPowerUsage(_dev, out uint mw) is var x && x == 0 ? SetPower(s, mw / 1000.0) : x);
                    if (pr != 0)
                    {
                        // Ersatz: Energiezähler (mJ) → Leistung aus der Differenz
                        ulong mj = 0;
                        int er = Nvml.Call(() => Nvml.nvmlDeviceGetTotalEnergyConsumption(_dev, out mj));
                        if (er == 0)
                        {
                            var now = DateTime.UtcNow;
                            if (_lastMj > 0 && mj >= _lastMj && (now - _lastMjTime).TotalSeconds > 0.2)
                                s.GpuPower = (mj - _lastMj) / (now - _lastMjTime).TotalMilliseconds; // mJ/ms = W
                            _lastMj = mj; _lastMjTime = now;
                        }
                        if (!_powerLogged) { _powerLogged = true; Log.Warn(LogModule.Monitor, $"NVML-Leistung nicht lesbar ({Nvml.Err(pr)}), Energiezähler: {(er == 0 ? "ok" : Nvml.Err(er))}"); }
                    }
                    // letzte Notlösung: aus Auslastung und Leistungslimit schätzen (als „geschätzt“ markiert)
                    if ((double.IsNaN(s.GpuPower) || s.GpuPower <= 0.5) && !double.IsNaN(s.GpuLoad))
                    {
                        if (_limitW <= 0)
                        {
                            if (Nvml.Call(() => Nvml.nvmlDeviceGetPowerManagementDefaultLimit(_dev, out uint lim) is var x && x == 0 ? SetLimit(lim) : x) != 0)
                                Nvml.Call(() => Nvml.nvmlDeviceGetPowerManagementLimit(_dev, out uint lim2) is var y && y == 0 ? SetLimit(lim2) : y);
                        }
                        if (_limitW > 0)
                        {
                            s.GpuPower = _limitW * (0.08 + 0.92 * Math.Clamp(s.GpuLoad, 0, 100) / 100);
                            s.GpuPowerEstimated = true;
                            if (!_estLogged) { _estLogged = true; Log.Warn(LogModule.Monitor, $"Grafikkarte meldet keine Watt-Werte – schätze aus Auslastung und Leistungslimit ({_limitW:0} W)"); }
                        }
                    }
                }
                if (double.IsNaN(s.GpuClock) && Nvml.nvmlDeviceGetClockInfo(_dev, Nvml.CLOCK_GRAPHICS, out uint c) == 0) s.GpuClock = c;
                if (double.IsNaN(s.GpuMemClock) && Nvml.nvmlDeviceGetClockInfo(_dev, Nvml.CLOCK_MEM, out uint m) == 0) s.GpuMemClock = m;
                if (double.IsNaN(s.GpuVramUsedGb) && Nvml.nvmlDeviceGetMemoryInfo(_dev, out var mi) == 0)
                {
                    s.GpuVramUsedGb = mi.Used / 1073741824.0;
                    s.GpuVramTotalGb = mi.Total / 1073741824.0;
                }
                if (!_logged) { _logged = true; Log.Info(LogModule.Monitor, $"Fehlende GPU-Werte kommen direkt vom NVIDIA-Treiber (NVML): {Name}"); }
            }
            catch { _ok = false; }
        }
    }

    /// <summary>Nur die GPU-Temperatur (für die Lüftersteuerung).</summary>
    public static float? Temp()
    {
        lock (_lock)
        {
            if (!Ensure()) return null;
            try
            {
                int r = Nvml.Call(() => Nvml.nvmlDeviceGetTemperature(_dev, Nvml.TEMP_GPU, out uint t) is var x && x == 0 ? Store(t) : x);
                if (r != 0) { if (r == Nvml.GPU_IS_LOST) _ok = false; return null; }
                return _last;
            }
            catch { _ok = false; return null; }
        }
    }

    private static float _last;
    private static ulong _lastMj;
    private static DateTime _lastMjTime;
    private static bool _powerLogged, _estLogged;
    private static double _limitW;
    private static int SetLimit(uint mw) { _limitW = mw / 1000.0; return 0; }
    private static int SetPower(SensorSnapshot s, double w) { s.GpuPower = w; return 0; }
    private static int Store(uint t) { _last = t; return 0; }
    private static int Put(SensorSnapshot s, uint t) { s.GpuTemp = t; return 0; }

    /// <summary>Vor einer Treiber-Installation loslassen, damit der Installer nvml.dll ersetzen kann.</summary>
    public static void Release()
    {
        lock (_lock)
        {
            if (_ok) try { Nvml.nvmlShutdown(); } catch { }
            _ok = false;
            _nextTry = DateTime.Now.AddMinutes(10);
        }
    }

    public static void AllowRetry() { lock (_lock) _nextTry = DateTime.MinValue; }
}
