using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

public partial class TilesPage : UserControl
{
    private static TilesPage? _instance;
    private readonly TilesService _t = Services.Tiles;
    private TileConfig? _sel;
    private bool _init = true;

    public TilesPage()
    {
        InitializeComponent();
        _instance = this;
        DataContext = _t;
        Catalog.ItemsSource = TilesService.Catalog;
        foreach (RadioButton r in StyleBox.Children) if ((string)r.Content == _t.Settings.Style) r.IsChecked = true;
        _init = false;
    }

    /// <summary>Aus dem Kachel-Rechtsklickmenü: diese Kachel zum Bearbeiten auswählen.</summary>
    public static void Select(TileConfig t) => _instance?.Dispatcher.BeginInvoke(() => _instance.TileList.SelectedItem = t);

    private void Style_Checked(object s, RoutedEventArgs e)
    {
        if (_init || s is not RadioButton { Content: string st }) return;
        _t.Settings.Style = st;
        _t.Rebuild();
    }

    private void Save_Click(object s, RoutedEventArgs e) => _t.Save();
    private void Rebuild_Click(object s, RoutedEventArgs e) => _t.Rebuild();
    private void Rebuild_Up(object s, System.Windows.Input.MouseButtonEventArgs e) => _t.Rebuild();
    private void Arrange_Click(object s, RoutedEventArgs e) => _t.Arrange();

    private void Add_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is TileType type)
        {
            var t = _t.Add(type);
            TileList.SelectedItem = t;
            Ui.Toast("Kachel hinzugefügt – auf dem Desktop an die gewünschte Stelle ziehen.");
        }
    }

    private void Tile_Selected(object s, SelectionChangedEventArgs e)
    {
        _sel = TileList.SelectedItem as TileConfig;
        Editor.Visibility = _sel == null ? Visibility.Collapsed : Visibility.Visible;
        if (_sel == null) return;
        ETitle.Text = _sel.Title;
        ESize.SelectedIndex = _sel.Size switch { TileSize.Wide => 1, TileSize.Large => 2, TileSize.Tall => 3, _ => 0 };
        ETarget.Text = _sel.Target;
        EColor.Text = _sel.Color ?? "";
        TargetRow.Visibility = _sel.Type is TileType.Launcher or TileType.Photos or TileType.Countdown ? Visibility.Visible : Visibility.Collapsed;
        TargetLabel.Text = _sel.Type switch { TileType.Photos => "Fotoordner", TileType.Countdown => "Datum", _ => "Programm/Ordner/URL" };
        ActionRow.Visibility = _sel.Type == TileType.Action ? Visibility.Visible : Visibility.Collapsed;
        EAction.SelectedItem = _sel.Target;
        MetricRow.Visibility = _sel.Type is TileType.System or TileType.Graph ? Visibility.Visible : Visibility.Collapsed;
        EMetrics.ItemsSource = Services.Monitor.Metrics.Values.Where(m => !m.Key.StartsWith("sensor:"))
            .Select(m => new OverlayItem { Key = m.Key, Label = m.Label, On = _sel.Metrics.Contains(m.Key) }).ToList();
    }

    private void Browse_Click(object s, RoutedEventArgs e)
    {
        if (_sel?.Type == TileType.Photos)
        {
            var d = new Microsoft.Win32.OpenFolderDialog();
            if (d.ShowDialog() == true) ETarget.Text = d.FolderName;
        }
        else
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Programme und Dateien|*.*" };
            if (d.ShowDialog() == true) ETarget.Text = d.FileName;
        }
    }

    private void EditSave_Click(object s, RoutedEventArgs e)
    {
        if (_sel == null) return;
        _sel.Title = ETitle.Text.Trim();
        _sel.Size = ESize.SelectedIndex switch { 1 => TileSize.Wide, 2 => TileSize.Large, 3 => TileSize.Tall, _ => TileSize.Small };
        _sel.Target = _sel.Type == TileType.Action ? EAction.SelectedItem as string ?? "Sperren" : ETarget.Text.Trim();
        _sel.Color = RgbF.TryParseHex(EColor.Text, out var c) ? c.Hex : null;
        if (_sel.Type is TileType.System or TileType.Graph)
            _sel.Metrics = ((IEnumerable<OverlayItem>)EMetrics.ItemsSource).Where(i => i.On).Select(i => i.Key).Take(_sel.Type == TileType.Graph ? 1 : 8).ToList();
        _t.Update(_sel);
        TileList.Items.Refresh();
        Ui.Toast("Kachel gespeichert");
    }

    private void EditRemove_Click(object s, RoutedEventArgs e)
    {
        if (_sel != null) { _t.Remove(_sel); _sel = null; Editor.Visibility = Visibility.Collapsed; }
    }
}
