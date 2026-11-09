using System.Runtime.InteropServices;
using System.Text;

namespace NickControlCenter;

/// <summary>
/// Schlanker Wrapper für die offizielle NVIDIA-Schnittstelle NVML (nvml.dll, kommt mit jedem NVIDIA-Treiber).
/// Kein eigener Kernel-Treiber nötig – alles läuft über den NVIDIA-Treiber selbst.
/// </summary>
internal static class Nvml
{
    const string Dll = "nvml.dll";
    public const int SUCCESS = 0, NOT_SUPPORTED = 3, NO_PERMISSION = 4, GPU_IS_LOST = 15;
    public const int CLOCK_GRAPHICS = 0, CLOCK_SM = 1, CLOCK_MEM = 2;
    public const int TEMP_GPU = 0, THRESH_SHUTDOWN = 0, THRESH_SLOWDOWN = 1, THRESH_GPU_MAX = 3;

    [StructLayout(LayoutKind.Sequential)] public struct Util { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] public struct MemInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)]
    public struct ClockOffset
    {
        public uint Version; public int Type; public int PState;
        public int OffsetMHz, MinOffsetMHz, MaxOffsetMHz;
    }
    public const uint ClockOffsetV1 = 24u | (1u << 24);

    [DllImport(Dll)] public static extern int nvmlInit_v2();
    [DllImport(Dll)] public static extern int nvmlShutdown();
    [DllImport(Dll)] public static extern IntPtr nvmlErrorString(int r);
    [DllImport(Dll)] public static extern int nvmlSystemGetDriverVersion(byte[] buf, uint len);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint n);
    [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
    [DllImport(Dll)] public static extern int nvmlDeviceGetName(IntPtr dev, byte[] buf, uint len);
    [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
    [DllImport(Dll)] public static extern int nvmlDeviceGetTemperatureThreshold(IntPtr dev, int type, out uint t);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr dev, out uint min, out uint max);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr dev, out uint mw);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr dev, out uint mw);
    [DllImport(Dll)] public static extern int nvmlDeviceSetPowerManagementLimit(IntPtr dev, uint mw);
    [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMaxClockInfo(IntPtr dev, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMaxCustomerBoostClock(IntPtr dev, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Util u);
    [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint pct);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out MemInfo m);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr dev, out ulong r);
    [DllImport(Dll)] public static extern int nvmlDeviceGetGpcClkVfOffset(IntPtr dev, out int off);
    [DllImport(Dll)] public static extern int nvmlDeviceSetGpcClkVfOffset(IntPtr dev, int off);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemClkVfOffset(IntPtr dev, out int off);
    [DllImport(Dll)] public static extern int nvmlDeviceSetMemClkVfOffset(IntPtr dev, int off);
    [DllImport(Dll)] public static extern int nvmlDeviceGetGpcClkMinMaxVfOffset(IntPtr dev, out int min, out int max);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemClkMinMaxVfOffset(IntPtr dev, out int min, out int max);
    [DllImport(Dll)] public static extern int nvmlDeviceGetClockOffsets(IntPtr dev, ref ClockOffset info);
    [DllImport(Dll)] public static extern int nvmlDeviceSetClockOffsets(IntPtr dev, ref ClockOffset info);
    [DllImport(Dll)] public static extern int nvmlDeviceSetGpuLockedClocks(IntPtr dev, uint min, uint max);
    [DllImport(Dll)] public static extern int nvmlDeviceResetGpuLockedClocks(IntPtr dev);
    [DllImport(Dll)] public static extern int nvmlDeviceGetSupportedMemoryClocks(IntPtr dev, ref uint count, uint[] clocks);
    [DllImport(Dll)] public static extern int nvmlDeviceGetSupportedGraphicsClocks(IntPtr dev, uint mem, ref uint count, uint[] clocks);

    public static string Err(int r)
    {
        try { return Marshal.PtrToStringAnsi(nvmlErrorString(r)) ?? ("Fehler " + r); } catch { return "Fehler " + r; }
    }

    public static string Str(byte[] b) { int n = Array.IndexOf(b, (byte)0); return Encoding.ASCII.GetString(b, 0, n < 0 ? b.Length : n).Trim(); }

    /// <summary>Führt einen NVML-Aufruf aus; fehlt die Funktion im Treiber, gilt sie als "nicht unterstützt".</summary>
    public static int Call(Func<int> f)
    {
        try { return f(); }
        catch (EntryPointNotFoundException) { return NOT_SUPPORTED; }
        catch (DllNotFoundException) { return NOT_SUPPORTED; }
    }
}
