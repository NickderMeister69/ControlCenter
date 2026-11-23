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
        foreach (var m in ArkWeb.Maps) MapBox.Items.Add(m.Name);
        MapBox.SelectedItem = ArkWeb.Maps.Any(m => m.Name == st.Map) ? st.Map : ArkData.MapName;
        ApplyMapUi();
        Loaded += async (_, _) => { if (!_mapInit) { _mapInit = true; if (!await ArkWeb.InitAsync(MapView, st.CurrentMapUrl)) Ui.Toast("Eingebetteter Browser (WebView2) fehlt – Karte im Browser öffnen", true); } };
        Unloaded += (_, _) => { _cts?.Cancel(); ArkWeb.Save(); ArkWeb.Changed -= OnOverlayChanged; };
        ArkWeb.Changed += OnOverlayChanged;
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
        TabOv.Visibility = tag == "Ov" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Ov") LoadOv();
        if (tag == "Wiki" && !_wikiInit) { _wikiInit = true; await ArkWeb.InitAsync(WikiView, ArkWeb.Settings.CurrentWikiUrl); }
        if (tag == "Ai" && !_aiInit) { _aiInit = true; LoadShare(); await RefreshAi(); }
    }

    private void OpenTab(string tag)
    {
        foreach (var r in ((Panel)TabMap.Parent).Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<RadioButton>()))
            if ((string)r.Tag == tag) r.IsChecked = true;
    }

    // ---------- Spiel-Anbindung ----------
    private void Overlay_Click(object sender, RoutedEventArgs e) { ArkOverlayWindow.Toggle(); UpdateOvButton(); }

    private void InGame_Click(object sender, RoutedEventArgs e)
    {
        ArkWeb.Settings.InGame = InGameBox.IsChecked == true;
        OverlayBtn.IsEnabled = ArkWeb.Settings.InGame;
        if (!ArkWeb.Settings.InGame) ArkOverlayWindow.HideIfOpen();
        ArkWeb.Save();
        Ui.Toast(ArkWeb.Settings.InGame ? "Spiel-Anbindung an – Strg+Alt+K zeigt die Karte im Spiel" : "Spiel-Anbindung aus – im Spiel erscheint nichts");
    }

    // ---------- Map-Auswahl ----------
    private void Map_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MapBox.SelectedItem is not string map || map == ArkWeb.Settings.Map) return;
        ArkWeb.Settings.Map = map; ArkWeb.Save();
        ApplyMapUi();
        MapView.CoreWebView2?.Navigate(ArkWeb.Settings.CurrentMapUrl);
        if (_wikiInit) WikiView.CoreWebView2?.Navigate(ArkWeb.Settings.CurrentWikiUrl);
        Ui.Toast($"Map: {map}" + (ArkOverlayWindow.IsOpen ? " – Karte im Spiel einmal aus- und einschalten" : ""));
    }

    private void ApplyMapUi()
    {
        var other = ArkWeb.Settings.Map != ArkData.MapName ? Visibility.Visible : Visibility.Collapsed;
        DataNote1.Visibility = DataNote2.Visibility = DataNote3.Visibility = other;
    }

    // ---------- Karte im Spiel: Einstellungen ----------
    private bool _ovLoading;

    private void OnOverlayChanged() => Dispatcher.BeginInvoke(() => { if (TabOv.Visibility == Visibility.Visible) LoadOv(); else UpdateOvButton(); });

    private void UpdateOvButton() => OvShowBtn.Content = ArkOverlayWindow.IsOpen ? "Karte ausblenden" : "Karte anzeigen";

    private void LoadOv()
    {
        var st = ArkWeb.Settings;
        _ovLoading = true;
        try
        {
            if (MonitorBox.Items.Count == 0)
            {
                MonitorBox.Items.Add("Hauptbildschirm");
                var screens = System.Windows.Forms.Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                    MonitorBox.Items.Add($"Bildschirm {i + 1}  ({screens[i].Bounds.Width}×{screens[i].Bounds.Height}{(screens[i].Primary ? ", Haupt" : "")})");
            }
            MonitorBox.SelectedIndex = Math.Clamp(st.MonitorIndex + 1, 0, MonitorBox.Items.Count - 1);
            OvW.Value = st.OverlayWidth; OvH.Value = st.OverlayHeight; OvM.Value = st.EdgeMargin;
            OvO.Value = st.OverlayOpacity * 100; OvZ.Value = st.MapZoom * 100;
            OvMarkers.IsChecked = st.ShowMarkers; OvAsk.IsChecked = st.ShowAsk; OvClick.IsChecked = st.ClickThroughOnOpen;
            OvFree.IsChecked = st.Corner == "Frei";
            OvAds.IsChecked = st.BlockAds;
            UpdateSpots(); UpdateOvVals(); UpdateOvButton();
        }
        finally { _ovLoading = false; }
    }

    private void UpdateSpots()
    {
        var corner = ArkWeb.Settings.Corner;
        foreach (var b in SpotGrid.Children.OfType<Button>())
            b.Background = (string)b.Tag == corner ? (System.Windows.Media.Brush)FindResource("AccentBrush") : (System.Windows.Media.Brush)FindResource("InputBrush");
        SpotText.Text = corner == "Frei" ? "Frei – da, wo du sie hingezogen hast" : "Position: " + corner;
        OvM.IsEnabled = MonitorBox.IsEnabled = corner != "Frei";
    }

    private void UpdateOvVals()
    {
        OvWVal.Text = $"{OvW.Value:0} px"; OvHVal.Text = $"{OvH.Value:0} px"; OvMVal.Text = $"{OvM.Value:0} px";
        OvOVal.Text = $"{OvO.Value:0} %"; OvZVal.Text = $"{OvZ.Value:0} %";
    }

    private void Ov_Changed(object sender, RoutedEventArgs e)
    {
        if (_ovLoading || OvW == null || OvZVal == null) return;
        var st = ArkWeb.Settings;
        st.OverlayWidth = Math.Round(OvW.Value); st.OverlayHeight = Math.Round(OvH.Value); st.EdgeMargin = Math.Round(OvM.Value);
        st.OverlayOpacity = Math.Round(OvO.Value) / 100; st.MapZoom = Math.Round(OvZ.Value / 10) / 10;
        st.MonitorIndex = MonitorBox.SelectedIndex - 1;
        st.ShowMarkers = OvMarkers.IsChecked == true; st.ShowAsk = OvAsk.IsChecked == true; st.ClickThroughOnOpen = OvClick.IsChecked == true;
        UpdateOvVals();
        ArkOverlayWindow.ApplyLayout();
        ArkWeb.Save();
    }

    private void Ads_Click(object sender, RoutedEventArgs e)
    {
        ArkWeb.Settings.BlockAds = OvAds.IsChecked == true; ArkWeb.Save();
        Ui.Toast("Gilt ab dem nächsten Öffnen der Seite bzw. nach Neustart der App");
    }

    private void Spot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string corner }) return;
        ArkWeb.Settings.Corner = corner; OvFree.IsChecked = false;
        UpdateSpots(); ArkOverlayWindow.ApplyLayout(); ArkWeb.Save();
    }

    private void OvFree_Click(object sender, RoutedEventArgs e)
    {
        ArkWeb.Settings.Corner = OvFree.IsChecked == true ? "Frei" : "Oben rechts";
        UpdateSpots(); ArkOverlayWindow.ApplyLayout(); ArkWeb.Save();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var st = ArkWeb.Settings;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "mini": st.Corner = "Oben rechts"; st.OverlayWidth = 320; st.OverlayHeight = 320; st.EdgeMargin = 16; st.OverlayOpacity = 0.8; st.MapZoom = 0.8; st.ShowMarkers = false; st.ShowAsk = false; st.ClickThroughOnOpen = true; break;
            case "side": st.Corner = "Mitte rechts"; st.OverlayWidth = 460; st.OverlayHeight = 900; st.EdgeMargin = 10; st.OverlayOpacity = 0.9; st.MapZoom = 1.0; st.ShowMarkers = true; st.ShowAsk = true; st.ClickThroughOnOpen = false; break;
            case "big": st.Corner = "Mitte"; st.OverlayWidth = 1100; st.OverlayHeight = 850; st.OverlayOpacity = 1.0; st.MapZoom = 1.0; st.ShowMarkers = true; st.ShowAsk = true; st.ClickThroughOnOpen = false; break;
            default: st.Corner = "Oben rechts"; st.OverlayWidth = 520; st.OverlayHeight = 620; st.EdgeMargin = 20; st.OverlayOpacity = 0.85; st.MapZoom = 1.0; st.MonitorIndex = -1; st.ShowMarkers = true; st.ShowAsk = true; st.ClickThroughOnOpen = false; break;
        }
        ArkWeb.Save(); LoadOv(); ArkOverlayWindow.ApplyLayout();
        Ui.Toast("Karten-Einstellung übernommen" + (ArkOverlayWindow.IsOpen ? "" : " – „Karte anzeigen“ zum Ansehen"));
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
        ArkWeb.Save(); ArkOverlayWindow.ApplyLayout();
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
        WikiSearch.Text = b.Name.Split(' ')[0];
        OpenTab("Wiki");
        WikiGo_Click(sender, e);
    }

    private void BossAsk_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArkBoss b) return;
        Question.Text = $"Was brauche ich für den Boss {b.Name.Split(' ')[0]} auf {ArkData.MapName} (Tribute je Schwierigkeit, empfohlene Dinos, Taktik)?";
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
    private void WikiHome_Click(object sender, RoutedEventArgs e) => WikiView.CoreWebView2?.Navigate(ArkWeb.Settings.CurrentWikiUrl);

    // ---------- KI ----------
    private async Task RefreshAi()
    {
        var st = ArkWeb.Settings;
        var friend = ArkAi.UsesFriend;
        AiLocal.IsChecked = !friend && st.AiMode != "friend";
        AiFriend.IsChecked = st.AiMode == "friend";
        FriendRow.Visibility = st.AiMode == "friend" ? Visibility.Visible : Visibility.Collapsed;
        if (st.AiMode == "friend" && !friend) { AiState.Text = "Einladungs-Code von deinem Freund einfügen und „Verbinden“."; SetAiButtons(null); return; }
        AiState.Text = friend ? $"Verbinde mit der KI von {st.FriendName} …" : "Prüfe Ollama …";
        var models = await ArkAi.ModelsAsync();
        if (models == null)
        {
            if (friend) AiState.Text = $"Die KI von {st.FriendName} ist gerade nicht erreichbar – sein PC oder das Control Center ist aus, oder ihr seid nicht im selben Netz/Tailscale.";
            else
            {
                var exe = ArkAi.OllamaExe();
                AiState.Text = exe != null ? "Ollama ist installiert, läuft aber gerade nicht." : "Ollama ist auf diesem PC nicht installiert – installieren oder die KI eines Freundes nutzen.";
                StartOllama.Visibility = exe != null ? Visibility.Visible : Visibility.Collapsed;
                GetOllama.Visibility = exe == null ? Visibility.Visible : Visibility.Collapsed;
            }
            SetAiButtons(null);
            return;
        }
        SetAiButtons(models);
        var pick = st.Model is { } m && models.Contains(m) ? m
                 : models.FirstOrDefault(x => x.StartsWith("qwen2.5")) ?? models.FirstOrDefault(x => !x.Contains("embed"));
        ModelBox.SelectedItem = pick;
        AiState.Text = models.Count == 0 ? (friend ? $"Bei {st.FriendName} ist noch kein Modell installiert." : "Ollama läuft – aber noch kein Modell installiert. Empfohlenes Modell laden?")
                                         : ArkAi.WhereText + $" · {models.Count} Modell(e)";
    }

    private void SetAiButtons(List<string>? models)
    {
        var friend = ArkWeb.Settings.AiMode == "friend";
        ModelBox.ItemsSource = models;
        PullBtn.IsEnabled = true;
        AskBtn.IsEnabled = models is { Count: > 0 };
        if (models != null || friend) { StartOllama.Visibility = GetOllama.Visibility = Visibility.Collapsed; }
        PullBtn.Visibility = !friend && models != null && !models.Any(x => x.StartsWith("qwen2.5:7b")) ? Visibility.Visible : Visibility.Collapsed;
        AiHint.Text = friend
            ? "Deine Frage wird an den PC deines Freundes geschickt und dort beantwortet – die Wiki-Suche macht deine App. Nichts geht an KI-Firmen. KI kann sich irren: wichtige Dinge über die Quellen prüfen."
            : "Läuft komplett auf diesem PC. Mit einer Grafikkarte ab ca. 8 GB Speicher kommen Antworten in wenigen Sekunden, ohne starke Grafikkarte dauert es deutlich länger. Für jede Frage sucht die App passende Seiten im ARK-Wiki und gibt sie der KI mit – die Quellen stehen unter der Antwort. KI kann sich irren: wichtige Dinge über die Quellen prüfen.";
    }

    private async void AiMode_Click(object sender, RoutedEventArgs e)
    {
        ArkWeb.Settings.AiMode = AiFriend.IsChecked == true ? "friend" : "local";
        ArkWeb.Settings.Model = null;
        ArkWeb.Save();
        await RefreshAi();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var inv = AiShare.ParseInvite(InviteBox.Text);
        if (inv == null) { Ui.Toast("Das ist kein gültiger Einladungs-Code (beginnt mit CCKI-)", true); return; }
        var st = ArkWeb.Settings;
        st.AiMode = "friend"; st.FriendName = string.IsNullOrWhiteSpace(inv.n) ? "deinem Freund" : inv.n; st.FriendUrl = inv.u; st.FriendToken = inv.t; st.Model = null;
        ArkWeb.Save();
        InviteBox.Text = "";
        await RefreshAi();
    }

    private void GetOllama_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://ollama.com/download") { UseShellExecute = true }); } catch { }
    }

    // ---------- Eigene KI freigeben ----------
    private void LoadShare()
    {
        var st = ArkWeb.Settings;
        ShareToggle.IsChecked = st.ShareEnabled;
        SharePanel.IsEnabled = st.ShareEnabled;
        SharePanel.Opacity = st.ShareEnabled ? 1 : 0.5;
        ShareNameBox.Text = AiShare.ShareName();
        var addrs = AiShare.Addresses();
        AddrBox.ItemsSource = addrs.Select(a => $"{a.Ip}  ({a.Kind})").ToList();
        AddrBox.SelectedIndex = addrs.Count > 0 ? 0 : -1;
        UpdateCode();
    }

    private void UpdateCode()
    {
        var st = ArkWeb.Settings;
        var ip = (AddrBox.SelectedItem as string)?.Split(' ')[0];
        CodeBox.Text = st.ShareEnabled && ip != null ? AiShare.InviteCode(ip) : "";
        var hasTs = AiShare.Addresses().Any(a => a.Kind == "Tailscale");
        ShareState.Text = !st.ShareEnabled ? "Aus."
            : !AiShare.Running ? "Konnte nicht starten: " + AiShare.LastError
            : ip == null ? "Keine Netzwerk-Adresse gefunden."
            : $"Aktiv · {AiShare.Requests} Fragen seit dem Start." + (hasTs ? " Code an Freunde schicken – sie brauchen auch Tailscale im selben Tailnet."
                : " Funktioniert so nur im selben WLAN/Heimnetz. Für Freunde woanders: Tailscale (kostenlos) auf beiden PCs installieren, dann erscheint hier eine Tailscale-Adresse.");
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        var st = ArkWeb.Settings;
        st.ShareEnabled = ShareToggle.IsChecked == true;
        if (st.ShareEnabled) { if (!AiShare.Start()) Ui.Toast("Freigabe konnte nicht starten: " + AiShare.LastError, true); }
        else AiShare.Stop();
        ArkWeb.Save();
        LoadShare();
    }

    private void ShareName_LostFocus(object sender, RoutedEventArgs e)
    {
        ArkWeb.Settings.ShareName = ShareNameBox.Text.Trim(); ArkWeb.Save(); UpdateCode();
    }

    private void Addr_Changed(object sender, SelectionChangedEventArgs e) => UpdateCode();

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (CodeBox.Text.Length == 0) return;
        try { Clipboard.SetText(CodeBox.Text); Ui.Toast("Einladungs-Code kopiert – an deinen Freund schicken"); } catch { }
    }

    private void NewKey_Click(object sender, RoutedEventArgs e)
    {
        AiShare.NewKey(); UpdateCode();
        Ui.Toast("Neuer Code – Freunde mit dem alten Code kommen nicht mehr rein");
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
