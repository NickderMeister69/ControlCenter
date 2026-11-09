using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace NickControlCenter;

// ============================================================================ Farbe

/// <summary>
/// Eine Farbe im Effekt. Vorher gab es nur einen Farbton-Regler (immer volle Sättigung) und jede Bewegung
/// hat die Farbliste neu aufgebaut – dadurch ist der Regler beim Ziehen "abgerissen" und eigene Farben
/// ließen sich kaum einstellen. Jetzt: Farbton, Sättigung, Helligkeit, Hex-Eingabe, ohne Neuaufbau.
/// </summary>
public sealed class ColorItem : ObservableObject
{
    public EffectVM Owner { get; }
    private double _h, _s, _v;

    public ColorItem(EffectVM owner, string hex)
    {
        Owner = owner;
        RgbF.FromHex(hex).ToHsv(out _h, out _s, out _v);
    }

    public string Hex
    {
        get => RgbF.FromHsv(_h, _s, _v).Hex;
        set
        {
            if (!RgbF.TryParseHex(value, out var c)) return;
            c.ToHsv(out var h, out var s, out var v);
            if (s > 0.001 && v > 0.001) _h = h;
            _s = s; _v = v;
            Changed(true);
        }
    }

    /// <summary>Hex-Feld: wird erst bei gültiger Eingabe übernommen (Tippen zerstört nichts).</summary>
    public string HexInput
    {
        get => Hex;
        set { if (RgbF.TryParseHex(value, out _)) Hex = value; }
    }

    public double Hue { get => _h; set { _h = Math.Clamp(value, 0, 360); if (_s < 0.05) _s = 1; if (_v < 0.05) _v = 1; Changed(false); } }
    public double Sat { get => _s * 100; set { _s = Math.Clamp(value / 100, 0, 1); Changed(false); } }
    public double Val { get => _v * 100; set { _v = Math.Clamp(value / 100, 0, 1); Changed(false); } }

    public byte R { get => RgbF.FromHsv(_h, _s, _v).R8; set => SetRgb(value, G, B); }
    public byte G { get => RgbF.FromHsv(_h, _s, _v).G8; set => SetRgb(R, value, B); }
    public byte B { get => RgbF.FromHsv(_h, _s, _v).B8; set => SetRgb(R, G, value); }

    private void SetRgb(byte r, byte g, byte b)
    {
        new RgbF(r / 255.0, g / 255.0, b / 255.0).ToHsv(out var h, out var s, out var v);
        if (s > 0.001 && v > 0.001) _h = h;
        _s = s; _v = v;
        Changed(true);
    }

    public Brush Brush => Frozen(RgbF.FromHsv(_h, _s, _v));
    public Brush PureHueBrush => Frozen(RgbF.FromHsv(_h, 1, 1));
    public Brush SatTrack => Gradient(RgbF.FromHsv(_h, 0, Math.Max(_v, 0.3)), RgbF.FromHsv(_h, 1, Math.Max(_v, 0.3)));
    public Brush ValTrack => Gradient(RgbF.Black, RgbF.FromHsv(_h, _s, 1));

    private void Changed(bool all)
    {
        Raise(nameof(Hex)); Raise(nameof(HexInput)); Raise(nameof(Brush)); Raise(nameof(SatTrack)); Raise(nameof(ValTrack));
        Raise(nameof(R)); Raise(nameof(G)); Raise(nameof(B)); Raise(nameof(PureHueBrush));
        if (all) { Raise(nameof(Hue)); Raise(nameof(Sat)); Raise(nameof(Val)); }
        Owner.ColorsChanged(rebuildList: false);
    }

    public static Brush Frozen(RgbF c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R8, c.G8, c.B8));
        b.Freeze();
        return b;
    }

    private static Brush Gradient(RgbF a, RgbF b)
    {
        var g = new LinearGradientBrush(Color.FromRgb(a.R8, a.G8, a.B8), Color.FromRgb(b.R8, b.G8, b.B8), 0);
        g.Freeze();
        return g;
    }
}

// ============================================================================ Effekt

