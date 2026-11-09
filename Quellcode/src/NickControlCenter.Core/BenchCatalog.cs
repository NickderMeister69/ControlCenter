namespace NickControlCenter;

/// <summary>Ein gespeichertes Stresstest-Ergebnis (eigene Messungen zum Vergleichen vorher/nachher).</summary>
public sealed class BenchResult
{
    public DateTime Date { get; set; }
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string Mode { get; set; } = "";
    public double CpuScore { get; set; }
    public double GpuScore { get; set; }
    public double MaxCpuTemp { get; set; } = double.NaN;
    public double MaxGpuTemp { get; set; } = double.NaN;
    public int Seconds { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Eine Zeile im Vergleich.</summary>
public sealed record BenchCompare(string Name, double Score, double Percent, bool Mine)
{
    /// <summary>Balkenlänge 0…1 (bezogen auf den Stärksten der Liste).</summary>
    public double Fraction { get; init; }
}

/// <summary>
/// Vergleich mit anderen CPUs/Grafikkarten. Die Punkte des Stresstests sind eine eigene Einheit – deshalb wird
/// über bekannte Richtwerte umgerechnet: dein gemessenes Ergebnis × (Richtwert der anderen ÷ Richtwert deiner Hardware).
/// Richtwerte (gerundet): CPU = Cinebench R23 Multi-Core, GPU = 3DMark Time Spy Graphics.
/// </summary>
public static class BenchCatalog
{
    public static readonly (string Name, double Ref)[] Cpus =
    {
        ("Intel Core i3-12100F", 8500), ("Intel Core i3-13100F", 9000), ("Intel Core i5-12400F", 12000), ("Intel Core i5-13400F", 15800),
        ("Intel Core i5-14400F", 17000), ("Intel Core i5-12600K", 17500), ("Intel Core i5-13600K", 24000), ("Intel Core i5-14600K", 25000),
        ("Intel Core i7-12700K", 22500), ("Intel Core i7-13700K", 30500), ("Intel Core i7-14700K", 35000), ("Intel Core i9-12900K", 27000),
        ("Intel Core i9-13900K", 38500), ("Intel Core i9-14900K", 39000), ("Intel Core Ultra 5 245K", 24500), ("Intel Core Ultra 7 265K", 36000),
        ("Intel Core Ultra 9 285K", 42000), ("Intel Core i7-9700K", 9500), ("Intel Core i5-10400F", 9000), ("Intel Core i7-10700K", 13500),
        ("AMD Ryzen 5 3600", 9000), ("AMD Ryzen 5 5600", 10800), ("AMD Ryzen 5 5600X", 11000), ("AMD Ryzen 7 5700X", 13800),
        ("AMD Ryzen 7 5800X", 15200), ("AMD Ryzen 7 5800X3D", 14500), ("AMD Ryzen 9 5900X", 21500), ("AMD Ryzen 9 5950X", 26000),
        ("AMD Ryzen 5 7600", 14300), ("AMD Ryzen 5 7600X", 15000), ("AMD Ryzen 7 7700X", 19500), ("AMD Ryzen 7 7800X3D", 18000),
        ("AMD Ryzen 9 7900X", 29000), ("AMD Ryzen 9 7950X", 38000), ("AMD Ryzen 5 9600X", 16000), ("AMD Ryzen 7 9700X", 20000),
        ("AMD Ryzen 7 9800X3D", 23000), ("AMD Ryzen 9 9950X", 42000),
    };

    public static readonly (string Name, double Ref)[] Gpus =
    {
        ("NVIDIA GeForce GTX 1060 6GB", 4200), ("NVIDIA GeForce GTX 1660 Super", 6100), ("NVIDIA GeForce GTX 1080 Ti", 9500),
        ("NVIDIA GeForce RTX 2060", 7700), ("NVIDIA GeForce RTX 2070 Super", 10000), ("NVIDIA GeForce RTX 2080 Ti", 13500),
        ("NVIDIA GeForce RTX 3050", 6200), ("NVIDIA GeForce RTX 3060", 8800), ("NVIDIA GeForce RTX 3060 Ti", 11700), ("NVIDIA GeForce RTX 3070", 13500),
        ("NVIDIA GeForce RTX 3080", 17500), ("NVIDIA GeForce RTX 3090", 19500), ("NVIDIA GeForce RTX 4060", 10600), ("NVIDIA GeForce RTX 4060 Ti", 13500),
        ("NVIDIA GeForce RTX 4070", 17800), ("NVIDIA GeForce RTX 4070 Super", 20500), ("NVIDIA GeForce RTX 4070 Ti", 22700),
        ("NVIDIA GeForce RTX 4070 Ti Super", 24000), ("NVIDIA GeForce RTX 4080", 28000), ("NVIDIA GeForce RTX 4080 Super", 28500),
        ("NVIDIA GeForce RTX 4090", 36000), ("NVIDIA GeForce RTX 5060", 12500), ("NVIDIA GeForce RTX 5060 Ti", 15000), ("NVIDIA GeForce RTX 5070", 22500),
        ("NVIDIA GeForce RTX 5070 Ti", 27500), ("NVIDIA GeForce RTX 5080", 32000), ("NVIDIA GeForce RTX 5090", 48000),
        ("AMD Radeon RX 6600", 8000), ("AMD Radeon RX 6650 XT", 9500), ("AMD Radeon RX 6700 XT", 12500), ("AMD Radeon RX 6800 XT", 18500),
        ("AMD Radeon RX 7600", 10800), ("AMD Radeon RX 7700 XT", 16800), ("AMD Radeon RX 7800 XT", 19800), ("AMD Radeon RX 7900 GRE", 21500),
        ("AMD Radeon RX 7900 XT", 26500), ("AMD Radeon RX 7900 XTX", 29500), ("AMD Radeon RX 9060 XT", 15000), ("AMD Radeon RX 9070", 25500),
        ("AMD Radeon RX 9070 XT", 29000), ("Intel Arc A750", 12500), ("Intel Arc A770", 13500), ("Intel Arc B580", 14500),
    };

    private static string Norm(string s) => new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray())
        .Replace("INTEL", "").Replace("AMD", "").Replace("NVIDIA", "").Replace("GEFORCE", "").Replace("RADEON", "").Replace("CORE", "")
        .Replace("13THGEN", "").Replace("12THGEN", "").Replace("14THGEN", "").Replace("TM", "").Replace("R", "");

    /// <summary>Passenden Eintrag finden (längster Treffer gewinnt, damit „4060 Ti“ nicht als „4060“ zählt).</summary>
    public static (string Name, double Ref)? Find((string Name, double Ref)[] list, string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware)) return null;
        var h = Norm(hardware);
        (string, double)? best = null; int bestLen = 0;
        foreach (var e in list)
        {
            var k = Norm(e.Name);
            if (k.Length > bestLen && h.Contains(k)) { best = e; bestLen = k.Length; }
        }
        return best;
    }

    /// <summary>
    /// Vergleichsliste: deine Hardware mit deinem Ergebnis plus geschätzte Punkte der anderen.
    /// Percent = Leistung relativ zu dir (100 = gleich schnell).
    /// </summary>
    public static List<BenchCompare> Compare((string Name, double Ref)[] list, string mine, double myScore)
    {
        var res = new List<BenchCompare>();
        var me = Find(list, mine);
        if (me == null || myScore <= 0) return res;
        foreach (var e in list)
        {
            bool isMe = e.Name == me.Value.Name;
            double score = isMe ? myScore : myScore * e.Ref / me.Value.Ref;
            res.Add(new BenchCompare(isMe ? e.Name + "  (DU)" : e.Name, score, e.Ref / me.Value.Ref * 100, isMe));
        }
        double max = res.Max(r => r.Score);
        return res.OrderByDescending(r => r.Score).Select(r => r with { Fraction = r.Score / max }).ToList();
    }
}
