using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    /// <summary>Wo die Karte im Spiel erscheint: Frei, Oben links, Oben rechts, Unten links, Unten rechts, Mitte links, Mitte rechts, Mitte.</summary>
    public string Corner { get; set; } = "Oben rechts";
    /// <summary>-1 = Hauptbildschirm, sonst Index des Bildschirms.</summary>
    public int MonitorIndex { get; set; } = -1;
    public double EdgeMargin { get; set; } = 20;
    public double MapZoom { get; set; } = 1.0;
    public bool ShowMarkers { get; set; } = true;
    public bool ShowAsk { get; set; } = true;
    public bool ClickThroughOnOpen { get; set; }
    public string Map { get; set; } = ArkData.MapName;
    /// <summary>Werbung auf Karte/Wiki ausblenden (falls eine Seite dadurch nicht lädt: ausschalten).</summary>
    public bool BlockAds { get; set; } = true;
    // KI: "local" = eigenes Ollama, "friend" = KI eines Freundes über Einladungs-Code
    public string AiMode { get; set; } = "local";
    public string? FriendName { get; set; }
    public string? FriendUrl { get; set; }
    public string? FriendToken { get; set; }
    // Eigene KI für Freunde freigeben
    public bool ShareEnabled { get; set; }
    public string? ShareName { get; set; }
    public string? ShareToken { get; set; }
    public int SharePort { get; set; } = 11435;
    public string MapUrl { get; set; } = ArkWeb.MapUrl;
    public string CurrentMapUrl => ArkWeb.Maps.FirstOrDefault(m => m.Name == Map).Map ?? ArkWeb.MapUrl;
    public string CurrentWikiUrl => ArkWeb.Maps.FirstOrDefault(m => m.Name == Map).Wiki ?? ArkWeb.WikiUrl;
}

public static class ArkWeb
{
    public const string MapUrl = "https://wikily.gg/ark-survival-ascended/maps/astraeos";
    public const string WikiUrl = "https://ark.wiki.gg/wiki/Astraeos";
    private const string W = "https://wikily.gg/ark-survival-ascended/maps/", K = "https://ark.wiki.gg/wiki/";
    public static readonly (string Name, string Map, string Wiki)[] Maps =
    {
        ("Astraeos", W + "astraeos", K + "Astraeos"),
        ("The Island", W + "the-island", K + "The_Island"),
        ("Scorched Earth", W + "scorched-earth", K + "Scorched_Earth"),
        ("The Center", W + "the-center", K + "The_Center"),
        ("Aberration", W + "aberration", K + "Aberration"),
        ("Extinction", W + "extinction", K + "Extinction"),
        ("Ragnarok", W + "ragnarok", K + "Ragnarok"),
        ("Valguero", W + "valguero", K + "Valguero"),
        ("Lost Colony", W + "lost-colony", K + "Lost_Colony"),
    };
    public static ArkSettings Settings { get; } = JsonStore.Load<ArkSettings>("ark.json");
    public static void Save() => JsonStore.Save("ark.json", Settings);
    /// <summary>Overlay wurde verschoben/vergrößert/ein-/ausgeblendet – Seite aktualisiert ihre Regler.</summary>
    public static event Action? Changed;
    public static void RaiseChanged() => Changed?.Invoke();

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

    /// <summary>Gehört die Adresse zur Karte/zum Wiki selbst (Videos von dort sind erlaubt)?</summary>
    private static bool IsOwn(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Host.EndsWith("wikily.gg") || u.Host.EndsWith("wiki.gg") || u.Scheme is "blob" or "data");

    /// <summary>Ist die Adresse ein bekannter Werbe-/Tracking-Server?</summary>
    public static bool IsAd(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        if (AdHosts.Any(h => host.Contains(h))) return true;
        var path = u.AbsolutePath.ToLowerInvariant();
        return host.StartsWith("ads.") || host.Contains("adserver") || path.Contains("/pagead/") || path.Contains("/adsbygoogle");
    }

