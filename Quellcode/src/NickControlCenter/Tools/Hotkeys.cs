using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace NickControlCenter;

public sealed class HotkeyDef
{
    public string Action { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8</summary>
    public uint Mods { get; set; }
    public uint Vk { get; set; }
}

public sealed class HotkeySettings
{
    public List<HotkeyDef> Keys { get; set; } = new();
}

/// <summary>Eine Tastenkombination in der Liste.</summary>
public sealed class HotkeyVM : ObservableObject
{
    public HotkeyDef Def { get; }
    public string Title { get; }
    public string Description { get; }
    public HotkeyVM(HotkeyDef d, string title, string desc) { Def = d; Title = title; Description = desc; }
    public bool Enabled { get => Def.Enabled; set { Def.Enabled = value; Raise(); HotkeyService.Instance.Apply(); } }
    public string KeyText => HotkeyService.Text(Def.Mods, Def.Vk);
    private string _state = "";
    public string State { get => _state; set { Set(ref _state, value); Raise(nameof(HasState)); } }
    public bool HasState => _state.Length > 0;
    private bool _recording;
    public bool Recording { get => _recording; set { Set(ref _recording, value); Raise(nameof(RecordText)); } }
    public string RecordText => _recording ? "Tasten drücken …" : "Ändern";
    public void Changed() => RaiseAll(nameof(KeyText));
}

/// <summary>
/// Globale Tastenkürzel – funktionieren auch, wenn das Control Center im Tray ist oder ein Spiel läuft.
/// Feedback kommt als kleine Einblendung oben in der Mitte.
/// </summary>
public sealed class HotkeyService
{
    private static HotkeyService? _instance;
    // erst beim ersten Zugriff anlegen – sonst wäre „Defaults“ (steht weiter unten) noch leer
    public static HotkeyService Instance => _instance ??= new();
    public HotkeySettings Settings { get; } = JsonStore.Load<HotkeySettings>("tastenkuerzel.json");
    public List<HotkeyVM> Items { get; } = new();
    private HwndSource? _src;
    private readonly List<int> _registered = new();
    private double _rgbBefore = 100;

    private static readonly (string Id, string Title, string Desc, uint Mods, uint Vk, bool On)[] Defaults =
    {
        ("boost", "FPS-Boost ausführen", "Wendet die gewählten Boost-Einstellungen an, schließt Hintergrund-Apps und gibt RAM frei.", 2 | 1, 0x42, true),   // Strg+Alt+B
        ("ram", "Arbeitsspeicher freigeben", "Leert den Standby-Speicher – hilft bei Rucklern nach langem Spielen.", 2 | 1, 0x52, true),                   // Strg+Alt+R
        ("fans", "Lüfter: Leise ⇄ Normal", "Schaltet das Lüfterprofil um – z. B. für Filme oder nachts.", 2 | 1, 0x4C, true),                               // Strg+Alt+L
        ("fansmax", "Lüfter: Leistung", "Lüfterprofil „Leistung“ – kühlt früher und stärker.", 2 | 1, 0x50, false),                                       // Strg+Alt+P
        ("rgb", "Beleuchtung an/aus", "Dimmt alle RGB-Geräte auf 0 % und wieder zurück.", 2 | 1, 0x48, true),                                              // Strg+Alt+H
        ("overlay", "Overlay an/aus", "Blendet die FPS- und Temperaturanzeige im Spiel ein oder aus.", 2 | 1, 0x4F, true),                                // Strg+Alt+O
        ("screenoff", "Bildschirm aus", "Schaltet die Monitore aus (Maus bewegen schaltet wieder ein) – z. B. bei langen Downloads.", 2 | 1, 0x4D, true),     // Strg+Alt+M
        ("open", "Control Center öffnen", "Holt das Fenster nach vorne.", 2 | 1, 0x43, true),                                                               // Strg+Alt+C
        ("shot", "Leistungswerte merken", "Schreibt die aktuellen Werte (FPS, Temperaturen, Takt, Watt) ins Protokoll – praktisch bei Rucklern.", 2 | 1, 0x4E, false), // Strg+Alt+N
    };

    private HotkeyService()
    {
        foreach (var d in Defaults)
        {
            var def = Settings.Keys.FirstOrDefault(k => k.Action == d.Id);
            if (def == null) { def = new HotkeyDef { Action = d.Id, Enabled = d.On, Mods = d.Mods, Vk = d.Vk }; Settings.Keys.Add(def); }
            Items.Add(new HotkeyVM(def, d.Title, d.Desc));
        }
    }

    public void Start()
    {
        if (_src != null) return;
        var p = new HwndSourceParameters("NickControlCenterHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE
        _src = new HwndSource(p);
        _src.AddHook(Hook);
        Apply();
    }

    public void Save() => JsonStore.Save("tastenkuerzel.json", Settings);

    public void Apply()
    {
        Save();
        if (_src == null) return;
        foreach (var id in _registered) Native.UnregisterHotKey(_src.Handle, id);
        _registered.Clear();
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (!it.Def.Enabled || it.Def.Vk == 0) { it.State = ""; continue; }
            int id = 0x5100 + i;
            if (Native.RegisterHotKey(_src.Handle, id, it.Def.Mods | 0x4000, it.Def.Vk)) { _registered.Add(id); it.State = ""; }
            else { it.State = "Schon von einem anderen Programm belegt – andere Kombination wählen"; Log.Warn(LogModule.App, $"Tastenkürzel {Text(it.Def.Mods, it.Def.Vk)} ({it.Title}) ist belegt"); }
        }
    }

