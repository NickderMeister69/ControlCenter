using System.Numerics;
using System.Runtime.InteropServices;

namespace NickControlCenter;

public enum CpuStressMode { Mixed, Float, Vector, Integer }

/// <summary>
/// Rechenkerne für den CPU-Stresstest. Jeder Kern rechnet ein festes Paket und liefert ein Ergebnis,
/// das bei stabiler CPU immer bit-genau gleich ist – Abweichungen = Rechenfehler (instabil/überhitzt).
/// Läuft auf jedem x64-Prozessor (Intel, AMD); "Vector" nutzt automatisch SSE/AVX2/AVX-512, was die CPU kann.
/// </summary>
public static class StressKernels
{
    public static double FloatChunk(int seed, int iterations = 400_000)
    {
        double x = 1.0001 + seed * 0.01, y = 0.9999, z = 1.5;
        for (int i = 0; i < iterations; i++)
        {
            x = Math.Sqrt(x * y + 1.25);
            y = Math.Sin(x) + 2.0;
            z = (z * x + y) % 97.0 + 1.0;
        }
        return x + y + z;
    }

    public static ulong IntegerChunk(int seed, int iterations = 2_000_000)
    {
        ulong a = 0x9E3779B97F4A7C15UL ^ (ulong)seed, b = 0xD1B54A32D192ED03UL, h = 1469598103934665603UL;
        for (int i = 0; i < iterations; i++)
        {
            a ^= a << 13; a ^= a >> 7; a ^= a << 17;          // xorshift
            b = b * 6364136223846793005UL + 1442695040888963407UL; // LCG
            h = (h ^ (a + b)) * 1099511628211UL;               // FNV-Mix
            h = BitOperations.RotateLeft(h, (int)(a & 31));
        }
        return h;
    }

    /// <summary>SIMD-Last (AVX/AVX2 wenn vorhanden). Ergebnis als Bits, damit es exakt verglichen werden kann.</summary>
    public static ulong VectorChunk(int seed, int iterations = 200_000)
    {
        int w = Vector<float>.Count;
        var arrA = new float[w]; var arrB = new float[w]; var arrC = new float[w];
        for (int i = 0; i < w; i++) { arrA[i] = 0.5f + (seed + i) * 0.001f; arrB[i] = 0.25f + i * 0.0001f; arrC[i] = 0.75f; }
        var a = new Vector<float>(arrA); var b = new Vector<float>(arrB); var c = new Vector<float>(arrC);
        var one = Vector<float>.One; var half = new Vector<float>(0.5f);
        for (int i = 0; i < iterations; i++)
        {
            a = a * b + c;
            b = b * c + half;
            c = c * a + b;
            // in [0,1) halten, ohne Division: x - floor(x) über Vergleich/Subtraktion
            a -= Vector.ConditionalSelect(Vector.GreaterThanOrEqual(a, one), Vector.Floor(a), Vector<float>.Zero);
            b -= Vector.ConditionalSelect(Vector.GreaterThanOrEqual(b, one), Vector.Floor(b), Vector<float>.Zero);
            c -= Vector.ConditionalSelect(Vector.GreaterThanOrEqual(c, one), Vector.Floor(c), Vector<float>.Zero);
        }
        var sum = a + b + c;
        ulong h = 0;
        for (int i = 0; i < w; i++) h = h * 31 + BitConverter.SingleToUInt32Bits(sum[i]);
        return h;
    }

    /// <summary>Ein Arbeitspaket des gewählten Modus. Liefert einen Prüfwert.</summary>
    public static ulong Run(CpuStressMode mode, int seed, int round) => mode switch
    {
        CpuStressMode.Float => (ulong)BitConverter.DoubleToInt64Bits(FloatChunk(seed)),
        CpuStressMode.Integer => IntegerChunk(seed),
        CpuStressMode.Vector => VectorChunk(seed),
        _ => (round % 3) switch
        {
            0 => (ulong)BitConverter.DoubleToInt64Bits(FloatChunk(seed)),
            1 => VectorChunk(seed),
            _ => IntegerChunk(seed)
        }
    };

    /// <summary>Wie viele "Rechenpunkte" ein Paket wert ist (für die Punktzahl, damit Modi vergleichbar bleiben).</summary>
    public static double Weight(CpuStressMode mode, int round) => mode switch
    {
        CpuStressMode.Float => 1.0,
        CpuStressMode.Integer => 1.0,
        CpuStressMode.Vector => 1.0,
        _ => 1.0
    };
}

/// <summary>Arbeitsspeicher-Test: schreibt Muster in einen Block und prüft sie wieder.</summary>
public sealed class MemoryPattern : IDisposable
{
    private IntPtr _mem;
    public long Bytes { get; }

    public MemoryPattern(long bytes)
    {
        Bytes = bytes / 8 * 8;
        _mem = Marshal.AllocHGlobal((IntPtr)Bytes);
    }

    /// <summary>Schreibt ein Muster und prüft es. Liefert die Anzahl falscher Werte.</summary>
    public unsafe long Pass(ulong seed, CancellationToken ct)
    {
        if (_mem == IntPtr.Zero) return 0;
        long n = Bytes / 8;
        ulong* p = (ulong*)_mem;
        ulong x = seed | 1;
        for (long i = 0; i < n; i++)
        {
            if ((i & 0xFFFFF) == 0 && ct.IsCancellationRequested) return 0;
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            p[i] = x;
        }
        long errors = 0;
        x = seed | 1;
        for (long i = 0; i < n; i++)
        {
            if ((i & 0xFFFFF) == 0 && ct.IsCancellationRequested) return errors;
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            if (p[i] != x) errors++;
        }
        return errors;
    }

    public void Dispose()
    {
        if (_mem != IntPtr.Zero) { Marshal.FreeHGlobal(_mem); _mem = IntPtr.Zero; }
    }
}
