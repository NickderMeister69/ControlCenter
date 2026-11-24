using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NickControlCenter;

/// <summary>
/// Prüft beim Start (und alle 2 Stunden), ob Steam-Spiele ein Update haben – auch wenn Steam zu ist – und fragt oben
/// in einem kleinen Fenster, ob es jetzt im Hintergrund geladen werden soll. Dann startet Steam unsichtbar, lädt das
/// Update, das Fenster zeigt Fortschritt/Mbit/s/Restzeit, und danach wird Steam (wenn die App es gestartet hat) wieder geschlossen.
/// </summary>
public sealed class GameUpdateWatcher
{
    public static GameUpdateWatcher Instance { get; } = new();

    private readonly System.Windows.Threading.DispatcherTimer _check = new();
    private readonly System.Windows.Threading.DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<int, SteamGameVM> _active = new();
    private readonly Dictionary<int, string> _restoreBehavior = new();
    private readonly Dictionary<int, long> _startBuild = new();
    private bool _steamStartedByUs;
    private DateTime _requested;
    private UpdatePopup? _popup;
    private readonly HashSet<string> _notified = new();

    private GameUpdateWatcher()
    {
        _check.Tick += async (_, _) => { _check.Interval = TimeSpan.FromMinutes(5); await CheckAsync(); };   // alle 5 Minuten
        _watch.Tick += (_, _) => Watch();
    }

    public void Start()
    {
        _check.Interval = TimeSpan.FromSeconds(60); // kurz nach dem Start – Netzwerk ist dann sicher da
        _check.Start();
    }

    public async Task CheckAsync(bool manual = false)
    {
        var st = AppSettings.Current;
        if (!st.SteamUpdateNotify && !manual) return;
        if (_active.Count > 0) return; // läuft gerade schon
        // nicht mitten ins Spiel platzen: Vollbild vorne → in 10 Minuten nochmal
        if (!manual && Native.IsFullscreen(Native.GetForegroundWindow())) return;   // beim nächsten Durchgang (5 Min) nochmal
        List<SteamGameState> games;
        try { games = await GameUpdates.CheckOnlineAsync(await Task.Run(GameUpdates.Scan)); }
        catch { return; }
        // nur echte Updates, die noch nicht laufen; bereits weggeklickte (gleiche Version) nicht nochmal melden
        var pending = games.Where(g => g.NeedsUpdate && !g.Updating && !st.SteamUpdateDismissed.Contains(Key(g))).ToList();
        if (pending.Count == 0) return;
        // dieselbe Version nicht alle 5 Minuten erneut melden – nur neue Updates
        if (!manual && pending.All(p => _notified.Contains(Key(p)))) return;
        foreach (var p in pending) _notified.Add(Key(p));
        Log.Info(LogModule.Boost, "Spiele-Updates gefunden: " + string.Join(", ", pending.Select(p => p.Name)));
        if (st.SteamUpdateAuto) { StartUpdates(pending); return; }

        var names = pending.Select(p => p.Name).ToList();
        string title = pending.Count == 1 ? $"Update für {names[0]} verfügbar" : $"{pending.Count} Spiele-Updates verfügbar";
        string text = pending.Count == 1
            ? (pending[0].Remaining > 0 ? $"{Fmt.Bytes(pending[0].Remaining)} · {InstallEstimate.DurationText(InstallEstimate.Duration(pending[0].Remaining, 30))}. " : "") + "Jetzt im Hintergrund laden, damit du nachher direkt loszocken kannst?"
            : string.Join(", ", names.Take(4)) + (names.Count > 4 ? " …" : "") + ". Jetzt im Hintergrund laden?";
        // Windows-Benachrichtigung (Klick = installieren) + Hinweis in der App
        (Application.Current as App)?.Balloon(title, "Klicken zum Installieren. " + text, () => { _popup?.Close(); StartUpdates(pending); });
        if (Application.Current.MainWindow is MainWindow mw && mw.IsVisible) mw.Toast(title + " – unten rechts „Jetzt laden“");
        ShowPopup(title, text, "Jetzt laden", () => StartUpdates(pending), "Später", () =>
        {
            foreach (var p in pending) if (!st.SteamUpdateDismissed.Contains(Key(p))) st.SteamUpdateDismissed.Add(Key(p));
            if (st.SteamUpdateDismissed.Count > 50) st.SteamUpdateDismissed.RemoveRange(0, st.SteamUpdateDismissed.Count - 50);
            st.Save();
        });
    }

