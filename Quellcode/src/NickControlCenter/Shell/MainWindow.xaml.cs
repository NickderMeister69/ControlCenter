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
            ["FPS-Boost"] = new("FPS-Boost", "Windows für Spiele optimieren – mit Spielprofilen (ARK), Werkzeugen und Auto-Boost. Alles rückgängig machbar.", () => new BoostPage()),
            ["Lüfter"] = new("Lüfter", "Jeder Lüfter einzeln: Minimum, Temperaturkurve, fest oder BIOS. Mit Sicherheitsmodus.", () => new FansPage()),
            ["Beleuchtung"] = new("Beleuchtung", "RGB für Mainboard, Lüfter-Hubs und Razer – ohne Zusatzprogramm.", () => new RgbPage()),
            ["Tweaker"] = new("Tweaker", "CPU und Grafikkarte feinjustieren – Intel, AMD und NVIDIA, mit Sicherheitsrückfrage.", () => new TweakerPage()),
            ["Stresstest"] = new("Stresstest", "CPU, Grafikkarte (jeder Hersteller) und Arbeitsspeicher auf Stabilität prüfen.", () => new StressPage()),
            ["Design & Farben"] = new("Design & Farben", "Akzentfarbe, Hell/Dunkel, Hintergrundbild, Taskleiste, Startmenü und Explorer anpassen.", () => new CustomizePage()),
            ["Desktop-Kacheln"] = new("Desktop-Kacheln", "Live-Widgets auf dem Desktop: Uhr, Systemwerte, Netzwerk, Apps, Notizen und mehr.", () => new TilesPage()),
            ["Problemlöser"] = new("Problemlöser", "Findet Programme und Einstellungen, die stören – und behebt sie mit einem Klick.", () => new DiagnosePage()),
            ["Treiber"] = new("Treiber", "Grafiktreiber prüfen und aktualisieren – NVIDIA (Game Ready & Studio), AMD und Intel.", () => new DriversPage()),
            ["Protokoll"] = new("Protokoll", "Alles, was die App tut, steht hier und im Ordner „Logs“ neben der exe.", () => new LogPage()),
            ["Einstellungen"] = new("Einstellungen", "Aussehen, Start mit Windows, Verknüpfungen, Sensorquelle und mehr.", () => new SettingsPage()),
        };

        var s = AppSettings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        SizeChanged += (_, _) => { if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; } };
        StateChanged += (_, _) => RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
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
        MiniCpu.Text = Fmt.Temp(snap.CpuTemp, f);
        MiniGpu.Text = Fmt.Temp(snap.GpuTemp, f);
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
        Crumb.Text = "NICK CONTROL CENTER  /  " + def.Title.ToUpperInvariant();
        PageHost.Content = page;
        AppSettings.Current.LastPage = key;
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
