using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NickControlCenter;

/// <summary>Ein Punkt im Input-Lag-Check.</summary>
public sealed class LagCheck
{
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public bool Ok { get; init; }
    public bool Warn { get; init; }
    public Brush Brush => Ok ? Palette.Good : Warn ? Palette.Warn : Palette.Bad;
    public Action? Fix { get; init; }
    public string FixText { get; init; } = "Beheben";
    public bool CanFix => Fix != null && !Ok;
}

/// <summary>Ein Punkt der In-Game-Checkliste (wird gespeichert).</summary>
public sealed class InGameItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Why { get; init; } = "";
    private bool _on;
    public bool On { get => _on; set { if (Set(ref _on, value)) Changed?.Invoke(); } }
    public Action? Changed;
}

public sealed class AimSettings
{
    public List<string> Done { get; set; } = new();
    public int FromGame { get; set; } = 1;
    public int ToGame { get; set; }
    public string Sens { get; set; } = "1";
    public string Dpi { get; set; } = "800";
}

/// <summary>
/// Zielen & Maus: Input-Lag-Check, Empfindlichkeits-Umrechner, Abtastraten-Messung und Aufwärm-Routine.
/// Alles erlaubt – nichts greift ins Spiel ein.
/// </summary>
public partial class AimPage : UserControl, IPage
{
    private readonly AimSettings _s = JsonStore.Load<AimSettings>("zielen.json");
    private bool _init = true;

    public AimPage()
    {
        InitializeComponent();
        FromGame.ItemsSource = AimTools.Games; ToGame.ItemsSource = AimTools.Games;
        FromGame.SelectedIndex = Math.Clamp(_s.FromGame, 0, AimTools.Games.Length - 1);
        ToGame.SelectedIndex = Math.Clamp(_s.ToGame, 0, AimTools.Games.Length - 1);
        SensBox.Text = _s.Sens; DpiBox.Text = _s.Dpi;
        var items = new[]
        {
            new InGameItem { Key = "reflex", Label = "NVIDIA Reflex: „An + Boost“", Why = "Größter Einzelgewinn: Grafikkarte und CPU arbeiten ohne Warteschlange – deutlich weniger Verzögerung." },
            new InGameItem { Key = "vsync", Label = "V-Sync aus", Why = "V-Sync wartet auf den Monitor – kostet spürbar Reaktionszeit." },
            new InGameItem { Key = "fps", Label = "Max. FPS: unbegrenzt oder deutlich über deiner Hz-Zahl", Why = "Mehr Bilder = neuere Bilder. Mit G-Sync: ca. 3 FPS unter der Hz-Zahl begrenzen." },
            new InGameItem { Key = "mtr", Label = "Multithreaded Rendering: an", Why = "Verteilt die Arbeit auf mehrere CPU-Kerne – mehr FPS bei deinem 10-Kern-Prozessor." },
            new InGameItem { Key = "raw", Label = "Raw-Input-Puffer: an", Why = "Liest die Maus direkt aus – kein Umweg über Windows." },
            new InGameItem { Key = "gfx", Label = "Material/Textur/Details niedrig, Vignette/Bloom/Verzerrung aus", Why = "Mehr FPS und klarere Sicht auf Gegner." },
            new InGameItem { Key = "fullscreen", Label = "Anzeigemodus: Vollbild", Why = "Echtes Vollbild hat weniger Verzögerung als „Fenster“." },
            new InGameItem { Key = "hz", Label = "Monitor-Hz in Windows richtig gesetzt", Why = "Siehe Check links – ein 144-Hz-Monitor läuft oft versehentlich mit 60 Hz." },
        };
        foreach (var i in items) { i.On = _s.Done.Contains(i.Key); i.Changed = () => { _s.Done = items.Where(x => x.On).Select(x => x.Key).ToList(); Save(); }; }
        InGame.ItemsSource = items;
        _init = false;
        UpdateSens();
    }

    private void Save() => JsonStore.Save("zielen.json", _s);

    public void OnVisibleChanged(bool visible) { if (visible) RunChecks(); }

    // ------------------------------------------------------------ Input-Lag-Check
    private void Recheck_Click(object s, RoutedEventArgs e) => RunChecks();

