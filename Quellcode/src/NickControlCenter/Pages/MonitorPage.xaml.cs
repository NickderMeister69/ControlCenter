using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public sealed class SensorRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public SensorKind Kind { get; init; }
    public string Source { get; init; } = "";
    private string _v = "", _mm = "";
    public string ValueText { get => _v; set => Set(ref _v, value); }
    private readonly SmoothNumber _n = new();
    private Func<double, string>? _fmt;
    /// <summary>Wert setzen – die Anzeige gleitet weich hin.</summary>
    public void SetValue(double v, Func<double, string> fmt)
    {
        _fmt = fmt;
        bool snap = double.IsNaN(_n.Current) || double.IsNaN(v);
        _n.Set(v);
        if (snap) ValueText = fmt(v);
        if (!_hooked) { _hooked = true; Smooth.Frame += () => { if (_n.Step() && _fmt != null) ValueText = _fmt(_n.Current); }; }
    }
    private bool _hooked;
    public string MinMax { get => _mm; set => Set(ref _mm, value); }
}

/// <summary>Eine einstellbare Overlay-Farbe (Text, Beschriftung, Hintergrund).</summary>
public sealed class OverlayColor : ObservableObject
{
    private readonly Func<string> _get; private readonly Action<string> _set;
    public OverlayColor(string label, Func<string> get, Action<string> set, params string[] swatches) { Label = label; _get = get; _set = set; Swatches = swatches; }
    public string Label { get; }
    public string[] Swatches { get; }
    public string Hex
    {
        get => _get();
        set { if (RgbF.TryParseHex(value, out var c)) { _set(c.Hex); Changed?.Invoke(); } Raise(); }
    }
    public Action? Changed;
}

public sealed class OverlayItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public bool On { get; set; }
}

public partial class MonitorPage : UserControl, IPage
{
    private readonly MonitorService _m = Services.Monitor;
    private readonly ObservableCollection<SensorRow> _rows = new();
    private readonly Dictionary<string, SensorRow> _byId = new();
    private bool _visible, _init;
    private string _tab = "M1";

    public MonitorPage()
    {
        _init = true;
        InitializeComponent();
        DataContext = _m;
        SensorList.ItemsSource = _rows;
        AddList.ItemsSource = _m.Metrics.Values.Where(x => !x.Key.StartsWith("sensor:")).ToList();
        OverlayItems.Load(_m.Metrics.Values.Where(x => !x.Key.StartsWith("sensor:")).Select(x => (x.Key, x.Label)), _m.Settings.OverlayItems);
        OverlayItems.Changed += () => { _m.Settings.OverlayItems = OverlayItems.Selected; _m.Save(); _m.RefreshOverlay(); };
        SourceBox.SelectedIndex = (int)AppSettings.Current.SensorSource;
        IntervalBox.SelectedIndex = AppSettings.Current.SensorIntervalMs switch { 500 => 0, 2000 => 2, 5000 => 3, _ => 1 };
        Fahr.IsChecked = AppSettings.Current.Fahrenheit;
        SetCharts(120);
        _m.Updated += OnUpdate;
        InitOverlayStyle();
        _init = false;
    }

    public void OnVisibleChanged(bool visible)
    {
        _visible = visible;
        OverlayOn.IsChecked = _m.OverlayOn;
        OverlayGames.IsChecked = _m.OverlayOnlyInGames;
        _m.UseFps(visible || _m.OverlayOn);
        if (!visible) _m.Save();
        OnUpdate();
    }

    private void SetCharts(int points)
    {
        var m = _m.Metrics;
        (C1.Metric, C1T.Text) = (m["cpu.temp"], "CPU-TEMPERATUR");
        (C2.Metric, C2T.Text) = (m["gpu.temp"], "GPU-TEMPERATUR");
        (C3.Metric, C3T.Text) = (m["cpu.load"], "CPU-AUSLASTUNG");
        (C4.Metric, C4T.Text) = (m["gpu.load"], "GPU-AUSLASTUNG");
        foreach (var c in new[] { C1, C2, C3, C4 }) c.Points = points;
    }

    private void OnUpdate()
    {
        if (!_visible) return;
        var s = Services.Hub.Latest;
        SourceInfo.Text = $"Quelle: {s.TempSourceText}";
        HwStatus.Text = "HWiNFO: " + HardwareHub.HwinfoText(HwinfoSource.LastStatus);
        RecBtn.Content = _m.Recording ? "■ Aufzeichnung stoppen" : "● Aufzeichnen";
        if (_tab == "M3") UpdateSensors();
    }

