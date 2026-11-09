using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

/// <summary>Bearbeitbare Kopie der Tweaker-Werte (erst „Anwenden“ setzt sie).</summary>
public sealed class TweakerVM : ObservableObject
{
    public TweakerService Svc { get; } = Services.Tweaker;
    public CpuSettings Cpu { get; private set; }
    public GpuSettings Gpu { get; private set; }

    public TweakerVM()
    {
        Cpu = Svc.Settings.Cpu.Clone();
        Gpu = Svc.Settings.Gpu.Clone();
        Svc.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TweakerService.Gpu)) { Gpu = Svc.Gpu.Clamp(Svc.Settings.Gpu); RaiseAllValues(); } };
    }

    public void Load(CpuSettings c, GpuSettings g) { Cpu = c.Clone(); Gpu = Svc.Gpu.Clamp(g); RaiseAllValues(); }

    public void RaiseAllValues() => RaiseAll(nameof(UsePlan), nameof(Boost), nameof(MaxFreq), nameof(MaxFreqText), nameof(MinState), nameof(MaxState), nameof(Epp), nameof(EppText),
        nameof(MinCores), nameof(MinCoresText), nameof(PowerPct), nameof(CoreOffset), nameof(MemOffset), nameof(ClockCap), nameof(ClockCapText), nameof(VoltOffset),
        nameof(CpuCapMin), nameof(CpuCapMax), nameof(MemAdjustable), nameof(ApplyOnStart));

    public bool UsePlan { get => Cpu.UsePlan; set { Cpu.UsePlan = value; Raise(); } }
    public int Boost { get => Cpu.Boost; set { Cpu.Boost = value; Raise(); } }
    public int CpuCapMin => Svc.CapMin - 100; // ganz links = kein Limit
    public int CpuCapMax => Svc.CapMax;
    public double MaxFreq { get => Cpu.MaxFreq <= 0 ? CpuCapMin : Cpu.MaxFreq; set { Cpu.MaxFreq = value <= CpuCapMin ? 0 : (int)value; RaiseAll(nameof(MaxFreq), nameof(MaxFreqText)); } }
    public string MaxFreqText => Cpu.MaxFreq <= 0 ? "kein Limit" : $"{Cpu.MaxFreq} MHz";
    public int MinState { get => Cpu.MinState; set { Cpu.MinState = value; Raise(); } }
    public int MaxState { get => Cpu.MaxState; set { Cpu.MaxState = value; Raise(); } }
    public int Epp { get => Cpu.Epp; set { Cpu.Epp = value; RaiseAll(nameof(Epp), nameof(EppText)); } }
    public string EppText => Cpu.Epp < 0 ? "Windows" : Cpu.Epp.ToString();
    public int MinCores { get => Cpu.MinCores; set { Cpu.MinCores = value; RaiseAll(nameof(MinCores), nameof(MinCoresText)); } }
    public string MinCoresText => Cpu.MinCores < 0 ? "Windows" : $"{Cpu.MinCores} %";

    public int PowerPct { get => Gpu.PowerPct; set { Gpu.PowerPct = value; Raise(); } }
    public int CoreOffset { get => Gpu.CoreOffset; set { Gpu.CoreOffset = value; Raise(); } }
    public int MemOffset { get => Gpu.MemOffset; set { Gpu.MemOffset = value; Raise(); } }
    public bool MemAdjustable => Svc.Gpu.MemMax > Svc.Gpu.MemMin;
    public int ClockCap { get => Gpu.ClockCap; set { Gpu.ClockCap = value < Svc.Gpu.CapMin ? 0 : value; RaiseAll(nameof(ClockCap), nameof(ClockCapText)); } }
    public string ClockCapText => Gpu.ClockCap <= 0 ? "aus" : $"{Gpu.ClockCap} MHz";
    public int VoltOffset { get => Gpu.VoltOffset; set { Gpu.VoltOffset = value; Raise(); } }
    public bool ApplyOnStart { get => Svc.Settings.ApplyOnStart; set { Svc.Settings.ApplyOnStart = value; Svc.Save(); Raise(); } }
}

public partial class TweakerPage : UserControl
{
    private readonly TweakerVM _vm = new();

