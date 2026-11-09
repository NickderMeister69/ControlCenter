using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class SettingsPage : UserControl, IPage
{
    public SettingsPage()
    {
        InitializeComponent();
        Accents.ItemsSource = AppSettings.Accents.Select(a => new { a.Name, a.Hex }).ToList();
        AccentHex.Text = AppSettings.Current.Accent;
        LogPath.Text = Log.Folder;
        CfgPath.Text = JsonStore.Folder;
        About.Text = $"Control Center {typeof(App).Assembly.GetName().Version?.ToString(3)} · Windows-Build {Env.WindowsBuild} · {(Env.IsAdmin ? "mit Admin-Rechten" : "OHNE Admin-Rechte")}\n{Env.ExePath}";
    }

    public void OnVisibleChanged(bool visible)
    {
        if (!visible) return;
        var s = AppSettings.Current;
        ToTray.IsChecked = s.CloseToTray;
        Fahr.IsChecked = s.Fahrenheit;
        AutoStart.IsChecked = Autostart.IsEnabled();
        HwAuto.IsChecked = s.HwinfoAutostart;
        var hw = HwinfoLauncher.FindExe();
        HwState.Text = hw == null ? "HWiNFO ist nicht installiert (kostenlos auf hwinfo.com) – oder wähle die exe aus." :
            $"{(HwinfoLauncher.IsRunning ? "✔ läuft" : "läuft gerade nicht")} · {HardwareHub.HwinfoText(HwinfoSource.LastStatus)}\n{hw}";
        LinkState.Text = Shortcuts.Exists ? "✔ Verknüpfungen auf dem Desktop und im Startmenü sind vorhanden." : "Verknüpfungen fehlen.";
    }

    private void SetAccent(string hex)
    {
        if (!RgbF.TryParseHex(hex, out var c)) { Ui.Toast("Ungültige Farbe", true); return; }
        AppSettings.Current.Accent = c.Hex;
        AppSettings.Current.Save();
        AppSettings.ApplyAccent(c.Hex);
        AccentHex.Text = c.Hex;
    }

    private void Accent_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string h) SetAccent(h); }
    private void AccentHex_Click(object s, RoutedEventArgs e) => SetAccent(AccentHex.Text);

    private void Autostart_Click(object s, RoutedEventArgs e)
    {
        bool on = AutoStart.IsChecked == true;
        AppSettings.Current.Autostart = on;
        AppSettings.Current.Save();
        if (on) { if (!Autostart.Enable(out var err)) { Ui.Toast("Autostart fehlgeschlagen: " + err, true); AutoStart.IsChecked = false; } }
        else Autostart.Disable();
    }

    private void Tray_Click(object s, RoutedEventArgs e) { AppSettings.Current.CloseToTray = ToTray.IsChecked == true; AppSettings.Current.Save(); }
    private void Fahr_Click(object s, RoutedEventArgs e) { AppSettings.Current.Fahrenheit = Fahr.IsChecked == true; AppSettings.Current.Save(); }
    private void Exit_Click(object s, RoutedEventArgs e) => App.Instance.ExitApp(ask: true);
    private void Links_Click(object s, RoutedEventArgs e)
    {
        if (Shortcuts.CreateAll(out var err)) { AppSettings.Current.ShortcutsCreated = true; AppSettings.Current.Save(); Ui.Toast("Verknüpfungen erstellt"); }
        else Ui.Toast("Fehler: " + err, true);
        OnVisibleChanged(true);
    }
    private void HwAuto_Click(object s, RoutedEventArgs e)
    {
        AppSettings.Current.HwinfoAutostart = HwAuto.IsChecked == true;
        AppSettings.Current.Save();
        if (AppSettings.Current.HwinfoAutostart) Task.Run(HwinfoLauncher.StartIfWanted);
    }

    private async void HwStart_Click(object s, RoutedEventArgs e)
    {
        var exe = HwinfoLauncher.FindExe();
        if (exe == null) { Ui.Toast("HWiNFO nicht gefunden – bitte die exe wählen", true); return; }
        if (HwinfoLauncher.IsRunning) { Ui.Toast("HWiNFO läuft schon"); return; }
        await Task.Run(() => HwinfoLauncher.Start(exe));
        await Task.Delay(4000);
        OnVisibleChanged(true);
    }

    private void HwPick_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "HWiNFO (HWiNFO64.exe)|HWiNFO*.exe|Programme (*.exe)|*.exe", Title = "HWiNFO64.exe wählen" };
        if (dlg.ShowDialog() != true) return;
        AppSettings.Current.HwinfoPath = dlg.FileName;
        AppSettings.Current.Save();
        OnVisibleChanged(true);
    }

    private void Monitor_Click(object s, RoutedEventArgs e) => Ui.Go("Monitor");
    private void Diag_Click(object s, RoutedEventArgs e) => Ui.Go("Problemlöser");
    private void Logs_Click(object s, RoutedEventArgs e) => Env.Open(Log.Folder);
    private void Cfg_Click(object s, RoutedEventArgs e) => Env.Open(JsonStore.Folder);
}
