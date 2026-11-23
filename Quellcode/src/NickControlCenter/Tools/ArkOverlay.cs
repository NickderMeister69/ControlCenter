using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace NickControlCenter;

public sealed class ArkMarker
{
    public string Name { get; set; } = "";
    public string Coords { get; set; } = "";
    public string Note { get; set; } = "";
    public string Line => $"{Name}  ·  {Coords}" + (Note.Length > 0 ? $"  ·  {Note}" : "");
}

public sealed class ArkSettings
{
    public List<ArkMarker> Markers { get; set; } = new();
    public string? Model { get; set; }
    /// <summary>Spiel-Anbindung (Karte über dem Spiel + Tastenkürzel). Aus = nichts erscheint im Spiel.</summary>
    public bool InGame { get; set; } = true;
    public double OverlayOpacity { get; set; } = 0.85;
    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
    public double OverlayWidth { get; set; } = 520;
    public double OverlayHeight { get; set; } = 620;
    public string MapUrl { get; set; } = ArkWeb.MapUrl;
}

public static class ArkWeb
{
    public const string MapUrl = "https://wikily.gg/ark-survival-ascended/maps/astraeos";
    public const string WikiUrl = "https://ark.wiki.gg/wiki/Astraeos";
    public static ArkSettings Settings { get; } = JsonStore.Load<ArkSettings>("ark.json");
    public static void Save() => JsonStore.Save("ark.json", Settings);

    private static Task<CoreWebView2Environment>? _env;
    /// <summary>Gemeinsame WebView2-Umgebung (Browser-Teil von Edge, ist in Windows 10/11 enthalten).</summary>
    public static Task<CoreWebView2Environment> Env2() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NickControlCenter", "WebView2"));

    private static readonly string[] AdHosts =
    {
        "doubleclick.net", "googlesyndication.com", "googletagservices.com", "googleadservices.com", "adservice.google.", "pagead2.", "imasdk.googleapis.com",
        "amazon-adsystem.com", "adnxs.com", "pubmatic.com", "rubiconproject.com", "openx.net", "criteo.com", "criteo.net", "casalemedia.com", "indexww.com",
        "taboola.com", "outbrain.com", "teads.tv", "smartadserver.com", "adform.net", "33across.com", "sharethrough.com", "triplelift.com", "yieldmo.com",
        "media.net", "primis.tech", "aniview.com", "connatix.com", "playwire.com", "nitropay.com", "venatus.com", "venatusmedia.com", "adinplay.com",
        "ezoic.net", "ezojs.com", "freestar.io", "freestar.com", "snigelweb.com", "vidazoo.com", "adsrvr.org", "bidswitch.net", "sonobi.com", "gumgum.com",
        "kargo.com", "minutemedia", "htlbid.com", "ay.delivery", "assertiveyield", "adthrive.com", "raptive.com", "mediavine.com", "springserve.com",
        "spotxchange.com", "spotx.tv", "lijit.com", "sovrn.com", "contextweb.com", "rlcdn.com", "3lift.com", "adsafeprotected.com", "moatads.com",
        "doubleverify.com", "quantserve.com", "scorecardresearch.com", "id5-sync.com", "presage.io", "seedtag.com", "richaudience.com", "onetag-sys.com",
        "e-planning.net", "smilewanted.com", "adagio.io", "improvedigital.com", "yieldlab.net", "stroeer", "adition.com", "a-mo.net", "unrulymedia.com",
    };

    /// <summary>Ist die Adresse ein bekannter Werbe-/Tracking-Server?</summary>
    public static bool IsAd(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        if (AdHosts.Any(h => host.Contains(h))) return true;
        var path = u.AbsolutePath.ToLowerInvariant();
        return host.StartsWith("ads.") || host.StartsWith("ad.") || host.Contains("adserver") || path.Contains("/prebid") || path.Contains("/pagead/") || path.Contains("/adsbygoogle");
    }