    public TweakerPage()
    {
        InitializeComponent();
        DataContext = _vm;
        var s = _vm.Svc;
        CpuMeta.Text = $"{s.Cores} Kerne · {s.Threads} Threads · Basis {s.BaseClock} MHz · gemessen bis {Math.Max(s.MaxSeen, s.TurboClock)} MHz · Sicherheitsgrenze {s.SafetyTemp} °C";
        ProfList.ItemsSource = s.Settings.Profiles;
    }

    private void CpuPreset_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.Svc;
        var c = (sender as FrameworkElement)?.Tag switch
        {
            "quiet" => s.PresetQuiet(), "eff" => s.PresetEfficient(), "bal" => s.PresetBalanced(), "perf" => s.PresetPerformance(), _ => s.PresetDefault()
        };
        _vm.Load(c, _vm.Gpu);
    }

    private void GpuPreset_Click(object sender, RoutedEventArgs e)
    {
        var g = _vm.Svc.Gpu;
        var p = (sender as FrameworkElement)?.Tag switch { "uv" => g.PresetUndervolt(), "oc1" => g.PresetLightOc(), "oc2" => g.PresetMaxOc(), _ => new GpuSettings() };
        _vm.Load(_vm.Cpu, p);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var g = _vm.Gpu;
        bool oc = g.CoreOffset > 0 || g.MemOffset > 0 || g.PowerPct > 100;
        if (oc && !Dialogs.Confirm("Übertakten anwenden?",
                "Höhere Takte/Limits sind auf diese Karte abgestimmt und begrenzt, trotzdem ist jede Karte anders. Nach dem Anwenden 20 s Zeit zum Bestätigen – sonst wird automatisch zurückgesetzt. Danach am besten den Stabilitätstest laufen lassen."))
            return;
        await _vm.Svc.ApplyAsync(_vm.Cpu, _vm.Gpu);
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => _vm.Svc.Keep();
    private void Revert_Click(object sender, RoutedEventArgs e) => _vm.Svc.Revert("Auf vorherige Werte zurückgesetzt.");
    private async void Reset_Click(object sender, RoutedEventArgs e) { await _vm.Svc.ResetAllAsync(); _vm.Load(new CpuSettings(), new GpuSettings()); }
    private void Test_Click(object sender, RoutedEventArgs e) { Ui.Go("Stresstest"); Services.Stress.QuickTest(); }
    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        b.IsEnabled = false;
        var old = b.Content;
        b.Content = "Messe … (5 s, ein Kern unter Last)";
        try
        {
            int mhz = await _vm.Svc.ProbeTurboAsync();
            _vm.RaiseAllValues();
            UpdateMeta();
            if (mhz > 0) Dialogs.Info("Turbo-Takt gemessen", $"Höchster Takt unter Last: {mhz} MHz ({mhz / 1000.0:0.00} GHz).\n\nBasistakt: {_vm.Svc.BaseClock} MHz. " +
                (mhz > _vm.Svc.BaseClock + 200 ? "Der Turbo arbeitet – passt." : "Der Turbo greift kaum. Prüfe den Turbo-Modus oben (nicht „Turbo aus“) und den Energieplan."));
            else Ui.Toast("Keine Taktwerte von den Sensoren – läuft die App mit Admin-Rechten?", true);
        }
        finally { b.Content = old; b.IsEnabled = true; }
    }

    private void UpdateMeta()
    {
        var s = _vm.Svc;
        CpuMeta.Text = $"{s.Cores} Kerne · {s.Threads} Threads · Basis {s.BaseClock} MHz · gemessen bis {Math.Max(s.MaxSeen, s.TurboClock)} MHz · Sicherheitsgrenze {s.SafetyTemp} °C";
    }

    private void SaveProf_Click(object sender, RoutedEventArgs e)
    {
        var n = ProfName.Text.Trim();
        if (n.Length == 0) { Ui.Toast("Bitte einen Namen eingeben.", true); return; }
        _vm.Svc.Settings.Cpu = _vm.Cpu.Clone(); _vm.Svc.Settings.Gpu = _vm.Gpu.Clone();
        _vm.Svc.SaveProfile(n);
        ProfList.ItemsSource = null; ProfList.ItemsSource = _vm.Svc.Settings.Profiles;
        ProfName.Text = "";
    }

    private void LoadProf_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TweakProfile p) { _vm.Load(p.Cpu, p.Gpu); Ui.Toast($"Profil „{p.Name}“ geladen – „Anwenden“ drücken"); }
    }
}