    private void RunChecks()
    {
        var b = Services.Boost;
        var list = new List<LagCheck>();
        // Mausbeschleunigung
        int[] m = new int[3];
        Native.SystemParametersInfo(0x0003, 0, m, 0);
        bool accelOff = m[2] == 0;
        list.Add(new LagCheck { Title = accelOff ? "Mausbeschleunigung aus" : "Mausbeschleunigung ist AN", Ok = accelOff,
            Detail = accelOff ? "Mausweg und Bewegung im Spiel sind 1:1." : "Schnelle Bewegungen drehen weiter als langsame – Muskelgedächtnis funktioniert so nicht.",
            Fix = () => b.ApplySingle("mouse") });
        // Zeigergeschwindigkeit
        int speed = 10;
        try { var h = Marshal.AllocHGlobal(4); Native.SystemParametersInfo(0x0070, 0, h, 0); speed = Marshal.ReadInt32(h); Marshal.FreeHGlobal(h); } catch { }
        list.Add(new LagCheck { Title = $"Windows-Zeigergeschwindigkeit {speed} von 20", Ok = speed == 10, Warn = speed != 10,
            Detail = speed == 10 ? "Standard (6/11) – kein Überspringen von Pixeln." : "Nicht auf Standard: auf dem Desktop werden Pixel übersprungen oder die Maus wirkt zäh. In Spielen mit Raw Input egal.",
            FixText = "Auf 10 setzen", Fix = () => { Native.SystemParametersInfo(0x0071, 0, (IntPtr)10, 3); } });
        // Bildwiederholrate
        var (cur, max) = RefreshRates();
        bool hzOk = cur <= 0 || max <= 0 || cur >= max - 1;
        list.Add(new LagCheck { Title = cur > 0 ? $"Monitor läuft mit {cur} Hz" + (max > cur ? $" (kann {max} Hz)" : "") : "Bildwiederholrate unbekannt",
            Ok = hzOk, Detail = hzOk ? "Höchste Bildwiederholrate aktiv." : "Der Monitor läuft unter seinen Möglichkeiten! Windows-Einstellungen → Anzeige → Erweiterte Anzeige → Aktualisierungsrate.",
            FixText = "Einstellungen öffnen", Fix = () => Env.OpenAsUser("ms-settings:display-advanced") });
        // Windows-Spielmodus, Game DVR, HAGS, Timer, Energieplan
        void Tweak(string id, string ok, string bad, string detail)
        {
            bool on = b.IsActive(id);
            list.Add(new LagCheck { Title = on ? ok : bad, Ok = on, Warn = !on, Detail = detail, Fix = () => b.ApplySingle(id) });
        }
        Tweak("gamemode", "Windows-Spielmodus an", "Windows-Spielmodus aus", "Hintergrundaufgaben und Updates warten, solange du spielst.");
        Tweak("gamedvr", "Xbox-Hintergrundaufnahme aus", "Xbox-Hintergrundaufnahme läuft", "Die ständige Aufnahme kostet FPS und erhöht die Verzögerung.");
        Tweak("hags", "Hardwarebeschleunigte GPU-Planung an", "Hardwarebeschleunigte GPU-Planung aus", "Etwas weniger Latenz; nötig für Frame Generation. Wirkt nach Neustart.");
        Tweak("timer", "Timer-Auflösung 0,5 ms aktiv", "Timer-Auflösung Standard (15,6 ms)", "Gleichmäßigere Bildzeiten und schnellere Reaktion.");
        Tweak("power", "Energieplan Höchstleistung", "Energieplan nicht auf Höchstleistung", "Keine Verzögerung durch Hochtakten der CPU.");
        // VALORANT-Profil
        var val = b.Games.FirstOrDefault(g => g.Known?.Id == "valorant");
        list.Add(new LagCheck
        {
            Title = val == null ? "Kein VALORANT-Profil" : $"VALORANT-Profil: {GameCatalog.PresetNames[(int)val.P.Preset]}",
            Ok = val is { P.Preset: GamePreset.MaxFps or GamePreset.Custom }, Warn = val != null,
            Detail = "Beim Start: hohe Priorität, Timer, Höchstleistung, Hintergrund-Apps zu, RAM frei.",
            FixText = val == null ? "Anlegen" : "Auf Max-FPS",
            Fix = () => { var g = val ?? b.AddKnownGame("VALORANT"); if (g != null) b.SetPreset(g, GamePreset.MaxFps); }
        });
        Checks.ItemsSource = list;
    }