    // Blendet Werbe-Kästen aus, die trotzdem übrig bleiben (z. B. „ADVERTISEMENT“-Rahmen mit Video unten links auf der Karte)
    private const string HideAdsJs = """
        (() => {
          const css = `ins.adsbygoogle, [id^="google_ads"], [id*="div-gpt-ad"], [class*="adsbygoogle"], [class*="ad-container"], [class*="adContainer"],
            [class*="ad-slot"], [class*="adslot"], [class*="ad-wrapper"], [class*="video-ad"], [class*="videoAd"], [class*="sticky-ad"], [class*="stickyAd"],
            [id*="sticky-ad"], [data-ad], [data-ad-slot], [data-adunit], iframe[src*="ads"], iframe[id*="google_ads"] { display:none !important; }`;
          const add = () => { if (document.head && !document.getElementById('ncc-noads')) { const s = document.createElement('style'); s.id = 'ncc-noads'; s.textContent = css; document.head.appendChild(s); } };
          const sweep = () => {
            add();
            for (const el of document.querySelectorAll('div,span,p,small,header,aside')) {
              if (el.children.length > 2) continue;
              const t = (el.textContent || '').trim();
              if (!/^(advertisement|anzeige|werbung|ad)$/i.test(t)) continue;
              let box = el.parentElement, hops = 0;
              while (box && hops < 6) {
                const pos = getComputedStyle(box).position;
                if (pos === 'fixed' || pos === 'sticky' || pos === 'absolute') break;
                box = box.parentElement; hops++;
              }
              (box && hops < 6 ? box : el.parentElement || el).style.setProperty('display', 'none', 'important');
            }
          };
          document.addEventListener('DOMContentLoaded', sweep);
          setInterval(sweep, 1500);
        })();
        """;

    public static async Task<bool> InitAsync(WebView2 view, string url)
    {
        try
        {
            await view.EnsureCoreWebView2Async(await Env2());
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (!IsAd(e.Uri)) view.CoreWebView2.Navigate(e.Uri); }; // Links im selben Fenster
            // Werbung blocken: Anfragen an Werbe-Server gar nicht erst laden + übrig gebliebene Werbe-Kästen ausblenden
            view.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            view.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                if (IsAd(e.Request.Uri)) e.Response = view.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HideAdsJs);
            view.CoreWebView2.Navigate(url);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogModule.App, "WebView2 (eingebetteter Browser) nicht verfügbar: " + ex.Message);
            return false;
        }
    }
}

