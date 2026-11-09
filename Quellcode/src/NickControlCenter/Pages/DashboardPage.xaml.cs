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
        var m = Services.Monitor.Metrics;
        Kpis.ItemsSource = new[] { "cpu.temp", "gpu.temp", "cpu.load", "gpu.load", "ram.load", "cpu.power", "gpu.power", "net.down" }.Where(m.ContainsKey).Select(k => m[k]).ToList();
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
        HintsHead.Visibility = DiagnosticsService.Instance.Findings.Any(f => f.Severity >= Severity.Hint) ? Visibility.Visible : Visibility.Collapsed;
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

    private void Stress_Click(object sender, RoutedEventArgs e)
    {
        Services.Stress.Settings.Minutes = 5;
        Ui.Go("Stresstest");
        Services.Stress.Start();
    }
}