    // Blendet Werbe-Kästen aus, die trotzdem übrig bleiben (z. B. „ADVERTISEMENT“-Rahmen mit Video unten links auf der Karte)
    private const string HideAdsJs = """
        (() => {
          // Nur eindeutige Werbe-Elemente – keine „enthält ad“-Regeln, die sonst auch die Karte treffen (z. B. „load-container“)
          const css = `ins.adsbygoogle, [id^="google_ads_iframe"], [id^="div-gpt-ad"] { display:none !important; }`;
          const own = /(^|\.)(wikily\.gg|wiki\.gg|cloudflare\.com|youtube\.com|youtube-nocookie\.com)$/i;
          const small = el => { const r = el.getBoundingClientRect(); return r.width * r.height < innerWidth * innerHeight * 0.45; };
          const hide = el => { if (el && small(el)) el.style.setProperty('display', 'none', 'important'); };
          // Umgebenden Kasten nur ausblenden, wenn er wie ein reiner Werbekasten aussieht (klein, kaum Text) –
          // sonst würden Menüs/Seitenleisten mit einem Werbeplatz darin komplett verschwinden
          const adBox = (el, inner) => {
            const box = floating(inner);
            if (!box) return inner;
            const r = box.getBoundingClientRect();
            const txt = (box.innerText || '').trim();
            const links = box.querySelectorAll('a,button,input').length;
            return (r.width * r.height < innerWidth * innerHeight * 0.15 && txt.length < 60 && links < 4) ? box : inner;
          };
          const floating = el => { let b = el, n = 0; while (b && n < 8) { const p = getComputedStyle(b).position; if (p === 'fixed' || p === 'sticky') return b; b = b.parentElement; n++; } return null; };
          const sweep = () => {
            if (document.head && !document.getElementById('ncc-noads')) { const s = document.createElement('style'); s.id = 'ncc-noads'; s.textContent = css; document.head.appendChild(s); }
            // fremde iframes (Werbebanner) ausblenden – die Karte selbst ist kein fremdes iframe
            for (const f of document.querySelectorAll('iframe')) {
              let host = ''; try { host = new URL(f.src, location.href).host; } catch {}
              if (host && own.test(host)) continue;
              hide(adBox(f, f));
            }
            // schwebende Video-Werbung (z. B. kleiner „Live“-Player unten links) – Karte selbst hat kein Video
            for (const v of document.querySelectorAll('video')) {
              let src = v.currentSrc || v.src || ''; let host = ''; try { host = src ? new URL(src, location.href).host : ''; } catch {}
              if (host && own.test(host)) continue;
              try { v.pause(); v.muted = true; } catch {}
              hide(adBox(v, v.parentElement || v));
            }
            // „ADVERTISEMENT“-Beschriftung → nur den kleinen schwebenden Kasten drumherum ausblenden
            for (const el of document.querySelectorAll('div,span,p,small')) {
              if (el.children.length > 0) continue;
              if (!/^(advertisement|anzeige|werbung)$/i.test((el.textContent || '').trim())) continue;
              hide(adBox(el, el.parentElement || el));
            }
          };
          document.addEventListener('DOMContentLoaded', sweep);
          setInterval(sweep, 1500);
        })();
        """;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebView2, string> AdScripts = new();

    /// <summary>Werbe-Ausblender je nach Einstellung ein-/ausschalten (wirkt ab dem nächsten Laden der Seite).</summary>
    public static async Task ApplyAdsAsync(WebView2 view)
    {
        if (view.CoreWebView2 == null) return;
        if (AdScripts.TryGetValue(view, out var id)) { view.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(id); AdScripts.Remove(view); }
        if (Settings.BlockAds) AdScripts.AddOrUpdate(view, await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HideAdsJs));
    }

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
                if (Settings.BlockAds && (IsAd(e.Request.Uri) || e.ResourceContext == CoreWebView2WebResourceContext.Media && !IsOwn(e.Request.Uri))) e.Response = view.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            await ApplyAdsAsync(view);
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
/// durchklickbar. Ein/aus: Strg+Alt+A, durchklickbar an/aus: Strg+Alt+J.
/// </summary>
public sealed class ArkOverlayWindow : Window
{
    private static ArkOverlayWindow? _instance;
    private readonly WebView2 _web = new();
    private readonly TextBox _ask = new() { Padding = new Thickness(6, 4, 6, 4), FontSize = 12.5, ToolTip = "Frage an die KI, Enter zum Senden" };
    private readonly FlowDocumentScrollViewer _answer = new() { MaxHeight = 220, IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, Margin = new Thickness(0, 4, 0, 0) };
    private void SetAnswer(string md) => _answer.Document = MiniMarkdown.Build(md, 12.5);
    private bool _clickThrough;
    private CancellationTokenSource? _cts;

