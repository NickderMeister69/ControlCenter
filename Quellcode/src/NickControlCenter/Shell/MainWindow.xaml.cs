using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NickControlCenter;

public partial class MainWindow : Window
{
    private sealed record PageDef(string Title, string Sub, Func<FrameworkElement> Create);

    private readonly Dictionary<string, PageDef> _defs;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    private readonly DispatcherTimer _mini = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _current = "";

    public MainWindow()
    {
        InitializeComponent();
        _defs = new()
        {
            ["Dashboard"] = new("Dashboard", "Alles auf einen Blick – Temperaturen, Lüfter, Beleuchtung, Warnungen und Schnellaktionen.", () => new DashboardPage()),
            ["Monitor"] = new("Monitor", "Live-Werte aller Sensoren mit Verlauf, frei wählbaren Kacheln, Overlay und Aufzeichnung.", () => new MonitorPage()),
            ["FPS-Boost"] = new("FPS-Boost", "Windows für Spiele optimieren – mit eigenem Profil pro Spiel (Steam, Epic, Riot oder jede exe), Werkzeugen und Auto-Boost. Alles rückgängig machbar.", () => new BoostPage()),
            ["Zielen & Maus"] = new("Zielen & Maus", "Input-Lag-Check, Empfindlichkeit umrechnen, Maus-Abtastrate messen und Aufwärmen mit Aim Lab.", () => new AimPage()),
            ["Lüfter"] = new("Lüfter", "Jeder Lüfter einzeln: Minimum, Temperaturkurve, fest oder BIOS. Mit Sicherheitsmodus.", () => new FansPage()),
            ["Beleuchtung"] = new("Beleuchtung", "RGB für Mainboard, Lüfter-Hubs und Razer – ohne Zusatzprogramm.", () => new RgbPage()),
            ["Tweaker"] = new("Tweaker", "CPU und Grafikkarte feinjustieren – Intel, AMD und NVIDIA, mit Sicherheitsrückfrage.", () => new TweakerPage()),
            ["Stresstest"] = new("Stresstest", "CPU, Grafikkarte (jeder Hersteller) und Arbeitsspeicher auf Stabilität prüfen.", () => new StressPage()),
            ["Design & Farben"] = new("Design & Farben", "Akzentfarbe, Hell/Dunkel, Hintergrundbild, Taskleiste, Startmenü und Explorer anpassen.", () => new CustomizePage()),
            ["Desktop-Kacheln"] = new("Desktop-Kacheln", "Live-Widgets auf dem Desktop: Uhr, Systemwerte, Netzwerk, Apps, Notizen und mehr.", () => new TilesPage()),
            ["Werkzeuge"] = new("Werkzeuge", "Programme aktualisieren, Downloads aufräumen, Speicherplatz finden, automatisch ausschalten und Tastenkürzel.", () => new ToolsPage()),
            ["VPN & Werbeblocker"] = new("VPN & Werbeblocker", "Werbung und Tracker für den ganzen PC blocken – und ein VPN mit einem Klick verbinden.", () => new PrivacyPage()),
            ["Autostart"] = new("Autostart", "Programme, die mit Windows starten – mit Einschätzung, was den Start bremst. Abschalten wie im Task-Manager, alles umkehrbar.", () => new StartupPage()),
            ["Problemlöser"] = new("Problemlöser", "Findet Programme und Einstellungen, die stören – und behebt sie mit einem Klick.", () => new DiagnosePage()),
            ["Treiber"] = new("Treiber", "Grafiktreiber prüfen und aktualisieren – NVIDIA (Game Ready & Studio), AMD und Intel.", () => new DriversPage()),
            ["Protokoll"] = new("Protokoll", "Alles, was die App tut, steht hier und im Ordner „Logs“ neben der exe.", () => new LogPage()),
            ["Einstellungen"] = new("Einstellungen", "Aussehen, Start mit Windows, Verknüpfungen, Sensorquelle und mehr.", () => new SettingsPage()),
        };

        var s = AppSettings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        SizeChanged += (_, _) => { if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; } };
        // Maximiert: genau auf den Arbeitsbereich des Monitors (ohne Taskleiste) – sonst verschwindet unten „App beenden“ hinter der Taskleiste
        SourceInitialized += (_, _) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(MaxHook);
        StateChanged += (_, _) => RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        IsVisibleChanged += (_, _) => { if (!IsVisible) s.Save(); NotifyVisibility(); };

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };
        _mini.Tick += (_, _) => UpdateMini();
        _mini.Start();

        VersionRun.Text = "Control Center " + AppUpdater.CurrentText;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        };
        Loaded += (_, _) => Navigate(_defs.ContainsKey(s.LastPage) ? s.LastPage : "Dashboard");
    }

    private void UpdateMini()
    {
        if (!IsVisible) return;
        var snap = Services.Hub.Latest;
        bool f = AppSettings.Current.Fahrenheit;
        Smooth.Text(MiniCpu, snap.CpuTemp, v => Fmt.Temp(v, f));
        Smooth.Text(MiniGpu, snap.GpuTemp, v => Fmt.Temp(v, f));
        MiniCpu.Foreground = Palette.ForTemp(snap.CpuTemp, 80, 92);
        MiniGpu.Foreground = Palette.ForTemp(snap.GpuTemp, 78, 88);
        MiniSource.Text = "Quelle: " + (string.IsNullOrEmpty(snap.TempSourceText) ? "–" : snap.TempSourceText);
    }

    public void Navigate(string key)
    {
        if (!_defs.TryGetValue(key, out var def)) return;
        var nav = NavPanel.Children.OfType<RadioButton>().Concat(NavPanel.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<RadioButton>()))
                               .FirstOrDefault(r => (r.Content as string) == key);
        if (nav != null && nav.IsChecked != true) { nav.IsChecked = true; return; } // löst Nav_Checked aus
        Show(key, def);
    }

    private System.Windows.Threading.DispatcherTimer? _infoTimer;

    private void Show(string key, PageDef def)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            try { page = def.Create(); }
            catch (Exception ex)
            {
                Log.Error(LogModule.App, $"Seite {key} konnte nicht geladen werden", ex);
                page = new TextBlock { Text = $"Diese Seite konnte nicht geladen werden: {ex.Message}\nDetails im Protokoll.", Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap };
            }
            _pages[key] = page;
        }
        _current = key;
        PageTitle.Text = def.Title;
        PageSub.Text = def.Sub;
        Crumb.Text = "CONTROL CENTER  /  " + def.Title.ToUpperInvariant();
        PageHost.Content = page;
        AppSettings.Current.LastPage = key;
        // „i“-Erklärungen an die Überschriften hängen (nach dem Aufbau der Seite, und regelmäßig für später erscheinende Teile)
        Dispatcher.BeginInvoke(() => InfoTips.Attach(page), System.Windows.Threading.DispatcherPriority.Loaded);
        if (_infoTimer == null)
        {
            _infoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _infoTimer.Tick += (_, _) => { if (IsVisible && PageHost.Content is DependencyObject d) InfoTips.Attach(d); };
            _infoTimer.Start();
        }
        NotifyVisibility();
    }

    /// <summary>Seiten bekommen mit, ob sie sichtbar sind (Animationen/Vorschau nur dann).</summary>
    private void NotifyVisibility()
    {
        foreach (var (k, p) in _pages)
            if (p is IPage ip) ip.OnVisibleChanged(IsVisible && k == _current);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton r && r.Content is string key && _defs.TryGetValue(key, out var def)) Show(key, def);
    }

    public void Toast(string text, bool warn = false)
    {
        ToastText.Text = text;
        ToastText.Foreground = warn ? Palette.Warn : Palette.Text;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);

    private IntPtr MaxHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
        var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var mi = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition.X = mi.Work.Left - mi.Monitor.Left;
        mmi.MaxPosition.Y = mi.Work.Top - mi.Monitor.Top;
        mmi.MaxSize.X = mi.Work.Right - mi.Work.Left;
        mmi.MaxSize.Y = mi.Work.Bottom - mi.Work.Top;
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    // ================= Suche
    private sealed record SearchHit(string Page, string Title, string Hint, int Score);

    /// <summary>Zusätzliche Suchwörter je Seite (was man dort findet).</summary>
    private static readonly Dictionary<string, string> Keywords = new()
    {
        ["Dashboard"] = "übersicht start home strom kosten kwh stromverbrauch stromkosten euro watt schnellaktionen",
        ["Monitor"] = "overlay fps bilder pro sekunde frames temperatur temp grad hitze heiß warm sensoren cpu prozessor gpu grafikkarte auslastung last aufzeichnung csv hz hertz monitor bildwiederholrate frametime watt verlauf ram arbeitsspeicher spannung",
        ["FPS-Boost"] = "boost schneller ruckeln lag laggy performance leistung spiele game gaming zocken ark spielprofil grafik grafikeinstellungen unreal konsole konsolenbefehle befehle startoptionen ram arbeitsspeicher tweaks timer valorant fortnite cs2 steam epic feineinstellungen auto-boost schatten wolken nebel texturen",
        ["Zielen & Maus"] = "maus sensitivity empfindlichkeit dpi input lag aim abtastrate polling",
        ["Lüfter"] = "lüfter luefter fans ventilator kurve lüfterkurve leise laut lautstärke geräusch drehzahl rpm pumpe aio wasserkühlung kühlung kühler",
        ["Beleuchtung"] = "rgb licht lichter led leds farbe farben bunt razer corsair argb effekt regenbogen helligkeit dunkel beleuchtung aus nachtmodus",
        ["Tweaker"] = "übertakten übertaktung overclock overclocking oc undervolt undervolting takt mhz ghz grafikkarte cpu prozessor leistungslimit powerlimit spannung absturz",
        ["Stresstest"] = "stabilität test belastung stresstest cpu gpu ram speicher",
        ["Design & Farben"] = "design farbe akzent dunkel hell hintergrund wallpaper taskleiste startmenü explorer",
        ["Desktop-Kacheln"] = "widgets kacheln uhr notizen desktop",
        ["Werkzeuge"] = "updates update winget programme apps aktualisieren steam spiele spieleupdates downloads download ordner aufräumen sortieren speicherplatz speicher voll platz festplatte ssd hdd laufwerk gesundheit smart verschleiß lebensdauer speedtest internet langsam schnell wlan geschwindigkeit mbit ping jitter herunterfahren ausschalten shutdown timer tastenkürzel hotkeys tastenkombination löschen temp müll papierkorb",
        ["VPN & Werbeblocker"] = "vpn werbung werbeblocker adblock adblocker ads reklame dns wireguard proton ip adresse standort datenschutz privat tracker youtube",
        ["Autostart"] = "autostart hochfahren windows start programme langsam",
        ["Problemlöser"] = "probleme fehler konflikt flackern gigabyte wallpaper engine dienst diagnose",
        ["Treiber"] = "treiber driver nvidia geforce amd radeon intel chipsatz audio sound ton netzwerk lan wlan bluetooth usb windows update installieren aktuell",
        ["Protokoll"] = "log logs protokoll fehler",
        ["Einstellungen"] = "einstellungen optionen version appversion update neue version sicherung backup wiederherstellen zurücksetzen hwinfo autostart tray verknüpfung desktop fahrenheit",
    };

    /// <summary>Kleinschreibung, Umlaute vereinheitlichen (ü = ue), Satzzeichen raus.</summary>
    private static string Norm(string t)
    {
        t = t.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new System.Text.StringBuilder(t.Length);
        foreach (var c in t) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.ToString();
    }

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 99;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int k = 0; k <= b.Length; k++) d[0, k] = k;
        for (int i = 1; i <= a.Length; i++)
            for (int k = 1; k <= b.Length; k++)
                d[i, k] = Math.Min(Math.Min(d[i - 1, k] + 1, d[i, k - 1] + 1), d[i - 1, k - 1] + (a[i - 1] == b[k - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Wie gut passt ein Suchwort zu einem Wort? 0 = gar nicht. Tippfehler und Wortteile zählen auch.</summary>
    private static int Match(string w, string t)
    {
        if (t == w) return 10;
        if (w.Length >= 2 && t.StartsWith(w)) return 8;                 // „luef“ → „luefter“
        if (w.Length >= 4 && t.Length >= 3 && w.StartsWith(t)) return 6; // „luefterkurve“ → „luefter“
        if (w.Length >= 3 && t.Contains(w)) return 6;                    // „kurve“ in „temperaturkurve“
        if (w.Length >= 4 && t.Length >= 4)
        {
            int dist = Distance(w, t.Length > w.Length + 2 ? t[..(w.Length + 1)] : t);
            if (dist <= (w.Length >= 7 ? 2 : 1)) return 4;               // Tippfehler: „luefer“, „belauchtung“
        }
        return 0;
    }

    private List<SearchHit> Search(string q)
    {
        var words = Norm(q).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).ToArray();
        var hits = new List<SearchHit>();
        if (words.Length == 0) return hits;
        foreach (var (key, def) in _defs)
        {
            var titleWords = Norm(def.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var textWords = Norm(def.Sub + " " + (Keywords.TryGetValue(key, out var k) ? k : "")).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            int score = 0, matched = 0;
            var why = new List<string>();
            foreach (var w in words)
            {
                int best = titleWords.Select(t => Match(w, t) * 2).DefaultIfEmpty(0).Max();
                string? bestWord = null;
                foreach (var t in textWords)
                {
                    int m = Match(w, t);
                    if (m > best) { best = m; bestWord = t; }
                }
                if (best > 0) { matched++; score += best; if (bestWord != null && why.Count < 3 && !why.Contains(bestWord)) why.Add(bestWord); }
            }
            if (matched == 0) continue;
            // mehr passende Wörter schlagen höhere Einzelwertung
            score += matched * 100;
            var hint = why.Count > 0 ? "passt zu: " + string.Join(", ", why) + " · " + def.Sub : def.Sub;
            hits.Add(new SearchHit(key, def.Title, hint, score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title).Take(8).ToList();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q.Length < 2) { SearchPopup.IsOpen = false; return; }
        var hits = Search(q);
        SearchResults.ItemsSource = hits.Count > 0 ? hits : new List<SearchHit> { new("", "Nichts gefunden", "Anderes Wort versuchen, z. B. „Lüfter“, „RGB“, „Update“", 0) };
        SearchResults.SelectedIndex = hits.Count > 0 ? 0 : -1;
        SearchPopup.IsOpen = true;
    }

    private void GoToHit(SearchHit? h)
    {
        if (h == null || h.Page.Length == 0) return;
        SearchPopup.IsOpen = false;
        SearchBox.Text = "";
        Navigate(h.Page);
    }

    private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { GoToHit(SearchResults.SelectedItem as SearchHit); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { SearchBox.Text = ""; SearchPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Down && SearchPopup.IsOpen)
        {
            if (SearchResults.SelectedIndex < SearchResults.Items.Count - 1) SearchResults.SelectedIndex++;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Up && SearchPopup.IsOpen)
        {
            if (SearchResults.SelectedIndex > 0) SearchResults.SelectedIndex--;
            e.Handled = true;
        }
    }

    private void SearchResult_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => GoToHit(SearchResults.SelectedItem as SearchHit);
    private void SearchResults_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) GoToHit(SearchResults.SelectedItem as SearchHit); }

    private void GitHub_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) { Env.Open(e.Uri.AbsoluteUri); e.Handled = true; }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var u = AppUpdater.Instance;
        if (u.Busy) return;
        var notes = string.IsNullOrWhiteSpace(u.Notes) ? "" : "\n\nNeu in dieser Version:\n" + (u.Notes.Length > 900 ? u.Notes[..900] + " …" : u.Notes);
        if (!Dialogs.Confirm($"Update auf {u.LatestText}?",
                $"Installiert: {AppUpdater.CurrentText} → neu: {u.LatestText}\nGröße: {Fmt.Bytes(u.Size)} · Dauer {InstallEstimate.DurationText(InstallEstimate.Duration(u.Size, 10))}{notes}\n\nDie App lädt die neue Version, startet kurz neu – alle Einstellungen bleiben erhalten.",
                "Jetzt aktualisieren", "Später")) return;
        if (!await u.DownloadAsync()) Ui.Toast(u.Status, true);
    }

    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close(); // App fängt das ab → läuft im Tray weiter
    private void Exit_Click(object sender, RoutedEventArgs e) => App.Instance.ExitApp(ask: true);
}

/// <summary>Seiten, die wissen wollen, ob sie gerade angezeigt werden.</summary>
public interface IPage
{
    void OnVisibleChanged(bool visible);
}

public static class Ui
{
    public static void Toast(string text, bool warn = false) => (Application.Current?.MainWindow as MainWindow)?.Toast(text, warn);
    public static void Go(string page) => (Application.Current?.MainWindow as MainWindow)?.Navigate(page);
}
