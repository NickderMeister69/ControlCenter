using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NickControlCenter;

public partial class ToolsPage : UserControl, IPage
{
    private List<AppUpdate> _updates = new();
    private CancellationTokenSource? _diskCts;
    private string? _diskRoot;
    private HotkeyVM? _recording;
    private bool _init;

    public ToolsPage()
    {
        InitializeComponent();
        Keys.ItemsSource = HotkeyService.Instance.Items;
        var st = AppSettings.Current;
        DlWeekly.IsChecked = st.DownloadsAutoSort;
        foreach (ComboBoxItem i in DlDays.Items) if ((string)i.Tag == st.DownloadsOldDays.ToString()) i.IsSelected = true;
        _init = true;
        if (!AppUpdates.Available) { UpdSearch.IsEnabled = false; UpdSummary.Text = "winget fehlt – im Microsoft Store „App Installer“ installieren"; }
    }

    public void OnVisibleChanged(bool visible)
    {
        if (!visible) { _gamesTimer?.Stop(); if (_recording != null) { _recording.Recording = false; HotkeyService.Instance.Apply(); } _recording = null; return; }
        RefreshDownloads();
        RefreshDrives();
        RefreshGames();
        _ = InstallEstimate.MeasureAsync();
        if (_gamesTimer == null)
        {
            _gamesTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _gamesTimer.Tick += (_, _) => { if (_games.Any(g => g.NeedsUpdate || g.RequestedAt != null)) RefreshGames(); };
        }
        _gamesTimer.Start();
    }

