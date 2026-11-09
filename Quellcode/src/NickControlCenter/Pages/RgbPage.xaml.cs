using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NickControlCenter;

public partial class RgbPage : UserControl, IPage
{
    private readonly RgbController _c = Services.Fans.Rgb;

    public RgbPage()
    {
        InitializeComponent();
        DataContext = _c;
    }

    public void OnVisibleChanged(bool visible) => _c.PreviewActive = visible;

    /// <summary>Zum Effekt-Editor gehöriges EffectVM finden (Global oder Anschluss).</summary>
    private static EffectVM? Fx(object sender)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d != null)
        {
            if (d is FrameworkElement fe)
            {
                if (fe.DataContext is EffectVM fx) return fx;
                if (fe is ContentPresenter { Content: EffectVM e }) return e;
            }
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void AddColor_Click(object s, RoutedEventArgs e) => Fx(s)?.AddColor();
    private void RemoveColor_Click(object s, RoutedEventArgs e) { if (Fx(s) is { Selected: { } c } fx) fx.RemoveColor(c); }
    private void MoveLeft_Click(object s, RoutedEventArgs e) { if (Fx(s) is { Selected: { } c } fx) fx.MoveColor(c, -1); }
    private void MoveRight_Click(object s, RoutedEventArgs e) { if (Fx(s) is { Selected: { } c } fx) fx.MoveColor(c, 1); }

    private void Preset_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: string hex } && Fx(s) is { } fx)
        {
            if (fx.Selected != null) fx.Selected.Hex = hex; else fx.AddColor(hex);
        }
    }

    private void Saved_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: string hex } && Fx(s) is { } fx)
        {
            if (fx.Selected != null) fx.Selected.Hex = hex; else fx.AddColor(hex);
        }
    }

    private void Saved_Forget(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (s is FrameworkElement { Tag: string hex }) _c.ForgetColor(hex);
    }

    private void SaveColor_Click(object s, RoutedEventArgs e)
    {
        if (Fx(s)?.Selected is { } c) { _c.SaveColor(c.Hex); Ui.Toast($"{c.Hex} zu „Meine Farben“ hinzugefügt"); }
    }

    private void Rescan_Click(object s, RoutedEventArgs e) => _c.Rescan();
    private void Hardware_Click(object s, RoutedEventArgs e) => _c.OpenHardwareList();
    private void Log_Click(object s, RoutedEventArgs e) => Env.Open(System.IO.Path.Combine(Log.Folder, "Beleuchtung.log"));
    private void Identify_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is LightZoneVM z) z.Identify(); }
    private void FanMinus_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is LightZoneVM z) z.ChangeFanCount(-1); }
    private void FanPlus_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is LightZoneVM z) z.ChangeFanCount(1); }

    private void SaveProfile_Click(object s, RoutedEventArgs e)
    {
        var n = ProfileName.Text.Trim();
        if (n.Length == 0) { Ui.Toast("Bitte einen Namen für das Profil eingeben.", true); return; }
        _c.SaveProfile(n);
        ProfileName.Text = "";
        Ui.Toast($"Profil „{n}“ gespeichert");
    }
    private void LoadProfile_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is RgbProfile p) _c.LoadProfile(p); }
    private void DeleteProfile_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is RgbProfile p) _c.DeleteProfile(p); }
}