public sealed record EffectChoice(LightEffect Type, string Name, string Icon);

public sealed class EffectVM : ObservableObject
{
    public static string[] RegionNames { get; } = { "Ganzer Bildschirm", "Linke Seite", "Rechte Seite", "Oben", "Unten", "Mitte" };
    public static string[] TempSourceNames { get; } = { "CPU", "Grafikkarte", "Die heißere von beiden" };

    public static EffectChoice[] Choices { get; } =
    {
        new(LightEffect.Static, "Statisch", ""), new(LightEffect.Slideshow, "Diashow", ""),
        new(LightEffect.Rainbow, "Regenbogen", ""), new(LightEffect.Breathing, "Atmen", ""),
        new(LightEffect.Wave, "Welle", ""), new(LightEffect.Comet, "Komet", ""),
        new(LightEffect.Temperature, "Temperatur", ""), new(LightEffect.Screen, "Bildschirm", ""),
        new(LightEffect.Off, "Aus", ""),
    };

    private readonly EffectConfig _c;
    private readonly Action _changed;
    public ObservableCollection<ColorItem> Colors { get; } = new();

    public EffectVM(EffectConfig c, Action changed)
    {
        _c = c;
        _changed = changed;
        if (_c.Colors.Count == 0) _c.Colors.Add("#FF0000");
        foreach (var h in _c.Colors) Colors.Add(new ColorItem(this, h));
        _selected = Colors.FirstOrDefault();
    }

    public EffectConfig Config => _c;
    public EffectSnap Snap() => EffectSnap.From(_c);

    private void Changed(params string[] props)
    {
        foreach (var p in props) Raise(p);
        _changed();
    }

    public LightEffect Type
    {
        get => _c.Type;
        set
        {
            if (_c.Type == value) return;
            _c.Type = value;
            // Temperatur-Effekt startet mit sinnvollen Farben (blau → rot)
            if (value == LightEffect.Temperature && Colors.Count < 2) SetColors("#0050FF", "#00FF60", "#FFB000", "#FF0000");
            Changed(nameof(Type), nameof(ShowColors), nameof(ShowAddColor), nameof(ShowSeconds), nameof(SecondsLabel), nameof(ShowFade),
                    nameof(ShowSpeed), nameof(ShowWave), nameof(ShowTail), nameof(ShowScreen), nameof(ShowTemp), nameof(Description),
                    nameof(VisibleColors), nameof(CanRemove), nameof(ColorsLabel));
        }
    }

    public bool ShowColors => _c.Type is LightEffect.Static or LightEffect.Slideshow or LightEffect.Breathing or LightEffect.Wave or LightEffect.Comet or LightEffect.Temperature;
    public bool ShowAddColor => _c.Type is LightEffect.Slideshow or LightEffect.Breathing or LightEffect.Wave or LightEffect.Comet or LightEffect.Temperature;
    public bool ShowSeconds => _c.Type is LightEffect.Slideshow or LightEffect.Breathing;
    public bool ShowFade => _c.Type == LightEffect.Slideshow;
    public bool ShowSpeed => _c.Type is LightEffect.Rainbow or LightEffect.Wave or LightEffect.Comet;
    public bool ShowWave => _c.Type == LightEffect.Rainbow;
    public bool ShowTail => _c.Type == LightEffect.Comet;
    public bool ShowScreen => _c.Type == LightEffect.Screen;
    public bool ShowTemp => _c.Type == LightEffect.Temperature;
    public string SecondsLabel => _c.Type == LightEffect.Breathing ? "Atemzug" : "Pro Farbe";
    public string ColorsLabel => _c.Type == LightEffect.Temperature ? "Farben von kalt → heiß" : "Farben";

    /// <summary>Bei "Statisch" nur die erste Farbe zeigen.</summary>
    public IEnumerable<ColorItem> VisibleColors => _c.Type == LightEffect.Static ? Colors.Take(1) : Colors;

