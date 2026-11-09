using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NickControlCenter;

public enum TileType { Clock, AnalogClock, System, Graph, Network, Drives, Security, Launcher, Notes, Action, ControlCenter, Photos, Calendar, Countdown, Uptime }
public enum TileSize { Small, Wide, Large, Tall }
public sealed record TileKind(TileType Type, string Name, string Icon, string Desc);

public sealed class TileConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public TileType Type { get; set; }
    public TileSize Size { get; set; } = TileSize.Small;
    public double X { get; set; } = 40;
    public double Y { get; set; } = 40;
    public string Title { get; set; } = "";
    public string Target { get; set; } = "";       // Launcher: Pfad/URL · Fotos: Ordner · Aktion: Name · Countdown: Datum
    public List<string> Metrics { get; set; } = new();
    public string? Color { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class TilesSettings
{
    public bool Visible { get; set; }
    public string Style { get; set; } = "Mac";          // Mac, Glas, Akzent, Bunt, Minimal
    public double Corner { get; set; } = 18;
    public double Opacity { get; set; } = 0.92;
    public double Scale { get; set; } = 1.0;
    public bool Shadow { get; set; } = true;
    public bool Locked { get; set; }
    public bool Snap { get; set; } = true;
    public string Accent { get; set; } = "#22D3EE";
    public List<TileConfig> Tiles { get; set; } = new();
}

/// <summary>
/// Desktop-Kacheln: kleine Live-Widgets direkt auf dem Desktop (bleiben bei Win+D sichtbar, liegen unter allen Fenstern).
/// Früher ein eigenes PowerShell-Programm – jetzt Teil der App, nutzt die gleichen Sensoren (inkl. HWiNFO).
/// </summary>
public sealed class TilesService : ObservableObject, IDisposable
{
    public TilesSettings Settings { get; } = JsonStore.Load<TilesSettings>("kacheln.json");
    private readonly Dictionary<string, TileWindow> _windows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private int _sub;
    public ObservableCollection<TileConfig> Tiles { get; } = new();
    public string SecuritySummary { get; private set; } = "Wird geprüft …";
    public Brush SecurityBrush { get; private set; } = Palette.Muted;
    /// <summary>Zeilen für die Sicherheits-Kachel: (Text, Farbe).</summary>
    public (string Av, Brush AvBrush, string Sig, Brush SigBrush, string Upd, Brush UpdBrush, DateTime Checked) Security { get; private set; } =
        ("Virenschutz wird geprüft …", Palette.Muted, "", Palette.Muted, "Updates werden geprüft …", Palette.Muted, DateTime.MinValue);
    private DateTime _lastSecurity = DateTime.MinValue, _lastOnline = DateTime.MinValue;
    private int _securityBusy;

    public static readonly TileKind[] Catalog =
    {
        new(TileType.Clock, "Uhr (digital)", "", "Uhrzeit und Datum"),
        new(TileType.AnalogClock, "Uhr (analog)", "", "Klassische Zeiger-Uhr"),
        new(TileType.System, "Systemwerte", "", "CPU/GPU-Temperatur, Auslastung, RAM … frei wählbar"),
        new(TileType.Graph, "Verlauf", "", "Ein Wert als Live-Diagramm"),
        new(TileType.Network, "Netzwerk", "", "Download/Upload mit Verlauf"),
        new(TileType.Drives, "Laufwerke", "", "Freier Speicher aller Laufwerke"),
        new(TileType.Security, "Sicherheit & Updates", "", "Defender-Status und Windows-Updates"),
        new(TileType.ControlCenter, "Control Center", "", "Boost-Button, Lüfterprofil, Beleuchtung an/aus"),
        new(TileType.Launcher, "App / Ordner / Webseite", "", "Ein Klick öffnet Programm, Ordner oder Link"),
        new(TileType.Notes, "Notiz", "", "Notizzettel zum Reinschreiben"),
        new(TileType.Action, "Aktion", "", "Sperren, Neustart, Herunterfahren, Energiesparen …"),
        new(TileType.Photos, "Fotos", "", "Wechselnde Bilder aus einem Ordner"),
        new(TileType.Calendar, "Kalender", "", "Aktueller Monat"),
        new(TileType.Countdown, "Countdown", "", "Tage bis zu einem Datum"),
        new(TileType.Uptime, "PC-Laufzeit", "", "Wie lange der PC schon läuft"),
    };

    public static readonly string[] Actions = { "Sperren", "Energiesparen", "Neustart", "Herunterfahren", "Explorer neu starten", "Gaming-Boost", "Papierkorb leeren" };

    public TilesService()
    {
        if (Settings.Tiles.Count == 0)
        {
            Settings.Tiles.Add(new TileConfig { Type = TileType.Clock, Size = TileSize.Wide, X = 40, Y = 40 });
            Settings.Tiles.Add(new TileConfig { Type = TileType.System, Size = TileSize.Large, X = 40, Y = 210, Metrics = new() { "cpu.temp", "gpu.temp", "cpu.load", "gpu.load", "ram.load" } });
            Settings.Tiles.Add(new TileConfig { Type = TileType.ControlCenter, Size = TileSize.Wide, X = 40, Y = 540 });
        }
        foreach (var t in Settings.Tiles) Tiles.Add(t);
        _timer.Tick += (_, _) => Tick();
    }

    public void Save() => JsonStore.Save("kacheln.json", Settings);

    public bool Visible
    {
        get => Settings.Visible;
        set
        {
            Settings.Visible = value;
            Save();
            if (value) ShowAll(); else HideAll();
            Raise();
            Log.Info(LogModule.App, value ? "Desktop-Kacheln eingeblendet" : "Desktop-Kacheln ausgeblendet");
        }
    }

    public void Start()
    {
        _timer.Start();
        if (Settings.Visible) ShowAll();
    }

    private bool _gamePause;
    /// <summary>Während eines Spiels ausblenden (ohne die Einstellung zu ändern).</summary>
    public void SetGamePause(bool pause)
    {
        if (_gamePause == pause) return;
        _gamePause = pause;
        if (pause) HideAll(); else if (Settings.Visible) ShowAll();
        Log.Info(LogModule.App, pause ? "Desktop-Kacheln während des Spiels pausiert" : "Desktop-Kacheln wieder eingeblendet");
    }

    private void ShowAll()
    {
        if (_gamePause) return;
        foreach (var t in Settings.Tiles) Open(t);
    }

    private void HideAll()
    {
        foreach (var w in _windows.Values.ToList()) w.Close();
        _windows.Clear();
    }

    private void Open(TileConfig t)
    {
        if (_windows.ContainsKey(t.Id)) return;
        var w = new TileWindow(this, t);
        _windows[t.Id] = w;
        w.Closed += (_, _) => _windows.Remove(t.Id);
        w.Show();
    }

    /// <summary>Alle Kacheln neu zeichnen (nach Stil-Änderung).</summary>
    public void Rebuild()
    {
        Save();
        if (!Settings.Visible) return;
        HideAll();
        ShowAll();
    }

    public TileConfig Add(TileType type)
    {
        var t = new TileConfig { Type = type, Size = type is TileType.System or TileType.Calendar or TileType.Photos ? TileSize.Large : type is TileType.Clock or TileType.Network or TileType.Graph or TileType.ControlCenter ? TileSize.Wide : TileSize.Small };
        var wa = SystemParameters.WorkArea;
        t.X = wa.Left + 60 + (Settings.Tiles.Count % 6) * 30;
        t.Y = wa.Top + 60 + (Settings.Tiles.Count % 6) * 30;
        if (type == TileType.System) t.Metrics = new() { "cpu.temp", "gpu.temp", "cpu.load", "ram.load" };
        if (type == TileType.Graph) t.Metrics = new() { "cpu.temp" };
        if (type == TileType.Action) t.Target = "Sperren";
        if (type == TileType.Countdown) t.Target = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd");
        Settings.Tiles.Add(t);
        Tiles.Add(t);
        Save();
        if (!Settings.Visible) Visible = true; else Open(t);
        Log.Info(LogModule.App, $"Kachel hinzugefügt: {type}");
        return t;
    }

    public void Remove(TileConfig t)
    {
        Settings.Tiles.Remove(t);
        Tiles.Remove(t);
        if (_windows.TryGetValue(t.Id, out var w)) w.Close();
        Save();
    }

    public void Update(TileConfig t)
    {
        Save();
        if (_windows.TryGetValue(t.Id, out var w)) { w.Close(); }
        if (Settings.Visible) Open(t);
    }

    /// <summary>Kacheln ordentlich untereinander anordnen.</summary>
    public void Arrange()
    {
        var wa = SystemParameters.WorkArea;
        double x = wa.Left + 30, y = wa.Top + 30, colW = 0;
        foreach (var t in Settings.Tiles)
        {
            var (w, h) = TileWindow.SizeOf(t.Size, Settings.Scale);
            if (y + h > wa.Bottom - 20) { y = wa.Top + 30; x += colW + 16; colW = 0; }
            t.X = x; t.Y = y;
            y += h + 16;
            colW = Math.Max(colW, w);
        }
        Rebuild();
    }

    private void Tick()
    {
        if (!Settings.Visible || _windows.Count == 0) return;
        // alle 5 Minuten lokal prüfen, jede Stunde zusätzlich online bei Windows Update nachfragen
        if ((DateTime.Now - _lastSecurity).TotalMinutes >= 5 && Settings.Tiles.Any(t => t.Type == TileType.Security)) RefreshSecurity(false);
        // Werte-Kacheln 10× pro Sekunde (weich laufende Zahlen), alles andere einmal pro Sekunde
        bool full = ++_sub % 10 == 0;
        foreach (var w in _windows.Values) if (full || w.IsLive) w.Tick();
    }

    /// <summary>Sicherheits-Status neu prüfen (im Hintergrund). online = auch bei Microsoft nachfragen (dauert länger).</summary>
    public void RefreshSecurity(bool online)
    {
        _lastSecurity = DateTime.Now;
        if ((DateTime.Now - _lastOnline).TotalMinutes >= 60) online = true;
        if (Interlocked.Exchange(ref _securityBusy, 1) == 1) return;
        _ = Task.Run(() => { try { CheckSecurity(online); } finally { _securityBusy = 0; } });
    }

    private void CheckSecurity(bool online)
    {
        string av = "Virenschutz unbekannt", sig = "";
        Brush avB = Palette.Warn, sigB = Palette.Muted;
        // Microsoft Defender direkt fragen (genauer als das Sicherheitscenter)
        try
        {
            using var d = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus");
            foreach (ManagementObject o in d.Get())
                using (o)
                {
                    bool on = o["AntivirusEnabled"] is true && o["RealTimeProtectionEnabled"] is true;
                    int age = Convert.ToInt32(o["AntivirusSignatureAge"] ?? 0);
                    av = on ? "Defender aktiv" : "Defender-Echtzeitschutz AUS";
                    avB = on ? Palette.Good : Palette.Bad;
                    sig = age <= 0 ? "Virendefinitionen von heute" : age == 1 ? "Virendefinitionen von gestern" : $"Virendefinitionen {age} Tage alt";
                    sigB = age <= 2 ? Palette.Good : Palette.Warn;
                }
        }
        catch { }
        // anderer Virenschutz (Avast, Kaspersky …) über das Sicherheitscenter
        if (av == "Virenschutz unbekannt" || avB == Palette.Bad)
            try
            {
                using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
                foreach (ManagementObject o in s.Get())
                    using (o)
                    {
                        uint state = Convert.ToUInt32(o["productState"]);
                        bool on = ((state >> 12) & 0xF) == 1;
                        if (!on) continue;
                        bool upToDate = ((state >> 4) & 0xF) == 0;
                        av = $"{o["displayName"]} aktiv";
                        avB = Palette.Good;
                        sig = upToDate ? "Virendefinitionen aktuell" : "Virendefinitionen veraltet";
                        sigB = upToDate ? Palette.Good : Palette.Warn;
                        break;
                    }
            }
            catch { }

        string upd = "Updates: unbekannt";
        Brush updB = Palette.Muted;
        try
        {
            bool reboot = false;
            try { var si = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo"); if (si != null) { dynamic sys = Activator.CreateInstance(si)!; reboot = sys.RebootRequired; } } catch { }
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type != null)
            {
                dynamic session = Activator.CreateInstance(type)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = online;
                dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                int pending = 0;
                foreach (dynamic u in result.Updates)
                {
                    string title = u.Title ?? "";
                    // Virendefinitionen kommen fast täglich und installieren sich selbst → nicht als "Update bereit" zählen
                    bool definition = title.Contains("KB2267602") || title.Contains("Security Intelligence", StringComparison.OrdinalIgnoreCase) ||
                                      title.Contains("Sicherheitsinformationsupdate", StringComparison.OrdinalIgnoreCase) || title.Contains("Definition", StringComparison.OrdinalIgnoreCase);
                    if (!definition) pending++;
                }
                if (online) _lastOnline = DateTime.Now;
                if (reboot) { upd = "Neustart für Updates nötig"; updB = Palette.Warn; }
                else if (pending == 0) { upd = "Windows ist aktuell"; updB = Palette.Good; }
                else { upd = pending == 1 ? "1 Update bereit" : $"{pending} Updates bereit"; updB = Palette.Warn; }
                Log.Info(LogModule.App, $"Sicherheit: {av}, {sig}, {upd} ({(online ? "online" : "lokal")} geprüft)");
            }
        }
        catch (Exception ex) { Log.Warn(LogModule.App, "Update-Prüfung: " + ex.Message); }

        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            Security = (av, avB, sig, sigB, upd, updB, DateTime.Now);
            SecuritySummary = av + "\n" + sig + "\n" + upd;
            SecurityBrush = avB == Palette.Bad ? Palette.Bad : updB == Palette.Warn || sigB == Palette.Warn ? Palette.Warn : Palette.Good;
            RaiseAll(nameof(SecuritySummary), nameof(SecurityBrush), nameof(Security));
        });
    }

    public void RunAction(string a)
    {
        Log.Info(LogModule.App, "Kachel-Aktion: " + a);
        switch (a)
        {
            case "Sperren": Native.LockWorkStation(); break;
            case "Energiesparen": System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); break;
            case "Neustart": if (Dialogs.Confirm("PC neu starten?", "Nicht gespeicherte Arbeit geht verloren.")) Env.Run("shutdown.exe", "/r /t 0"); break;
            case "Herunterfahren": if (Dialogs.Confirm("PC herunterfahren?", "Nicht gespeicherte Arbeit geht verloren.")) Env.Run("shutdown.exe", "/s /t 0"); break;
            case "Explorer neu starten": Services.Customize.RestartExplorer(); break;
            case "Gaming-Boost": _ = Services.Boost.BoostAsync(); break;
            case "Papierkorb leeren": SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); break;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public void Dispose()
    {
        _timer.Stop();
        HideAll();
    }
}

