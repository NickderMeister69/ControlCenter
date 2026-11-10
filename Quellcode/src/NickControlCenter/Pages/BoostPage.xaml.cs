using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class BoostPage : UserControl, IPage
{
    private readonly BoostService _b = Services.Boost;

    public BoostPage()
    {
        InitializeComponent();
        DataContext = _b;
        // entfernte Spiele (Hidden) nicht anzeigen
        var view = new System.Windows.Data.ListCollectionView(_b.Games) { Filter = o => o is GameVM { P.Hidden: false } };
        GameList.ItemsSource = view;
        _b.Games.CollectionChanged += (_, _) => view.Refresh();
        _b.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BoostService.GamesText)) view.Refresh(); };
        _b.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(BoostService.SteamConnected) or nameof(BoostService.SelectedGame)) Update(); };
        _b.BackgroundApps.CollectionChanged += (_, _) => Update();
        Unloaded += (_, _) => _b.Save();
        Update();
    }

    public void OnVisibleChanged(bool visible)
    {
        if (!visible) { _b.Save(); return; }
        _b.RefreshActive();
        _b.RefreshApps();
        if (_b.SelectedGame == null) _b.SelectedGame = _b.Games.FirstOrDefault(g => !g.P.Hidden);
    }

    private void Update()
    {
        NoApps.Visibility = _b.BackgroundApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SteamBtn.Content = _b.SteamConnected ? "Steam neu einlesen" : "Mit Steam verbinden";
        SteamOffBtn.Visibility = _b.SteamConnected ? Visibility.Visible : Visibility.Collapsed;
        bool has = _b.SelectedGame != null;
        GameDetail.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoGame.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (T1 == null || sender is not RadioButton { Tag: string t }) return;
        foreach (var p in new FrameworkElement[] { T1, T2, T4, T5, T6 }) p.Visibility = p.Name == t ? Visibility.Visible : Visibility.Collapsed;
        _b.Save();
    }

    private async void Boost_Click(object s, RoutedEventArgs e) { _b.Save(); await _b.BoostAsync(); }

    private async void Undo_Click(object s, RoutedEventArgs e)
    {
        if (Dialogs.Confirm("Alles zurücksetzen?", "Alle Änderungen des FPS-Boosts (Registry, Energieplan, Spiel-Configs, Intro-Videos) werden auf den Originalzustand zurückgesetzt."))
            await _b.UndoAsync();
    }

    private GameVM? Sel => _b.SelectedGame;

    private void Steam_Click(object s, RoutedEventArgs e) => _b.ConnectSteam(true);
    private void SteamOff_Click(object s, RoutedEventArgs e) => _b.ConnectSteam(false);
    private void Rescan_Click(object s, RoutedEventArgs e) { _b.Rescan(); Ui.Toast("Suche Spiele (Steam, Epic, Riot) …"); }

    private void AddExe_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Spiele / Programme (*.exe)|*.exe", Title = "Spiel-exe wählen (auch Launcher-exe geht)" };
        if (dlg.ShowDialog() == true) _b.AddGameExe(dlg.FileName);
    }

    private void AddKnown_Click(object s, RoutedEventArgs e)
    {
        if (KnownBox.SelectedItem is string n) _b.AddKnownGame(n);
        else Ui.Toast("Erst ein Spiel in der Liste daneben auswählen", true);
    }

    private void Preset_Click(object s, RoutedEventArgs e)
    {
        if (Sel != null && (s as FrameworkElement)?.Tag is string t && int.TryParse(t, out var i)) Sel.PresetIndex = i;
    }

    private void LookPreset_Click(object s, RoutedEventArgs e)
    {
        if (Sel is { } g && (s as FrameworkElement)?.DataContext is LookPreset lp)
        {
            g.ApplyLook(lp);
            Ui.Toast($"{g.Name}: „{lp.Name}“ gewählt – wird beim nächsten Start übernommen");
            if (!g.Running) Task.Run(() => _b.ApplyGameFiles(g, false));
        }
    }
    private void ClearFine_Click(object s, RoutedEventArgs e) { if (Sel is { } g) { g.ClearFine(); if (!g.Running) Task.Run(() => _b.ApplyGameFiles(g, false)); } }
    private void CopyConsole_Click(object s, RoutedEventArgs e)
    {
        if (Sel?.ConsoleCommands is { Length: > 0 } c) { Clipboard.SetText(c); Ui.Toast("Konsolenbefehle kopiert – im Spiel Konsole öffnen und Zeile für Zeile einfügen"); }
        else Ui.Toast("Erst Profi-Einstellungen auswählen", true);
    }

    private void Recommend_Click(object s, RoutedEventArgs e)
    {
        if (Sel is not { } g) return;
        var (r, text) = _b.Recommend(g);
        if (Dialogs.Confirm($"Empfehlung für {g.Name}", text, "Übernehmen", "Abbrechen")) _b.ApplyRecommendation(g, r);
    }

    private void Play_Click(object s, RoutedEventArgs e) { if (Sel != null) _b.Launch(Sel); }

    private void Remove_Click(object s, RoutedEventArgs e)
    {
        if (Sel != null && Dialogs.Confirm($"{Sel.Name} entfernen?", "Das Spiel verschwindet aus der Liste und sein Profil wird ausgeschaltet. Über „Spiel hinzufügen“ kannst du es jederzeit wieder aufnehmen.", "Entfernen", "Abbrechen"))
            _b.RemoveGame(Sel);
    }

    private async void ApplyGfx_Click(object s, RoutedEventArgs e) { if (Sel is { } g) await Task.Run(() => _b.ApplyGameFiles(g, false)); }
    private async void RestoreGfx_Click(object s, RoutedEventArgs e) { if (Sel is { } g) await Task.Run(() => _b.RestoreGameFiles(g)); }
    private void CopyLaunch_Click(object s, RoutedEventArgs e) { if (Sel?.LaunchOptions is { } o) { Clipboard.SetText(o); Ui.Toast("Startoptionen kopiert – in Steam einfügen"); } }

    private async void Clean_Click(object s, RoutedEventArgs e) => await _b.CleanAsync(includeShader: false);
    private void CleanCancel_Click(object s, RoutedEventArgs e) => _b.CancelClean();
    private async void Shader_Click(object s, RoutedEventArgs e) => await _b.CleanAsync(includeShader: true, shaderOnly: true);
    private async void Ram_Click(object s, RoutedEventArgs e) => await _b.CleanRamAsync();
    private async void Dns_Click(object s, RoutedEventArgs e) { await _b.FlushDnsAsync(); Ui.Toast("DNS-Cache geleert"); }
    private async void Ping_Click(object s, RoutedEventArgs e) { PingText.Text = "Messe …"; PingText.Text = await _b.PingAsync(); }
    private async void Restore_Click(object s, RoutedEventArgs e) { Ui.Toast("Erstelle Wiederherstellungspunkt …"); await _b.RestorePointAsync(); }
    private void TimerOn_Click(object s, RoutedEventArgs e) => _b.SetTimerResolution(true);
    private void TimerOff_Click(object s, RoutedEventArgs e) => _b.SetTimerResolution(false);
    private void RefreshApps_Click(object s, RoutedEventArgs e) => _b.RefreshApps();
    private async void CloseApps_Click(object s, RoutedEventArgs e) => await _b.CloseSelectedAppsAsync();
}