    private static (int Cur, int Max) RefreshRates()
    {
        int cur = 0, max = 0;
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT CurrentRefreshRate, MaxRefreshRate FROM Win32_VideoController");
            foreach (System.Management.ManagementObject o in q.Get())
                using (o)
                {
                    int c = Convert.ToInt32(o["CurrentRefreshRate"] ?? 0), mx = Convert.ToInt32(o["MaxRefreshRate"] ?? 0);
                    if (c > cur) { cur = c; max = mx; }
                }
        }
        catch { }
        return (cur, max);
    }

    private void Fix_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is LagCheck c && c.Fix != null)
        {
            try { c.Fix(); } catch (Exception ex) { Ui.Toast(ex.Message, true); }
            RunChecks();
        }
    }

    // ------------------------------------------------------------ Empfindlichkeit
    private void Sens_Changed(object s, RoutedEventArgs e) { if (!_init) UpdateSens(); }

    private static double Num(string t) => double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    private void UpdateSens()
    {
        if (FromGame.SelectedItem is not SensGame from || ToGame.SelectedItem is not SensGame to) return;
        double sens = Num(SensBox.Text); int dpi = (int)(double.IsNaN(Num(DpiBox.Text)) ? 0 : Num(DpiBox.Text));
        _s.FromGame = FromGame.SelectedIndex; _s.ToGame = ToGame.SelectedIndex; _s.Sens = SensBox.Text; _s.Dpi = DpiBox.Text; Save();
        if (double.IsNaN(sens) || sens <= 0) { SensResult.Text = "–"; SensCm.Text = "Empfindlichkeit eingeben (z. B. 0,4)"; SensAll.ItemsSource = null; return; }
        double r = AimTools.Convert(sens, from, to);
        SensResult.Text = $"{to.Name}: {r:0.###}";
        double cm = AimTools.CmPer360(sens, dpi, from);
        SensCm.Text = dpi > 0 ? $"{cm:0.0} cm pro 360°-Drehung bei {dpi} DPI · eDPI {sens * dpi:0} – {AimTools.Judge(cm)}" : "DPI eingeben, um cm/360° zu sehen";
        SensHint.Text = to.Hint;
        SensAll.ItemsSource = AimTools.Games.Where(g => g != from).Select(g => $"{g.Name,-30} {AimTools.Convert(sens, from, g),8:0.###}").ToList();
    }

    // ------------------------------------------------------------ Abtastrate (Polling Rate)
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint num, uint size);

    private int _events;
    private HwndSource? _src;

    private async void Poll_Click(object s, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win == null) return;
        var h = new WindowInteropHelper(win).Handle;
        _src = HwndSource.FromHwnd(h);
        _src.AddHook(Hook);
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = 0x100, Target = h } }; // RIDEV_INPUTSINK
        RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        PollBtn.IsEnabled = false;
        PollResult.Text = "Maus jetzt schnell im Kreis bewegen …";
        PollHint.Text = "";
        var samples = new List<int>();
        for (int i = 0; i < 12; i++)
        {
            _events = 0;
            await Task.Delay(250);
            samples.Add(_events * 4);
        }
        _src.RemoveHook(Hook);
        dev[0].Flags = 0x1; dev[0].Target = IntPtr.Zero; // RIDEV_REMOVE
        RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        PollBtn.IsEnabled = true;
        // obere Hälfte der Messungen = Phasen mit schneller Bewegung
        var top = samples.OrderByDescending(x => x).Take(4).ToList();
        if (top.Count == 0 || top[0] < 60) { PollResult.Text = "Keine Bewegung erkannt"; PollHint.Text = "Nochmal versuchen und die Maus währenddessen ständig bewegen."; return; }
        double avg = top.Average();
        int rate = AimTools.NearestPollingRate(avg);
        PollResult.Text = $"≈ {rate} Hz Abtastrate (gemessen {avg:0}/s)";
        PollHint.Text = rate >= 1000 ? "Sehr gut – 1000 Hz oder mehr ist der Standard für Shooter."
                     : rate >= 500 ? "Okay. Im Maus-Programm auf 1000 Hz stellen bringt etwas weniger Verzögerung und glattere Bewegungen."
                     : "Niedrig! Im Treiber/Programm deiner Maus auf 1000 Hz stellen (falls möglich) – die Maus meldet sich sonst nur alle 4–8 ms.";
        Log.Info(LogModule.Boost, $"Maus-Abtastrate gemessen: ~{rate} Hz ({avg:0} Ereignisse/s)");
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x00FF) _events++; // WM_INPUT
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------ Aufwärmen
    private const int AimLabAppId = 714010;

    private void AimLabStore_Click(object s, RoutedEventArgs e) => Env.OpenAsUser($"steam://store/{AimLabAppId}");

    private async void Warmup_Click(object s, RoutedEventArgs e)
    {
        WarmupStatus.Text = "Starte Aim Lab …";
        Env.OpenAsUser($"steam://rungameid/{AimLabAppId}");
        Log.Info(LogModule.Boost, "Aufwärmen: Aim Lab gestartet");
        // warten bis Aim Lab läuft (max. 3 min) und dann bis es wieder zu ist
        bool seen = false;
        var until = DateTime.Now.AddMinutes(3);
        while (true)
        {
            await Task.Delay(3000);
            bool running = Process.GetProcesses().Any(p => { var n = p.ProcessName; p.Dispose(); return n.StartsWith("AimLab", StringComparison.OrdinalIgnoreCase); });
            if (running && !seen) { seen = true; WarmupStatus.Text = "Aim Lab läuft – viel Spaß beim Aufwärmen. Schließ Aim Lab, dann startet VALORANT."; }
            if (seen && !running) break;
            if (!seen && DateTime.Now > until) { WarmupStatus.Text = "Aim Lab wurde nicht gestartet (installiert?)."; return; }
        }
        WarmupStatus.Text = "Aufgewärmt – starte VALORANT …";
        var b = Services.Boost;
        var val = b.Games.FirstOrDefault(g => g.Known?.Id == "valorant") ?? b.AddKnownGame("VALORANT");
        if (val != null && val.CanLaunch) b.Launch(val);
        else
        {
            var client = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
            if (System.IO.File.Exists(client)) Env.OpenAsUser(client, "--launch-product=valorant --launch-patchline=live");
            else WarmupStatus.Text = "VALORANT nicht gefunden – bitte selbst starten.";
        }
        Log.Info(LogModule.Boost, "Aufwärmen fertig – VALORANT gestartet");
    }
}