    /// <summary>Während eine neue Kombination aufgenommen wird, alle Kürzel kurz abmelden.</summary>
    public void Suspend()
    {
        if (_src == null) return;
        foreach (var id in _registered) Native.UnregisterHotKey(_src.Handle, id);
        _registered.Clear();
    }

    public void SetKey(HotkeyVM vm, uint mods, uint vk)
    {
        vm.Def.Mods = mods; vm.Def.Vk = vk; vm.Def.Enabled = true;
        vm.Changed(); vm.Recording = false;
        Apply();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            int i = wParam.ToInt32() - 0x5100;
            if (i >= 0 && i < Items.Count) { handled = true; Run(Items[i].Def.Action); }
        }
        return IntPtr.Zero;
    }

    public void Run(string action)
    {
        try
        {
            switch (action)
            {
                case "boost": Osd.Show("FPS-Boost wird ausgeführt …"); _ = Services.Boost.BoostAsync(); break;
                case "ram": Osd.Show("Arbeitsspeicher wird freigegeben …"); _ = Services.Boost.CleanRamAsync(); break;
                case "fans":
                    Services.Fans.Profile = Services.Fans.Profile == "Leise" ? "Normal" : "Leise";
                    Osd.Show("Lüfter: " + Services.Fans.Profile); break;
                case "fansmax": Services.Fans.Profile = "Leistung"; Osd.Show("Lüfter: Leistung"); break;
                case "rgb":
                {
                    var r = Services.Fans.Rgb;
                    if (r.Brightness > 0.5) { _rgbBefore = r.Brightness; r.Brightness = 0; Osd.Show("Beleuchtung aus"); }
                    else { r.Brightness = _rgbBefore > 0.5 ? _rgbBefore : 100; Osd.Show("Beleuchtung an"); }
                    break;
                }
                case "overlay":
                    Services.Monitor.SetOverlay(!Services.Monitor.Settings.Overlay);
                    Osd.Show("Overlay " + (Services.Monitor.Settings.Overlay ? "an" : "aus")); break;
                case "screenoff":
                    // kurz warten, damit das Loslassen der Tasten den Bildschirm nicht gleich wieder weckt
                    Task.Delay(600).ContinueWith(_ => Native.SendMessageW(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2)));
                    break;
                case "open": (Application.Current as App)?.ShowWindow(); break;
                case "shot":
                {
                    var s = Services.Hub.Latest;
                    var fps = FpsCounter.Instance.Foreground().Fps;
                    Log.Info(LogModule.Monitor, $"Momentaufnahme: FPS {(double.IsNaN(fps) ? "–" : fps.ToString("0"))} · CPU {s.CpuTemp:0} °C {s.CpuLoad:0} % {s.CpuClock:0} MHz {s.CpuPower:0} W · GPU {s.GpuTemp:0} °C {s.GpuLoad:0} % {s.GpuClock:0} MHz {s.GpuPower:0} W · RAM {s.RamLoad:0} %");
                    Osd.Show("Werte ins Protokoll geschrieben");
                    break;
                }
            }
            Log.Info(LogModule.App, "Tastenkürzel: " + action);
        }
        catch (Exception ex) { Log.Warn(LogModule.App, $"Tastenkürzel {action}: {ex.Message}"); }
    }

    public static string Text(uint mods, uint vk)
    {
        if (vk == 0) return "–";
        var parts = new List<string>();
        if ((mods & 2) != 0) parts.Add("Strg");
        if ((mods & 1) != 0) parts.Add("Alt");
        if ((mods & 4) != 0) parts.Add("Umschalt");
        if ((mods & 8) != 0) parts.Add("Win");
        var k = KeyInterop.KeyFromVirtualKey((int)vk);
        string name = k switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
            Key.OemPlus => "+", Key.OemMinus => "-", Key.Space => "Leertaste", Key.Pause => "Pause", Key.Scroll => "Rollen",
            Key.Insert => "Einfg", Key.Delete => "Entf", Key.Home => "Pos1", Key.End => "Ende", Key.PageUp => "Bild↑", Key.Next => "Bild↓",
            _ => k.ToString()
        };
        parts.Add(name);
        return string.Join(" + ", parts);
    }
}

/// <summary>Kurze Einblendung oben in der Mitte (auch über Spielen im randlosen Fenstermodus).</summary>
public static class Osd
{
    private static Window? _w;
    private static System.Windows.Controls.TextBlock? _t;
    private static readonly System.Windows.Threading.DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1.6) };

    public static void Show(string text)
    {
        if (_w == null)
        {
            _t = new System.Windows.Controls.TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White };
            _w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, Topmost = true,
                ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Focusable = false,
                Content = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(225, 12, 16, 22)),
                    BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush"), BorderThickness = new Thickness(0, 0, 0, 2),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(18, 9, 18, 9), Child = _t
                }
            };
            _w.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(_w).Handle;
                long ex = Native.GetWindowLongPtr(h, -20).ToInt64();
                Native.SetWindowLongPtr(h, -20, new IntPtr(ex | 0x20 | 0x80 | 0x08000000));
            };
            _hide.Tick += (_, _) => { _hide.Stop(); _w?.Hide(); };
        }
        _t!.Text = text;
        _w.Show();
        _w.UpdateLayout();
        var wa = SystemParameters.WorkArea;
        _w.Left = wa.Left + (wa.Width - _w.ActualWidth) / 2; _w.Top = wa.Top + 40;
        _hide.Stop(); _hide.Start();
    }
}
