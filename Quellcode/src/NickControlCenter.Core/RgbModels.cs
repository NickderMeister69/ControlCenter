namespace NickControlCenter;

public enum LightEffect { Static, Slideshow, Rainbow, Breathing, Wave, Comet, Temperature, Screen, Off }
public enum ScreenRegion { All, Left, Right, Top, Bottom, Center }
public enum TempSource { Cpu, Gpu, Hottest }

// ======================================================================= gespeicherte Einstellungen

public sealed class EffectConfig
{
    public LightEffect Type { get; set; } = LightEffect.Static;
    public List<string> Colors { get; set; } = new() { "#FF0000" };
    /// <summary>Diashow: Sekunden pro Farbe. Atmen: Sekunden pro Atemzug.</summary>
    public double Seconds { get; set; } = 4;
    public bool Fade { get; set; } = true;
    /// <summary>Regenbogen/Welle/Komet: Tempo (Umdrehungen pro 10 s).</summary>
    public double Speed { get; set; } = 1;
    public bool Wave { get; set; } = true;
    /// <summary>Komet: Länge des Schweifs (0.05 … 1 der LEDs).</summary>
    public double Tail { get; set; } = 0.35;
    public ScreenRegion Region { get; set; } = ScreenRegion.All;
    /// <summary>Bildschirm: Farbverstärkung (1 = neutral).</summary>
    public double Boost { get; set; } = 1.3;
    /// <summary>Bildschirm: Glättung 0 (sofort) … 0.95 (sehr träge).</summary>
    public double Smooth { get; set; } = 0.45;
    /// <summary>Temperatur-Effekt: Quelle und Bereich (kalt → heiß).</summary>
    public TempSource TempSource { get; set; } = TempSource.Hottest;
    public double TempLow { get; set; } = 40;
    public double TempHigh { get; set; } = 85;

    public EffectConfig Clone() => new()
    {
        Type = Type, Colors = new List<string>(Colors), Seconds = Seconds, Fade = Fade, Speed = Speed, Wave = Wave, Tail = Tail,
        Region = Region, Boost = Boost, Smooth = Smooth, TempSource = TempSource, TempLow = TempLow, TempHigh = TempHigh
    };
}

public sealed class LightFanConfig
{
    public string? Name { get; set; }
    public bool FollowSync { get; set; } = true;
    public EffectConfig Effect { get; set; } = new();
}

public sealed class LightZoneConfig
{
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public int FanCount { get; set; } = 1;
    /// <summary>LED-Anzahl am ARGB-Anschluss (0 = Standard).</summary>
    public int LedCount { get; set; }
    /// <summary>Bildschirm-Effekt: eigener Bereich für diesen Anschluss (-1 = wie eingestellt).</summary>
    public int ScreenRegion { get; set; } = -1;
    /// <summary>true = dieser Anschluss bekommt einen eigenen Effekt / eigene Farben.</summary>
    public bool OwnEffect { get; set; }
    public EffectConfig Effect { get; set; } = new();
    /// <summary>Flacker-Schutz: Anschluss wird seltener und nur bei sichtbarer Änderung aktualisiert.</summary>
    public bool FlickerGuard { get; set; }
    /// <summary>Gamma-Korrektur: 0 = automatisch (an für ARGB-Lüfter/Streifen, aus für Razer & Co.), 1 = an, 2 = aus.</summary>
    public int Gamma { get; set; }
    /// <summary>Farbabgleich (Weißabgleich) je Kanal 0…1 – damit alle Geräte die gleiche Farbe zeigen.</summary>
    public double GainR { get; set; } = 1;
    public double GainG { get; set; } = 1;
    public double GainB { get; set; } = 1;
    /// <summary>Farbreihenfolge der LEDs: 0 = automatisch, sonst Index in <see cref="ColorOrders"/>.</summary>
    public int ColorOrder { get; set; }
    public static readonly string[] ColorOrderNames = { "Automatisch", "RGB", "RBG", "GRB", "GBR", "BRG", "BGR" };
    /// <summary>Position von R, G, B im gesendeten Tripel je Reihenfolge.</summary>
    public static int[]? OrderMap(int order) => order switch
    {
        // Ausgabe[0..2] = Eingabe-Kanal
        1 => new[] { 0, 1, 2 }, 2 => new[] { 0, 2, 1 }, 3 => new[] { 1, 0, 2 }, 4 => new[] { 1, 2, 0 }, 5 => new[] { 2, 0, 1 }, 6 => new[] { 2, 1, 0 }, _ => null
    };
    /// <summary>Alte Daten (vor v4) – wird nur noch gelesen.</summary>
    public List<LightFanConfig> Fans { get; set; } = new();
}