    public string Description => _c.Type switch
    {
        LightEffect.Static => "Eine feste Farbe.",
        LightEffect.Slideshow => "Wechselt nacheinander durch deine Farben – wie eine Diashow.",
        LightEffect.Rainbow => "Läuft durch alle Farben des Regenbogens.",
        LightEffect.Breathing => "Pulsiert langsam – bei mehreren Farben abwechselnd.",
        LightEffect.Wave => "Ein Verlauf aus deinen Farben wandert über die LEDs.",
        LightEffect.Comet => "Ein Lichtpunkt mit Schweif läuft um den Lüfter.",
        LightEffect.Temperature => "Die Farbe zeigt die Temperatur: kühl = erste Farbe, heiß = letzte Farbe.",
        LightEffect.Screen => "Übernimmt die Farben von dem, was gerade auf dem Bildschirm läuft (wie SignalRGB).",
        _ => "Beleuchtung aus."
    };

    // ---- Farben
    private ColorItem? _selected;
    /// <summary>Farbe, die gerade im Farbwähler bearbeitet wird.</summary>
    public ColorItem? Selected { get => _selected; set => Set(ref _selected, value); }

    public void SetColors(params string[] hex)
    {
        Colors.Clear();
        foreach (var h in hex) Colors.Add(new ColorItem(this, h));
        Selected = Colors.FirstOrDefault();
        ColorsChanged(true);
    }

    public void AddColor(string? hex = null)
    {
        string[] presets = { "#FF0000", "#0000FF", "#00FF00", "#FF00FF", "#00FFFF", "#FFB000", "#FFFFFF" };
        hex ??= presets.FirstOrDefault(p => Colors.All(c => !c.Hex.Equals(p, StringComparison.OrdinalIgnoreCase))) ?? "#FFFFFF";
        var item = new ColorItem(this, hex);
        if (_c.Type == LightEffect.Static && Colors.Count > 0) { Colors[0].Hex = hex; Selected = Colors[0]; return; }
        Colors.Add(item);
        Selected = item;
        ColorsChanged(true);
    }

    public void RemoveColor(ColorItem item)
    {
        if (Colors.Count <= 1) return;
        Colors.Remove(item);
        if (Selected == item) Selected = Colors.FirstOrDefault();
        ColorsChanged(true);
    }