    public static void Toggle()
    {
        if (!ArkWeb.Settings.InGame) { if (_instance is { IsVisible: true }) _instance.Hide(); else Osd.Show("ARK-Spielanbindung ist aus (Seite ARK)"); return; }
        if (_instance == null) { _instance = new ArkOverlayWindow(); _instance.Show(); _instance.OnOpened(); }
        else if (_instance.IsVisible) _instance.Hide();
        else { _instance.Show(); _instance.OnOpened(); }
    }

    public static bool IsOpen => _instance is { IsVisible: true };
    /// <summary>Einstellungen sofort auf die offene Karte anwenden.</summary>
    public static void ApplyLayout() { if (_instance is { IsVisible: true } w) w.Layout(); }

    public static readonly string[] Corners = { "Oben links", "Oben rechts", "Unten links", "Unten rechts", "Mitte links", "Mitte rechts", "Mitte", "Frei" };

    private void OnOpened()
    {
        Layout();
        if (ArkWeb.Settings.ClickThroughOnOpen != _clickThrough) SetClickThrough(ArkWeb.Settings.ClickThroughOnOpen);
    }

    private TextBlock? _markers;
    private StackPanel? _askPanel;
    private Slider? _opacity;
    private bool _layouting;
    private CheckBox? _ctBox;

