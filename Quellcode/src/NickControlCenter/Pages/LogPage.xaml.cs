using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class LogPage : UserControl, IPage
{
    private bool _visible;
    private volatile bool _dirty;
    private readonly System.Windows.Threading.DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };

    public LogPage()
    {
        InitializeComponent();
        Log.Written += _ => _dirty = true;
        _t.Tick += (_, _) => { if (_dirty && _visible && Live.IsChecked == true) { _dirty = false; Load(); } };
        _t.Start();
    }

    public void OnVisibleChanged(bool visible) { _visible = visible; if (visible) Load(); }

    private string Module => (FileBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Gesamt";

    private void Load()
    {
        var raw = Log.ReadTail(Module);
        var lines = raw.Split('\n').Where(l => l.Length > 0);
        int lvl = LevelBox.SelectedIndex;
        if (lvl == 1) lines = lines.Where(l => l.Contains("WARNUNG") || l.Contains("FEHLER"));
        if (lvl == 2) lines = lines.Where(l => l.Contains("FEHLER"));
        var q = Filter.Text.Trim();
        if (q.Length > 0) lines = lines.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase));
        Text.Text = string.Join("\n", lines.TakeLast(3000));
        Text.ScrollToEnd();
    }

    private void File_Changed(object s, SelectionChangedEventArgs e) { if (Text != null) Load(); }
    private void Filter_Changed(object s, TextChangedEventArgs e) { if (Text != null) Load(); }
    private void Copy_Click(object s, RoutedEventArgs e) { Clipboard.SetText(Text.Text); Ui.Toast("Protokoll kopiert"); }
    private void Folder_Click(object s, RoutedEventArgs e) => Env.Open(Log.Folder);
}
