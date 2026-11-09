using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace NickControlCenter;

/// <summary>
/// Schnelles Verlaufsdiagramm (zeichnet direkt, keine tausend UI-Elemente → läuft flüssig, auch mit vielen Kacheln).
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(nameof(Metric), typeof(Metric), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((Sparkline)d).Hook(e.OldValue as Metric, e.NewValue as Metric)));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Metric? Metric { get => (Metric?)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Points { get => (int)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool Fill { get => (bool)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static readonly Pen GridPen = MakePen(Color.FromArgb(28, 255, 255, 255), 1);
    private static readonly Typeface Face = new("Bahnschrift");

    private void Hook(Metric? old, Metric? nw)
    {
        if (old != null) old.PropertyChanged -= OnChanged;
        if (nw != null) nw.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NickControlCenter.Metric.Value) && IsVisible) InvalidateVisual();
    }

    private static Pen MakePen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var m = Metric;
        if (ShowGrid)
            for (int i = 1; i < 4; i++) dc.DrawLine(GridPen, new Point(0, h * i / 4), new Point(w, h * i / 4));
        if (m == null) return;
        var vals = m.Last(Math.Max(2, Points));
        if (vals.Length < 2) return;

        double max = m.Max;
        double lo = 0;
        var valid = vals.Where(v => !double.IsNaN(v)).ToList();
        if (valid.Count == 0) return;
        if (m.IsTemp) { lo = Math.Max(0, Math.Floor((valid.Min() - 10) / 10) * 10); max = Math.Max(lo + 20, Math.Ceiling((valid.Max() + 5) / 10) * 10); }
        else if (m.IsRate || m.Unit is "W" or "MHz" or "GB" or "U/min") max = Math.Max(1, valid.Max() * 1.2);

        var pts = new List<Point>(vals.Length);
        for (int i = 0; i < vals.Length; i++)
        {
            if (double.IsNaN(vals[i])) continue;
            double x = w * i / (Points - 1) + (w - w * (vals.Length - 1) / (Points - 1));
            double y = h - Math.Clamp((vals[i] - lo) / (max - lo), 0, 1) * (h - 2) - 1;
            pts.Add(new Point(x, y));
        }
        if (pts.Count < 2) return;
        var stroke = Stroke;
        if (Fill && stroke is SolidColorBrush sb)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, h), true, true);
                foreach (var pt in pts) ctx.LineTo(pt, false, false);
                ctx.LineTo(new Point(pts[^1].X, h), false, false);
            }
            area.Freeze();
            var grad = new LinearGradientBrush(Color.FromArgb(70, sb.Color.R, sb.Color.G, sb.Color.B), Color.FromArgb(0, sb.Color.R, sb.Color.G, sb.Color.B), 90);
            grad.Freeze();
            dc.DrawGeometry(grad, null, area);
        }
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, true);
        }
        line.Freeze();
        var pen = new Pen(stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        if (ShowGrid)
        {
            var ft = new FormattedText(m.Format(max, AppSettings.Current.Fahrenheit), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 10,
                                       new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.0);
            dc.DrawText(ft, new Point(4, 2));
        }
    }
}

