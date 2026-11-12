using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NickControlCenter;

public sealed class ServicesVM
{
    public FanController Fans => Services.Fans;
    public RgbController Rgb => Services.Fans.Rgb;
    public BoostService Boost => Services.Boost;
    public MonitorService Monitor => Services.Monitor;
    public StressService Stress => Services.Stress;
    public TweakerService Tweaker => Services.Tweaker;
    public CustomizeService Customize => Services.Customize;
    public TilesService Tiles => Services.Tiles;
    public DriverService Drivers => Services.Drivers;
    public DiagnosticsService Diagnose => DiagnosticsService.Instance;
}

public partial class DashboardPage : UserControl
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(2) };

    public DashboardPage()
    {
        InitializeComponent();
        DataContext = new ServicesVM();
        SetupEditor();
        Hints.OnlyImportant = true;
        SelectProfile();
        _t.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _t.Start(); };
        Unloaded += (_, _) => _t.Stop();
    }

    private void Refresh()
    {
        var s = Services.Hub.Latest;
        CpuText.Text = string.IsNullOrEmpty(s.CpuName) ? Services.Tweaker.CpuName : s.CpuName;
        GpuText.Text = string.IsNullOrEmpty(s.GpuName) ? Services.Tweaker.Gpu.Name : s.GpuName;
        RamText.Text = double.IsNaN(s.RamTotalGb) ? "RAM" : $"{s.RamTotalGb:0} GB RAM";
        SourceText.Text = $"Temperaturen: {s.TempSourceText}" + (string.IsNullOrEmpty(s.CpuTempLabel) ? "" : $" · CPU = {s.CpuTempLabel}");
        TweakCpu.Text = "CPU: " + Services.Tweaker.Settings.Cpu;
        TweakGpu.Text = "Grafikkarte: " + (Services.Tweaker.Gpu.Available ? Services.Tweaker.Settings.Gpu.ToString() : Services.Tweaker.Gpu.Info);
        OverlayToggle.IsChecked = Services.Monitor.OverlayOn;
        OverlayGamesToggle.IsChecked = Services.Monitor.OverlayOnlyInGames;
        var en = EnergyService.Instance;
        Smooth.Text(PowerNow, en.NowWatts, v => double.IsNaN(v) ? "–" : $"{v:0} W");
        PowerText.Text = en.Summary.Substring(Math.Max(0, en.Summary.IndexOf('\n') + 1));
        PowerNow.ToolTip = en.Summary;
        if (!PriceBox.IsKeyboardFocused) PriceBox.Text = en.Settings.PricePerKwh.ToString("0.00");
        HintsHead.Visibility = DiagnosticsService.Instance.Findings.Any(f => f.Severity >= Severity.Hint) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- Dashboard anpassen
    private static readonly string[] DefSections = { "system", "kpis", "cards", "hints" };
    private static readonly string[] DefCards = { "fans", "rgb", "boost", "power", "drivers", "tweaker", "extras" };
    private static readonly string[] DefKpis = { "cpu.temp", "gpu.temp", "cpu.load", "gpu.load", "ram.load", "cpu.power", "gpu.power", "net.down" };

    private Dictionary<string, FrameworkElement> SectionMap => new()
    {
        ["system"] = SecSystem, ["kpis"] = Kpis, ["cards"] = Cards, ["hints"] = SecHints
    };
    private Dictionary<string, FrameworkElement> CardMap => new()
    {
        ["fans"] = CardFans, ["rgb"] = CardRgb, ["boost"] = CardBoost, ["drivers"] = CardDrivers, ["tweaker"] = CardTweaker, ["extras"] = CardExtras, ["power"] = CardPower
    };

    private void SetupEditor()
    {
        var st = AppSettings.Current;
        SecList.Load(new[] { ("system", "Systemzeile (CPU, GPU, RAM)"), ("kpis", "Kennzahlen"), ("cards", "Karten"), ("hints", "Wichtige Hinweise") }, st.DashSections ?? DefSections.ToList());
        CardList.Load(new[] { ("fans", "Lüfter"), ("rgb", "Beleuchtung"), ("boost", "FPS-Boost"), ("drivers", "Grafiktreiber"), ("tweaker", "Tweaker"), ("extras", "Kacheln, Overlay & Stresstest"), ("power", "Stromverbrauch & Kosten") }, st.DashCards ?? DefCards.ToList());
        KpiList.Load(Services.Monitor.Metrics.Values.Where(x => !x.Key.StartsWith("sensor:")).Select(x => (x.Key, x.Label)), st.DashKpis ?? DefKpis.ToList());
        SecList.Changed += SaveLayout; CardList.Changed += SaveLayout; KpiList.Changed += SaveLayout;
        ApplyLayout();
    }

    private void SaveLayout()
    {
        var st = AppSettings.Current;
        st.DashSections = SecList.Selected; st.DashCards = CardList.Selected; st.DashKpis = KpiList.Selected;
        st.Save();
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var st = AppSettings.Current;
        var secs = st.DashSections ?? DefSections.ToList();
        var cards = st.DashCards ?? DefCards.ToList();
        if (st.DashCards != null && !st.DashPowerSeen) { cards.Insert(Math.Min(3, cards.Count), "power"); st.DashCards = cards; st.DashPowerSeen = true; st.Save(); }
        var kpis = st.DashKpis ?? DefKpis.ToList();
        // Bereiche: Reihenfolge im Haupt-Stapel (Bearbeiten-Leiste und -Panel bleiben oben)
        var sm = SectionMap;
        foreach (var e in sm.Values) (e.Parent as Panel)?.Children.Remove(e);
        foreach (var k in secs.Concat(sm.Keys.Where(k => !secs.Contains(k))))
        {
            var e = sm[k];
            e.Visibility = secs.Contains(k) ? Visibility.Visible : Visibility.Collapsed;
            Root.Children.Add(e);
        }
        var cm = CardMap;
        foreach (var e in cm.Values) (e.Parent as Panel)?.Children.Remove(e);
        foreach (var k in cards.Concat(cm.Keys.Where(k => !cards.Contains(k))))
        {
            var e = cm[k];
            e.Visibility = cards.Contains(k) ? Visibility.Visible : Visibility.Collapsed;
            Cards.Children.Add(e);
        }
        var m = Services.Monitor.Metrics;
        Kpis.ItemsSource = kpis.Where(m.ContainsKey).Select(k => m[k]).ToList();
    }

    private void Price_LostFocus(object s, RoutedEventArgs e)
    {
        if (double.TryParse(PriceBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is > 0 and < 5)
        { EnergyService.Instance.Settings.PricePerKwh = p; EnergyService.Instance.Save(); }
    }

    private void Edit_Click(object s, RoutedEventArgs e)
    {
        bool open = EditPanel.Visibility != Visibility.Visible;
        EditPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        EditBar.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EditReset_Click(object s, RoutedEventArgs e)
    {
        var st = AppSettings.Current;
        st.DashSections = null; st.DashCards = null; st.DashKpis = null;
        st.Save();
        SecList.Changed -= SaveLayout; CardList.Changed -= SaveLayout; KpiList.Changed -= SaveLayout;
        SetupEditor();
        Ui.Toast("Dashboard auf Standard zurückgesetzt");
    }

    private bool _init;
    private void SelectProfile()
    {
        _init = true;
        var p = Services.Fans.Profile;
        (p == "Leise" ? ProfQuiet : p == "Leistung" ? ProfPerf : ProfNormal).IsChecked = true;
        _init = false;
    }

    private void FanProfile_Checked(object sender, RoutedEventArgs e)
    {
        if (_init || sender is not RadioButton { Content: string p }) return;
        Services.Fans.Profile = p;
        Ui.Toast($"Lüfterprofil „{p}“");
    }

    private void Go_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is string p) Ui.Go(p); }
    private async void Boost_Click(object sender, RoutedEventArgs e) => await Services.Boost.BoostAsync();
    private async void Ram_Click(object sender, RoutedEventArgs e) => await Services.Boost.CleanRamAsync();
    private void Overlay_Click(object sender, RoutedEventArgs e) => Services.Monitor.SetOverlay(OverlayToggle.IsChecked == true);
    private void OverlayGames_Click(object sender, RoutedEventArgs e) => Services.Monitor.OverlayOnlyInGames = OverlayGamesToggle.IsChecked == true;

    private void Stress_Click(object sender, RoutedEventArgs e)
    {
        Services.Stress.Settings.Minutes = 5;
        Ui.Go("Stresstest");
        Services.Stress.Start();
    }
}