/// <summary>Ein Kachel-Fenster auf dem Desktop.</summary>
public sealed class TileWindow : Window
{
    private readonly TilesService _svc;
    private readonly TileConfig _t;
    private readonly Border _card = new();
    private Action? _update;
    private Point _downPos;
    private bool _dragging;

    public static (double W, double H) SizeOf(TileSize s, double scale) => s switch
    {
        TileSize.Wide => (316 * scale, 150 * scale),
        TileSize.Large => (316 * scale, 316 * scale),
        TileSize.Tall => (150 * scale, 316 * scale),
        _ => (150 * scale, 150 * scale)
    };

    public TileWindow(TilesService svc, TileConfig t)
    {
        _svc = svc; _t = t;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        var (w, h) = SizeOf(t.Size, svc.Settings.Scale);
        Width = w + 24; Height = h + 24;
        var wa = SystemParameters.VirtualScreenWidth;
        Left = Math.Clamp(t.X, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + wa - 60) - 12;
        Top = Math.Clamp(t.Y, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60) - 12;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");

        Content = _card;
        _card.Margin = new Thickness(12);
        ApplyLook(_card);
        _card.Child = Build();

        SourceInitialized += (_, _) => PinToDesktop();
        Activated += (_, _) => SendToBottom();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
    }

