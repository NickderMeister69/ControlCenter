using System.Runtime.InteropServices;

namespace NickControlCenter;

/// <summary>Erkennt Intel-Hybrid-CPUs (P- und E-Kerne) und liefert die Maske der Leistungskerne.</summary>
public static class CpuTopology
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr info, uint length, out uint returned, IntPtr process, uint flags);

    private static readonly Lazy<(bool Hybrid, long Mask)> Info = new(Detect);
    public static bool IsHybrid => Info.Value.Hybrid;
    /// <summary>Bitmaske der logischen Prozessoren auf Leistungskernen (Gruppe 0).</summary>
    public static long PerformanceMask => Info.Value.Mask;

    private static (bool, long) Detect()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint len, IntPtr.Zero, 0);
            if (len == 0) return (false, 0);
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0)) return (false, 0);
                var list = new List<(int Lp, byte Eff, ushort Group)>();
                int off = 0;
                while (off < len)
                {
                    int size = Marshal.ReadInt32(buf, off);
                    int type = Marshal.ReadInt32(buf, off + 4);
                    if (size <= 0) break;
                    if (type == 0)
                    {
                        ushort group = (ushort)Marshal.ReadInt16(buf, off + 12);
                        byte lp = Marshal.ReadByte(buf, off + 14);
                        byte eff = Marshal.ReadByte(buf, off + 18);
                        list.Add((lp, eff, group));
                    }
                    off += size;
                }
                var g0 = list.Where(x => x.Group == 0 && x.Lp < 64).ToList();
                if (g0.Select(x => x.Eff).Distinct().Count() < 2) return (false, 0);
                byte max = g0.Max(x => x.Eff);
                long mask = g0.Where(x => x.Eff == max).Aggregate(0L, (m, x) => m | (1L << x.Lp));
                return (true, mask);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return (false, 0); }
    }
}
