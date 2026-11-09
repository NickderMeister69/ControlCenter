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
        if (!Dialogs.Confirm("Treiber herunterladen?", $"{r.Title} {r.Latest} wird in deinen Download-Ordner geladen (mehrere hundert MB) und danach der Installer gestartet. Während der Installation flackert kurz der Bildschirm.", "Herunterladen", "Abbrechen")) return;
        await Services.Drivers.DownloadAndInstallAsync(r);
    }

    private void Details_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is DriverRow r) Env.Open(r.Offer?.DetailsUrl is { Length: > 0 } u ? u : DriverCheck.AmdDriverPage);
    }

    private void Link_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string u) Env.Open(u); }
}