    private void UpdateSensors()
    {
        var q = Search.Text.Trim();
        var kind = KindFilter.SelectedIndex switch
        {
            1 => SensorKind.Temperature, 2 => SensorKind.Fan, 3 => SensorKind.Load, 4 => SensorKind.Clock, 5 => SensorKind.Power, 6 => SensorKind.Voltage, _ => (SensorKind?)null
        };
        bool f = AppSettings.Current.Fahrenheit;
        var list = _m.LatestAll.Where(r => (kind == null || r.Kind == kind) &&
                        (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Group.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        var ids = list.Select(r => r.Id).ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--) if (!ids.Contains(_rows[i].Id)) { _byId.Remove(_rows[i].Id); _rows.RemoveAt(i); }
        foreach (var r in list)
        {
            if (!_byId.TryGetValue(r.Id, out var row))
            {
                row = new SensorRow { Id = r.Id, Group = r.Group, Name = r.Name, Kind = r.Kind, Source = r.Source == SensorSource.Hwinfo ? "HWiNFO" : "LHM" };
                _byId[r.Id] = row;
                _rows.Add(row);
            }
            var rr = r;
            row.SetValue(r.Value, v => Format(v, rr, f));
            row.MinMax = double.IsNaN(r.Min) ? "" : $"{Format(r.Min, r, f)} … {Format(r.Max, r, f)}";
        }
    }

    private static string Format(double v, SensorReading r, bool f)
    {
        if (double.IsNaN(v)) return "–";
        if (r.Kind == SensorKind.Temperature) return Fmt.Temp(v, f);
        if (r.Kind == SensorKind.Throughput) return Fmt.Rate(v);
        return r.Kind switch { SensorKind.Voltage => $"{v:0.000} V", SensorKind.Clock => $"{v:0} MHz", _ => $"{v:0.#} {r.Unit}" };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (M1 == null || sender is not RadioButton { Tag: string t }) return;
        _tab = t;
        foreach (var p in new FrameworkElement[] { M1, M2, M3, M4 }) p.Visibility = p.Name == t ? Visibility.Visible : Visibility.Collapsed;
        if (t == "M3") UpdateSensors();
    }

    private void Span_Checked(object sender, RoutedEventArgs e) { if (C1 != null && sender is RadioButton { Tag: string s }) SetCharts(int.Parse(s)); }
    private void Search_Changed(object sender, RoutedEventArgs e) { if (!_init) UpdateSensors(); }
    private void Rec_Click(object s, RoutedEventArgs e) { _m.ToggleRecording(); OnUpdate(); }
    private void Reset_Click(object s, RoutedEventArgs e) => _m.ResetStats();
    private void AddTile_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string k) _m.AddTile(k); }
    private void RemoveTile_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Metric m) _m.RemoveTile(m.Key); }
    private void Left_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Metric m) _m.MoveTile(m.Key, -1); }
    private void Right_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Metric m) _m.MoveTile(m.Key, 1); }
    private void PinSensor_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string id) { _m.AddTile("sensor:" + id); Ui.Toast("Als Kachel hinzugefügt"); } }
    private void OverlayLayout_Click(object s, RoutedEventArgs e) { _m.Save(); _m.RefreshOverlay(); }
    private void Overlay_Click(object s, RoutedEventArgs e) => _m.SetOverlay(OverlayOn.IsChecked == true);
    private void OverlayGames_Click(object s, RoutedEventArgs e) => _m.OverlayOnlyInGames = OverlayGames.IsChecked == true;
    private void InitOverlayStyle()
    {
        var st = _m.Settings;
        var fonts = new[] { "Bahnschrift", "Segoe UI", "Segoe UI Black", "Consolas", "Arial", "Arial Black", "Verdana", "Tahoma", "Trebuchet MS", "Impact", "Courier New", "Cascadia Mono" }
            .Where(f => System.Windows.Media.Fonts.SystemFontFamilies.Any(x => x.Source == f)).ToList();
        if (!fonts.Contains(st.OverlayFont)) fonts.Insert(0, st.OverlayFont);
        FontBox.ItemsSource = fonts;
        FontBox.SelectedItem = st.OverlayFont;
        string[] bright = { "#FFFFFF", "#22D3EE", "#A3E635", "#FACC15", "#FB923C", "#F43F5E", "#F472B6", "#A78BFA" };
        string[] dark = { "#080B0F", "#000000", "#1E293B", "#0F172A", "#1F1235", "#06281F", "#2A0A12", "#FFFFFF" };
        var items = new[]
        {
            new OverlayColor("Werte", () => st.OverlayTextColor, v => st.OverlayTextColor = v, bright),
            new OverlayColor("Beschriftung", () => st.OverlayLabelColor, v => st.OverlayLabelColor = v, new[] { "#96A0AF", "#FFFFFF", "#22D3EE", "#A3E635", "#FACC15", "#FB923C", "#F472B6", "#64748B" }),
            new OverlayColor("Hintergrund", () => st.OverlayBgColor, v => st.OverlayBgColor = v, dark),
        };
        foreach (var i in items) i.Changed = () => { _m.Save(); _m.RefreshOverlay(); };
        OverlayColors.ItemsSource = items;
    }

    private void OverlaySwatch_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string hex } fe) return;
        DependencyObject? d = fe;
        while (d != null && (d as FrameworkElement)?.DataContext is not OverlayColor) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if ((d as FrameworkElement)?.DataContext is OverlayColor oc) oc.Hex = hex;
    }

    private void OverlayFont_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_init || FontBox.SelectedItem is not string f) return;
        _m.Settings.OverlayFont = f; _m.Save(); _m.RefreshOverlay();
    }

    private void OverlayChanged(object s, SelectionChangedEventArgs e) { if (!_init) { _m.Save(); _m.RefreshOverlay(); } }
    private void OverlaySlider(object s, RoutedPropertyChangedEventArgs<double> e) { if (!_init) { _m.RefreshOverlay(); _m.Save(); } }


    private void Source_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_init) return;
        AppSettings.Current.SensorSource = (SourcePreference)SourceBox.SelectedIndex;
        AppSettings.Current.Save();
        Services.Hub.Preference = AppSettings.Current.SensorSource;
        Log.Info(LogModule.Monitor, "Messquelle: " + ((ComboBoxItem)SourceBox.SelectedItem).Content);
    }

    private void Interval_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_init || IntervalBox.SelectedItem is not ComboBoxItem { Tag: string t }) return;
        AppSettings.Current.SensorIntervalMs = int.Parse(t);
        AppSettings.Current.Save();
        Services.Hub.IntervalMs = int.Parse(t);
    }

    private void Fahr_Click(object s, RoutedEventArgs e)
    {
        AppSettings.Current.Fahrenheit = Fahr.IsChecked == true;
        AppSettings.Current.Save();
    }
}
