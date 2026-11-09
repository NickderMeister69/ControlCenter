using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NickControlCenter;

public sealed class OptKindTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Toggle { get; set; }
    public DataTemplate? Choice { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is WinOption { Kind: OptKind.Choice } ? Choice : Toggle;
}

public sealed record ThemeCard(ThemePreset P, Brush Preview, Brush AccentBrush)
{
    public string Name => P.Name;
    public string Mode => P.Light ? "Hell" : "Dunkel";
}

public partial class CustomizePage : UserControl, IPage
{
    private readonly CustomizeService _c = Services.Customize;

    public CustomizePage()
    {
        InitializeComponent();
        DataContext = _c;
        AccentGrid.ItemsSource = CustomizeService.AccentPalette;
        Themes.ItemsSource = CustomizeService.Presets.Select(p =>
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            for (int i = 0; i < p.Colors.Length; i++) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(p.Colors[i]), i / (double)Math.Max(1, p.Colors.Length - 1)));
            g.Freeze();
            return new ThemeCard(p, g, new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Accent)));
        }).ToList();
        OptTask.ItemsSource = _c.Options.Where(o => o.Group is "Taskleiste" or "Startmenü").ToList();
        OptExplorer.ItemsSource = _c.Options.Where(o => o.Group is "Explorer" or "Desktop").ToList();
        OptFx.ItemsSource = _c.Options.Where(o => o.Group is "Effekte & Maus" or "Werbung & Tipps").ToList();
        MonBox.Items.Add("Alle Bildschirme");
        for (int i = 0; i < _c.MonitorCount; i++) MonBox.Items.Add($"Bildschirm {i + 1}");
        MonBox.SelectedIndex = 0;
    }

    public void OnVisibleChanged(bool visible)
    {
        if (!visible) return;
        Refresh();
        foreach (var o in _c.Options) o.Refresh();
    }

    private void Refresh()
    {
        CurAccent.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_c.AccentHex));
        Recent.ItemsSource = _c.Settings.RecentAccents.ToList();
        var wp = _c.CurrentWallpaper();
        WallPath.Text = string.IsNullOrEmpty(wp) ? "Kein Bild (Einfarbig oder Blickpunkt)" : wp;
        try
        {
            if (File.Exists(wp))
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 640; bi.UriSource = new Uri(wp); bi.EndInit(); bi.Freeze();
                WallImg.Source = bi;
            }
        }
        catch { WallImg.Source = null; }
        SlideInfo.Text = _c.SlideshowOn ? $"Läuft: {_c.Settings.SlideshowFolder}" : "Aus";
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (C1 == null || sender is not RadioButton { Tag: string t }) return;
        foreach (var p in new FrameworkElement[] { C1, C2, C3, C4, C5, C6 }) p.Visibility = p.Name == t ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private async Task Accent(string hex)
    {
        Ui.Toast("Farbe wird angewendet … (Windows-Einstellungen öffnen sich kurz)");
        await _c.SetAccentAsync(hex);
        Refresh();
        Ui.Toast($"Akzentfarbe {hex} gesetzt");
    }

    private async void Accent_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string hex) await Accent(hex); }

    private async void Hex_Click(object s, RoutedEventArgs e)
    {
        if (RgbF.TryParseHex(HexBox.Text, out var c)) await Accent(c.Hex);
        else Ui.Toast("Ungültiger Hex-Code (Beispiel: #FF5500)", true);
    }

    private async void Pick_Click(object s, RoutedEventArgs e)
    {
        using var d = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (RgbF.TryParseHex(_c.AccentHex, out var cur)) d.Color = System.Drawing.Color.FromArgb(cur.R8, cur.G8, cur.B8);
        if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var hex = $"#{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}";
        HexBox.Text = hex;
        await Accent(hex);
    }

    private void Sync_Click(object s, RoutedEventArgs e) => _c.Save();

    private async void Theme_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not ThemeCard t) return;
        Ui.Toast($"Theme „{t.Name}“ wird angewendet …");
        try { await _c.ApplyPresetAsync(t.P); } catch (Exception ex) { Ui.Toast("Fehler: " + ex.Message, true); Log.Warn(LogModule.App, "Theme: " + ex); }
        Refresh();
    }

    private void Wall_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "Bilder|*.jpg;*.jpeg;*.png;*.bmp;*.webp", Title = "Hintergrundbild wählen" };
        if (d.ShowDialog() != true) return;
        try { _c.SetWallpaper(d.FileName, PosBox.SelectedIndex, MonBox.SelectedIndex - 1); Ui.Toast("Hintergrundbild gesetzt"); }
        catch (Exception ex) { Ui.Toast("Hintergrund: " + ex.Message, true); Log.Warn(LogModule.App, "Hintergrund: " + ex); }
        Refresh();
    }

    private void Solid_Click(object s, RoutedEventArgs e)
    {
        using var d = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        _c.SetSolidColor($"#{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}");
        Refresh();
    }

    private void Gradient_Click(object s, RoutedEventArgs e)
    {
        var cols = new[] { G1.Text, G2.Text, G3.Text }.Where(x => RgbF.TryParseHex(x, out _)).ToArray();
        if (cols.Length < 2 || !RgbF.TryParseHex(GG.Text, out _)) { Ui.Toast("Bitte mindestens 2 gültige Farben und eine Leuchtfarbe eingeben.", true); return; }
        try
        {
            var file = CustomizeService.MakeGradientWallpaper("Eigener Verlauf", cols, GG.Text);
            _c.SetWallpaper(file, 4, MonBox.SelectedIndex - 1);
            Ui.Toast("Verlauf erstellt und gesetzt");
        }
        catch (Exception ex) { Ui.Toast("Fehler: " + ex.Message, true); }
        Refresh();
    }

    private void Slide_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner mit Hintergrundbildern" };
        if (d.ShowDialog() != true) return;
        int min = SlideMin.SelectedItem is ComboBoxItem { Tag: string t } ? int.Parse(t) : 30;
        _c.StartSlideshow(d.FolderName, min);
        Refresh();
    }

    private void SlideNext_Click(object s, RoutedEventArgs e) { _c.NextSlide(); Refresh(); }
    private void SlideStop_Click(object s, RoutedEventArgs e) { _c.StopSlideshow(); Refresh(); }
    private void Explorer_Click(object s, RoutedEventArgs e) => _c.RestartExplorer();

    private void Restore_Click(object s, RoutedEventArgs e)
    {
        if (Dialogs.Confirm("Alles auf Original zurücksetzen?", "Alle Design-Änderungen (Farben, Taskleiste, Startmenü, Explorer, Maus, Werbung) werden auf die gesicherten Originalwerte gesetzt."))
            _c.RestoreAll();
    }
}
