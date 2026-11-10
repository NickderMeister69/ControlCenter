using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class StartupPage : UserControl, IPage
{
    private List<StartupItem> _items = new();

    public StartupPage() { InitializeComponent(); }

    public void OnVisibleChanged(bool visible) { if (visible) Reload(); }

    private async void Reload()
    {
        Summary.Text = "Lese Autostart …";
        _items = await Task.Run(StartupManager.Load);
        Items.ItemsSource = _items;
        foreach (var i in _items) i.PropertyChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int on = _items.Count(i => i.Enabled), canOff = _items.Count(i => i.Enabled && i.Verdict == 2);
        Summary.Text = $"{on} von {_items.Count} Programmen starten mit Windows" + (canOff > 0 ? $" – {canOff} davon können aus" : " ✔");
    }

    private void Reload_Click(object s, RoutedEventArgs e) => Reload();

    private void Folder_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is StartupItem { ExePath: { } p } && System.IO.File.Exists(p)) Env.ShowInExplorer(p);
        else Ui.Toast("Datei nicht gefunden", true);
    }
}