    private void PinToDesktop()
    {
        var h = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(h, -20).ToInt64();
        Native.SetWindowLongPtr(h, -20, new IntPtr((ex | 0x80) & ~0x40000)); // ToolWindow, kein Alt-Tab
        var progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero) Native.SetWindowLongPtr(h, -8, progman); // Besitzer = Desktop → bleibt bei Win+D sichtbar
        SendToBottom();
    }

    private void SendToBottom()
    {
        try { Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); } catch { }
    }

    private Color AccentColor
    {
        get
        {
            var hex = _t.Color ?? _svc.Settings.Accent;
            if (_svc.Settings.Style == "Bunt" && _t.Color == null)
            {
                string[] bunt = { "#F43F5E", "#F97316", "#EAB308", "#22C55E", "#06B6D4", "#6366F1", "#A855F7", "#EC4899" };
                hex = bunt[Math.Abs(_t.Id.GetHashCode()) % bunt.Length];
            }
            RgbF.TryParseHex(hex, out var c);
            return Color.FromRgb(c.R8, c.G8, c.B8);
        }
    }

    private Brush Fg => _svc.Settings.Style is "Akzent" or "Bunt" ? Brushes.White : new SolidColorBrush(Color.FromRgb(235, 238, 243));
    private Brush Sub => new SolidColorBrush(Color.FromArgb(_svc.Settings.Style is "Akzent" or "Bunt" ? (byte)210 : (byte)150, 235, 238, 243));
    private Brush AccentBrush => new SolidColorBrush(_svc.Settings.Style is "Akzent" or "Bunt" ? Colors.White : AccentColor);

    private void ApplyLook(Border b)
    {
        var s = _svc.Settings;
        byte a = (byte)(Math.Clamp(s.Opacity, 0.2, 1) * 255);
        b.CornerRadius = new CornerRadius(s.Corner);
        b.Padding = new Thickness(16 * s.Scale, 14 * s.Scale, 16 * s.Scale, 14 * s.Scale);
        var ac = AccentColor;
        b.Background = s.Style switch
        {
            "Glas" => new SolidColorBrush(Color.FromArgb((byte)(a * 0.45), 255, 255, 255)),
            "Akzent" or "Bunt" => new LinearGradientBrush(Color.FromArgb(a, ac.R, ac.G, ac.B), Color.FromArgb(a, (byte)(ac.R * 0.6), (byte)(ac.G * 0.6), (byte)(ac.B * 0.6)), 45),
            "Minimal" => new SolidColorBrush(Color.FromArgb((byte)(a * 0.6), 0, 0, 0)),
            _ => new SolidColorBrush(Color.FromArgb(a, 28, 30, 36))
        };
        b.BorderBrush = new SolidColorBrush(Color.FromArgb(s.Style == "Glas" ? (byte)110 : (byte)40, 255, 255, 255));
        b.BorderThickness = new Thickness(1);
        if (s.Shadow) b.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Direction = 270 };
    }

    private TextBlock Text(string t, double size, bool bold = false, Brush? fg = null) => new()
    {
        Text = t, FontSize = size * _svc.Settings.Scale, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = fg ?? Fg, TextTrimming = TextTrimming.CharacterEllipsis
    };

    private TextBlock Glyph(string g, double size) => new() { Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size * _svc.Settings.Scale, Foreground = AccentBrush };

    private UIElement Build()
    {
        try
        {
            return _t.Type switch
            {
                TileType.Clock => BuildClock(),
                TileType.AnalogClock => BuildAnalog(),
                TileType.System => BuildSystem(),
                TileType.Graph => BuildGraph(_t.Metrics.FirstOrDefault() ?? "cpu.temp"),
                TileType.Network => BuildNetwork(),
                TileType.Drives => BuildDrives(),
                TileType.Security => BuildSecurity(),
                TileType.Launcher => BuildLauncher(),
                TileType.Notes => BuildNotes(),
                TileType.Action => BuildAction(),
                TileType.ControlCenter => BuildControl(),
                TileType.Photos => BuildPhotos(),
                TileType.Calendar => BuildCalendar(),
                TileType.Countdown => BuildCountdown(),
                TileType.Uptime => BuildUptime(),
                _ => Text("?", 20)
            };
        }
        catch (Exception ex) { Log.Warn(LogModule.App, $"Kachel {_t.Type}: {ex.Message}"); return Text("Fehler: " + ex.Message, 11); }
    }

    public void Tick() => _update?.Invoke();
    /// <summary>Zeigt Messwerte → öfter auffrischen, damit die Zahlen weich laufen.</summary>
    public bool IsLive => _t.Type is TileType.System or TileType.Graph or TileType.Network;

    private UIElement BuildClock()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var time = Text("", _t.Size == TileSize.Small ? 30 : 46, true);
        var date = Text("", 13, false, Sub);
        sp.Children.Add(time); sp.Children.Add(date);
        _update = () => { time.Text = DateTime.Now.ToString(_t.Size == TileSize.Small ? "HH:mm" : "HH:mm:ss"); date.Text = DateTime.Now.ToString("dddd, d. MMMM"); };
        _update();
        return sp;
    }

    private UIElement BuildAnalog()
    {
        var g = new Grid();
        var canvas = new Canvas();
        g.Children.Add(canvas);
        var (w, h) = SizeOf(_t.Size, _svc.Settings.Scale);
        double size = Math.Min(w, h) - 32 * _svc.Settings.Scale, c = size / 2;
        canvas.Width = size; canvas.Height = size;
        canvas.Children.Add(new Ellipse { Width = size, Height = size, Stroke = Sub, StrokeThickness = 1.5 });
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6, r1 = c - 4, r2 = c - (i % 3 == 0 ? 12 : 8);
            canvas.Children.Add(new Line { X1 = c + r1 * Math.Sin(a), Y1 = c - r1 * Math.Cos(a), X2 = c + r2 * Math.Sin(a), Y2 = c - r2 * Math.Cos(a), Stroke = Fg, StrokeThickness = i % 3 == 0 ? 2.5 : 1.2 });
        }
        Line Hand(double len, double th, Brush b) { var l = new Line { X1 = c, Y1 = c, Stroke = b, StrokeThickness = th, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round }; canvas.Children.Add(l); l.Tag = len; return l; }
        var hh = Hand(c * 0.5, 4, Fg); var mm = Hand(c * 0.75, 3, Fg); var ss = Hand(c * 0.85, 1.4, AccentBrush);
        canvas.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = AccentBrush, Margin = new Thickness(c - 4, c - 4, 0, 0) });
        void Set(Line l, double frac) { double a = frac * 2 * Math.PI, len = (double)l.Tag; l.X2 = c + len * Math.Sin(a); l.Y2 = c - len * Math.Cos(a); }
        _update = () => { var n = DateTime.Now; Set(hh, (n.Hour % 12 + n.Minute / 60.0) / 12); Set(mm, (n.Minute + n.Second / 60.0) / 60); Set(ss, n.Second / 60.0); };
        _update();
        return g;
    }

    private UIElement BuildSystem()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header("", string.IsNullOrEmpty(_t.Title) ? "System" : _t.Title));
        var rows = new List<(Metric m, TextBlock val, Border bar, double maxW)>();
        var (w, _) = SizeOf(_t.Size, _svc.Settings.Scale);
        foreach (var key in _t.Metrics)
        {
            if (!Services.Monitor.Metrics.TryGetValue(key, out var m)) continue;
            var g = new Grid { Margin = new Thickness(0, 5 * _svc.Settings.Scale, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Text(m.Label.Replace("-Temperatur", "").Replace("-Auslastung", " Last"), 12, false, Sub));
            var val = Text("–", 13, true);
            Grid.SetColumn(val, 1);
            g.Children.Add(val);
            sp.Children.Add(g);
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Margin = new Thickness(0, 3, 0, 0) };
            var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            var gg = new Grid(); gg.Children.Add(track); gg.Children.Add(bar);
            sp.Children.Add(gg);
            rows.Add((m, val, bar, w - 32 * _svc.Settings.Scale));
        }
        _update = () =>
        {
            foreach (var (m, val, bar, maxW) in rows)
            {
                val.Text = m.Text;
                val.Foreground = m.Brush == Palette.Text ? Fg : m.Brush;
                bar.Width = Math.Max(0, maxW * m.Percent / 100);
            }
        };
        _update();
        return sp;
    }

    private UIElement Header(string glyph, string title)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(Glyph(glyph, 14));
        var t = Text(title.ToUpperInvariant(), 11, true, Sub);
        t.Margin = new Thickness(7, 0, 0, 0);
        sp.Children.Add(t);
        return sp;
    }

    private UIElement BuildGraph(string key)
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        Services.Monitor.Metrics.TryGetValue(key, out var m);
        var head = new Grid();
        head.Children.Add(Text(m?.Label ?? key, 12, false, Sub));
        var val = Text("–", 16, true);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(val);
        g.Children.Add(head);
        var sl = new Sparkline { Metric = m, Stroke = AccentBrush, Points = 90, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(sl, 1);
        g.Children.Add(sl);
        _update = () => { if (m != null) { val.Text = m.Text; sl.InvalidateVisual(); } };
        return g;
    }

    private UIElement BuildNetwork()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var down = Text("↓ –", 15, true); var up = Text("↑ –", 15, true, Sub);
        up.Margin = new Thickness(14, 0, 0, 0);
        head.Children.Add(down); head.Children.Add(up);
        g.Children.Add(head);
        Services.Monitor.Metrics.TryGetValue("net.down", out var m);
        Services.Monitor.Metrics.TryGetValue("net.up", out var mu);
        var sl = new Sparkline { Metric = m, Stroke = AccentBrush, Points = 90, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(sl, 1);
        g.Children.Add(sl);
        _update = () => { down.Text = "↓ " + m?.Text; up.Text = "↑ " + mu?.Text; sl.InvalidateVisual(); };
        return g;
    }

    private UIElement BuildDrives()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header("", "Laufwerke"));
        var holder = new StackPanel();
        sp.Children.Add(holder);
        int tick = 0;
        _update = () =>
        {
            if (tick++ % 30 != 0) return;
            holder.Children.Clear();
            var (w, _) = SizeOf(_t.Size, _svc.Settings.Scale);
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Take(_t.Size == TileSize.Small ? 2 : 5))
            {
                double used = 1 - d.AvailableFreeSpace / (double)d.TotalSize;
                holder.Children.Add(Text($"{d.Name.TrimEnd('\\')}  {Fmt.Size(d.AvailableFreeSpace)} frei", 12, false, Sub));
                var gg = new Grid { Margin = new Thickness(0, 2, 0, 6) };
                gg.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) });
                gg.Children.Add(new Border { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Width = (w - 32) * used, Background = used > 0.9 ? Palette.Bad : AccentBrush });
                holder.Children.Add(gg);
            }
        };
        _update();
        return sp;
    }

    private UIElement BuildSecurity()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header("", "Sicherheit"));
        bool small = _t.Size == TileSize.Small;
        double fs = small ? 11.5 : 13;
        (Border Row, TextBlock Text, Ellipse Dot) Line(string glyph)
        {
            var g = new Grid { Margin = new Thickness(0, small ? 3 : 5, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var t = Text("", fs, false, Fg);
            t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
            Grid.SetColumn(t, 1);
            g.Children.Add(dot); g.Children.Add(t);
            var b = new Border { Child = g };
            sp.Children.Add(b);
            return (b, t, dot);
        }
        var av = Line(""); var sig = Line(""); var upd = Line("");
        var when = Text("", 10.5, false, Sub);
        when.Margin = new Thickness(0, small ? 4 : 8, 0, 0);
        sp.Children.Add(when);
        _update = () =>
        {
            var s = _svc.Security;
            av.Text.Text = s.Av; av.Dot.Fill = s.AvBrush;
            sig.Text.Text = s.Sig; sig.Dot.Fill = s.SigBrush; sig.Row.Visibility = s.Sig.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            upd.Text.Text = s.Upd; upd.Dot.Fill = s.UpdBrush;
            when.Text = s.Checked == DateTime.MinValue ? "wird geprüft …" : small ? $"geprüft {s.Checked:HH:mm}" : $"geprüft {s.Checked:HH:mm} · Klick = Windows Update";
        };
        _update();
        if (!small) return sp;
        // kleine Kachel: Inhalt passend verkleinern statt abschneiden
        sp.Width = 128;
        return new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = sp, VerticalAlignment = VerticalAlignment.Top };
    }

    private UIElement BuildLauncher()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        ImageSource? icon = null;
        try
        {
            if (File.Exists(_t.Target) || Directory.Exists(_t.Target))
            {
                using var ic = System.Drawing.Icon.ExtractAssociatedIcon(File.Exists(_t.Target) ? _t.Target : Env.ExePath);
                if (ic != null && File.Exists(_t.Target)) icon = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
        }
        catch { }
        if (icon != null) sp.Children.Add(new Image { Source = icon, Width = 44 * _svc.Settings.Scale, Height = 44 * _svc.Settings.Scale });
        else sp.Children.Add(Glyph(Directory.Exists(_t.Target) ? "" : _t.Target.StartsWith("http") ? "" : "", 36));
        var name = string.IsNullOrEmpty(_t.Title) ? System.IO.Path.GetFileNameWithoutExtension(_t.Target.TrimEnd('\\', '/')) : _t.Title;
        var tb = Text(name, 13, true);
        tb.HorizontalAlignment = HorizontalAlignment.Center; tb.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(tb);
        return sp;
    }

    private UIElement BuildNotes()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        g.Children.Add(Header("", string.IsNullOrEmpty(_t.Title) ? "Notiz" : _t.Title));
        var tb = new TextBox
        {
            Text = _t.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Fg, CaretBrush = AccentBrush, FontSize = 13 * _svc.Settings.Scale, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        tb.LostFocus += (_, _) => { _t.Notes = tb.Text; _svc.Save(); };
        tb.PreviewMouseLeftButtonDown += (_, e) => { Activate(); tb.Focus(); };
        Grid.SetRow(tb, 1);
        g.Children.Add(tb);
        return g;
    }

    private UIElement BuildAction()
    {
        string glyph = _t.Target switch { "Sperren" => "", "Energiesparen" => "", "Neustart" => "", "Herunterfahren" => "", "Explorer neu starten" => "", "Gaming-Boost" => "", "Papierkorb leeren" => "", _ => "" };
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(Glyph(glyph, 36));
        var t = Text(_t.Target, 13, true);
        t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(t);
        return sp;
    }

    private UIElement BuildControl()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header("", "Control Center"));
        var row = new WrapPanel();
        Button B(string glyph, string text, Action click)
        {
            var b = new Button
            {
                Content = new StackPanel { Children = { Glyph(glyph, 16), Text(text, 10.5, false, Sub) } },
                Margin = new Thickness(0, 4, 8, 0), Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, MinWidth = 62 * _svc.Settings.Scale
            };
            b.Click += (_, _) => click();
            b.Template = MakeFlatTemplate();
            return b;
        }
        row.Children.Add(B("", "Boost", () => _ = Services.Boost.BoostAsync()));
        row.Children.Add(B("", "Licht", () => Services.Fans.Rgb.Enabled = !Services.Fans.Rgb.Enabled));
        row.Children.Add(B("", "Leise", () => Services.Fans.Profile = Services.Fans.Profile == "Leise" ? "Normal" : "Leise"));
        row.Children.Add(B("", "Öffnen", () => App.Instance.ShowWindow()));
        sp.Children.Add(row);
        var st = Text("", 11, false, Sub);
        st.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(st);
        _update = () => st.Text = $"Lüfter: {Services.Fans.Profile} · Licht: {(Services.Fans.Rgb.Enabled ? "an" : "aus")}";
        _update();
        return sp;
    }

    private static ControlTemplate MakeFlatTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        return t;
    }

    private UIElement BuildPhotos()
    {
        var img = new Image { Stretch = Stretch.UniformToFill };
        var border = new Border { CornerRadius = new CornerRadius(Math.Max(0, _svc.Settings.Corner - 8)), ClipToBounds = true, Child = img, Margin = new Thickness(-6) };
        int idx = 0, tick = 0;
        void Next()
        {
            try
            {
                if (!Directory.Exists(_t.Target)) return;
                var files = Directory.GetFiles(_t.Target).Where(f => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())).ToList();
                if (files.Count == 0) return;
                idx = (idx + 1) % files.Count;
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 640; bi.UriSource = new Uri(files[idx]); bi.EndInit(); bi.Freeze();
                img.Source = bi;
            }
            catch { }
        }
        Next();
        _update = () => { if (++tick % 15 == 0) Next(); };
        if (!Directory.Exists(_t.Target)) return Text("Rechtsklick → Bearbeiten: Fotoordner wählen", 12, false, Sub);
        return border;
    }

    private UIElement BuildCalendar()
    {
        var sp = new StackPanel();
        var now = DateTime.Today;
        sp.Children.Add(Text(now.ToString("MMMM yyyy"), 15, true));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var d in new[] { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" }) grid.Children.Add(Center(Text(d, 10.5, true, Sub)));
        var first = new DateTime(now.Year, now.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) grid.Children.Add(new Border());
        for (int d = 1; d <= DateTime.DaysInMonth(now.Year, now.Month); d++)
        {
            var t = Text(d.ToString(), 12, d == now.Day, d == now.Day ? (_svc.Settings.Style is "Akzent" or "Bunt" ? Brushes.Black : Brushes.Black) : Fg);
            var b = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(1), Padding = new Thickness(0, 2, 0, 2), Child = Center(t) };
            if (d == now.Day) b.Background = AccentBrush;
            grid.Children.Add(b);
        }
        sp.Children.Add(grid);
        return sp;
    }

    private static UIElement Center(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Center; return t; }

    private UIElement BuildCountdown()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var big = Text("", 36, true);
        var sub = Text(string.IsNullOrEmpty(_t.Title) ? "Tage" : _t.Title, 12, false, Sub);
        sp.Children.Add(big); sp.Children.Add(sub);
        _update = () =>
        {
            if (DateTime.TryParse(_t.Target, out var d)) { var days = (d.Date - DateTime.Today).TotalDays; big.Text = days >= 0 ? $"{days:0}" : "✓"; sub.Text = (string.IsNullOrEmpty(_t.Title) ? "Tage bis " : _t.Title + " · ") + d.ToString("dd.MM.yyyy"); }
            else big.Text = "?";
        };
        _update();
        return sp;
    }

    private UIElement BuildUptime()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Header("", "Laufzeit"));
        var t = Text("", 22, true);
        sp.Children.Add(t);
        _update = () => { var up = TimeSpan.FromMilliseconds(Native.GetTickCount64()); t.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays} T {up.Hours} h" : $"{up.Hours} h {up.Minutes} min"; };
        _update();
        return sp;
    }

    // ---------------------------------------------------------------- Maus: ziehen / klicken
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox || IsInButton(e.OriginalSource as DependencyObject)) return;
        _downPos = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
    }

    private static bool IsInButton(DependencyObject? d)
    {
        while (d != null) { if (d is Button) return true; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d); }
        return false;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured || _svc.Settings.Locked) return;
        var p = e.GetPosition(this);
        if (!_dragging && (Math.Abs(p.X - _downPos.X) > 4 || Math.Abs(p.Y - _downPos.Y) > 4)) _dragging = true;
        if (_dragging) { Left += p.X - _downPos.X; Top += p.Y - _downPos.Y; }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        if (_dragging)
        {
            double x = Left + 12, y = Top + 12;
            if (_svc.Settings.Snap) { x = Math.Round(x / 10) * 10; y = Math.Round(y / 10) * 10; Left = x - 12; Top = y - 12; }
            _t.X = x; _t.Y = y;
            _svc.Save();
        }
        else Click();
        _dragging = false;
        SendToBottom();
    }

    private void Click()
    {
        switch (_t.Type)
        {
            case TileType.Launcher when !string.IsNullOrEmpty(_t.Target): Env.OpenAsUser(_t.Target); break;
            case TileType.Action: _svc.RunAction(_t.Target); break;
            case TileType.Security: Env.OpenAsUser("ms-settings:windowsupdate"); _svc.RefreshSecurity(true); break;
            case TileType.System or TileType.Graph or TileType.Network: App.Instance.ShowWindow(); Ui.Go("Monitor"); break;
            case TileType.Drives: Env.OpenAsUser("shell:MyComputerFolder"); break;
            case TileType.Calendar: Env.OpenAsUser("outlookcal:"); break;
        }
    }

    private void ShowMenu()
    {
        var m = new ContextMenu();
        MenuItem I(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); return mi; }
        m.Items.Add(I("Bearbeiten …", () => { App.Instance.ShowWindow(); Ui.Go("Desktop-Kacheln"); TilesPage.Select(_t); }));
        var size = new MenuItem { Header = "Größe" };
        foreach (var (s, n) in new[] { (TileSize.Small, "Klein"), (TileSize.Wide, "Breit"), (TileSize.Tall, "Hoch"), (TileSize.Large, "Groß") })
            size.Items.Add(I(n + (_t.Size == s ? "  ✓" : ""), () => { _t.Size = s; _svc.Update(_t); }));
        m.Items.Add(size);
        if (_t.Type == TileType.Security)
        {
            m.Items.Add(I("Jetzt neu prüfen", () => _svc.RefreshSecurity(true)));
            m.Items.Add(I("Windows-Sicherheit öffnen", () => Env.OpenAsUser("windowsdefender:")));
        }
        m.Items.Add(I(_svc.Settings.Locked ? "Positionen entsperren" : "Positionen sperren", () => { _svc.Settings.Locked = !_svc.Settings.Locked; _svc.Save(); }));
        m.Items.Add(I("Alle Kacheln ausblenden", () => _svc.Visible = false));
        m.Items.Add(new Separator());
        m.Items.Add(I("Entfernen", () => _svc.Remove(_t)));
        m.PlacementTarget = this;
        m.IsOpen = true;
    }
}