    public void MoveColor(ColorItem item, int delta)
    {
        int i = Colors.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= Colors.Count) return;
        Colors.Move(i, j);
        ColorsChanged(true);
    }

    public bool CanRemove => Colors.Count > 1 && ShowAddColor;

    /// <summary>Farben übernehmen. Die Liste selbst nur neu aufbauen, wenn Farben hinzu/weg kamen –
    /// sonst verliert der gerade gezogene Regler die Maus (alter Fehler bei "eigene Farben").</summary>
    public void ColorsChanged(bool rebuildList)
    {
        _c.Colors = Colors.Select(c => c.Hex).ToList();
        if (rebuildList) Changed(nameof(VisibleColors), nameof(CanRemove));
        else _changed();
    }

    // ---- Parameter
    public double Seconds { get => _c.Seconds; set { _c.Seconds = Math.Round(Math.Clamp(value, 0.5, 60), 1); Changed(nameof(Seconds), nameof(SecondsText)); } }
    public string SecondsText => $"{_c.Seconds:0.#} s";
    public bool Fade { get => _c.Fade; set { _c.Fade = value; Changed(nameof(Fade)); } }
    public double Speed { get => _c.Speed; set { _c.Speed = Math.Round(Math.Clamp(value, 0.1, 5), 1); Changed(nameof(Speed), nameof(SpeedText)); } }
    public string SpeedText => $"{_c.Speed:0.0}×";
    public bool Wave { get => _c.Wave; set { _c.Wave = value; Changed(nameof(Wave)); } }
    public double Tail { get => _c.Tail * 100; set { _c.Tail = Math.Clamp(value / 100, 0.05, 1); Changed(nameof(Tail), nameof(TailText)); } }
    public string TailText => $"{_c.Tail * 100:0} %";
    public int RegionIndex { get => (int)_c.Region; set { _c.Region = (ScreenRegion)Math.Clamp(value, 0, 5); Changed(nameof(RegionIndex)); } }
    public double Boost { get => _c.Boost * 100; set { _c.Boost = Math.Clamp(value / 100, 0.5, 2.5); Changed(nameof(Boost), nameof(BoostText)); } }
    public string BoostText => $"{_c.Boost * 100:0} %";
    public double Reaction { get => (1 - _c.Smooth) * 100; set { _c.Smooth = Math.Clamp(1 - value / 100, 0, 0.97); Changed(nameof(Reaction), nameof(ReactionText)); } }
    public string ReactionText => $"{(1 - _c.Smooth) * 100:0} %";
    public int TempSourceIndex { get => (int)_c.TempSource; set { _c.TempSource = (TempSource)Math.Clamp(value, 0, 2); Changed(nameof(TempSourceIndex)); } }
    public double TempLow { get => _c.TempLow; set { _c.TempLow = Math.Round(Math.Clamp(value, 20, _c.TempHigh - 5)); Changed(nameof(TempLow), nameof(TempRangeText)); } }
    public double TempHigh { get => _c.TempHigh; set { _c.TempHigh = Math.Round(Math.Clamp(value, _c.TempLow + 5, 105)); Changed(nameof(TempHigh), nameof(TempRangeText)); } }
    public string TempRangeText => $"{_c.TempLow:0} °C → {_c.TempHigh:0} °C";

    public void LoadFrom(EffectConfig src)
    {
        var c = src.Clone();
        _c.Type = c.Type; _c.Seconds = c.Seconds; _c.Fade = c.Fade; _c.Speed = c.Speed; _c.Wave = c.Wave; _c.Tail = c.Tail;
        _c.Region = c.Region; _c.Boost = c.Boost; _c.Smooth = c.Smooth; _c.TempSource = c.TempSource; _c.TempLow = c.TempLow; _c.TempHigh = c.TempHigh;
        SetColors(c.Colors.Count == 0 ? new[] { "#FFFFFF" } : c.Colors.ToArray());
        Changed(nameof(Type), nameof(ShowColors), nameof(ShowAddColor), nameof(ShowSeconds), nameof(SecondsLabel), nameof(ShowFade), nameof(ShowSpeed),
                nameof(ShowWave), nameof(ShowTail), nameof(ShowScreen), nameof(ShowTemp), nameof(Description), nameof(Seconds), nameof(SecondsText),
                nameof(Fade), nameof(Speed), nameof(SpeedText), nameof(Wave), nameof(Tail), nameof(TailText), nameof(RegionIndex), nameof(Boost),
                nameof(BoostText), nameof(Reaction), nameof(ReactionText), nameof(TempSourceIndex), nameof(TempLow), nameof(TempHigh), nameof(TempRangeText),
                nameof(ColorsLabel));
    }
}

// ============================================================================ Zone (Anschluss / Gerät)

public sealed class LightZoneVM : ObservableObject
{
    private readonly RgbController _ctl;
    public ZoneDescriptor Desc { get; }
    public LightZoneConfig Cfg { get; }
    public EffectVM Effect { get; }
    public EngineZone? Engine { get; set; }

    public LightZoneVM(RgbController ctl, ZoneDescriptor desc, LightZoneConfig cfg)
    {
        _ctl = ctl;
        Desc = desc;
        Cfg = cfg;
        if (Cfg.Effect == null || (Cfg.Effect.Colors.Count == 0)) Cfg.Effect = ctl.Global.Config.Clone();
        Effect = new EffectVM(Cfg.Effect, ctl.Changed);
    }

    public bool IsHub => Desc.LedsPerFan > 0;
    public bool ShowLedSlider => Desc.Adjustable && !IsHub;
    public int MaxFans => IsHub ? Math.Max(1, Desc.MaxLeds / Desc.LedsPerFan) : 8;

    public string DisplayName
    {
        get => Cfg.Name ?? Desc.ZoneName;
        set
        {
            var v = value?.Trim();
            Cfg.Name = string.IsNullOrEmpty(v) || v == Desc.ZoneName ? null : v;
            _ctl.Changed();
            Raise();
        }
    }

