using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class DriversPage : UserControl
{
    public DriversPage()
    {
        InitializeComponent();
        DataContext = Services.Drivers;
        if (Services.Drivers.Rows.Count == 0) _ = Services.Drivers.CheckAsync(false);
    }

    private async void Check_Click(object s, RoutedEventArgs e) => await Services.Drivers.CheckAsync(false);

    private async void Install_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not DriverRow r) return;
        if (r.Offer == null) { Env.Open(DriverCheck.AmdDriverPage); return; }
        if (!Dialogs.Confirm("Treiber herunterladen?", $"{r.Title} {r.Latest} wird in deinen Download-Ordner geladen und danach der Installer gestartet.\n\nGröße und Dauer: {r.SizeInfo}\n\nWährend der Installation flackert kurz der Bildschirm.", "Herunterladen", "Abbrechen")) return;
        await Services.Drivers.DownloadAndInstallAsync(r);
    }

    private void Details_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is DriverRow r) Env.Open(r.Offer?.DetailsUrl is { Length: > 0 } u ? u : DriverCheck.AmdDriverPage);
    }

    private List<HwDriver> _hw = new();

    private async void HwCheck_Click(object s, RoutedEventArgs e)
    {
        HwCheck.IsEnabled = false; HwAll.Visibility = Visibility.Collapsed; HwSummary.Text = "Lese Treiber …";
        try
        {
            _hw = await Task.Run(DriverAudit.Scan);
            HwList.ItemsSource = _hw;
            if (DriverAudit.Board.Length > 0)
            {
                BoardBtn.Content = $"Support-Seite {DriverAudit.Board}";
                BoardBtn.Tag = DriverAudit.BoardUrl;
                HwLinks.Visibility = Visibility.Visible;
            }
            HwSummary.Text = $"{_hw.Count} Geräte gelesen – frage Windows Update nach neueren Treibern … (dauert bis zu einer Minute)";
            List<WuDriver> wu;
            string? wuError = null;
            try { wu = await Task.Run(WindowsDriverUpdates.Search); }
            catch (Exception ex) { wu = new(); wuError = ex.Message; Log.Warn(LogModule.Diagnose, "Windows-Update-Suche: " + ex.Message); }
            var extra = new List<HwDriver>();
            foreach (var u in wu)
            {
                var d = _hw.FirstOrDefault(h => h.Update == null && WindowsDriverUpdates.Match(h, new() { u }) != null);
                if (d != null) d.Update = u;
                else extra.Add(new HwDriver { Category = string.IsNullOrEmpty(u.Class) ? "Windows Update" : u.Class, Device = u.Model.Length > 0 ? u.Model : u.Title, Maker = u.Maker, Version = "", Level = 1, Update = u });
            }
            if (extra.Count > 0) _hw = extra.Concat(_hw).ToList();
            bool wuOk = wuError == null;
            foreach (var h in _hw) h.Checked = wuOk;
            // Geräte mit Update nach oben
            _hw = _hw.OrderByDescending(h => h.HasUpdate).ToList();
            HwList.ItemsSource = _hw;
            int upd = _hw.Count(h => h.HasUpdate);
            HwSummary.Text = wuError != null ? $"{_hw.Count} Geräte gelesen – Windows Update war nicht erreichbar ({wuError})" :
                             upd > 0 ? $"{_hw.Count} Geräte geprüft · {upd} Treiber-Updates zum Installieren" : $"{_hw.Count} Geräte geprüft · alle Treiber sind aktuell ✔";
            HwAll.Visibility = upd > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (upd > 0)
            {
                await InstallEstimate.MeasureAsync();
                foreach (var h in _hw) h.Refresh();
                var (b, t) = HwTotals();
                HwSummary.Text += $"\nAlle installieren: {Fmt.Bytes(b)} · {InstallEstimate.DurationText(t)} ({InstallEstimate.SpeedText})";
                HwAll.Content = $"Alle installieren ({InstallEstimate.DurationText(t)})";
            }
            Log.Info(LogModule.Diagnose, $"Treiberprüfung: {_hw.Count} Geräte, {upd} Updates über Windows Update, Mainboard {DriverAudit.Board}");
        }
        catch (Exception ex) { HwSummary.Text = "Fehler: " + ex.Message; }
        finally { HwCheck.IsEnabled = true; }
    }

    private (long Bytes, TimeSpan Time) HwTotals()
    {
        var todo = _hw.Where(h => h.HasUpdate).Select(h => h.Update!).ToList();
        long b = todo.Sum(u => u.Bytes);
        var t = TimeSpan.FromSeconds(todo.Sum(u => InstallEstimate.Duration(u.Bytes, InstallEstimate.DriverInstallSeconds(u.Bytes)).TotalSeconds));
        return (b, t);
    }

    private async void HwInstall_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not HwDriver d || d.Update == null) return;
        if (!Dialogs.Confirm("Treiber installieren?", $"{d.Device}\n{d.Update.Title}\n\nGröße und Dauer: {InstallEstimate.Text(d.Update.Bytes, InstallEstimate.DriverInstallSeconds(d.Update.Bytes))}\n\nDer Treiber kommt von Windows Update. Vorher wird ein Wiederherstellungspunkt erstellt. Bei Netzwerk-Treibern kann das Internet kurz weg sein.", "Installieren", "Abbrechen")) return;
        await InstallAsync(new[] { d });
    }

    private async void HwAll_Click(object s, RoutedEventArgs e)
    {
        var todo = _hw.Where(h => h.HasUpdate).ToList();
        if (todo.Count == 0) return;
        if (!Dialogs.Confirm("Alle Treiber installieren?", $"{todo.Count} Treiber von Windows Update werden geladen und installiert:\n\n" + string.Join("\n", todo.Select(t => $"• {t.Device} ({Fmt.Bytes(t.Update!.Bytes)})")) + $"\n\nZusammen: {Fmt.Bytes(HwTotals().Bytes)} · Dauer {InstallEstimate.DurationText(HwTotals().Time)}\nDanach ist evtl. ein Neustart nötig.", "Alle installieren", "Abbrechen")) return;
        HwAll.IsEnabled = false;
        await InstallAsync(todo);
        HwAll.IsEnabled = true;
    }

    private async Task InstallAsync(IList<HwDriver> rows)
    {
        try { await Services.Boost.RestorePointAsync(); } catch { }
        foreach (var r in rows) { r.Busy = true; r.Status = "Wartet …"; }
        bool anyReboot = false; int ok = 0;
        // einzeln nacheinander – ein fehlschlagender Treiber reißt so die anderen nicht mit
        foreach (var r in rows)
        {
            r.Status = $"Wird geladen und installiert … ({InstallEstimate.DurationText(InstallEstimate.Duration(r.Update!.Bytes, InstallEstimate.DriverInstallSeconds(r.Update.Bytes)))})";
            try
            {
                var (good, reboot, msg) = await Task.Run(() => WindowsDriverUpdates.Install(new[] { r.Update! }));
                anyReboot |= reboot;
                if (good) ok++;
                r.Status = r.Update!.Installed ? (reboot ? "✔ Installiert – wirkt nach dem Neustart" : "✔ Installiert") : msg;
                r.Update = r.Update; r.Refresh();
            }
            catch (Exception ex) { r.Status = "Fehler: " + ex.Message; Log.Warn(LogModule.Diagnose, "Treiber-Installation: " + ex.Message); }
            finally { r.Busy = false; }
        }
        Ui.Toast($"{ok} von {rows.Count} Treibern installiert", ok < rows.Count);
        HwAll.Visibility = _hw.Any(h => h.HasUpdate) ? Visibility.Visible : Visibility.Collapsed;
        if (anyReboot && Dialogs.Confirm("Neustart nötig", "Die neuen Treiber werden erst nach einem Neustart aktiv. Jetzt neu starten?", "Jetzt neu starten", "Später"))
            Env.Run("shutdown.exe", "/r /t 5");
    }

    private void Link_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string u) Env.Open(u); }
}
