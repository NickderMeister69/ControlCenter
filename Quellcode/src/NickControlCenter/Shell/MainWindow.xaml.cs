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
            ["Passwort-Tresor"] = new("Passwort-Tresor", "Alle Passwörter an einem Ort – AES-256-verschlüsselt, mit Master-Passwort, nur auf deinem PC.", () => new PasswordPage()),
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