    public string SubTitle => $"{Desc.DeviceName} · {Desc.Kind}" + (Desc.PerLed ? "" : " · eine Farbe");

    public bool Enabled { get => Cfg.Enabled; set { Cfg.Enabled = value; Raise(); _ctl.Changed(); } }

    /// <summary>Eigener Effekt/eigene Farben für diesen Anschluss (z. B. Lüfter vorne blau, hinten rot).</summary>
    public bool OwnEffect
    {
        get => Cfg.OwnEffect;
        set
        {
            if (Cfg.OwnEffect == value) return;
            Cfg.OwnEffect = value;
            if (value) Effect.LoadFrom(_ctl.Global.Config); // Start: aktueller gemeinsamer Effekt
            Raise(); Raise(nameof(ModeText));
            _ctl.Changed();
        }
    }
    public string ModeText => Cfg.OwnEffect ? "Eigener Effekt" : "Wie alle Geräte";

    public bool FlickerGuard { get => Cfg.FlickerGuard; set { Cfg.FlickerGuard = value; Raise(); _ctl.Changed(); } }

    public static string[] ScreenRegionChoices { get; } = { "Wie oben eingestellt", "Ganzer Bildschirm", "Linke Seite", "Rechte Seite", "Oben", "Unten", "Mitte" };
    public int ScreenRegionIndex
    {
        get => Cfg.ScreenRegion + 1;
        set { Cfg.ScreenRegion = Math.Clamp(value, 0, 6) - 1; Raise(); _ctl.Changed(); }
    }
    public bool ShowScreenRegion => !Cfg.OwnEffect && _ctl.Global.Type == LightEffect.Screen;
    public void RefreshScreen() => Raise(nameof(ShowScreenRegion));

    public bool Adjustable => Desc.Adjustable;
    public int MaxLeds => Desc.MaxLeds;

    public int LedCount => IsHub ? Cfg.FanCount * Desc.LedsPerFan : Cfg.LedCount > 0 ? Cfg.LedCount : Desc.LedCount;
    public double LedCountValue
    {
        get => LedCount;
        set
        {
            int v = (int)Math.Round(Math.Clamp(value, 1, Desc.MaxLeds));
            if (v == LedCount) return;
            Cfg.LedCount = v;
            _ctl.SetLedCount(this, v);
            Raise(); Raise(nameof(LedText));
            _ctl.Changed();
        }
    }
    public string LedText => $"{LedCount} LEDs";

    public string FanCountText => Cfg.FanCount == 1 ? "1 Lüfter" : $"{Cfg.FanCount} Lüfter";
    public void ChangeFanCount(int delta)
    {
        if (!IsHub) return;
        int n = Math.Clamp(Cfg.FanCount + delta, 1, MaxFans);
        if (n == Cfg.FanCount) return;
        Cfg.FanCount = n;
        Cfg.LedCount = n * Desc.LedsPerFan;
        _ctl.SetLedCount(this, Cfg.LedCount);
        RaiseAll(nameof(LedText), nameof(LedCountValue), nameof(FanCountText));
        _ctl.Changed();
    }

    private Brush _preview = Brushes.Transparent;
    public Brush Preview { get => _preview; set => Set(ref _preview, value); }

    private string _health = "";
    /// <summary>Zustand der Datenübertragung (für die Fehlersuche).</summary>
    public string Health { get => _health; set => Set(ref _health, value); }
    private Brush _healthBrush = Palette.Muted;
    public Brush HealthBrush { get => _healthBrush; set => Set(ref _healthBrush, value); }

    public void Identify() => _ctl.Identify(this);
}

// ============================================================================ Steuerung der Beleuchtung

public sealed class RgbController : ObservableObject, IDisposable
{
    private readonly AppConfig _cfg;
    private readonly RgbEngine _engine;
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public EffectVM Global { get; }
    public ObservableCollection<LightZoneVM> Zones { get; } = new();
    public ObservableCollection<DetectedHardware> Hardware { get; } = new();
    public ObservableCollection<string> SavedColors { get; } = new();
    public ObservableCollection<RgbProfile> Profiles { get; } = new();
    public bool PreviewActive { get; set; }

