namespace NickControlCenter;

/// <summary>Was die App über den PC weiß – Grundlage für die Empfehlung.</summary>
public sealed record PcInfo(
    string GpuName, double VramGb, string CpuName, int Cores, int Threads, double RamGb,
    int Width, int Height, int RefreshHz);

/// <summary>Empfohlene Einstellungen für ein Spiel auf diesem PC – mit Begründung.</summary>
public sealed class Recommendation
{
    public GamePreset Preset { get; set; }
    public int Priority { get; set; }
    public int GfxQuality { get; set; }
    public int FpsLimit { get; set; }
    public int ResScale { get; set; }
    public bool CloseApps { get; set; }
    public bool CleanRam { get; set; }
    public bool VsyncOff { get; set; } = true;
    public int GpuTier { get; set; }
    public List<string> Reasons { get; } = new();
    public List<string> InGame { get; } = new();
}

/// <summary>
/// Schätzt aus Grafikkarte, Monitor, CPU und RAM, wie ein Spiel am besten läuft.
/// Faustregeln wie in Tuning-Guides: Leistungsklasse der GPU, minus eine Stufe pro höherer Auflösung,
/// FPS-Limit passend zur Bildwiederholrate, bei wenig RAM/Kernen Hintergrund aufräumen.
/// </summary>
public static class HardwareAdvisor
{
    public static readonly string[] TierNames = { "Einstieg", "Mittelklasse", "Obere Mittelklasse", "Oberklasse" };

    /// <summary>Leistungsklasse 0–3 aus dem GPU-Namen (und Grafikspeicher als Notlösung).</summary>
    public static int GpuTier(string name, double vramGb)
    {
        var n = name.ToUpperInvariant();
        bool Has(params string[] keys) => keys.Any(k => n.Contains(k));
        if (Has("4090", "4080", "5090", "5080", "7900 XT", "9070 XT", "3090")) return 3;
        if (Has("4070", "5070", "3080", "3070", "7800", "7700", "6800", "6900", "9070", "2080 TI")) return 2;
        if (Has("4060", "5060", "3060", "2080", "2070", "7600", "6700", "6650", "6600", "A770", "A750", "B580", "B570", "1080 TI")) return 1;
        if (Has("RTX", "RX ", "ARC", "GTX 16", "GTX 10")) return 0;
        return vramGb >= 12 ? 2 : vramGb >= 8 ? 1 : 0;
    }

    public static Recommendation Recommend(PcInfo pc, KnownGame? game, bool configAvailable)
    {
        var r = new Recommendation();
        bool competitive = game?.Competitive ?? false;
        int tier = GpuTier(pc.GpuName, pc.VramGb);
        r.GpuTier = tier;
        r.Reasons.Add($"Grafikkarte {pc.GpuName}: {TierNames[tier]}" + (pc.VramGb > 0 ? $" ({pc.VramGb:0} GB Grafikspeicher)" : ""));

        // höhere Auflösung frisst Leistung: 1440p eine Stufe, 4K zwei Stufen weniger
        int px = pc.Width * pc.Height;
        int resPenalty = px >= 3840 * 2160 * 0.9 ? 2 : px >= 2560 * 1440 * 0.9 ? 1 : 0;
        int eff = Math.Max(0, tier - resPenalty);
        r.Reasons.Add($"Monitor {pc.Width}×{pc.Height} mit {pc.RefreshHz} Hz" + (resPenalty > 0 ? $" – hohe Auflösung kostet Leistung (−{resPenalty} Stufe)" : ""));

        if (competitive)
        {
            // Shooter: so viele FPS wie möglich, Grafik runter – Sichtbarkeit und Reaktionszeit zählen
            r.Preset = GamePreset.MaxFps;
            r.GfxQuality = 1;
            r.Priority = 2;
            r.FpsLimit = 1; // unbegrenzt – mit Reflex/Anti-Lag kommt das niedrigste Input-Lag
            r.Reasons.Add("Wettkampf-Spiel: niedrige Grafik und unbegrenzte FPS – weniger Verzögerung beim Zielen");
            r.InGame.Add("NVIDIA Reflex „An + Boost“ bzw. AMD Anti-Lag einschalten");
            r.InGame.Add($"Hast du G-Sync/FreeSync: FPS im Treiber auf {Math.Max(30, pc.RefreshHz - 3)} begrenzen – kein Tearing, kaum mehr Lag");
        }
        else
        {
            r.Preset = eff >= 2 ? GamePreset.Balanced : GamePreset.MaxFps;
            r.GfxQuality = eff switch { >= 3 => 3, 2 => 2, 1 => 2, _ => 1 };
            r.Priority = eff >= 2 ? 1 : 2;
            r.FpsLimit = NearestLimit(pc.RefreshHz);
            r.Reasons.Add($"Grafik „{GameCatalog.QualityNames[r.GfxQuality].Split(' ')[0]}“ passt zu deiner Leistungsklasse; FPS-Limit = Bildwiederholrate spart Strom, Hitze und Ruckler");
            if (eff == 0 && pc.Height >= 1440) { r.ResScale = 4; r.Reasons.Add("Auflösungs-Skalierung 75 % – bei hoher Auflösung und schwächerer Karte der größte FPS-Gewinn"); }
            if (tier >= 1) r.InGame.Add(pc.GpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase) ? "DLSS auf „Qualität“ (bei RTX 40/50 zusätzlich Frame Generation)" : "FSR/XeSS auf „Qualität“");
        }

        if (pc.RamGb > 0 && pc.RamGb <= 16.5)
        {
            r.CloseApps = true; r.CleanRam = true;
            r.Reasons.Add($"{pc.RamGb:0} GB RAM: Hintergrund-Apps schließen und RAM freigeben hilft gegen Nachladeruckler");
        }
        else r.CleanRam = pc.RamGb <= 24;
        if (pc.Cores > 0 && pc.Cores <= 6)
        {
            r.Priority = 2; r.CloseApps = true;
            r.Reasons.Add($"{pc.Cores} CPU-Kerne: hohe Priorität fürs Spiel, damit Hintergrund-Programme nicht dazwischenfunken");
        }
        if (!configAvailable) r.InGame.Add("Grafik kann bei diesem Spiel nur im Spiel selbst eingestellt werden – die Werte oben sind die Empfehlung dafür");
        if (pc.RefreshHz >= 100) r.InGame.Add($"In Windows prüfen: Anzeige → Erweitert → {pc.RefreshHz} Hz ausgewählt");
        return r;
    }

    /// <summary>Index in <see cref="GameCatalog.FpsLimits"/>, der am besten zur Bildwiederholrate passt.</summary>
    public static int NearestLimit(int hz)
    {
        if (hz <= 0) return 0;
        int best = 1; int diff = int.MaxValue;
        for (int i = 2; i < GameCatalog.FpsLimits.Length; i++)
        {
            int d = Math.Abs(GameCatalog.FpsLimits[i] - hz);
            if (d < diff) { diff = d; best = i; }
        }
        return diff <= 10 ? best : 1;
    }

    public static void ApplyTo(GameProfile p, Recommendation r, bool configAvailable)
    {
        p.Preset = GamePreset.Custom;
        p.Priority = r.Priority;
        p.ExeTweaks = true; p.Timer = true; p.PowerPlan = true;
        p.CloseApps = r.CloseApps; p.CleanRam = r.CleanRam;
        p.Graphics = configAvailable;
        p.GfxQuality = r.GfxQuality; p.FpsLimit = r.FpsLimit; p.ResScale = r.ResScale; p.VsyncOff = r.VsyncOff;
    }
}
