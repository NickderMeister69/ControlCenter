using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NickControlCenter;

public partial class FansPage : UserControl, IPage
{
    private readonly FanController _c = Services.Fans;
    private TimeSpan _last = TimeSpan.Zero;
    private bool _init;

    public FansPage()
    {
        InitializeComponent();
        DataContext = _c;
        _c.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FanController.CpuTemp) or nameof(FanController.GpuTemp)) Temps(); };
        SelectProfile();
        Temps();
    }

    private void Temps()
    {
        bool f = AppSettings.Current.Fahrenheit;
        Smooth.Text(CpuBig, _c.CpuTemp, v => Fmt.Temp(v, f));
        Smooth.Text(GpuBig, _c.GpuTemp, v => Fmt.Temp(v, f));
        CpuBig.Foreground = Palette.ForTemp(_c.CpuTemp, 80, 92);
        GpuBig.Foreground = Palette.ForTemp(_c.GpuTemp, 78, 88);
        var gn = Services.Hub.Latest.GpuName;
        GpuSrc.Text = double.IsNaN(_c.GpuTemp) ? "nicht erkannt – Grafiktreiber prüfen" : string.IsNullOrEmpty(gn) ? " " : gn;
        GpuSrc.ToolTip = GpuSrc.Text;
    }

    private void SelectProfile()
    {
        _init = true;
        (_c.Profile == "Leise" ? PQ : _c.Profile == "Leistung" ? PP : PN).IsChecked = true;
        _init = false;
    }

    public void OnVisibleChanged(bool visible)
    {
        CompositionTarget.Rendering -= OnFrame;
        if (visible) { _last = TimeSpan.Zero; CompositionTarget.Rendering += OnFrame; SelectProfile(); }
    }

    /// <summary>Lüfter-Symbole drehen passend zur echten Drehzahl (nur wenn die Seite sichtbar ist).</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last == TimeSpan.Zero || now == _last) { _last = now; return; }
        double dt = Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;
        foreach (var f in _c.VisibleFans)
            if (f.Rpm > 1) f.Angle = (f.Angle + f.Rpm * 0.4 * dt) % 360;
    }

    private static FanVM? Vm(object s) => (s as FrameworkElement)?.DataContext as FanVM;
    private void Calibrate_Click(object s, RoutedEventArgs e) { if (Vm(s) is { } f) _c.Calibrate(f); }
    private void Identify_Click(object s, RoutedEventArgs e) { if (Vm(s) is { } f) _c.Identify(f); }
    private void CalibrateAll_Click(object s, RoutedEventArgs e) => _c.RecalibrateAll();
    private void Welcome_Click(object s, RoutedEventArgs e) => _c.DismissWelcome();
    private void Sensors_Click(object s, RoutedEventArgs e) => _c.WriteSensorList(true);
    private void Log_Click(object s, RoutedEventArgs e) => Env.Open(System.IO.Path.Combine(Log.Folder, "Luefter.log"));
    private void PawnIo_Click(object s, RoutedEventArgs e) => Env.Open("https://pawnio.eu");

    private void Profile_Checked(object sender, RoutedEventArgs e)
    {
        if (!_init && sender is RadioButton { Content: string p }) _c.Profile = p;
    }

    private void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && sender is TextBox tb)
        {
            if (e.Key == Key.Enter) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            else tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }
}
