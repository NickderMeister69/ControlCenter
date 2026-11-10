using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class PrivacyPage : UserControl, IPage
{
    private bool _init;
    private List<VpnTunnel> _tunnels = new();

    public PrivacyPage()
    {
        InitializeComponent();
        AdProvider.ItemsSource = AdBlocker.Providers;
        AdProvider.SelectedItem = AdBlocker.Current;
        AdProviderDesc.Text = AdBlocker.Current.Description;
        _init = true;
    }

    public void OnVisibleChanged(bool visible)
    {
        if (!visible) return;
        RefreshAd();
        RefreshVpn();
    }

    // ---------------- Werbeblocker
    private async void RefreshAd()
    {
        bool on = await Task.Run(AdBlocker.IsActive);
        AdToggle.IsChecked = on;
        AdState.Text = on ? $"An – {AdBlocker.Current.Name}" : "Aus";
        AdTest.Text = on ? "Teste …" : "";
        if (on)
        {
            var t = await AdBlocker.TestAsync();
            AdTest.Text = t == true ? "✔ Test: Werbe-Adresse (doubleclick.net) wird geblockt" : t == false ? "Test: Werbe-Adresse wird noch erreicht – evtl. „Sicheres DNS“ im Browser oder ein VPN umgeht den Filter" : "";
        }
    }

    private async void AdToggle_Click(object s, RoutedEventArgs e)
    {
        bool want = AdToggle.IsChecked == true;
        AdToggle.IsEnabled = false;
        try
        {
            var msg = await Task.Run(() => want ? AdBlocker.Enable() : AdBlocker.Disable());
            Ui.Toast(msg);
        }
        catch (Exception ex) { Ui.Toast("Fehler: " + ex.Message, true); }
        finally { AdToggle.IsEnabled = true; RefreshAd(); }
    }

    private async void AdProvider_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_init || AdProvider.SelectedItem is not DnsProvider p) return;
        AdBlocker.Settings.AdBlockProvider = p.Id; AdBlocker.Save();
        AdProviderDesc.Text = p.Description;
        if (AdToggle.IsChecked == true) { await Task.Run(AdBlocker.Enable); RefreshAd(); }
    }

    // ---------------- VPN
    private async void RefreshVpn()
    {
        WgInstall.Visibility = VpnManager.Installed ? Visibility.Collapsed : Visibility.Visible;
        ImportBtn.IsEnabled = VpnManager.Installed;
        _tunnels = await Task.Run(VpnManager.Load);
        Tunnels.ItemsSource = _tunnels;
        var on = _tunnels.FirstOrDefault(t => t.Connected);
        VpnState.Text = !VpnManager.Installed ? "WireGuard ist noch nicht installiert" :
                        _tunnels.Count == 0 ? "Noch keine VPN-Verbindung eingerichtet" :
                        on != null ? $"Verbunden über „{on.Name}“" : "Nicht verbunden";
        RefreshIp();
    }

    private async void RefreshIp()
    {
        IpText.Text = "Deine IP im Internet: wird ermittelt …";
        IpText.Text = "Deine IP im Internet: " + await VpnManager.PublicIpAsync();
    }

    private void Ip_Click(object s, RoutedEventArgs e) => RefreshIp();

    private async void WgInstall_Click(object s, RoutedEventArgs e)
    {
        WgInstall.IsEnabled = false; VpnBusy.Visibility = Visibility.Visible;
        VpnState.Text = $"Installiere WireGuard … ({InstallEstimate.DurationText(InstallEstimate.Duration(4 << 20, 30))})";
        bool ok = await VpnManager.InstallAsync();
        VpnBusy.Visibility = Visibility.Collapsed; WgInstall.IsEnabled = true;
        Ui.Toast(ok ? "WireGuard installiert" : "Installation fehlgeschlagen – siehe Protokoll", !ok);
        RefreshVpn();
    }

    private void Import_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "WireGuard-Konfiguration (*.conf)|*.conf", Title = "VPN-Konfiguration wählen" };
        if (d.ShowDialog() != true) return;
        try { VpnManager.Import(d.FileName); Ui.Toast("VPN-Konfiguration hinzugefügt"); }
        catch (Exception ex) { Ui.Toast(ex.Message, true); }
        RefreshVpn();
    }

    private async void Toggle_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not VpnTunnel t) return;
        t.Busy = true; VpnBusy.Visibility = Visibility.Visible;
        try
        {
            bool connect = !t.Connected;
            var msg = await Task.Run(() => connect ? VpnManager.Connect(t) : VpnManager.Disconnect(t));
            Ui.Toast(msg, msg.Contains("fehlgeschlagen"));
        }
        catch (Exception ex) { Ui.Toast("Fehler: " + ex.Message, true); }
        finally { t.Busy = false; VpnBusy.Visibility = Visibility.Collapsed; RefreshVpn(); }
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not VpnTunnel t) return;
        if (!Dialogs.Confirm("VPN entfernen?", $"Die Verbindung „{t.Name}“ wird getrennt und die Konfiguration gelöscht.", "Entfernen", "Abbrechen")) return;
        await Task.Run(() => VpnManager.Delete(t));
        RefreshVpn();
    }

    private void Link_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string u) Env.Open(u); }
}
