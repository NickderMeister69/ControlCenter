using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;

namespace NickControlCenter;

public sealed class JoinConverter : IValueConverter
{
    public static readonly JoinConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is IEnumerable<string> e ? string.Join(", ", e) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public partial class ArkPage : UserControl
{
    private readonly ObservableCollection<ArkMarker> _markers;
    private bool _mapInit, _wikiInit, _aiInit, _busy;
    private CancellationTokenSource? _cts;

    public ArkPage()
    {
        InitializeComponent();
        var st = ArkWeb.Settings;
        _markers = new ObservableCollection<ArkMarker>(st.Markers);
        MarkerList.ItemsSource = _markers;
        CaveList.ItemsSource = ArkData.ArtifactCaves;
        CaveSource.Text = ArkData.SourcesText + " Koordinaten = Eingang (Breite / Länge).";
        BossList.ItemsSource = ArkData.Bosses;
        ResList.ItemsSource = ArkData.Resources;
        PlaceList.ItemsSource = ArkData.Places;
        InGameBox.IsChecked = st.InGame;
        OverlayBtn.IsEnabled = st.InGame;
        Loaded += async (_, _) => { if (!_mapInit) { _mapInit = true; if (!await ArkWeb.InitAsync(MapView, st.MapUrl)) Ui.Toast("Eingebetteter Browser (WebView2) fehlt – Karte im Browser öffnen", true); } };
        Unloaded += (_, _) => { _cts?.Cancel(); ArkWeb.Save(); };
    }

    // ---------- Tabs ----------
    private async void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || TabMap == null) return;
        TabMap.Visibility = tag == "Map" ? Visibility.Visible : Visibility.Collapsed;
        TabCaves.Visibility = tag == "Caves" ? Visibility.Visible : Visibility.Collapsed;
        TabBosses.Visibility = tag == "Bosses" ? Visibility.Visible : Visibility.Collapsed;
        TabRes.Visibility = tag == "Res" ? Visibility.Visible : Visibility.Collapsed;
        TabWiki.Visibility = tag == "Wiki" ? Visibility.Visible : Visibility.Collapsed;
        TabAi.Visibility = tag == "Ai" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Wiki" && !_wikiInit) { _wikiInit = true; await ArkWeb.InitAsync(WikiView, ArkWeb.WikiUrl); }
        if (tag == "Ai" && !_aiInit) { _aiInit = true; await RefreshAi(); }
    }

    private void OpenTab(string tag)
    {
        foreach (var r in ((Panel)TabMap.Parent).Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<RadioButton>()))
            if ((string)r.Tag == tag) r.IsChecked = true;
    }

    // ---------- Spiel-Anbindung ----------
    private void Overlay_Click(object sender, RoutedEventArgs e) => ArkOverlayWindow.Toggle();

    private void InGame_Click(object sender, RoutedEventArgs e)
    {
        ArkWeb.Settings.InGame = InGameBox.IsChecked == true;
        OverlayBtn.IsEnabled = ArkWeb.Settings.InGame;
        if (!ArkWeb.Settings.InGame) ArkOverlayWindow.HideIfOpen();
        ArkWeb.Save();
        Ui.Toast(ArkWeb.Settings.InGame ? "Spiel-Anbindung an – Strg+Alt+K zeigt die Karte im Spiel" : "Spiel-Anbindung aus – im Spiel erscheint nichts");
    }

    // ---------- Markierungen ----------
    private void AddMarker_Click(object sender, RoutedEventArgs e)
    {
        var name = MName.Text.Trim();
        var coords = MCoords.Text.Trim().Replace(',', '.');
        if (name.Length == 0 || coords.Length == 0) { Ui.Toast("Name und Koordinaten eingeben (z. B. 48.6 / 49.8)", true); return; }
        AddMarker(new ArkMarker { Name = name, Coords = coords, Note = MNote.Text.Trim() });
        MName.Text = MCoords.Text = MNote.Text = "";
    }

    private void AddMarker(ArkMarker m)
    {
        _markers.Add(m);
        ArkWeb.Settings.Markers = _markers.ToList();
        ArkWeb.Save();
    }

    private void DelMarker_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArkMarker m) return;
        _markers.Remove(m);
        ArkWeb.Settings.Markers = _markers.ToList();
        ArkWeb.Save();
    }

    private void CaveMark_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArkCave c) return;
        if (_markers.Any(m => m.Name == c.Name)) { Ui.Toast("Schon in deinen Markierungen"); return; }
        AddMarker(new ArkMarker { Name = c.Name, Coords = c.Coords, Note = c.Boss });
        try { Clipboard.SetText(c.Coords); } catch { }
        Ui.Toast($"„{c.Name}“ gemerkt (Koordinaten auch kopiert)");
    }

    // ---------- Bosse ----------
    private void BossWiki_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArkBoss b) return;
        WikiSearch.Text = b.Name.Split(' ')[0] + " Astraeos";
        OpenTab("Wiki");
        WikiGo_Click(sender, e);
    }

    private void BossAsk_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArkBoss b) return;
        Question.Text = $"Was brauche ich für den Boss {b.Name.Split(' ')[0]} auf Astraeos (Tribute je Schwierigkeit, empfohlene Dinos, Taktik)?";
        OpenTab("Ai");
        Ask_Click(sender, e);
    }

    // ---------- Wiki ----------
    private async void WikiGo_Click(object sender, RoutedEventArgs e)
    {
        var q = WikiSearch.Text.Trim();
        if (q.Length == 0) return;
        var url = "https://ark.wiki.gg/index.php?search=" + Uri.EscapeDataString(q);
        if (WikiView.CoreWebView2 == null) { _wikiInit = true; await ArkWeb.InitAsync(WikiView, url); }
        else WikiView.CoreWebView2.Navigate(url);
    }

    private void WikiSearch_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) WikiGo_Click(sender, e); }
    private void WikiHome_Click(object sender, RoutedEventArgs e) => WikiView.CoreWebView2?.Navigate(ArkWeb.WikiUrl);

    // ---------- KI ----------
    private async Task RefreshAi()
    {
        AiState.Text = "Prüfe Ollama …";
        var models = await ArkAi.ModelsAsync();
        if (models == null)
        {
            var exe = ArkAi.OllamaExe();
            AiState.Text = exe != null ? "Ollama ist installiert, läuft aber gerade nicht." : "Ollama wurde nicht gefunden – bitte installieren (ollama.com).";
            StartOllama.Visibility = exe != null ? Visibility.Visible : Visibility.Collapsed;
            ModelBox.ItemsSource = null;
            PullBtn.IsEnabled = AskBtn.IsEnabled = false;
            return;
        }
        StartOllama.Visibility = Visibility.Collapsed;
        PullBtn.IsEnabled = true;
        ModelBox.ItemsSource = models;
        var pick = ArkWeb.Settings.Model is { } m && models.Contains(m) ? m
                 : models.FirstOrDefault(x => x.StartsWith("qwen2.5")) ?? models.FirstOrDefault(x => !x.Contains("embed"));
        ModelBox.SelectedItem = pick;
        PullBtn.Visibility = models.Any(x => x.StartsWith("qwen2.5:7b")) ? Visibility.Collapsed : Visibility.Visible;
        AskBtn.IsEnabled = pick != null;
        AiState.Text = models.Count == 0 ? "Ollama läuft – aber noch kein Modell installiert. Empfohlenes Modell laden?"
                                         : $"Ollama läuft · {models.Count} Modell(e) installiert";
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is string m) { ArkWeb.Settings.Model = m; ArkWeb.Save(); AskBtn.IsEnabled = true; }
    }

    private async void StartOllama_Click(object sender, RoutedEventArgs e)
    {
        StartOllama.IsEnabled = false; AiState.Text = "Starte Ollama …";
        var ok = await ArkAi.EnsureRunningAsync();
        StartOllama.IsEnabled = true;
        if (!ok) Ui.Toast("Ollama ließ sich nicht starten", true);
        await RefreshAi();
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; PullBtn.IsEnabled = false; PullBar.Visibility = Visibility.Visible; PullBar.Value = 0;
        _cts = new CancellationTokenSource();
        var prog = new Progress<(double Percent, string Text)>(p => { PullBar.Value = p.Percent; AiState.Text = p.Text; });
        bool ok;
        try { ok = await ArkAi.PullAsync("qwen2.5:7b", prog, _cts.Token); }
        catch (Exception ex) { ok = false; Log.Warn(LogModule.App, "Modell laden: " + ex.Message); }
        _busy = false; PullBar.Visibility = Visibility.Collapsed;
        if (ok) { ArkWeb.Settings.Model = "qwen2.5:7b"; ArkWeb.Save(); Ui.Toast("Modell qwen2.5:7b ist bereit"); }
        else Ui.Toast("Modell konnte nicht geladen werden", true);
        await RefreshAi();
    }

    private void Question_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Ask_Click(sender, e); }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        var q = Question.Text.Trim();
        if (q.Length == 0) return;
        if (_busy) { _cts?.Cancel(); return; }
        if (!_aiInit) { _aiInit = true; await RefreshAi(); }
        if (ArkWeb.Settings.Model is not { } model || ModelBox.SelectedItem == null) { Ui.Toast("Erst Ollama starten und ein Modell wählen", true); return; }
        _busy = true; AskBtn.Content = "Stopp";
        Answer.Text = ""; Sources.Children.Clear();
        _cts = new CancellationTokenSource();
        var started = false;
        try
        {
            var src = await ArkAi.AskAsync(model, q,
                tok => Dispatcher.Invoke(() => { if (!started) { started = true; Answer.Text = ""; } Answer.AppendText(tok); Answer.ScrollToEnd(); }),
                status => Dispatcher.Invoke(() => { if (!started) Answer.Text = status; }),
                _cts.Token);
            if (src.Count > 0)
            {
                Sources.Children.Add(new TextBlock { Text = "Quellen: ", Foreground = Palette.Muted, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) });
                foreach (var s in src)
                {
                    var link = new Hyperlink(new Run(s.Title)) { NavigateUri = new Uri(s.Url) };
                    link.RequestNavigate += (_, a) => { try { Process.Start(new ProcessStartInfo(a.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { } };
                    Sources.Children.Add(new TextBlock(link) { FontSize = 12, Margin = new Thickness(0, 0, 12, 0) });
                }
            }
        }
        catch (OperationCanceledException) { Answer.AppendText("\n\n(abgebrochen)"); }
        catch (Exception ex)
        {
            Answer.Text = "Fehler: " + ex.Message + "\n\nLäuft Ollama? (Tab oben: „Ollama starten“)";
            Log.Warn(LogModule.App, "ARK-KI: " + ex.Message);
        }
        finally { _busy = false; AskBtn.Content = "Fragen"; }
    }
}
