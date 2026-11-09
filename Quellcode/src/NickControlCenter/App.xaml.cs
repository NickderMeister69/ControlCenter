using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace NickControlCenter;

/// <summary>Alle Dienste der App (einmal vorhanden, von allen Seiten genutzt).</summary>
public static class Services
{
    public static FanController Fans { get; set; } = null!;
    public static BoostService Boost { get; set; } = null!;
    public static MonitorService Monitor { get; set; } = null!;
    public static StressService Stress { get; set; } = null!;
    public static TweakerService Tweaker { get; set; } = null!;
    public static CustomizeService Customize { get; set; } = null!;
    public static TilesService Tiles { get; set; } = null!;
    public static DriverService Drivers { get; set; } = null!;
    public static DiagnosticsService Diagnose => DiagnosticsService.Instance;
    public static HardwareHub Hub => HardwareHub.Instance;
}

public partial class App : Application
{
    private const string ShowEventName = "NickControlCenter_ShowWindow_Event";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _win;
    private WF.NotifyIcon? _tray;
    private WF.ToolStripMenuItem? _trayPause, _trayRgb, _trayTiles;
    private bool _exiting;

    public static App Instance => (App)Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase) || a.Equals("/tray", StringComparison.OrdinalIgnoreCase));

        // Nur eine Instanz: ein zweiter Start holt das Fenster nach vorne
        _mutex = new Mutex(true, "Global\\NickControlCenter_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            if (!startInTray) { try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { } }
            Shutdown();
            return;
        }

        Log.Init(Env.ExeDir);
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error(LogModule.App, "Schwerer Fehler", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(LogModule.App, "Hintergrund-Fehler", a.Exception); a.SetObserved(); };

        AppSettings.Load();
        var s = AppSettings.Current;
        AppSettings.ApplyAccent(s.Accent);
        Log.Info(LogModule.App, $"Start Nick Control Center {typeof(App).Assembly.GetName().Version} – Windows-Build {Env.WindowsBuild}, Admin: {Env.IsAdmin}, Tray-Start: {startInTray}, Ordner: {Env.ExeDir}");

        // Sensoren zuerst (Lüfter, Monitor, Kacheln, Stresstest hängen daran)
        Services.Hub.Preference = s.SensorSource;
        Services.Hub.IntervalMs = s.SensorIntervalMs;
        Services.Hub.Start();

        Services.Fans = new FanController();
        Services.Boost = new BoostService();
        Services.Monitor = new MonitorService();
        Services.Stress = new StressService();
        Services.Tweaker = new TweakerService();
        Services.Customize = new CustomizeService();
        Services.Tiles = new TilesService();
        Services.Drivers = new DriverService();

        Services.Fans.Rgb.DeviceProblem += (name, msg) => Services.Diagnose.Report(RgbProblem(name, msg));

        _win = new MainWindow();
        MainWindow = _win;
        _win.Closing += OnWindowClosing;

        BuildTray();
        Services.Fans.Notify += (title, text, warn) => _tray?.ShowBalloonTip(4000, title, text, warn ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (!_exiting)
            {
                try { if (_showEvent.WaitOne(1000)) Dispatcher.BeginInvoke(ShowWindow); }
                catch { break; }
            }
        }) { IsBackground = true, Name = "ShowEventListener" }.Start();

        SessionEnding += (_, _) => { Services.Fans?.RestoreAll("Abmelden/Herunterfahren"); Services.Boost?.OnExit(); };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Services.Fans.Start(manualStart: !startInTray);
        Services.Diagnose.Start();
        Services.Boost.Start();
        Services.Tweaker.ApplyOnStartup(startInTray);
        Services.Tiles.Start();
        _ = Services.Drivers.CheckAsync(silent: true);

        FirstRun();
        if (!startInTray) ShowWindow();
    }

    private static Finding RgbProblem(string device, string msg)
    {
        var f = new Finding
        {
            Id = "rgb:dev:" + device, Title = $"Beleuchtung: {device} reagiert nicht zuverlässig", Severity = Severity.Warning, Areas = Area.Rgb,
            Detail = $"Beim Senden der Farben kommt es wiederholt zu Fehlern ({msg}). Das führt zu Flackern oder falschen Farben.",
            Advice = "Meist greift ein zweites RGB-Programm auf den Controller zu. Unten bei den anderen Hinweisen schauen, ob ein RGB-Programm läuft. Sonst „Geräte neu suchen“."
        };
        f.Actions.Add(new FixAction { Label = "Geräte neu suchen", Icon = "", Primary = true, Run = () => { Services.Fans.Rgb.Rescan(); Services.Diagnose.Clear(f.Id); return Task.FromResult("Suche gestartet."); } });
        return f;
    }

    private void FirstRun()
    {
        var s = AppSettings.Current;
        if (!s.ShortcutsCreated || !Shortcuts.Exists)
        {
            if (Shortcuts.CreateAll(out _)) { s.ShortcutsCreated = true; s.Save(); }
        }
        if (!s.FirstRunDone)
        {
            if (s.Autostart) Autostart.Enable(out _);
            s.FirstRunDone = true;
            s.Save();
        }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs a)
    {
        Log.Error(LogModule.App, "Fehler in der Oberfläche", a.Exception);
        a.Handled = true;
        _win?.Toast("Ein Fehler ist aufgetreten – Details stehen im Protokoll (Logs\\Allgemein.log).", true);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => Services.Fans?.OnResume());
    }

    // ------------------------------------------------------------------ Tray

    private void BuildTray()
    {
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Env.ExePath) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }

        _tray = new WF.NotifyIcon { Icon = icon, Text = "Nick Control Center", Visible = true };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => ShowWindow());
        menu.Items.Add("Gaming-Boost starten", null, async (_, _) => await Services.Boost.BoostAsync());
        menu.Items.Add(new WF.ToolStripSeparator());
        _trayPause = new WF.ToolStripMenuItem("Lüfter pausieren (BIOS regelt)") { CheckOnClick = false };
        _trayPause.Click += (_, _) => Services.Fans.Paused = !Services.Fans.Paused;
        menu.Items.Add(_trayPause);
        _trayRgb = new WF.ToolStripMenuItem("Beleuchtung an");
        _trayRgb.Click += (_, _) => Services.Fans.Rgb.Enabled = !Services.Fans.Rgb.Enabled;
        menu.Items.Add(_trayRgb);
        _trayTiles = new WF.ToolStripMenuItem("Desktop-Kacheln anzeigen");
        _trayTiles.Click += (_, _) => Services.Tiles.Visible = !Services.Tiles.Visible;
        menu.Items.Add(_trayTiles);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Protokoll-Ordner öffnen", null, (_, _) => Env.Open(Log.Folder));
        menu.Items.Add("Beenden", null, (_, _) => ExitApp(ask: false));
        menu.Opening += (_, _) =>
        {
            _trayPause.Checked = Services.Fans.Paused;
            _trayRgb.Checked = Services.Fans.Rgb.Enabled;
            _trayTiles.Checked = Services.Tiles.Visible;
        };
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowWindow(); };

        Services.Fans.PropertyChanged += (_, e) =>
        {
            if (_tray == null || e.PropertyName is not (nameof(FanController.CpuTemp) or nameof(FanController.StateText))) return;
            var snap = Services.Hub.Latest;
            var text = "Nick Control Center";
            if (SensorSnapshot.Plausible(snap.CpuTemp)) text += $"\nCPU {snap.CpuTemp:0} °C";
            if (SensorSnapshot.Plausible(snap.GpuTemp)) text += $" · GPU {snap.GpuTemp:0} °C";
            text += $"\nLüfter: {Services.Fans.StateText}";
            _tray.Text = text.Length > 127 ? text[..127] : text;
        };
    }

    public void ShowWindow()
    {
        if (_win == null) return;
        _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
        _win.Topmost = true;
        _win.Topmost = false;
        _win.Focus();
    }

    public void Balloon(string title, string text, bool warn = false) =>
        _tray?.ShowBalloonTip(4000, title, text, warn ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (!AppSettings.Current.CloseToTray) { e.Cancel = true; ExitApp(ask: false); return; }
        // X = im Hintergrund weiterlaufen (Lüfter/RGB/Kacheln bleiben aktiv), Symbol unten rechts
        e.Cancel = true;
        _win!.Hide();
        if (!AppSettings.Current.TrayHintShown)
        {
            AppSettings.Current.TrayHintShown = true;
            AppSettings.Current.Save();
            Balloon("Läuft im Hintergrund weiter", "Lüfter, Beleuchtung und Kacheln bleiben aktiv. Ganz beenden: Button „Beenden“ unten links oder Rechtsklick auf dieses Symbol → Beenden.");
        }
    }

    /// <summary>App komplett beenden: Lüfter ans BIOS, Boost-Timer zurück, Kacheln schließen.</summary>
    public void ExitApp(bool ask)
    {
        if (_exiting) return;
        if (ask && !Dialogs.Confirm("Nick Control Center beenden?",
                "Die App wird komplett geschlossen. Die Lüfter gehen zurück an das BIOS, die Beleuchtung behält die letzte Farbe, Desktop-Kacheln werden ausgeblendet.",
                "Beenden", "Abbrechen")) return;
        _exiting = true;
        Log.Info(LogModule.App, "Beenden …");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { Services.Stress?.StopAll("App beendet"); } catch { }
        try { Services.Tiles?.Dispose(); } catch { }
        try { Services.Boost?.OnExit(); } catch { }
        try { Services.Monitor?.Dispose(); } catch { }
        try { Services.Fans?.Dispose(); } catch { }
        try { Services.Hub.Dispose(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        try { _win?.Close(); } catch { }
        Log.Info(LogModule.App, "Beendet.");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            _exiting = true;
            try { Services.Fans?.Dispose(); } catch { }
            try { Services.Boost?.OnExit(); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
