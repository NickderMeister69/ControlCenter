using System.Windows;
using System.Windows.Media;

namespace NickControlCenter;

/// <summary>Allgemeine Einstellungen der App (einstellungen.json).</summary>
public sealed class AppSettings
{
    public string Accent { get; set; } = "#22D3EE";
    public bool CloseToTray { get; set; } = true;
    public bool Autostart { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public bool ShortcutsCreated { get; set; }
    public bool TrayHintShown { get; set; }
    public bool Fahrenheit { get; set; }
    public SourcePreference SensorSource { get; set; } = SourcePreference.Auto;
    public int SensorIntervalMs { get; set; } = 1000;
    public string LastPage { get; set; } = "Dashboard";
    /// <summary>Vom Nutzer ausgeblendete Hinweise im Problemlöser (Id).</summary>
    public List<string> IgnoredFindings { get; set; } = new();
    /// <summary>Vom Problemlöser deaktivierte Dienste (zum Wieder-Aktivieren): Name → alter Starttyp.</summary>
    public Dictionary<string, string> DisabledServices { get; set; } = new();
    public double WindowWidth { get; set; } = 1320;
    public double WindowHeight { get; set; } = 860;

    public static AppSettings Current { get; private set; } = new();

    public static void Load() => Current = JsonStore.Load<AppSettings>("einstellungen.json");
    public void Save() => JsonStore.Save("einstellungen.json", this);

    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Cyan", "#22D3EE"), ("Neon-Grün", "#A3E635"), ("Smaragd", "#34D399"), ("Orange", "#FB923C"),
        ("Rot", "#F43F5E"), ("Pink", "#F472B6"), ("Violett", "#A78BFA"), ("Blau", "#60A5FA"), ("Gold", "#FACC15")
    };

    /// <summary>Akzentfarbe live in der ganzen App tauschen.</summary>
    public static void ApplyAccent(string hex)
    {
        var app = Application.Current;
        if (app == null || !RgbF.TryParseHex(hex, out var c)) return;
        var col = Color.FromRgb(c.R8, c.G8, c.B8);
        app.Resources["AccentColor"] = col;
        app.Resources["AccentBrush"] = Freeze(new SolidColorBrush(col));
        app.Resources["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x24, col.R, col.G, col.B)));
        app.Resources["AccentDimBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x70, col.R, col.G, col.B)));
        // Text auf Akzent: dunkel bei hellen Farben, sonst weiß
        double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        app.Resources["OnAccentBrush"] = Freeze(new SolidColorBrush(lum > 0.55 ? Color.FromRgb(7, 16, 22) : Colors.White));
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}