    private static string Key(SteamGameState g) => $"{g.AppId}:{(g.LatestBuild > 0 ? g.LatestBuild : g.BuildId)}";

    public void StartUpdates(List<SteamGameState> games)
    {
        if (games.Count == 0) return;
        bool wasRunning = GameUpdates.SteamRunning;
        // „Nur beim Spielstart aktualisieren“ würde Steam nichts laden lassen → für dieses eine Update auf „immer aktuell“ (nur wenn Steam zu ist)
        if (!wasRunning) foreach (var g in games) SetBehavior(g, restore: false);
        if (!GameUpdates.StartSteamSilent()) { ShowPopup("Steam nicht gefunden", "Das Update kann nur über Steam geladen werden.", "OK", null, null, null); return; }
        _steamStartedByUs = !wasRunning;
        _requested = DateTime.Now;
        foreach (var g in games) { _active[g.AppId] = new SteamGameVM(g) { RequestedAt = DateTime.Now }; _startBuild[g.AppId] = g.BuildId; }
        Log.Info(LogModule.Boost, $"Spiele-Update gestartet ({string.Join(", ", games.Select(g => g.Name))}) – Steam {(wasRunning ? "lief schon" : "unsichtbar gestartet")}");
        ShowPopup(games.Count == 1 ? $"{games[0].Name} wird aktualisiert" : $"{games.Count} Spiele werden aktualisiert",
                  "Steam startet im Hintergrund …", "Ausblenden", null, null, null, progress: true);
        _watch.Start();
    }

    private void Watch()
    {
        List<SteamGameState> now;
        try { now = GameUpdates.Scan(); } catch { return; }
        foreach (var s in now) if (_active.TryGetValue(s.AppId, out var vm)) vm.Update(s with { LatestBuild = vm.S.LatestBuild });

        var running = _active.Values.Where(v => v.S.Updating).ToList();
        var waiting = _active.Values.Where(v => !v.S.Updating && StillNeeds(v)).ToList();
        var done = _active.Values.Where(v => !v.S.Updating && !StillNeeds(v)).ToList();

        if (running.Count > 0)
        {
            var g = running[0];
            _popup?.SetProgress(g.S.Progress, $"{g.Name}: {g.StateText}" + (running.Count + waiting.Count > 1 ? $"  (+{running.Count + waiting.Count - 1} weitere)" : ""));
        }
        else if (waiting.Count > 0 && (DateTime.Now - _requested).TotalMinutes > 4)
        {
            // Steam fängt nicht von selbst an (z. B. Download-Zeitplan oder Spiel steht auf „nur beim Starten“)
            _watch.Stop();
            var ids = waiting.Select(w => w.S.AppId).ToList();
            ShowPopup("Steam lädt das Update nicht von selbst", string.Join(", ", waiting.Select(w => w.Name)) + " – in Steam auf „Aktualisieren“ klicken.",
                      "In Steam öffnen", () => GameUpdates.OpenGame(ids[0]), "Schließen", null);
            Finish(closeSteam: false);
            return;
        }
        else if (waiting.Count > 0) _popup?.SetProgress(-1, "Steam prüft das Update …");

        if (running.Count == 0 && waiting.Count == 0 && done.Count == _active.Count && (DateTime.Now - _requested).TotalSeconds > 20)
        {
            _watch.Stop();
            var names = string.Join(", ", done.Select(d => d.Name));
            Log.Ok(LogModule.Boost, "Spiele-Updates fertig: " + names);
            ShowPopup("Fertig – viel Spaß beim Zocken! 🎮", $"{names} ist aktuell.", "OK", null, null, null);
            Finish(closeSteam: AppSettings.Current.SteamCloseAfterUpdate);
        }
    }

    /// <summary>Update noch nötig? Laut Steam-Datei, oder – wenn das Update nur online erkannt wurde – solange die alte Version installiert ist.</summary>
    private bool StillNeeds(SteamGameVM v) =>
        v.S.NeedsUpdate || (v.S.LatestBuild > 0 && v.S.BuildId != v.S.LatestBuild && _startBuild.TryGetValue(v.S.AppId, out var b) && v.S.BuildId == b);