/// <summary>Ring-Anzeige (für Dashboard-Werte).</summary>
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(0.0, (d, e) => ((Ring)d).OnValue((double)e.NewValue)));
    private readonly SmoothNumber _n = new();
    private bool _hooked;
    private void OnValue(double v)
    {
        _n.Set(double.IsNaN(v) ? 0 : v);
        if (!_hooked) { _hooked = true; Smooth.Frame += () => { if (_n.Step()) InvalidateVisual(); }; }
        InvalidateVisual();
    }
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Ring),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private static readonly Pen Track = MakeTrack();
    private static Pen MakeTrack() { var p = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 6); p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s < 10) return;
        double r = s / 2 - 4;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        // 270°-Bogen (unten offen)
        DrawArc(dc, Track, c, r, 135, 270);
        double v = double.IsNaN(_n.Current) ? 0 : Math.Clamp(_n.Current, 0, 100);
        if (v > 0.5)
        {
            var pen = new Pen(Stroke, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            DrawArc(dc, pen, c, r, 135, 270 * v / 100);
        }
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg <= 0) return;
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>
/// In-Game-Overlay: kleines, durchsichtiges Fenster immer im Vordergrund, klick-durchlässig.
/// Strg+Umschalt+O blendet es ein/aus. Läuft im randlosen Fenstermodus von Spielen (nicht im exklusiven Vollbild).
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly MonitorService _svc;
    private readonly StackPanel _panel = new();
    private readonly Border _box;
    private Brush _textBrush = Brushes.White;

    private static Color Col(string hex, Color fallback) => RgbF.TryParseHex(hex, out var c) ? Color.FromRgb(c.R8, c.G8, c.B8) : fallback;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HwndSource? _src;
    private const int HotkeyId = 0x4E43;

    public OverlayWindow(MonitorService svc)
    {
        _svc = svc;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Content = _box = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6),
            Child = _panel
        };
        Rebuild();
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        int placeTick = 0;
        _timer.Tick += (_, _) => { Refresh(); if (++placeTick % 12 == 0) Place(); };
        Loaded += (_, _) => { Place(); _timer.Start(); };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // klick-durchlässig (WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
            long ex = Native.GetWindowLongPtr(h, -20).ToInt64();
            Native.SetWindowLongPtr(h, -20, new IntPtr(ex | 0x20 | 0x80000 | 0x80 | 0x08000000));
            _src = HwndSource.FromHwnd(h);
            _src.AddHook(Hook);
            if (_svc.Settings.OverlayHotkey) Native.RegisterHotKey(h, HotkeyId, 0x0002 | 0x0004 | 0x4000, 0x4F); // Strg+Umschalt+O
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            try { Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId); } catch { }
        };
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Rebuild()
    {
        _panel.Children.Clear();
        var st = _svc.Settings;
        bool horiz = st.OverlayHorizontal;
        var bg = Col(st.OverlayBgColor, Color.FromRgb(8, 11, 15));
        _box.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(st.OverlayBgOpacity, 0, 1) * 255), bg.R, bg.G, bg.B));
        _box.BorderThickness = new Thickness(st.OverlayBorder ? 1 : 0);
        _box.Padding = new Thickness(st.OverlayFontSize * 0.7, st.OverlayFontSize * 0.4, st.OverlayFontSize * 0.7, st.OverlayFontSize * 0.4);
        _textBrush = new SolidColorBrush(Col(st.OverlayTextColor, Colors.White));
        var labelBrush = new SolidColorBrush(Col(st.OverlayLabelColor, Color.FromRgb(150, 160, 175)));
        var font = new FontFamily(string.IsNullOrWhiteSpace(st.OverlayFont) ? "Bahnschrift" : st.OverlayFont);
        _panel.Orientation = horiz ? Orientation.Horizontal : Orientation.Vertical;
        Opacity = Math.Clamp(_svc.Settings.OverlayOpacity, 0.2, 1);
        double fs = _svc.Settings.OverlayFontSize;
        bool first = true;
        foreach (var k in _svc.Settings.OverlayItems)
        {
            if (!_svc.Metrics.TryGetValue(k, out var m)) continue;
            var lbl = new TextBlock { Text = Short(m), Foreground = labelBrush, FontSize = fs * (horiz ? 0.8 : 1), FontFamily = font, VerticalAlignment = VerticalAlignment.Center };
            var val = new TextBlock { Tag = m, FontSize = fs, FontWeight = FontWeights.SemiBold, FontFamily = font, VerticalAlignment = VerticalAlignment.Center, Foreground = _textBrush };
            if (horiz)
            {
                // nebeneinander: "FPS 144 | CPU 54 °C | GPU 61 °C …"
                if (!first) _panel.Children.Add(new Border { Width = 1, Margin = new Thickness(10, 3, 10, 3), Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) });
                var cell = new StackPanel { Orientation = Orientation.Horizontal };
                lbl.Margin = new Thickness(0, 0, 6, 0);
                val.MinWidth = k == "fps" ? fs * 2.2 : 0;
                if (k == "fps") { val.FontSize = fs * 1.25; val.FontWeight = FontWeights.Bold; }
                cell.Children.Add(lbl); cell.Children.Add(val);
                _panel.Children.Add(cell);
            }
            else
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
                val.MinWidth = 60;
                Grid.SetColumn(val, 2);
                row.Children.Add(lbl); row.Children.Add(val);
                _panel.Children.Add(row);
            }
            first = false;
        }
        Refresh();
    }

    private static string Short(Metric m) => m.Key switch
    {
        "fps" => "FPS", "fps.low" => "1% Low", "frametime" => "Frametime",
        "cpu.temp" => "CPU", "cpu.load" => "CPU %", "cpu.clock" => "CPU Takt", "cpu.power" => "CPU W", "gpu.temp" => "GPU", "gpu.load" => "GPU %",
        "gpu.clock" => "GPU Takt", "gpu.power" => "GPU W", "gpu.vram" => "VRAM", "ram.load" => "RAM", "net.down" => "↓", "net.up" => "↑", _ => m.Label
    };

    private void Refresh()
    {
        foreach (var row in _panel.Children.OfType<Panel>())
            foreach (var tb in row.Children.OfType<TextBlock>().Where(t => t.Tag is Metric))
            {
                var m = (Metric)tb.Tag;
                tb.Text = m.Key is "fps" or "fps.low" ? (double.IsNaN(m.ShownValue) ? "–" : $"{m.ShownValue:0}") : m.Text;
                tb.Foreground = !_svc.Settings.OverlayAlertColors || m.Brush == Palette.Text || m.Brush == Palette.Muted ? _textBrush : m.Brush;
            }
    }

    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        double m = 12;
        switch (_svc.Settings.OverlayCorner)
        {
            case 1: Left = wa.Right - ActualWidth - m; Top = wa.Top + m; break;
            case 2: Left = wa.Left + m; Top = wa.Bottom - ActualHeight - m; break;
            case 3: Left = wa.Right - ActualWidth - m; Top = wa.Bottom - ActualHeight - m; break;
            case 4: Left = wa.Left + (wa.Width - ActualWidth) / 2; Top = wa.Top + m; break;
            default: Left = wa.Left + m; Top = wa.Top + m; break;
        }
    }
}

public sealed class BoolInvert : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is bool b ? !b : true;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is bool b ? !b : false;
}

public sealed class BoolToVisInv : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Vergleicht einen Wert mit dem Parameter (für RadioButtons an Enums/Zahlen).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v?.ToString() == p?.ToString();
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
    {
        if (v is not true) return Binding.DoNothing;
        var target = Nullable.GetUnderlyingType(t) ?? t;
        if (target.IsEnum) return Enum.Parse(target, p.ToString()!);
        return System.Convert.ChangeType(p, target, CultureInfo.InvariantCulture);
    }
}

public sealed class StringNotEmptyToVis : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => string.IsNullOrWhiteSpace(v as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Balkenbreite = Anteil (0…1) × verfügbare Breite.</summary>
public sealed class FractionWidth : IMultiValueConverter
{
    public object Convert(object[] v, Type t, object p, CultureInfo c) =>
        v.Length == 2 && v[0] is double f && v[1] is double w && !double.IsNaN(f) ? Math.Max(0, Math.Min(1, f) * w) : 0.0;
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
