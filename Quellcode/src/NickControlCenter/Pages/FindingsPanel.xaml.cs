using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

/// <summary>Zeigt die Hinweise des Problemlösers – gefiltert nach Bereich – mit Buttons zum Beheben.</summary>
public partial class FindingsPanel : UserControl
{
    public static readonly DependencyProperty AreaProperty = DependencyProperty.Register(nameof(Area), typeof(Area), typeof(FindingsPanel),
        new PropertyMetadata(Area.All, (d, _) => ((FindingsPanel)d).Refresh()));
    public Area Area { get => (Area)GetValue(AreaProperty); set => SetValue(AreaProperty, value); }
    /// <summary>Nur Warnungen/Probleme anzeigen (für Bereichsseiten), Infos nur im Problemlöser.</summary>
    public bool OnlyImportant { get; set; } = true;
    public int MaxItems { get; set; } = 50;

    private readonly ObservableCollection<Finding> _items = new();

    public FindingsPanel()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        Loaded += (_, _) => { DiagnosticsService.Instance.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => DiagnosticsService.Instance.Changed -= Refresh;
    }

    private void Refresh()
    {
        var want = DiagnosticsService.Instance.Findings
            .Where(f => (Area == Area.All || (f.Areas & Area) != 0) && (!OnlyImportant || f.Severity >= Severity.Hint))
            .Take(MaxItems).ToList();
        if (want.SequenceEqual(_items)) return;
        _items.Clear();
        foreach (var f in want) _items.Add(f);
        Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FixAction a, DataContext: FixAction } b && FindFinding(b) is { } f)
            await DiagnosticsService.Instance.RunAction(f, a);
    }

    private static Finding? FindFinding(DependencyObject d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: Finding f }) return f;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Finding f) DiagnosticsService.Instance.Ignore(f);
    }
}