    private void Finish(bool closeSteam)
    {
        var restore = _restoreBehavior.ToList();
        bool started = _steamStartedByUs;
        _active.Clear(); _startBuild.Clear(); _steamStartedByUs = false;
        _ = Task.Run(async () =>
        {
            // läuft inzwischen ein Spiel (du hast schon losgelegt)? Dann Steam natürlich offen lassen
            bool gameRunning = false;
            try { gameRunning = await Application.Current.Dispatcher.InvokeAsync(() => Services.Boost.Games.Any(g => g.Running)); } catch { }
            if (closeSteam && started && !gameRunning)
            {
                await Task.Delay(5000);
                Env.Open("steam://exit");
                for (int i = 0; i < 40 && GameUpdates.SteamRunning; i++) await Task.Delay(500);
                Log.Info(LogModule.Boost, "Steam nach dem Update wieder geschlossen");
            }
            if (!GameUpdates.SteamRunning)
                foreach (var (appId, val) in restore) RestoreBehavior(appId, val);
        });
        _restoreBehavior.Clear();
    }

    // -------- AutoUpdateBehavior im appmanifest (0 = immer aktuell, 1 = nur beim Start, 2 = hohe Priorität)
    private static string? ManifestOf(int appId) =>
        GameUpdates.Libraries().Select(l => Path.Combine(l, "steamapps", $"appmanifest_{appId}.acf")).FirstOrDefault(File.Exists);

    private void SetBehavior(SteamGameState g, bool restore)
    {
        try
        {
            var f = ManifestOf(g.AppId); if (f == null) return;
            var txt = File.ReadAllText(f);
            var m = Regex.Match(txt, "\"AutoUpdateBehavior\"\\s+\"(\\d+)\"");
            if (!m.Success || m.Groups[1].Value != "1") return;
            _restoreBehavior[g.AppId] = "1";
            File.WriteAllText(f, txt.Remove(m.Groups[1].Index, m.Groups[1].Length).Insert(m.Groups[1].Index, "0"));
            Log.Info(LogModule.Boost, $"{g.Name}: für dieses Update kurz auf „immer aktuell halten“ gestellt");
        }
        catch { }
    }

    private static void RestoreBehavior(int appId, string val)
    {
        try
        {
            var f = ManifestOf(appId); if (f == null) return;
            var txt = File.ReadAllText(f);
            var m = Regex.Match(txt, "\"AutoUpdateBehavior\"\\s+\"(\\d+)\"");
            if (m.Success) File.WriteAllText(f, txt.Remove(m.Groups[1].Index, m.Groups[1].Length).Insert(m.Groups[1].Index, val));
        }
        catch { }
    }

    private void ShowPopup(string title, string text, string ok, Action? onOk, string? cancel, Action? onCancel, bool progress = false)
    {
        _popup?.Close();
        _popup = new UpdatePopup(title, text, ok, onOk, cancel, onCancel, progress);
        _popup.Show();
    }
}

/// <summary>Kleines Hinweisfenster oben in der Mitte (wie eine Benachrichtigung), stiehlt Spielen nicht den Fokus.</summary>
public sealed class UpdatePopup : Window
{
    private readonly TextBlock _text;
    private readonly ProgressBar _bar;

    public UpdatePopup(string title, string text, string ok, Action? onOk, string? cancel, Action? onCancel, bool progress)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        _text = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(200, 206, 215)), FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 4, 0, 0) };
        _bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 10, 0, 0), Maximum = 100, IsIndeterminate = true, Visibility = progress ? Visibility.Visible : Visibility.Collapsed };
        var okBtn = new Button { Content = ok, Padding = new Thickness(14, 5, 14, 5), Style = (Style)Application.Current.FindResource("AccentButton") };
        okBtn.Click += (_, _) => { Close(); onOk?.Invoke(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        if (cancel != null)
        {
            var c = new Button { Content = cancel, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            c.Click += (_, _) => { Close(); onCancel?.Invoke(); };
            buttons.Children.Add(c);
        }
        buttons.Children.Add(okBtn);
        var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 14), MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = "CONTROL CENTER · STEAM", FontSize = 10, Foreground = accent, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        panel.Children.Add(_text);
        panel.Children.Add(_bar);
        panel.Children.Add(buttons);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 16, 20, 27)), BorderBrush = accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = panel
        };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2; Top = wa.Top + 16;
            System.Media.SystemSounds.Asterisk.Play();
        };
    }

    /// <summary>Fortschritt anzeigen (-1 = unbestimmt).</summary>
    public void SetProgress(double percent, string text)
    {
        _text.Text = text;
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = percent < 0;
        if (percent >= 0) _bar.Value = percent;
    }
}