    private void Layout()
    {
        var st = ArkWeb.Settings;
        _layouting = true;
        try
        {
            if (_markers != null) _markers.Text = st.Markers.Count == 0 ? "" : "📍 " + string.Join("   📍 ", st.Markers.Take(8).Select(m => $"{m.Name} {m.Coords}"));
            if (_markers != null) _markers.Visibility = st.ShowMarkers && _markers.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_askPanel != null) _askPanel.Visibility = st.ShowAsk ? Visibility.Visible : Visibility.Collapsed;
            if (_opacity != null) _opacity.Value = st.OverlayOpacity;
            try { _web.ZoomFactor = Math.Clamp(st.MapZoom, 0.25, 3); } catch { }
            Width = Math.Max(MinWidth, st.OverlayWidth); Height = Math.Max(MinHeight, st.OverlayHeight);
            ApplyAlpha();
            if (st.Corner == "Frei")
            {
                if (!double.IsNaN(st.OverlayLeft)) { Left = st.OverlayLeft; Top = st.OverlayTop; }
                return;
            }
            var screens = System.Windows.Forms.Screen.AllScreens;
            var scr = st.MonitorIndex >= 0 && st.MonitorIndex < screens.Length ? screens[st.MonitorIndex] : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
            var wa = scr.Bounds; // ganzer Bildschirm – im Spiel gibt es keine Taskleiste
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(Width * dpi.DpiScaleX), h = (int)Math.Round(Height * dpi.DpiScaleY), m = (int)Math.Round(st.EdgeMargin * dpi.DpiScaleX);
            int x = st.Corner.Contains("links") ? wa.Left + m : st.Corner.Contains("rechts") ? wa.Right - w - m : wa.Left + (wa.Width - w) / 2;
            int y = st.Corner.StartsWith("Oben") ? wa.Top + m : st.Corner.StartsWith("Unten") ? wa.Bottom - h - m : wa.Top + (wa.Height - h) / 2;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1), x, y, w, h, 0x0010); // HWND_TOPMOST, SWP_NOACTIVATE
        }
        finally { _layouting = false; }
    }

    public static void HideIfOpen() { if (_instance is { IsVisible: true } w) w.Hide(); }
    public static void ToggleClickThrough() { if (_instance is { IsVisible: true } w) w.SetClickThrough(!w._clickThrough); }

    private ArkOverlayWindow()
    {
        var st = ArkWeb.Settings;
        Title = "ARK-Karte"; WindowStyle = WindowStyle.None; Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(12, 15, 20));
        Width = st.OverlayWidth; Height = st.OverlayHeight; MinWidth = 220; MinHeight = 200;
        if (!double.IsNaN(st.OverlayLeft)) { Left = st.OverlayLeft; Top = st.OverlayTop; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var bar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(20, 24, 31)), Height = 30 };
        var close = new Button { Content = "✕", Width = 30, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, ToolTip = "Ausblenden (Strg+Alt+A)" };
        close.Click += (_, _) => Hide();
        DockPanel.SetDock(close, Dock.Right);
        var opacity = new Slider { Minimum = 0.3, Maximum = 1, Value = st.OverlayOpacity, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Durchsichtigkeit", Margin = new Thickness(0, 0, 8, 0) };
        opacity.ValueChanged += (_, e) => { if (_layouting) return; st.OverlayOpacity = e.NewValue; ApplyAlpha(); };
        _opacity = opacity;
        DockPanel.SetDock(opacity, Dock.Right);
        var ct = new CheckBox { Content = "durchklickbar", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 11.5, ToolTip = "Klicks gehen ans Spiel – zurück mit Strg+Alt+J" };
        ct.Click += (_, _) => SetClickThrough(ct.IsChecked == true);
        _ctBox = ct;
        DockPanel.SetDock(ct, Dock.Right);
        var title = new TextBlock { Text = $"ARK · {st.Map}  (Strg+Alt+A)", Foreground = accent, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        bar.Children.Add(close); bar.Children.Add(opacity); bar.Children.Add(ct); bar.Children.Add(title);
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            // selbst verschoben → ab jetzt „Frei“ an dieser Stelle
            st.Corner = "Frei"; st.OverlayLeft = Left; st.OverlayTop = Top; ArkWeb.Save(); ArkWeb.RaiseChanged();
        };
        SizeChanged += (_, _) => { if (_layouting || !IsLoaded) return; st.OverlayWidth = Width; st.OverlayHeight = Height; ArkWeb.RaiseChanged(); };

        var markers = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(200, 206, 215)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 0) };
        markers.Text = st.Markers.Count == 0 ? "" : "📍 " + string.Join("   📍 ", st.Markers.Take(8).Select(m => $"{m.Name} {m.Coords}"));
        _ask.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Ask(); };
        var askPanel = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        _markers = markers; _askPanel = askPanel;
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
        Loaded += async (_, _) => { Layout(); if (!await ArkWeb.InitAsync(_web, st.CurrentMapUrl)) SetAnswer("Eingebetteter Browser (WebView2) fehlt – bitte „Microsoft Edge WebView2 Runtime“ installieren."); };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { if (st.Corner == "Frei") { st.OverlayLeft = Left; st.OverlayTop = Top; } ArkWeb.Save(); } ArkWeb.RaiseChanged(); };
    }

    private async Task Ask()
    {
        var q = _ask.Text.Trim();
        if (q.Length == 0) return;
        _ask.Text = "";   // Feld leeren – gleich die nächste Frage tippen
        var model = ArkWeb.Settings.Model;
        if (string.IsNullOrEmpty(model)) { SetAnswer("Erst in der App unter ARK → KI-Assistent ein Modell wählen."); return; }
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        var sb = new System.Text.StringBuilder($"*„{q}“*\n\n");
        SetAnswer(sb.ToString());
        try
        {
            await ArkAi.AskAsync(model, q, t => Dispatcher.Invoke(() => { sb.Append(t); SetAnswer(sb.ToString()); }), s => Dispatcher.Invoke(() => SetAnswer(sb + "*" + s + "*")), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetAnswer("**KI nicht erreichbar:** " + ex.Message); }
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
        if (_ctBox != null) _ctBox.IsChecked = on;
        ApplyAlpha();
        Osd.Show(on ? "ARK-Karte: durchklickbar (Strg+Alt+J zum Bedienen)" : "ARK-Karte: bedienbar");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
}