public sealed class RgbProfile
{
    public string Name { get; set; } = "";
    public EffectConfig Effect { get; set; } = new();
    public double Brightness { get; set; } = 1;
}

public sealed class RgbConfig
{
    public bool Enabled { get; set; } = true;
    public bool Sync { get; set; } = true;
    public double Brightness { get; set; } = 1.0;
    public EffectConfig Global { get; set; } = new()
    {
        Type = LightEffect.Slideshow,
        Colors = new() { "#FF0000", "#0000FF", "#00FF00", "#FF00FF", "#00FFFF" },
        Seconds = 5,
        Fade = true
    };
    public Dictionary<string, LightZoneConfig> Zones { get; set; } = new();
    /// <summary>Prozesse, deren Warnung der Nutzer ausgeblendet hat.</summary>
    public List<string> IgnoredConflicts { get; set; } = new();
    /// <summary>Vom Nutzer gespeicherte eigene Farben (Palette im Farbwähler).</summary>
    public List<string> SavedColors { get; set; } = new();
    public List<RgbProfile> Profiles { get; set; } = new();
    /// <summary>Nachtmodus: Beleuchtung zwischen NightFrom und NightTo Uhr aus.</summary>
    public bool NightMode { get; set; }
    public int NightFrom { get; set; } = 23;
    public int NightTo { get; set; } = 7;
    /// <summary>Wiederholtes Senden (gegen Programme, die die Farben "zurückholen") – in Sekunden, 0 = aus.</summary>
    public int KeepAliveSeconds { get; set; }
    /// <summary>Einmalige Korrektur: ARGB-Anschlüsse auf alle LEDs stellen (sonst bleiben hintere LEDs weiß/alt).</summary>
    public bool LedFixDone { get; set; }

    public static bool IsNight(int from, int to, int hour) =>
        from == to ? false : from < to ? hour >= from && hour < to : hour >= from || hour < to;
}

// ======================================================================= Farbe

