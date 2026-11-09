using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NickControlCenter;

public partial class StressPage : UserControl
{
    private readonly StressService _s = Services.Stress;
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _init = true;

    public StressPage()
    {
        InitializeComponent();
        DataContext = _s;
        CpuInfo.Text = $"{_s.CpuName} · {_s.LogicalCores} Threads";
        ModeBox.SelectedIndex = (int)_s.Settings.CpuMode;
        ThreadSlider.Maximum = _s.LogicalCores;
        ThreadSlider.Value = _s.Settings.Threads <= 0 ? _s.LogicalCores : _s.Settings.Threads;
        AdapterBox.Items.Add(new ComboBoxItem { Content = "Automatisch (stärkste Karte)", Tag = -1 });
        foreach (var a in _s.Adapters) AdapterBox.Items.Add(new ComboBoxItem { Content = $"{a.Name} ({a.Vendor}, {a.VramGb:0.#} GB)", Tag = a.Index });
        AdapterBox.SelectedIndex = Math.Max(0, AdapterBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (int)i.Tag == _s.Settings.GpuAdapter));
        DurBox.SelectedIndex = Math.Max(0, DurBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == _s.Settings.Minutes.ToString()));
        CpuLim.Value = _s.CpuLimitEffective;
        CpuLimText.Text = $"{CpuLim.Value:0} °C";
        CpuChart.Metric = Services.Monitor.Metrics["cpu.temp"];
        GpuChart.Metric = Services.Monitor.Metrics["gpu.temp"];
        _t.Tick += (_, _) => Live();
        Loaded += (_, _) => { _t.Start(); Live(); };
        Unloaded += (_, _) => { _t.Stop(); _s.Save(); };
        _init = false;
    }

    private void Live()
    {
        var m = Services.Monitor.Metrics;
        CpuNow.Text = $"{m["cpu.temp"].Text} · {m["cpu.load"].Text} · {m["cpu.clock"].Text} · {m["cpu.power"].Text}";
        GpuNow.Text = $"{m["gpu.temp"].Text} · {m["gpu.load"].Text} · {m["gpu.power"].Text}";
    }

    private void Mode_Changed(object s, SelectionChangedEventArgs e) { if (!_init) _s.Settings.CpuMode = (CpuStressMode)ModeBox.SelectedIndex; }
    private void Threads_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)ThreadSlider.Value;
        ThreadText.Text = v >= _s.LogicalCores || v == 0 ? "alle" : v.ToString();
        if (!_init) _s.Settings.Threads = v >= _s.LogicalCores ? 0 : Math.Max(1, v);
    }
    private void Adapter_Changed(object s, SelectionChangedEventArgs e) { if (!_init && AdapterBox.SelectedItem is ComboBoxItem { Tag: int i }) _s.Settings.GpuAdapter = i; }
    private void Dur_Changed(object s, SelectionChangedEventArgs e) { if (!_init && DurBox.SelectedItem is ComboBoxItem { Tag: string t }) _s.Settings.Minutes = int.Parse(t); }
    private void CpuLim_Changed(object s, RoutedPropertyChangedEventArgs<double> e) { if (CpuLimText == null) return; CpuLimText.Text = $"{CpuLim.Value:0} °C"; if (!_init) _s.Settings.CpuLimit = CpuLim.Value; }
    private void Start_Click(object s, RoutedEventArgs e) => _s.Start();
    private void Stop_Click(object s, RoutedEventArgs e) => _s.StopAll("Gestoppt");
    private void Reports_Click(object s, RoutedEventArgs e) => Env.Open(Log.Folder);
}
