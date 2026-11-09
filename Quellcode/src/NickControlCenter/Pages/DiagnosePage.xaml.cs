using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class DiagnosePage : UserControl
{
    public DiagnosePage()
    {
        InitializeComponent();
        Panel.OnlyImportant = false;
        DataContext = DiagnosticsService.Instance;
        DiagnosticsService.Instance.Changed += Update;
        Loaded += (_, _) => Update();
    }

    private void Update()
    {
        var d = DiagnosticsService.Instance;
        int warn = d.Findings.Count(f => f.Severity >= Severity.Warning);
        int hints = d.Findings.Count - warn;
        Summary.Text = warn == 0 ? (hints == 0 ? "Keine Probleme gefunden" : $"Keine Probleme · {hints} Tipp(s)") : $"{warn} Problem(e) / Warnung(en) · {hints} Tipp(s)";
        AllGood.Visibility = d.Findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var dis = AppSettings.Current.DisabledServices.Keys.ToList();
        Disabled.ItemsSource = dis;
        DisabledCard.Visibility = dis.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await DiagnosticsService.Instance.ScanAsync(false);
    private void Reset_Click(object sender, RoutedEventArgs e) => DiagnosticsService.Instance.ResetIgnored();
    private void Log_Click(object sender, RoutedEventArgs e) => Env.Open(System.IO.Path.Combine(Log.Folder, "Problemloeser.log"));

    private async void Enable_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name)
        {
            var msg = await Task.Run(() => SystemActions.EnableService(name));
            Log.Info(LogModule.Diagnose, msg);
            Ui.Toast(msg);
            Update();
        }
    }
}