public readonly struct RgbF : IEquatable<RgbF>
{
    public readonly double R, G, B;
    public RgbF(double r, double g, double b) { R = r; G = g; B = b; }
    public static readonly RgbF Black = new(0, 0, 0);
    public static readonly RgbF White = new(1, 1, 1);

    public static bool TryParseHex(string? hex, out RgbF c)
    {
        c = Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        c = new(((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
        return true;
    }

    public static RgbF FromHex(string? hex) => TryParseHex(hex, out var c) ? c : Black;
    public string Hex => $"#{R8:X2}{G8:X2}{B8:X2}";

    public static RgbF Lerp(RgbF a, RgbF b, double t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    public RgbF Scale(double f) => new(R * f, G * f, B * f);

    public byte R8 => (byte)Math.Clamp(Math.Round(R * 255), 0, 255);
    public byte G8 => (byte)Math.Clamp(Math.Round(G * 255), 0, 255);
    public byte B8 => (byte)Math.Clamp(Math.Round(B * 255), 0, 255);

    public static RgbF FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new(r + m, g + m, b + m);
    }

    public void ToHsv(out double h, out double s, out double v)
    {
        double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == R) h = 60 * (((G - B) / d) % 6);
        else if (max == G) h = 60 * ((B - R) / d + 2);
        else h = 60 * ((R - G) / d + 4);
        if (h < 0) h += 360;
    }

    public bool Equals(RgbF o) => R8 == o.R8 && G8 == o.G8 && B8 == o.B8;
    public override bool Equals(object? obj) => obj is RgbF o && Equals(o);
    public override int GetHashCode() => (R8 << 16) | (G8 << 8) | B8;
}

/// <summary>Durchschnittsfarben der Bildschirmbereiche (wird vom Sampler ständig aktualisiert).</summary>
public sealed class ScreenColors
{
    private readonly RgbF[] _c = new RgbF[6];
    private readonly object _gate = new();

    public RgbF Get(ScreenRegion r) { lock (_gate) return _c[(int)r]; }
    public void Set(RgbF[] values) { lock (_gate) Array.Copy(values, _c, 6); }
}

/// <summary>Was ein Effekt außer der Zeit noch wissen muss.</summary>
public sealed class EffectInputs
{
    public ScreenColors? Screen { get; init; }
    public double CpuTemp { get; init; } = double.NaN;
    public double GpuTemp { get; init; } = double.NaN;
}

// ======================================================================= Laufzeit (unveränderlich, für den Effekt-Thread)

public sealed class EffectSnap
{
    public LightEffect Type { get; init; }
    public RgbF[] Colors { get; init; } = { RgbF.Black };
    public double Seconds { get; init; } = 4;
    public bool Fade { get; init; } = true;
    public double Speed { get; init; } = 1;
    public bool Wave { get; init; } = true;
    public double Tail { get; init; } = 0.35;
    public ScreenRegion Region { get; init; }
    public double Boost { get; init; } = 1.4;
    public double Smooth { get; init; } = 0.6;
    public TempSource TempSource { get; init; }
    public double TempLow { get; init; } = 40;
    public double TempHigh { get; init; } = 85;

    public static EffectSnap From(EffectConfig c) => new()
    {
        Type = c.Type,
        Colors = c.Colors.Count == 0 ? new[] { RgbF.White } : c.Colors.Select(RgbF.FromHex).ToArray(),
        Seconds = Math.Max(0.5, c.Seconds),
        Fade = c.Fade,
        Speed = Math.Clamp(c.Speed, 0.05, 10),
        Wave = c.Wave,
        Tail = Math.Clamp(c.Tail, 0.05, 1),
        Region = c.Region,
        Boost = c.Boost,
        Smooth = Math.Clamp(c.Smooth, 0, 0.97),
        TempSource = c.TempSource,
        TempLow = Math.Min(c.TempLow, c.TempHigh - 1),
        TempHigh = Math.Max(c.TempHigh, c.TempLow + 1)
    };

    /// <summary>Animiert sich der Effekt (muss laufend gesendet werden)?</summary>
    public bool Animated => Type switch
    {
        LightEffect.Static or LightEffect.Off => false,
        LightEffect.Slideshow => Colors.Length > 1,
        _ => true
    };
}

public static class EffectMath
{
    /// <summary>Standardfarben für den Temperatur-Effekt: kalt (blau) → warm (grün/gelb) → heiß (rot).</summary>
    public static readonly RgbF[] TempGradient = { RgbF.FromHex("#0050FF"), RgbF.FromHex("#00FF60"), RgbF.FromHex("#FFB000"), RgbF.FromHex("#FF0000") };

    /// <summary>Farbe eines Effekts zum Zeitpunkt t (Sekunden) an Position pos (0…1 über die LEDs des Anschlusses).</summary>
    public static RgbF Eval(EffectSnap e, double t, double pos, EffectInputs? input)
    {
        switch (e.Type)
        {
            case LightEffect.Off:
                return RgbF.Black;

            case LightEffect.Static:
                return e.Colors[0];

            case LightEffect.Slideshow:
            {
                int n = e.Colors.Length;
                if (n == 1) return e.Colors[0];
                double period = e.Seconds;
                long idx = (long)Math.Floor(t / period);
                double frac = t / period - idx;
                var a = e.Colors[(int)(idx % n)];
                var b = e.Colors[(int)((idx + 1) % n)];
                if (!e.Fade) return a;
                const double hold = 0.55; // Farbe hält 55 % der Zeit, dann weiche Überblendung
                if (frac <= hold) return a;
                return RgbF.Lerp(a, b, Smooth((frac - hold) / (1 - hold)));
            }

            case LightEffect.Rainbow:
            {
                double hue = t * e.Speed * 36 + (e.Wave ? pos * 360 : 0);
                return RgbF.FromHsv(hue, 1, 1);
            }

            case LightEffect.Breathing:
            {
                int n = e.Colors.Length;
                long cycle = (long)Math.Floor(t / e.Seconds);
                double frac = t / e.Seconds - cycle;
                double level = 0.04 + 0.96 * (0.5 - 0.5 * Math.Cos(frac * 2 * Math.PI));
                return e.Colors[(int)(cycle % n)].Scale(level);
            }

            case LightEffect.Wave:
            {
                // Farbverlauf aus den eigenen Farben, der über die LEDs wandert
                double x = pos - t * e.Speed * 0.1;
                return Gradient(e.Colors, x - Math.Floor(x), cyclic: true);
            }

            case LightEffect.Comet:
            {
                // ein heller Punkt läuft um, mit weichem Schweif
                double head = (t * e.Speed * 0.1) % 1.0;
                double d = head - pos;
                if (d < 0) d += 1;
                if (d > e.Tail) return RgbF.Black;
                double f = 1 - d / e.Tail;
                var col = e.Colors[(int)((long)Math.Floor(t * e.Speed * 0.1) % e.Colors.Length)];
                return col.Scale(f * f);
            }

            case LightEffect.Temperature:
            {
                double temp = input == null ? double.NaN : e.TempSource switch
                {
                    TempSource.Cpu => input.CpuTemp,
                    TempSource.Gpu => input.GpuTemp,
                    _ => MaxValid(input.CpuTemp, input.GpuTemp)
                };
                if (double.IsNaN(temp)) return e.Colors.Length > 0 ? e.Colors[0].Scale(0.3) : RgbF.Black;
                double f = Math.Clamp((temp - e.TempLow) / (e.TempHigh - e.TempLow), 0, 1);
                var grad = e.Colors.Length >= 2 ? e.Colors : TempGradient;
                return Gradient(grad, f, cyclic: false);
            }

            case LightEffect.Screen:
            {
                if (input?.Screen == null) return RgbF.Black;
                var c = input.Screen.Get(e.Region);
                c.ToHsv(out var h, out var s, out var v);
                s = Math.Clamp(s * e.Boost, 0, 1);
                v = Math.Clamp(v * (0.75 + 0.25 * e.Boost), 0, 1);
                return RgbF.FromHsv(h, s, v);
            }
        }
        return RgbF.Black;
    }

    private static double MaxValid(double a, double b) =>
        double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);

    private static double Smooth(double f) => f * f * (3 - 2 * f);

    /// <summary>Linearer Farbverlauf über mehrere Farben. f = 0…1.</summary>
    public static RgbF Gradient(RgbF[] colors, double f, bool cyclic)
    {
        int n = colors.Length;
        if (n == 0) return RgbF.Black;
        if (n == 1) return colors[0];
        f = Math.Clamp(f, 0, 1);
        int segs = cyclic ? n : n - 1;
        double p = f * segs;
        int i = Math.Min((int)Math.Floor(p), segs - 1);
        double local = p - i;
        return RgbF.Lerp(colors[i % n], colors[(i + 1) % n], local);
    }
}
