using System.Windows;
using System.Windows.Input;

namespace NickControlCenter;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow() { InitializeComponent(); MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } }; }

    /// <summary>Dunkler Dialog. no == null → nur ein OK-Button.</summary>
    public static bool? Show(string title, string text, string yes, string? no)
    {
        var w = new ConfirmWindow();
        w.TitleText.Text = title;
        w.BodyText.Text = text;
        w.YesButton.Content = yes;
        if (no == null) w.NoButton.Visibility = Visibility.Collapsed; else w.NoButton.Content = no;
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