    // ---------------- Programme aktualisieren
    private async void UpdSearch_Click(object s, RoutedEventArgs e)
    {
        UpdSearch.IsEnabled = UpdAll.IsEnabled = false; UpdBusy.Visibility = Visibility.Visible;
        UpdSummary.Text = "Suche Updates … (dauert beim ersten Mal etwas)";
        try
        {
            _updates = await AppUpdates.ListAsync();
            UpdList.ItemsSource = _updates;
            UpdSummary.Text = _updates.Count == 0 ? "Alles aktuell ✔" : $"{_updates.Count} Programme haben ein Update";
            UpdAll.IsEnabled = _updates.Count > 0;
            Log.Info(LogModule.App, $"winget: {_updates.Count} Updates verfügbar");
            if (_updates.Count > 0)
            {
                UpdTotal.Text = "Ermittle Größen und Dauer …";
                UpdTotal.Visibility = Visibility.Visible;
                await AppUpdates.FillSizesAsync(_updates);
                foreach (var u in _updates) u.RefreshEstimate();
                UpdateUpdTotal();
            }
            else UpdTotal.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { UpdSummary.Text = "Suche fehlgeschlagen: " + ex.Message; }
        finally { UpdSearch.IsEnabled = true; UpdBusy.Visibility = Visibility.Collapsed; }
    }

    private void UpdateUpdTotal()
    {
        var todo = _updates.Where(u => !u.Done).ToList();
        if (todo.Count == 0) { UpdTotal.Visibility = Visibility.Collapsed; return; }
        long bytes = todo.Sum(u => Math.Max(0, u.Bytes));
        var t = TimeSpan.FromSeconds(todo.Sum(u => InstallEstimate.Duration(Math.Max(0, u.Bytes), InstallEstimate.AppInstallSeconds(Math.Max(0, u.Bytes))).TotalSeconds));
        int unknown = todo.Count(u => u.Bytes <= 0);
        UpdTotal.Text = $"„Alle aktualisieren“: {Fmt.Bytes(bytes)} Download{(unknown > 0 ? $" (+ {unknown} ohne Größenangabe)" : "")} · Dauer {InstallEstimate.DurationText(t)}  ({InstallEstimate.SpeedText})";
        UpdAll.Content = $"Alle aktualisieren ({InstallEstimate.DurationText(t)})";
    }

    private async void UpdOne_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is AppUpdate a) { await AppUpdates.UpgradeAsync(a); UpdateUpdTotal(); }
    }

    // ---------------- Spiele aktualisieren (Steam)
    private readonly System.Collections.ObjectModel.ObservableCollection<SteamGameVM> _games = new();
    private System.Windows.Threading.DispatcherTimer? _gamesTimer;

    private async void RefreshGames()
    {
        try
        {
            var states = await Task.Run(GameUpdates.Scan);
            states = await GameUpdates.CheckOnlineAsync(states);
            states = states.OrderByDescending(g => g.NeedsUpdate).ThenBy(g => g.Name).ToList();
            if (states.Count == 0) { GamesSummary.Text = "Keine Steam-Spiele gefunden"; GamesAll.Visibility = GamesTotal.Visibility = Visibility.Collapsed; return; }
            // vorhandene Einträge aktualisieren (Fortschritt flackert so nicht)
            foreach (var st in states)
            {
                var vm = _games.FirstOrDefault(g => g.S.AppId == st.AppId);
                if (vm == null) _games.Add(new SteamGameVM(st)); else vm.Update(st);
            }
            foreach (var gone in _games.Where(g => states.All(s => s.AppId != g.S.AppId)).ToList()) _games.Remove(gone);
            var sorted = _games.OrderByDescending(g => g.NeedsUpdate).ThenBy(g => g.Name).ToList();
            for (int i = 0; i < sorted.Count; i++) { int cur = _games.IndexOf(sorted[i]); if (cur != i) _games.Move(cur, i); }
            if (GamesList.ItemsSource == null) GamesList.ItemsSource = _games;

            var pending = states.Where(g => g.NeedsUpdate).ToList();
            int running = pending.Count(g => g.Updating);
            foreach (var g in _games) g.Tick();
            GamesSummary.Text = pending.Count == 0 ? $"Alle {states.Count} Spiele sind aktuell ✔" :
                $"{pending.Count} von {states.Count} Spielen haben ein Update" + (running > 0 ? $" – {running} wird gerade geladen" : "");
            if (!GameUpdates.SteamRunning && pending.Count > 0) GamesSummary.Text += "  (Steam läuft nicht – „Alle in Steam aktualisieren“ startet es)";
            long bytes = pending.Sum(g => g.Remaining);
            GamesTotal.Text = pending.Count == 0 ? "" : $"Zusammen: {Fmt.Bytes(bytes)} · {InstallEstimate.DurationText(InstallEstimate.Duration(bytes, pending.Count * 10 + bytes / 1e6 * 0.05))} ({InstallEstimate.SpeedText})";
            GamesTotal.Visibility = GamesAll.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { GamesSummary.Text = "Steam konnte nicht gelesen werden: " + ex.Message; }
    }

    private void GamesRefresh_Click(object s, RoutedEventArgs e) => RefreshGames();

    private void GamesAll_Click(object s, RoutedEventArgs e) => StartGameUpdates(_games.Where(g => g.NeedsUpdate && !g.S.Updating).ToList());

    private void GameOne_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is SteamGameVM g) StartGameUpdates(new() { g });
    }

    private void GameSteam_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is SteamGameVM g) GameUpdates.OpenGame(g.S.AppId);
    }

    private void StartGameUpdates(List<SteamGameVM> list)
    {
        if (list.Count == 0) return;
        bool wasRunning = GameUpdates.SteamRunning;
        if (!GameUpdates.StartSteamSilent()) { Ui.Toast("Steam wurde nicht gefunden", true); return; }
        foreach (var g in list) { g.RequestedAt = DateTime.Now; g.Tick(); }
        Ui.Toast(wasRunning ? "Steam läuft schon und lädt Updates von selbst – Fortschritt siehst du hier" : "Steam startet unsichtbar im Hintergrund und lädt die Updates – Fortschritt siehst du hier");
        if (_gamesTimer != null) _gamesTimer.Interval = TimeSpan.FromSeconds(2);
    }

    private async void UpdAll_Click(object s, RoutedEventArgs e)
    {
        var todo = _updates.Where(u => !u.Done).ToList();
        if (todo.Count == 0) return;
        long bytesAll = todo.Sum(u => Math.Max(0, u.Bytes));
        var tAll = TimeSpan.FromSeconds(todo.Sum(u => InstallEstimate.Duration(Math.Max(0, u.Bytes), InstallEstimate.AppInstallSeconds(Math.Max(0, u.Bytes))).TotalSeconds));
        if (!Dialogs.Confirm("Alle aktualisieren?", $"{todo.Count} Programme werden nacheinander still aktualisiert.\n\nDownload: ca. {Fmt.Bytes(bytesAll)}\nDauer: {InstallEstimate.DurationText(tAll)} ({InstallEstimate.SpeedText})\n\nOffene Programme am besten vorher schließen.", "Los geht's", "Abbrechen")) return;
        UpdAll.IsEnabled = UpdSearch.IsEnabled = false; UpdBusy.Visibility = Visibility.Visible;
        int ok = 0, i = 0;
        foreach (var a in todo)
        {
            var rest = TimeSpan.FromSeconds(todo.Skip(i).Sum(u => InstallEstimate.Duration(Math.Max(0, u.Bytes), InstallEstimate.AppInstallSeconds(Math.Max(0, u.Bytes))).TotalSeconds));
            UpdSummary.Text = $"Aktualisiere {++i}/{todo.Count}: {a.Name} – noch {InstallEstimate.DurationText(rest)}";
            if (await AppUpdates.UpgradeAsync(a)) ok++;
        }
        UpdSummary.Text = $"Fertig: {ok} von {todo.Count} aktualisiert" + (ok < todo.Count ? " – Rest steht unten (meist: Programm läuft noch)" : " ✔");
        UpdSearch.IsEnabled = true; UpdAll.IsEnabled = ok < todo.Count; UpdBusy.Visibility = Visibility.Collapsed;
        UpdateUpdTotal();
    }

    // ---------------- Downloads
    private int OldDays => int.TryParse((DlDays.SelectedItem as ComboBoxItem)?.Tag as string, out var d) ? d : 30;

    private async void RefreshDownloads()
    {
        var days = OldDays;
        var (plan, old) = await Task.Run(() => (DownloadsCleaner.Plan(), DownloadsCleaner.OldInstallers(days)));
        DlSummary.Text = plan.Count == 0 ? "Download-Ordner ist schon ordentlich ✔" : $"{plan.Count} Dateien liegen lose im Download-Ordner ({Fmt.Bytes(plan.Sum(p => p.Bytes))})";
        DlDetail.Text = string.Join(" · ", plan.GroupBy(p => p.Category).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
                        + (old.Count > 0 ? $"\n{old.Count} Installer älter als {days} Tage ({Fmt.Bytes(old.Sum(f => f.Length))}) – werden meist nie wieder gebraucht" : "");
        DlSort.IsEnabled = plan.Count > 0;
        DlOld.IsEnabled = old.Count > 0;
        DlOld.Content = old.Count > 0 ? $"{old.Count} alte Installer in den Papierkorb" : "Keine alten Installer";
    }

    private async void DlSort_Click(object s, RoutedEventArgs e)
    {
        var (moved, failed) = await Task.Run(() => DownloadsCleaner.Sort(DownloadsCleaner.Plan()));
        AppSettings.Current.DownloadsLastSort = DateTime.Now; AppSettings.Current.Save();
        Ui.Toast($"{moved} Dateien sortiert" + (failed > 0 ? $", {failed} übersprungen (gerade geöffnet)" : ""));
        RefreshDownloads();
    }

    private async void DlOld_Click(object s, RoutedEventArgs e)
    {
        var old = DownloadsCleaner.OldInstallers(OldDays);
        if (old.Count == 0) return;
        var names = string.Join("\n", old.Take(12).Select(f => "• " + f.Name)) + (old.Count > 12 ? $"\n… und {old.Count - 12} weitere" : "");
        if (!Dialogs.Confirm("Alte Installer löschen?", $"Diese Dateien kommen in den Papierkorb (dort wiederherstellbar):\n\n{names}", "In den Papierkorb", "Abbrechen")) return;
        int n = await Task.Run(() => DownloadsCleaner.ToRecycleBin(old.Select(f => f.FullName)));
        Ui.Toast($"{n} Installer im Papierkorb – {Fmt.Bytes(old.Sum(f => f.Length))} frei, sobald du ihn leerst");
        RefreshDownloads();
    }

    private void DlDays_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_init) return;
        AppSettings.Current.DownloadsOldDays = OldDays; AppSettings.Current.Save();
        RefreshDownloads();
    }

    private void DlOpen_Click(object s, RoutedEventArgs e) => Env.Open(DownloadsCleaner.Folder);

    private void DlWeekly_Click(object s, RoutedEventArgs e)
    {
        AppSettings.Current.DownloadsAutoSort = DlWeekly.IsChecked == true; AppSettings.Current.Save();
    }

    // ---------------- Speicherplatz
    private sealed record DriveRow(string Text, double Used, Brush Brush, string Root);

    private void RefreshDrives()
    {
        var rows = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                double used = (d.TotalSize - d.TotalFreeSpace) * 100.0 / d.TotalSize;
                rows.Add(new DriveRow(DiskAnalyzer.DriveText(d), used, used > 90 ? Palette.Bad : used > 80 ? Palette.Warn : (Brush)FindResource("AccentBrush"), d.RootDirectory.FullName));
            }
            catch { }
        }
        Drives.ItemsSource = rows;
    }

    private void Analyze_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string r) Analyze(r); }
    private void AnalyzeHome_Click(object s, RoutedEventArgs e) => Analyze(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void DiskUp_Click(object s, RoutedEventArgs e)
    {
        var parent = _diskRoot == null ? null : Directory.GetParent(_diskRoot.TrimEnd('\\'))?.FullName;
        if (parent != null) Analyze(parent);
    }

    private void DiskCancel_Click(object s, RoutedEventArgs e) => _diskCts?.Cancel();

    private void DiskItem_Click(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject o && FindParent<Button>(o) != null) return;
        if ((s as FrameworkElement)?.DataContext is DirSize { IsDir: true } d) Analyze(d.Path);
    }

    private void DiskOpen_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is DirSize d) { if (d.IsDir || d.Name.StartsWith("(")) Env.Open(d.Path); else Env.ShowInExplorer(d.Path); }
    }

    private static T? FindParent<T>(DependencyObject o) where T : DependencyObject
    {
        while (o != null) { if (o is T t) return t; o = VisualTreeHelper.GetParent(o); }
        return null;
    }

    private async void Analyze(string root)
    {
        _diskCts?.Cancel();
        var cts = _diskCts = new CancellationTokenSource();
        _diskRoot = root;
        DiskBusy.Visibility = DiskCancel.Visibility = Visibility.Visible;
        DiskUp.IsEnabled = Directory.GetParent(root.TrimEnd('\\')) != null;
        DiskPath.Text = "Messe " + root + " …";
        DiskList.ItemsSource = null;
        var started = DateTime.Now;
        string current = "";
        var prog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        prog.Tick += (_, _) => DiskPath.Text = "Messe … " + current;
        prog.Start();
        try
        {
            var res = await Task.Run(() => DiskAnalyzer.Scan(root, cts.Token, p => current = p), cts.Token);
            if (cts != _diskCts) return;
            DiskList.ItemsSource = res.Take(40).ToList();
            DiskPath.Text = $"{root}  ·  {Fmt.Bytes(res.Sum(r => r.Bytes))} in {(DateTime.Now - started).TotalSeconds:0} s gemessen  ·  auf einen Ordner klicken, um hineinzuschauen";
        }
        catch (OperationCanceledException) { if (cts == _diskCts) DiskPath.Text = "Abgebrochen"; }
        catch (Exception ex) { DiskPath.Text = "Fehler: " + ex.Message; }
        finally
        {
            prog.Stop();
            if (cts == _diskCts) DiskBusy.Visibility = DiskCancel.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- Schnell-Check „Was kann weg?“
    private System.Collections.ObjectModel.ObservableCollection<SpaceItem> _quick = new();
    private string _quickDrive = "";

    private void Quick_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string r) QuickCheck(r); }

    private async void QuickCheck(string drive)
    {
        _quickDrive = drive;
        QuickPanel.Visibility = Visibility.Visible;
        QuickBusy.Visibility = Visibility.Visible;
        QuickAll.Visibility = Visibility.Collapsed;
        QuickTitle.Text = $"Prüfe {drive} …";
        QuickList.ItemsSource = null;
        try
        {
            _quick = new(await Task.Run(() => SpaceAdvisor.QuickCheck(drive, CancellationToken.None)));
            QuickList.ItemsSource = _quick;
            UpdateQuickTitle();
        }
        catch (Exception ex) { QuickTitle.Text = "Fehler: " + ex.Message; }
        finally { QuickBusy.Visibility = Visibility.Collapsed; }
    }

    private List<SpaceItem> SafeOnes => _quick.Where(i => i.Verdict == SpaceVerdict.Safe && i.HasAction && i.Bytes > 0 && !i.ActionLabel.Contains("öffnen") && !i.Status.StartsWith("Fehler")).ToList();

    private void UpdateQuickTitle()
    {
        long safe = _quick.Where(i => i.Verdict == SpaceVerdict.Safe && i.Bytes > 0).Sum(i => i.Bytes);
        QuickTitle.Text = safe > 0 ? $"Auf {_quickDrive} können ca. {Fmt.Bytes(safe)} gefahrlos weg" : $"Auf {_quickDrive} ist nichts Großes übrig, das gefahrlos weg kann ✔";
        var safeList = SafeOnes;
        QuickAll.Content = $"Alles Grüne aufräumen ({Fmt.Bytes(safeList.Sum(i => i.Bytes))})";
        QuickAll.Visibility = safeList.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void QuickAction_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not SpaceItem i || i.Action == null) return;
        if (i.Confirm != null && !Dialogs.Confirm(i.Title, i.Confirm, i.ActionLabel, "Abbrechen")) return;
        await RunItem(i);
        UpdateQuickTitle();
        RefreshDrives();
    }

    private async void QuickAll_Click(object s, RoutedEventArgs e)
    {
        var list = SafeOnes;
        if (list.Count == 0) return;
        if (!Dialogs.Confirm("Alles Grüne aufräumen?", "Folgendes wird geleert:\n\n" + string.Join("\n", list.Select(i => $"• {i.Title} ({i.SizeText})")) + "\n\nAlles davon wird von Windows bzw. den Programmen bei Bedarf neu angelegt. Dateien, die gerade benutzt werden, bleiben einfach liegen. Browser, Discord und Steam am besten vorher schließen.", "Aufräumen", "Abbrechen")) return;
        QuickAll.IsEnabled = false;
        foreach (var i in list) await RunItem(i);
        QuickAll.IsEnabled = true;
        UpdateQuickTitle();
        RefreshDrives();
    }

    private async Task RunItem(SpaceItem i)
    {
        i.Busy = true; i.Status = "Läuft …";
        bool ok = false;
        try { i.Status = await Task.Run(() => i.Action!(i)); ok = true; }
        catch (Exception ex) { i.Status = "Fehler: " + ex.Message; }
        finally { i.Busy = false; }
        if (!ok || i.ActionLabel.Contains("öffnen", StringComparison.OrdinalIgnoreCase)) return;

        // nachmessen: ist es weg, verschwindet der Eintrag – bleibt etwas übrig (Dateien in Benutzung), steht das dabei
        long left = await Task.Run(() =>
            i.Path.Length == 0 ? 0 :
            Directory.Exists(i.Path) ? SpaceAdvisor.DirSize(i.Path, CancellationToken.None) :
            File.Exists(i.Path) ? new FileInfo(i.Path).Length : 0);
        if (i.Bytes < 0) return; // Einträge ohne Größe (DISM, Systemschutz) bleiben stehen
        if (left < (5L << 20)) { await Task.Delay(1200); _quick.Remove(i); }
        else
        {
            i.Bytes = left;
            i.Status = $"{Fmt.Bytes(left)} sind übrig – gerade von einem Programm benutzt. Programm schließen und nochmal klicken.";
        }
    }

    // ---------------- Tastenkürzel
    private void KeyRecord_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not HotkeyVM vm) return;
        if (_recording != null) _recording.Recording = false;
        if (_recording == vm) { _recording = null; HotkeyService.Instance.Apply(); return; }
        _recording = vm; vm.Recording = true;
        HotkeyService.Instance.Suspend();
        Focus();
        Keyboard.Focus(this);
    }

    private void Page_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (_recording == null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { _recording.Recording = false; _recording = null; e.Handled = true; HotkeyService.Instance.Apply(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) { e.Handled = true; return; }
        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (m.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= 8;
        e.Handled = true;
        bool fKey = key is >= Key.F1 and <= Key.F24;
        if ((mods & (1 | 2 | 8)) == 0 && !fKey) { Ui.Toast("Bitte mit Strg, Alt oder Win kombinieren (F-Tasten gehen auch allein)", true); return; }
        HotkeyService.Instance.SetKey(_recording, mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        _recording = null;
    }
}