/// <summary>
/// ARK-Karte als Fenster über dem Spiel (Spiel im Modus „Vollbild-Fenster“ / „Windowed Fullscreen“). Liest nichts aus dem
/// Spiel aus und schleust nichts ein – ist also für BattlEye nur ein normales Fenster. Durchsichtig, verschiebbar, auf Wunsch
/// durchklickbar. Ein/aus: Strg+Alt+K, durchklickbar an/aus: Strg+Alt+J.
/// </summary>
public sealed class ArkOverlayWindow : Window
{
    private static ArkOverlayWindow? _instance;
    private readonly WebView2 _web = new();
    private readonly TextBox _ask = new() { Padding = new Thickness(6, 4, 6, 4), FontSize = 12.5, ToolTip = "Frage an die KI, Enter zum Senden" };
    private readonly TextBox _answer = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12.5, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.White };
    private bool _clickThrough;
    private CancellationTokenSource? _cts;

    public static void Toggle()
    {
        if (!ArkWeb.Settings.InGame) { if (_instance is { IsVisible: true }) _instance.Hide(); else Osd.Show("ARK-Spielanbindung ist aus (Seite ARK)"); return; }
        if (_instance == null) { _instance = new ArkOverlayWindow(); _instance.Show(); }
        else if (_instance.IsVisible) _instance.Hide();
        else _instance.Show();
    }

    public static void HideIfOpen() { if (_instance is { IsVisible: true } w) w.Hide(); }
    public static void ToggleClickThrough() { if (_instance is { IsVisible: true } w) w.SetClickThrough(!w._clickThrough); }

    private ArkOverlayWindow()
    {
        var st = ArkWeb.Settings;
        Title = "ARK-Karte"; WindowStyle = WindowStyle.None; Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(12, 15, 20));
        Width = st.OverlayWidth; Height = st.OverlayHeight;
        if (!double.IsNaN(st.OverlayLeft)) { Left = st.OverlayLeft; Top = st.OverlayTop; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var bar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(20, 24, 31)), Height = 30 };
        var close = new Button { Content = "✕", Width = 30, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, ToolTip = "Ausblenden (Strg+Alt+K)" };
        close.Click += (_, _) => Hide();
        DockPanel.SetDock(close, Dock.Right);
        var opacity = new Slider { Minimum = 0.3, Maximum = 1, Value = st.OverlayOpacity, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Durchsichtigkeit", Margin = new Thickness(0, 0, 8, 0) };
        opacity.ValueChanged += (_, e) => { st.OverlayOpacity = e.NewValue; ApplyAlpha(); };
        DockPanel.SetDock(opacity, Dock.Right);
        var ct = new CheckBox { Content = "durchklickbar", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 11.5, ToolTip = "Klicks gehen ans Spiel – zurück mit Strg+Alt+J" };
        ct.Click += (_, _) => SetClickThrough(ct.IsChecked == true);
        DockPanel.SetDock(ct, Dock.Right);
        var title = new TextBlock { Text = $"ARK · {ArkData.MapName}  (Strg+Alt+K)", Foreground = accent, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        bar.Children.Add(close); bar.Children.Add(opacity); bar.Children.Add(ct); bar.Children.Add(title);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var markers = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(200, 206, 215)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 0) };
        markers.Text = st.Markers.Count == 0 ? "" : "📍 " + string.Join("   📍 ", st.Markers.Take(8).Select(m => $"{m.Name} {m.Coords}"));
        _ask.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Ask(); };
        var askPanel = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        askPanel.Children.Add(_ask); askPanel.Children.Add(_answer);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_web, 1); Grid.SetRow(markers, 2); Grid.SetRow(askPanel, 3);
        grid.Children.Add(bar); grid.Children.Add(_web); grid.Children.Add(markers); grid.Children.Add(askPanel);
        Content = new Border { BorderBrush = accent, BorderThickness = new Thickness(1), Child = grid };

        SourceInitialized += (_, _) => ApplyAlpha();
        Loaded += async (_, _) => { if (!await ArkWeb.InitAsync(_web, st.MapUrl)) _answer.Text = "Eingebetteter Browser (WebView2) fehlt – bitte „Microsoft Edge WebView2 Runtime“ installieren."; };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { st.OverlayLeft = Left; st.OverlayTop = Top; st.OverlayWidth = Width; st.OverlayHeight = Height; ArkWeb.Save(); } };
    }

    private async Task Ask()
    {
        var q = _ask.Text.Trim();
        if (q.Length == 0) return;
        var model = ArkWeb.Settings.Model;
        if (string.IsNullOrEmpty(model)) { _answer.Text = "Erst in der App unter ARK → KI-Assistent ein Modell wählen."; return; }
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        _answer.Text = "";
        try
        {
            await ArkAi.AskAsync(model, q, t => Dispatcher.Invoke(() => { _answer.AppendText(t); _answer.ScrollToEnd(); }), s => Dispatcher.Invoke(() => { if (_answer.Text.Length == 0) _answer.Text = s + "\n"; }), _cts.Token);
        }
        catch (Exception ex) { _answer.Text = "KI nicht erreichbar: " + ex.Message; }
    }

    private void ApplyAlpha()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(h, -20).ToInt64() | 0x80000; // WS_EX_LAYERED (für Durchsichtigkeit)
        if (_clickThrough) ex |= 0x20; else ex &= ~0x20L;
        SetWindowLongPtr(h, -20, new IntPtr(ex));
        SetLayeredWindowAttributes(h, 0, (byte)Math.Round(Math.Clamp(ArkWeb.Settings.OverlayOpacity, 0.3, 1) * 255), 2);
    }

    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        ApplyAlpha();
        Osd.Show(on ? "ARK-Karte: durchklickbar (Strg+Alt+J zum Bedienen)" : "ARK-Karte: bedienbar");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
}
