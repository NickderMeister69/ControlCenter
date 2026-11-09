using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace NickControlCenter;

/// <summary>Ein Eintrag in einer sortierbaren Auswahlliste (an/aus + Reihenfolge).</summary>
public sealed class OrderItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    private bool _on;
    public bool On { get => _on; set { if (Set(ref _on, value)) Changed?.Invoke(); } }
    internal Action? Changed;
}

/// <summary>
/// Liste mit Häkchen und ▲▼ zum Sortieren: was oben steht, wird zuerst angezeigt.
/// Benutzt für Dashboard, Kacheln und Overlay.
/// </summary>
public partial class OrderList : UserControl
{
    public ObservableCollection<OrderItem> Items { get; private set; } = new();
    /// <summary>Etwas wurde an-/abgehakt oder verschoben.</summary>
    public event Action? Changed;

    public OrderList() { InitializeComponent(); }

    /// <summary>Gewählte Einträge in gespeicherter Reihenfolge zuerst, danach alle anderen.</summary>
    public void Load(IEnumerable<(string Key, string Label)> all, IEnumerable<string> selectedInOrder)
    {
        var allList = all.ToList();
        var sel = selectedInOrder.ToList();
        Items = new ObservableCollection<OrderItem>();
        foreach (var k in sel)
            if (allList.FirstOrDefault(a => a.Key == k) is { Key: not null } a) Items.Add(Make(a.Key, a.Label, true));
        foreach (var a in allList.Where(a => !sel.Contains(a.Key))) Items.Add(Make(a.Key, a.Label, false));
        List.ItemsSource = Items;
    }

    private OrderItem Make(string key, string label, bool on)
    {
        var i = new OrderItem { Key = key, Label = label };
        i.On = on;
        i.Changed = () => Changed?.Invoke();
        return i;
    }

    /// <summary>Angehakte Schlüssel in der eingestellten Reihenfolge.</summary>
    public List<string> Selected => Items.Where(i => i.On).Select(i => i.Key).ToList();

    private void Move(object sender, int d)
    {
        if ((sender as FrameworkElement)?.DataContext is not OrderItem it) return;
        int i = Items.IndexOf(it), j = i + d;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        Items.Move(i, j);
        Changed?.Invoke();
    }

    private void Up_Click(object s, RoutedEventArgs e) => Move(s, -1);
    private void Down_Click(object s, RoutedEventArgs e) => Move(s, +1);
}
