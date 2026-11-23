namespace NickControlCenter;

/// <summary>
/// Macht Beispiele in den „i“-Erklärungen passend zur Hardware des jeweiligen PCs – jeder sieht Werte für seine eigene CPU/Grafikkarte.
/// </summary>
public static class HardwareHints
{
    // Turbo-Takt (GHz) laut Hersteller für verbreitete CPUs
    private static readonly (string Key, double Ghz)[] Turbo =
    {
        ("14900K", 6.0), ("14700K", 5.6), ("14600K", 5.3), ("14400F", 4.7), ("14400", 4.7), ("14100F", 4.7),
        ("13900K", 5.8), ("13700K", 5.4), ("13600K", 5.1), ("13500", 4.8), ("13400F", 4.6), ("13400", 4.6), ("13100F", 4.5),
        ("12900K", 5.2), ("12700K", 5.0), ("12600K", 4.9), ("12400F", 4.4), ("12400", 4.4), ("12100F", 4.3),
        ("9800X3D", 5.2), ("9700X", 5.5), ("9600X", 5.4), ("7950X3D", 5.7), ("7800X3D", 5.0), ("7700X", 5.4), ("7600X", 5.3), ("7600", 5.1),
        ("5800X3D", 4.5), ("5700X3D", 4.1), ("5800X", 4.7), ("5700X", 4.6), ("5600X", 4.6), ("5600", 4.4), ("5500", 4.2),
    };

    private static string Cpu => Short(HardwareHub.Instance.Latest.CpuName);
    private static string Gpu => Short(HardwareHub.Instance.Latest.GpuName);

    private static string Short(string? n) => (n ?? "").Replace("Intel(R) Core(TM)", "").Replace("Intel Core", "").Replace("AMD Ryzen", "Ryzen")
        .Replace("NVIDIA GeForce", "").Replace("AMD Radeon", "Radeon").Replace("Processor", "").Replace("CPU", "").Trim();

    private static double? TurboGhz()
    {
        var name = HardwareHub.Instance.Latest.CpuName ?? "";
        foreach (var (k, g) in Turbo) if (name.Contains(k, StringComparison.OrdinalIgnoreCase)) return g;
        return null;
    }

    /// <summary>Beispiele für eine Überschrift – mit einer zusätzlichen Zeile für diesen PC, wenn es passt.</summary>
    public static IEnumerable<string> For(string key, string[] examples)
    {
        string? mine = null;
        try { mine = Line(key); } catch { }
        if (mine != null) yield return mine;
        foreach (var e in examples) yield return e;
    }

    private static string? Line(string key)
    {
        var s = HardwareHub.Instance.Latest;
        var t = TurboGhz();
        switch (key)
        {
            case "Echten Turbo-Takt messen":
                return t is { } g ? $"Dein {Cpu}: ca. {g:0.0} GHz erwartet" : Cpu.Length > 0 ? $"Dein {Cpu}: Turbo-Takt steht im Datenblatt des Herstellers" : null;
            case "Max. Takt":
                return t is { } g2 ? $"Bei deinem {Cpu}: z. B. {g2 * 0.88:0.0} statt {g2:0.0} GHz – kaum langsamer, deutlich kühler" : null;
            case "Turbo-Modus":
                return t is { } g3 ? $"Dein {Cpu} taktet mit Turbo bis ca. {g3:0.0} GHz" : null;
            case "Takt-Obergrenze":
                if (Gpu.Length == 0) return null;
                return Gpu.Contains("4060") && !Gpu.Contains("Ti") ? $"Deine {Gpu}: z. B. 2600 MHz – kaum weniger FPS, deutlich kühler"
                     : $"Deine {Gpu}: etwa 150–200 MHz unter dem Höchsttakt, den du im Monitor beim Spielen siehst";
            case "Speicher-Takt":
                return Gpu.Contains("RTX 40") || Gpu.Contains("RTX 50") ? $"Deine {Gpu}: +500 MHz meist sicher" : null;
            case "STROMVERBRAUCH":
            {
                if (Cpu.Length == 0 && Gpu.Length == 0) return null;
                var tier = HardwareAdvisor.GpuTier(s.GpuName ?? "", double.IsNaN(s.GpuVramTotalGb) ? 8 : s.GpuVramTotalGb);
                var (lo, hi) = tier switch { >= 3 => (450, 700), 2 => (350, 550), 1 => (250, 400), _ => (150, 300) };
                return $"Dein PC ({Cpu}{(Gpu.Length > 0 ? " + " + Gpu : "")}): beim Zocken ca. {lo}–{hi} W";
            }
        }
        return null;
    }
}
