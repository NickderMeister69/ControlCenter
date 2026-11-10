using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NickControlCenter;

public partial class PasswordPage : UserControl, IPage
{
    private static readonly Vault V = new(Path.Combine(JsonStore.Folder, "passwort-tresor.vault"));
    private static readonly DispatcherTimer AutoLock = new() { Interval = TimeSpan.FromSeconds(5) };
    private static PasswordPage? _current;
    private VaultEntry? _sel;
    private bool _syncPw;

    static PasswordPage()
    {
        AutoLock.Tick += (_, _) =>
        {
            if (!V.Unlocked) return;
            bool hidden = Application.Current?.MainWindow is { IsVisible: false } or { WindowState: WindowState.Minimized };
            if (hidden || AutoShutdown.IdleTime() > TimeSpan.FromMinutes(5))
            {
                V.Lock();
                Log.Info(LogModule.App, "Passwort-Tresor automatisch gesperrt");
                _current?.ShowState();
            }
        };
        AutoLock.Start();
    }

    public PasswordPage() { InitializeComponent(); _current = this; }

    public void OnVisibleChanged(bool visible) { if (visible) ShowState(); }

    private void ShowState()
    {
        SetupPanel.Visibility = !V.Exists ? Visibility.Visible : Visibility.Collapsed;
        LockPanel.Visibility = V.Exists && !V.Unlocked ? Visibility.Visible : Visibility.Collapsed;
        OpenPanel.Visibility = V.Unlocked ? Visibility.Visible : Visibility.Collapsed;
        if (V.Unlocked) Refresh(); else { _sel = null; UnlockPw.Clear(); Dispatcher.BeginInvoke(() => UnlockPw.Focus(), DispatcherPriority.Input); }
    }

    // ---------------- Anlegen / Entsperren
    private void NewPw_Changed(object s, RoutedEventArgs e)
    {
        var (score, text) = Vault.Strength(NewPw1.Password);
        NewPwStrength.Text = text.Length == 0 ? "" : "Stärke: " + text;
        NewPwStrength.Foreground = score >= 3 ? Palette.Good : score == 2 ? Palette.Warn : Palette.Bad;
    }

    private async void Create_Click(object s, RoutedEventArgs e)
    {
        if (NewPw1.Password.Length < 10) { Ui.Toast("Mindestens 10 Zeichen", true); return; }
        if (NewPw1.Password != NewPw2.Password) { Ui.Toast("Die Passwörter stimmen nicht überein", true); return; }
        var pw = NewPw1.Password;
        await Task.Run(() => V.Create(pw));
        NewPw1.Clear(); NewPw2.Clear();
        Log.Ok(LogModule.App, "Passwort-Tresor angelegt");
        ShowState();
    }

