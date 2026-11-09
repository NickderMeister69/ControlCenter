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
        _b.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(BoostService.ArkFound) or nameof(BoostService.SteamConnected)) Update(); };
        _b.Games.CollectionChanged += (_, _) => Update();
        _b.BackgroundApps.CollectionChanged += (_, _) => Update();
        Unloaded += (_, _) => _b.Save();
        Update();
    }

    public void OnVisibleChanged(bool visible)
    {
        if (!visible) { _b.Save(); return; }
        _b.RefreshActive();
        _b.RefreshApps();
    }

    private void Update()
    {
        var ark = _b.Games.Where(g => g.IsArk).ToList();
        ArkFound.Text = ark.Count == 0 ? "ARK nicht gefunden – unter „Spiele & Steam“ den Spielordner wählen." : "Gefunden: " + string.Join(", ", ark.Select(a => a.Ark!.Name));
        NoApps.Visibility = _b.BackgroundApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SteamBtn.Content = _b.SteamConnected ? "Spiele neu suchen" : "Mit Steam verbinden";
        SteamOffBtn.Visibility = _b.SteamConnected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (T1 == null || sender is not RadioButton { Tag: string t }) return;
        foreach (var p in new FrameworkElement[] { T1, T2, T3, T4, T5, T6 }) p.Visibility = p.Name == t ? Visibility.Visible : Visibility.Collapsed;
        _b.Save();
    }

    private async void Boost_Click(object s, RoutedEventArgs e) { _b.Save(); await _b.BoostAsync(); }

    private async void Undo_Click(object s, RoutedEventArgs e)
    {
        if (Dialogs.Confirm("Alles zurücksetzen?", "Alle Änderungen des FPS-Boosts (Registry, Energieplan, Spiel-Configs, Intro-Videos) werden auf den Originalzustand zurückgesetzt."))
            await _b.UndoAsync();
    }

    private void Steam_Click(object s, RoutedEventArgs e) => _b.ConnectSteam(true);
    private void SteamOff_Click(object s, RoutedEventArgs e) => _b.ConnectSteam(false);
    private void Play_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is GameVM g) { Log.Info(LogModule.Boost, "Starte " + g.Name); if (g.AppId > 0) Env.OpenAsUser($"steam://rungameid/{g.AppId}"); else if (g.Exe != null) Env.OpenAsUser(g.Exe); } }
    private void RemoveAuto_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string n) _b.RemoveAutoBoost(n); }

    private void AddExe_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programme (*.exe)|*.exe", Title = "Spiel-exe für Auto-Boost wählen" };
        if (dlg.ShowDialog() == true) _b.AddAutoBoostExe(dlg.FileName);
    }

    private void AddFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Spielordner wählen (z. B. ...\\ARK Survival Ascended)" };
        if (dlg.ShowDialog() == true) _b.AddGameFolder(dlg.FolderName);
    }

    private void CopyAsa_Click(object s, RoutedEventArgs e) { Clipboard.SetText(AsaArgs.Text); Ui.Toast("Startoptionen kopiert – in Steam einfügen"); }
    private void CopyAse_Click(object s, RoutedEventArgs e) { Clipboard.SetText(AseArgs.Text); Ui.Toast("Startoptionen kopiert – in Steam einfügen"); }
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