    /// <summary>Gerät mit wiederholten Fehlern (Name, Meldung) – geht an den Problemlöser.</summary>
    public event Action<string, string>? DeviceProblem;

    public RgbController(AppConfig cfg)
    {
        _cfg = cfg;
        Global = new EffectVM(cfg.Rgb.Global, Changed);
        Global.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EffectVM.Type))
                foreach (var z in Zones) z.RefreshScreen();
        };
        foreach (var c in cfg.Rgb.SavedColors) SavedColors.Add(c);
        foreach (var p in cfg.Rgb.Profiles) Profiles.Add(p);

        _engine = new RgbEngine(SynchronizationContext.Current ?? new DispatcherSynchronizationContext());
        _engine.DevicesChanged += OnDevices;
        _engine.StatusChanged += (text, ok) => { StatusText = text; StatusBrush = ok ? Palette.Good : Palette.Warn; };
        _engine.DeviceProblem += (n, m) => DeviceProblem?.Invoke(n, m);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _cfg.Save(); };
        _preview.Tick += (_, _) => UpdatePreview();
        _preview.Start();
        _healthTimer.Tick += (_, _) => UpdateHealth();
        _healthTimer.Start();
        Push();
    }

    // ---- Eigenschaften
    public bool Enabled { get => _cfg.Rgb.Enabled; set { _cfg.Rgb.Enabled = value; Raise(); Changed(); Log.Info(LogModule.Rgb, value ? "Beleuchtung an" : "Beleuchtung aus (Geräte behalten letzte Farbe)"); } }

    public double Brightness
    {
        get => _cfg.Rgb.Brightness * 100;
        set { _cfg.Rgb.Brightness = Math.Clamp(value / 100, 0, 1); Raise(); Raise(nameof(BrightnessText)); Changed(); }
    }
    public string BrightnessText => $"{_cfg.Rgb.Brightness * 100:0} %";

    public bool NightMode { get => _cfg.Rgb.NightMode; set { _cfg.Rgb.NightMode = value; Raise(); Changed(); } }
    public int NightFrom { get => _cfg.Rgb.NightFrom; set { _cfg.Rgb.NightFrom = Math.Clamp(value, 0, 23); Raise(); Changed(); } }
    public int NightTo { get => _cfg.Rgb.NightTo; set { _cfg.Rgb.NightTo = Math.Clamp(value, 0, 23); Raise(); Changed(); } }
    public static int[] Hours { get; } = Enumerable.Range(0, 24).ToArray();
    public bool KeepAlive { get => _cfg.Rgb.KeepAliveSeconds > 0; set { _cfg.Rgb.KeepAliveSeconds = value ? 5 : 0; Raise(); Changed(); } }

    private string _statusText = "Suche Geräte …";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    private Brush _statusBrush = Palette.Muted;
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    private Brush _globalPreview = Brushes.Transparent;
    public Brush GlobalPreview { get => _globalPreview; private set => Set(ref _globalPreview, value); }

    private Brush _sAll = Brushes.Black, _sL = Brushes.Black, _sR = Brushes.Black, _sT = Brushes.Black, _sB = Brushes.Black, _sC = Brushes.Black;
    public Brush ScreenAll { get => _sAll; private set => Set(ref _sAll, value); }
    public Brush ScreenLeft { get => _sL; private set => Set(ref _sL, value); }
    public Brush ScreenRight { get => _sR; private set => Set(ref _sR, value); }
    public Brush ScreenTop { get => _sT; private set => Set(ref _sT, value); }
    public Brush ScreenBottom { get => _sB; private set => Set(ref _sB, value); }
    public Brush ScreenCenter { get => _sC; private set => Set(ref _sC, value); }

    public bool HasZones => Zones.Count > 0;
    public bool NoZones => Zones.Count == 0;
    public string EffectName => EffectVM.Choices.FirstOrDefault(c => c.Type == Global.Type)?.Name ?? "";

    // ---- eigene Farben (Palette)
    public void SaveColor(string hex)
    {
        hex = RgbF.FromHex(hex).Hex;
        if (SavedColors.Contains(hex, StringComparer.OrdinalIgnoreCase)) return;
        SavedColors.Insert(0, hex);
        while (SavedColors.Count > 18) SavedColors.RemoveAt(SavedColors.Count - 1);
        _cfg.Rgb.SavedColors = SavedColors.ToList();
        _cfg.Save();
    }

    public void ForgetColor(string hex)
    {
        SavedColors.Remove(hex);
        _cfg.Rgb.SavedColors = SavedColors.ToList();
        _cfg.Save();
    }

    // ---- Profile
    public void SaveProfile(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var existing = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Profiles.Remove(existing);
        var p = new RgbProfile { Name = name, Effect = Global.Config.Clone(), Brightness = _cfg.Rgb.Brightness };
        Profiles.Insert(0, p);
        _cfg.Rgb.Profiles = Profiles.ToList();
        _cfg.Save();
        Log.Info(LogModule.Rgb, $"Profil „{name}“ gespeichert");
    }

    public void LoadProfile(RgbProfile p)
    {
        Global.LoadFrom(p.Effect);
        Brightness = p.Brightness * 100;
        Raise(nameof(EffectName));
        Log.Info(LogModule.Rgb, $"Profil „{p.Name}“ geladen");
    }

    public void DeleteProfile(RgbProfile p)
    {
        Profiles.Remove(p);
        _cfg.Rgb.Profiles = Profiles.ToList();
        _cfg.Save();
    }

    // ---- Änderungen
    public void Changed()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Raise(nameof(EffectName));
        Push();
    }

    public void SetLedCount(LightZoneVM z, int count) => _engine.SetLedCount(z.Desc.Zone, count);
    public void Identify(LightZoneVM z) { _engine.Identify(z.Desc.Zone); Log.Info(LogModule.Rgb, $"Anschluss „{z.DisplayName}“ blinkt zum Erkennen"); }
    public void Rescan() { Log.Info(LogModule.Rgb, "Geräte werden neu gesucht"); _engine.Rescan(); }

    public void OpenHardwareList()
    {
        try
        {
            var p = Path.Combine(Log.Folder, "RGB-Hardware.txt");
            File.WriteAllText(p, _engine.HardwareReportText);
            Env.Open(p);
        }
        catch (Exception ex) { Log.Warn(LogModule.Rgb, "Hardware-Liste: " + ex.Message); }
    }

    private void OnDevices(List<ZoneDescriptor> zones, List<DetectedHardware> report)
    {
        Zones.Clear();
        foreach (var d in zones)
        {
            if (!_cfg.Rgb.Zones.TryGetValue(d.Key, out var zc))
            {
                zc = new LightZoneConfig();
                _cfg.Rgb.Zones[d.Key] = zc;
            }
            // alte Daten (v3): eigener Effekt pro Lüfter → auf den Anschluss übertragen
            if (zc.Fans.Count > 0 && !zc.Fans[0].FollowSync && !zc.OwnEffect)
            {
                zc.OwnEffect = true;
                zc.Effect = zc.Fans[0].Effect.Clone();
            }
            zc.Fans.Clear();
            if (d.LedsPerFan > 0) zc.FanCount = Math.Clamp(zc.FanCount <= 1 ? Math.Max(1, d.DefaultFans) : zc.FanCount, 1, Math.Max(1, d.MaxLeds / d.LedsPerFan));
            else if (d.Adjustable && zc.LedCount <= 0) zc.LedCount = Math.Min(d.MaxLeds, 60); // reicht für 3–4 Lüfter am Daisy-Chain
            var vm = new LightZoneVM(this, d, zc);
            if ((d.Adjustable || vm.IsHub) && vm.LedCount != d.LedCount) _engine.SetLedCount(d.Zone, vm.LedCount);
            Zones.Add(vm);
        }
        Hardware.Clear();
        foreach (var h in report) Hardware.Add(h);
        RaiseAll(nameof(HasZones), nameof(NoZones));
        Changed();
    }

    private void Push()
    {
        var globalSnap = Global.Snap();
        var zones = new List<EngineZone>();
        foreach (var z in Zones)
        {
            if (z.LedCount <= 0) continue;
            EffectSnap snap;
            if (z.Cfg.OwnEffect) snap = z.Effect.Snap();
            else if (Global.Type == LightEffect.Screen && z.Cfg.ScreenRegion >= 0)
            {
                var c = Global.Config.Clone();
                c.Region = (ScreenRegion)z.Cfg.ScreenRegion;
                snap = EffectSnap.From(c);
            }
            else snap = globalSnap;
            var ez = new EngineZone { Target = z.Desc.Zone, Enabled = z.Enabled, LedCount = Math.Max(1, z.LedCount), Effect = snap, FlickerGuard = z.Cfg.FlickerGuard };
            if (z.Engine != null) { ez.Current = z.Engine.Current; ez.LastSent = z.Engine.LastSent; ez.LastSendTime = z.Engine.LastSendTime; }
            z.Engine = ez;
            zones.Add(ez);
        }
        _engine.SetState(new EngineState
        {
            Enabled = _cfg.Rgb.Enabled, Brightness = _cfg.Rgb.Brightness, Global = globalSnap, Zones = zones.ToArray(),
            NightMode = _cfg.Rgb.NightMode, NightFrom = _cfg.Rgb.NightFrom, NightTo = _cfg.Rgb.NightTo, KeepAliveSeconds = _cfg.Rgb.KeepAliveSeconds
        });
    }

    private void UpdateHealth()
    {
        foreach (var z in Zones)
        {
            if (!_engine.Stats.TryGetValue(z.Desc.Zone.Device.Key, out var st)) { z.Health = ""; continue; }
            if (st.ConsecutiveErrors > 0 || (st.Errors > 0 && (DateTime.Now - st.LastErrorTime).TotalSeconds < 30))
            {
                z.Health = $"Übertragungsfehler ({st.Errors}×): {st.LastError}";
                z.HealthBrush = Palette.Bad;
            }
            else
            {
                z.Health = _engine.NightActive ? "Nachtmodus – aus" : "Verbindung OK";
                z.HealthBrush = Palette.Good;
            }
        }
    }

    private void UpdatePreview()
    {
        if (!PreviewActive) return;
        GlobalPreview = MakeBrush(_engine.GlobalPreview);
        if (Global.Type == LightEffect.Screen && _engine.ScreenActive)
        {
            var sc = _engine.Screen;
            ScreenAll = ColorItem.Frozen(sc.Get(ScreenRegion.All));
            ScreenLeft = ColorItem.Frozen(sc.Get(ScreenRegion.Left));
            ScreenRight = ColorItem.Frozen(sc.Get(ScreenRegion.Right));
            ScreenTop = ColorItem.Frozen(sc.Get(ScreenRegion.Top));
            ScreenBottom = ColorItem.Frozen(sc.Get(ScreenRegion.Bottom));
            ScreenCenter = ColorItem.Frozen(sc.Get(ScreenRegion.Center));
        }
        foreach (var z in Zones)
            if (z.Engine != null) z.Preview = MakeBrush(z.Engine.Current);
    }

    private Brush MakeBrush(RgbF[] colors)
    {
        if (colors.Length == 0) return Brushes.Transparent;
        double b = Math.Max(0.15, _cfg.Rgb.Enabled ? _cfg.Rgb.Brightness : 1);
        if (colors.Length == 1) return ColorItem.Frozen(colors[0].Scale(b));
        var stops = new GradientStopCollection();
        int steps = Math.Min(colors.Length, 16);
        for (int i = 0; i < steps; i++)
        {
            int idx = (int)Math.Round(i * (colors.Length - 1) / (double)(steps - 1));
            var c = colors[idx].Scale(b);
            stops.Add(new GradientStop(Color.FromRgb(c.R8, c.G8, c.B8), i / (double)(steps - 1)));
        }
        var g = new LinearGradientBrush(stops, 0);
        g.Freeze();
        return g;
    }

    public void Dispose()
    {
        _preview.Stop();
        _healthTimer.Stop();
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); _cfg.Save(); }
        _engine.Dispose();
    }
}