    private void UnlockPw_KeyDown(object s, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) Unlock_Click(s, e); }

    private async void Unlock_Click(object s, RoutedEventArgs e)
    {
        var pw = UnlockPw.Password;
        if (pw.Length == 0) return;
        UnlockBtn.IsEnabled = false; UnlockMsg.Text = "";
        bool ok;
        try { ok = await Task.Run(() => V.Unlock(pw)); }
        catch (Exception ex) { UnlockMsg.Text = "Tresor kann nicht gelesen werden: " + ex.Message; UnlockBtn.IsEnabled = true; return; }
        UnlockBtn.IsEnabled = true; UnlockPw.Clear();
        if (!ok) { UnlockMsg.Text = "Falsches Master-Passwort"; await Task.Delay(800); return; }
        ShowState();
    }

    private void Lock_Click(object s, RoutedEventArgs e) { V.Lock(); ShowState(); }

    private void ChangeMaster_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "Master-Passwort ändern", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize, Background = (System.Windows.Media.Brush)FindResource("PanelBrush")
        };
        var oldPw = new PasswordBox { Width = 300, Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
        var newPw = new PasswordBox { Width = 300, Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
        var newPw2 = new PasswordBox { Width = 300, Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
        var ok = new Button { Content = "Ändern", Padding = new Thickness(16, 6, 16, 6), Style = (Style)FindResource("AccentButton"), IsDefault = true };
        var panel = new StackPanel { Margin = new Thickness(20) };
        foreach (var (label, box) in new[] { ("Bisheriges Master-Passwort", oldPw), ("Neues Master-Passwort (mind. 10 Zeichen)", newPw), ("Neues nochmal", newPw2) })
        { panel.Children.Add(new TextBlock { Text = label, Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") }); panel.Children.Add(box); }
        panel.Children.Add(ok);
        dlg.Content = panel;
        ok.Click += async (_, _) =>
        {
            if (newPw.Password.Length < 10 || newPw.Password != newPw2.Password) { Ui.Toast("Neues Passwort zu kurz oder ungleich", true); return; }
            string o = oldPw.Password, n = newPw.Password;
            bool done = await Task.Run(() => V.ChangePassword(o, n));
            if (!done) { Ui.Toast("Bisheriges Passwort ist falsch", true); return; }
            Log.Ok(LogModule.App, "Master-Passwort des Tresors geändert");
            Ui.Toast("Master-Passwort geändert");
            dlg.Close();
        };
        dlg.ShowDialog();
    }

    // ---------------- Liste & Bearbeiten
    private void Refresh()
    {
        var q = Search.Text.Trim();
        var items = V.Entries.Where(x => q.Length == 0 || x.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || x.User.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        List.ItemsSource = items;
        if (_sel != null && items.Contains(_sel)) List.SelectedItem = _sel;
        Editor.Visibility = _sel != null ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = _sel == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_Changed(object s, TextChangedEventArgs e) => Refresh();

    private void List_Changed(object s, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not VaultEntry v) return;
        Load(v);
    }

    private void Load(VaultEntry v)
    {
        _sel = v;
        ETitle.Text = v.Title; EUser.Text = v.User; EUrl.Text = v.Url; ENotes.Text = v.Notes;
        SetPw(v.Password);
        EChanged.Text = $"geändert {v.Changed:dd.MM.yyyy HH:mm}";
        Editor.Visibility = Visibility.Visible; EmptyHint.Visibility = Visibility.Collapsed;
        if (EPwShown.Visibility == Visibility.Visible) Show_Click(this, new RoutedEventArgs()); // beim Wechseln wieder verbergen
    }

    private void SetPw(string pw) { _syncPw = true; EPwHidden.Password = pw; EPwShown.Text = pw; _syncPw = false; UpdateStrength(); }
    private string CurrentPw => EPwShown.Visibility == Visibility.Visible ? EPwShown.Text : EPwHidden.Password;
    private void EPw_Changed(object s, RoutedEventArgs e) { if (_syncPw) return; _syncPw = true; EPwShown.Text = EPwHidden.Password; _syncPw = false; UpdateStrength(); }
    private void EPwShown_Changed(object s, TextChangedEventArgs e) { if (_syncPw) return; _syncPw = true; EPwHidden.Password = EPwShown.Text; _syncPw = false; UpdateStrength(); }

    private void UpdateStrength()
    {
        var (score, text) = Vault.Strength(CurrentPw);
        EPwStrength.Text = text.Length == 0 ? "" : "Stärke: " + text;
        EPwStrength.Foreground = score >= 3 ? Palette.Good : score == 2 ? Palette.Warn : Palette.Bad;
    }

    private void Show_Click(object s, RoutedEventArgs e)
    {
        bool show = EPwShown.Visibility != Visibility.Visible;
        EPwShown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        EPwHidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ShowBtn.Content = show ? "Verbergen" : "Zeigen";
    }

    private void Generate_Click(object s, RoutedEventArgs e) => SetPw(Vault.Generate((int)GenLen.Value, GenSym.IsChecked == true));

    private void New_Click(object s, RoutedEventArgs e)
    {
        var v = new VaultEntry { Title = "Neuer Eintrag", Password = Vault.Generate(20, true) };
        V.Entries.Add(v);
        _sel = v; Search.Text = "";
        Refresh(); Load(v);
        ETitle.Focus(); ETitle.SelectAll();
    }

    private async void Save_Click(object s, RoutedEventArgs e)
    {
        if (_sel == null) return;
        _sel.Title = ETitle.Text.Trim().Length > 0 ? ETitle.Text.Trim() : "Ohne Namen";
        _sel.User = EUser.Text.Trim(); _sel.Password = CurrentPw; _sel.Url = EUrl.Text.Trim(); _sel.Notes = ENotes.Text;
        _sel.Changed = DateTime.Now;
        try { await Task.Run(V.Save); Ui.Toast("Gespeichert (verschlüsselt)"); }
        catch (Exception ex) { Ui.Toast("Speichern fehlgeschlagen: " + ex.Message, true); }
        Refresh(); EChanged.Text = $"geändert {_sel.Changed:dd.MM.yyyy HH:mm}";
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        if (_sel == null) return;
        if (!Dialogs.Confirm("Eintrag löschen?", $"„{_sel.Title}“ wird endgültig aus dem Tresor gelöscht.", "Löschen", "Abbrechen")) return;
        V.Entries.Remove(_sel); _sel = null;
        await Task.Run(V.Save);
        Refresh();
    }

    // ---------------- Zwischenablage (Passwort nach 30 s wieder löschen)
    private static DispatcherTimer? _clipTimer;
    private static void CopySecret(string text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { return; }
        Ui.Toast($"{what} kopiert – wird in 30 s aus der Zwischenablage gelöscht");
        _clipTimer?.Stop();
        _clipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clipTimer.Tick += (_, _) =>
        {
            _clipTimer!.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); } catch { }
        };
        _clipTimer.Start();
    }

    private void CopyPw_Click(object s, RoutedEventArgs e) => CopySecret(CurrentPw, "Passwort");
    private void CopyUser_Click(object s, RoutedEventArgs e) { try { Clipboard.SetText(EUser.Text); Ui.Toast("Benutzername kopiert"); } catch { } }

    private void OpenUrl_Click(object s, RoutedEventArgs e)
    {
        var u = EUrl.Text.Trim();
        if (u.Length == 0) return;
        if (!u.Contains("://")) u = "https://" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")) Env.Open(uri.ToString());
    }
}
