using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NickControlCenter;

/// <summary>Eine Aktion zum Beheben (Button am Hinweis).</summary>
public sealed class FixAction
{
    public required string Label { get; init; }
    public string Icon { get; init; } = "";
    /// <summary>Rückfrage vor dem Ausführen (null = keine).</summary>
    public string? Confirm { get; init; }
    public bool Primary { get; init; }
    public required Func<Task<string>> Run { get; init; }
}

/// <summary>Ein Hinweis/Problem, das der Problemlöser gefunden hat.</summary>
public sealed class Finding : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Advice { get; init; } = "";
    public Severity Severity { get; init; }
    public Area Areas { get; init; }
    public string Technical { get; init; } = "";
    public List<FixAction> Actions { get; } = new();

    public Brush SeverityBrush => Palette.ForSeverity(Severity);
    public string SeverityText => Severity switch { Severity.Problem => "PROBLEM", Severity.Warning => "WARNUNG", Severity.Hint => "TIPP", _ => "INFO" };
    public string SeverityIcon => Severity switch { Severity.Problem => "", Severity.Warning => "", Severity.Hint => "", _ => "" };
    public bool HasTechnical => Technical.Length > 0;
    public bool HasAdvice => Advice.Length > 0;

    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    private string _result = "";
    public string Result { get => _result; set { Set(ref _result, value); Raise(nameof(HasResult)); } }
    public bool HasResult => _result.Length > 0;
}

/// <summary>
/// Der Problemlöser: erkennt andere Programme, die sich mit der App beißen (RGB-/Lüfter-Software, Wallpaper Engine,
/// alte Einzel-Apps …) und Systemeinstellungen, die Probleme machen. Bietet zu jedem Fund passende Aktionen an:
/// Programm schließen, Dienst beenden/deaktivieren, Autostart entfernen, deinstallieren oder ignorieren.
/// Alles wird in Logs\Problemloeser.log protokolliert.
/// </summary>
public sealed class DiagnosticsService : ObservableObject
{
    public static DiagnosticsService Instance { get; } = new();

    public ObservableCollection<Finding> Findings { get; } = new();
    private readonly Dictionary<string, Finding> _dynamic = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _scanning;
    private HashSet<string> _lastIds = new();

    public event Action? Changed;

    private int _problemCount;
    public int ProblemCount { get => _problemCount; private set { if (Set(ref _problemCount, value)) RaiseAll(nameof(Badge), nameof(HasBadge)); } }
    public string Badge => _problemCount > 9 ? "9+" : _problemCount.ToString();
    public bool HasBadge => _problemCount > 0;

    private string _lastScan = "Noch nicht geprüft";
    public string LastScan { get => _lastScan; private set => Set(ref _lastScan, value); }
    public bool Scanning { get => _scanning; private set { _scanning = value; Raise(); } }

    public void Start()
    {
        _timer.Tick += async (_, _) => await ScanAsync(quiet: true);
        _timer.Start();
        _ = ScanAsync(quiet: false);
    }

    public IEnumerable<Finding> For(Area area) => Findings.Where(f => (f.Areas & area) != 0);

    // ------------------------------------------------------------------ Dynamische Hinweise aus den Modulen

    /// <summary>Ein Modul meldet ein Problem (z. B. RGB-Gerät antwortet nicht). Gleiche Id ersetzt.</summary>
    public void Report(Finding f)
    {
        _dynamic[f.Id] = f;
        Merge();
    }

    public void Clear(string id)
    {
        if (_dynamic.Remove(id)) Merge();
    }

    // ------------------------------------------------------------------ Scan

    public async Task ScanAsync(bool quiet)
    {
        if (_scanning) return;
        Scanning = true;
        try
        {
            var found = await Task.Run(ScanCore);
            _scanned = found;
            Merge();
            LastScan = $"Zuletzt geprüft: {DateTime.Now:HH:mm:ss}";
            var ids = Findings.Select(f => f.Id).ToHashSet();
            foreach (var n in Findings.Where(f => !_lastIds.Contains(f.Id)))
                Log.Write(n.Severity >= Severity.Warning ? LogLevel.Warn : LogLevel.Info, LogModule.Diagnose, $"Gefunden: {n.Title} – {n.Detail}");
            foreach (var gone in _lastIds.Where(i => !ids.Contains(i)))
                Log.Ok(LogModule.Diagnose, $"Erledigt: {gone}");
            _lastIds = ids;
        }
        catch (Exception ex) { Log.Error(LogModule.Diagnose, "Prüfung fehlgeschlagen", ex); }
        finally { Scanning = false; }
    }

    private List<Finding> _scanned = new();

    private void Merge()
    {
        var ignored = AppSettings.Current.IgnoredFindings;
        var all = _scanned.Concat(_dynamic.Values).Where(f => !ignored.Contains(f.Id))
                          .OrderByDescending(f => f.Severity).ThenBy(f => f.Title).ToList();
        // Liste nur ändern, wo nötig (Buttons/Status bleiben stabil)
        for (int i = Findings.Count - 1; i >= 0; i--)
            if (all.All(f => f.Id != Findings[i].Id)) Findings.RemoveAt(i);
        for (int i = 0; i < all.Count; i++)
        {
            int cur = -1;
            for (int k = 0; k < Findings.Count; k++) if (Findings[k].Id == all[i].Id) { cur = k; break; }
            if (cur < 0) Findings.Insert(Math.Min(i, Findings.Count), all[i]);
            else
            {
                if (!Findings[cur].Busy && !Findings[cur].HasResult) Findings[cur] = all[i];
                if (cur != i && i < Findings.Count) Findings.Move(cur, i);
            }
        }
        ProblemCount = Findings.Count(f => f.Severity >= Severity.Warning);
        Changed?.Invoke();
    }

    public void Ignore(Finding f)
    {
        if (!AppSettings.Current.IgnoredFindings.Contains(f.Id)) AppSettings.Current.IgnoredFindings.Add(f.Id);
        AppSettings.Current.Save();
        Log.Info(LogModule.Diagnose, $"Ignoriert: {f.Title}");
        Merge();
    }

    public void ResetIgnored()
    {
        AppSettings.Current.IgnoredFindings.Clear();
        AppSettings.Current.Save();
        _ = ScanAsync(false);
    }

    public async Task RunAction(Finding f, FixAction a)
    {
        if (a.Confirm != null && !Dialogs.Confirm(a.Label, a.Confirm)) return;
        f.Busy = true;
        f.Result = "";
        try
        {
            Log.Info(LogModule.Diagnose, $"Aktion „{a.Label}“ für „{f.Title}“ …");
            var msg = await a.Run();
            f.Result = msg;
            Log.Ok(LogModule.Diagnose, $"„{a.Label}“: {msg}");
        }
        catch (Exception ex)
        {
            f.Result = "Fehlgeschlagen: " + ex.Message;
            Log.Error(LogModule.Diagnose, $"„{a.Label}“ fehlgeschlagen", ex);
        }
        finally
        {
            f.Busy = false;
            await Task.Delay(1500);
            await ScanAsync(quiet: true);
        }
    }

    // ------------------------------------------------------------------ eigentliche Prüfung (Hintergrund-Thread)

    private sealed record ProcInfo(string Name, int Pid, string Path);
    private sealed record SvcInfo(string Name, string Display, int Pid, string Path, string StartMode, string State);

    private List<Finding> ScanCore()
    {
        var res = new List<Finding>();
        int me = Environment.ProcessId;
        var procs = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == me || p.Id <= 4) continue;
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                procs.Add(new ProcInfo(p.ProcessName, p.Id, path));
            }
            catch { }
            finally { p.Dispose(); }
        }

        var services = new List<SvcInfo>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DisplayName, ProcessId, PathName, StartMode, State FROM Win32_Service");
            foreach (ManagementObject o in s.Get())
                using (o)
                    services.Add(new SvcInfo(o["Name"]?.ToString() ?? "", o["DisplayName"]?.ToString() ?? "", Convert.ToInt32(o["ProcessId"] ?? 0),
                                             o["PathName"]?.ToString() ?? "", o["StartMode"]?.ToString() ?? "", o["State"]?.ToString() ?? ""));
        }
        catch (Exception ex) { Log.Warn(LogModule.Diagnose, "Dienste konnten nicht gelesen werden: " + ex.Message); }

        // ---------------- bekannte Programme
        foreach (var (prog, running) in ConflictCatalog.Match(procs.Select(p => p.Name)))
        {
            var hits = procs.Where(p => running.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var pids = hits.Select(h => h.Pid).ToHashSet();
            var svc = services.Where(s => pids.Contains(s.Pid) && s.Pid != 0).ToList();
            var f = new Finding
            {
                Id = "prog:" + prog.Id,
                Title = $"{prog.Name} läuft",
                Detail = prog.Why,
                Advice = prog.Advice,
                Severity = prog.Severity,
                Areas = prog.Areas,
                Technical = string.Join("\n", hits.Select(h => $"Prozess {h.Name}.exe (PID {h.Pid}) {h.Path}")) +
                            (svc.Count > 0 ? "\n" + string.Join("\n", svc.Select(s => $"Dienst „{s.Display}“ ({s.Name}), Start: {s.StartMode}")) : "")
            };
            AddProgramActions(f, prog, hits.Select(h => h.Name).Distinct().ToList(), svc, hits.Select(h => h.Path).FirstOrDefault(p => p.Length > 0));
            res.Add(f);
        }

        // Hersteller-Dienste, die gerade nicht als Prozess auffallen, aber automatisch starten (z. B. LightingService)
        string[] rgbServices = { "LightingService", "ArmouryCrateService", "AsusAppService", "Mystic_Light_Service", "MSI_Central_Service", "CorsairService",
                                 "CorsairDeviceControlService", "RzChromaSDKService", "Razer Synapse Service", "GigabyteUpdateService", "RGBFusion2Service" };
        foreach (var s in services.Where(s => rgbServices.Contains(s.Name, StringComparer.OrdinalIgnoreCase) && s.StartMode.StartsWith("Auto") && s.State != "Running"))
        {
            res.Add(WithServiceActions(new Finding
            {
                Id = "svc:" + s.Name,
                Title = $"Dienst „{s.Display}“ startet automatisch",
                Detail = "Dieser Hersteller-Dienst läuft gerade nicht, startet aber mit Windows und übernimmt dann die Beleuchtung/Lüfter. Häufige Ursache für Flackern nach dem Hochfahren.",
                Advice = "Deaktivieren (lässt sich hier wieder einschalten).",
                Severity = Severity.Warning, Areas = Area.Rgb | Area.Fans, Technical = $"{s.Name}: {s.Path}"
            }, new List<SvcInfo> { s }));
        }

        // ---------------- alte Einzel-Apps im Autostart
        foreach (var (task, app) in new[] { ("NickFanControl", "Nick Fan Control"), ("NickTweaker-Apply", "Tweaker") })
        {
            if (Env.Run("schtasks.exe", $"/Query /TN \"{task}\"", 5000).Code != 0) continue;
            var f = new Finding
            {
                Id = "oldtask:" + task,
                Title = $"Alte App „{app}“ startet noch mit Windows",
                Detail = $"Die Aufgabe „{task}“ startet die alte Einzel-App beim Anmelden. Dann steuern zwei Programme dieselbe Hardware (Flackern, springende Lüfter, überschriebene Takt-Werte).",
                Advice = "Autostart der alten App entfernen – das Control Center übernimmt alles.",
                Severity = Severity.Problem, Areas = task.Contains("Tweaker") ? Area.Tweaker : Area.Fans | Area.Rgb
            };
            f.Actions.Add(new FixAction
            {
                Label = "Autostart entfernen", Icon = "", Primary = true,
                Run = () => Task.Run(() =>
                {
                    var r = Env.Run("schtasks.exe", $"/Delete /F /TN \"{task}\"");
                    return r.Code == 0 ? "Autostart entfernt." : "Konnte nicht entfernt werden: " + r.Output.Trim();
                })
            });
            res.Add(f);
        }

        // ---------------- System
        if (!Env.IsAdmin)
            res.Add(new Finding
            {
                Id = "sys:admin", Title = "Ohne Administratorrechte gestartet", Severity = Severity.Problem, Areas = Area.All,
                Detail = "Lüftersteuerung, Tweaker, FPS-Boost und Problemlöser brauchen Administratorrechte.",
                Advice = "App über die Verknüpfung starten und die Windows-Abfrage mit „Ja“ bestätigen."
            });

        bool pawnIo = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO") != null;
        if (!pawnIo)
        {
            var f = new Finding
            {
                Id = "sys:pawnio", Title = "Treiber PawnIO fehlt", Severity = Severity.Warning, Areas = Area.Fans | Area.Monitor,
                Detail = "Ohne PawnIO kann Windows die Lüfter-Chips des Mainboards nicht ansprechen: keine Lüftersteuerung, weniger Sensoren (Mainboard-Temperaturen, Lüfterdrehzahlen).",
                Advice = "PawnIO einmal installieren, danach die App neu starten."
            };
            f.Actions.Add(new FixAction { Label = "PawnIO herunterladen", Icon = "", Primary = true, Run = () => { Env.Open("https://pawnio.eu"); return Task.FromResult("Webseite geöffnet."); } });
            res.Add(f);
        }

        // HWiNFO
        bool hwRunning = procs.Any(p => p.Name.StartsWith("HWiNFO", StringComparison.OrdinalIgnoreCase));
        var hwStatus = HwinfoSource.LastStatus;
        if (AppSettings.Current.SensorSource != SourcePreference.LhmOnly)
        {
            if (hwRunning && hwStatus == HwinfoSource.Status.Inactive)
                res.Add(new Finding
                {
                    Id = "hw:shm", Title = "HWiNFO läuft, aber „Shared Memory Support“ ist aus", Severity = Severity.Warning, Areas = Area.Monitor | Area.Fans,
                    Detail = "Damit die App die genauen HWiNFO-Temperaturen nutzen kann, muss HWiNFO seine Werte freigeben.",
                    Advice = "HWiNFO → Einstellungen (Zahnrad) → Haken bei „Shared Memory Support“ → OK. Ohne das nutzt die App ihre eigenen Sensoren."
                });
            else if (hwRunning && hwStatus == HwinfoSource.Status.Stale)
            {
                var hwPath = procs.First(p => p.Name.StartsWith("HWiNFO", StringComparison.OrdinalIgnoreCase)).Path;
                var f = new Finding
                {
                    Id = "hw:stale", Title = "HWiNFO liefert keine neuen Werte mehr", Severity = Severity.Warning, Areas = Area.Monitor | Area.Fans,
                    Detail = "In der kostenlosen HWiNFO-Version schaltet sich „Shared Memory“ nach 12 Stunden ab. Die App ist automatisch auf die eigenen Sensoren umgestiegen.",
                    Advice = "HWiNFO neu starten (Button), dann gibt es wieder 12 Stunden HWiNFO-Werte."
                };
                if (hwPath.Length > 0)
                    f.Actions.Add(new FixAction
                    {
                        Label = "HWiNFO neu starten", Icon = "", Primary = true,
                        Run = () => Task.Run(() =>
                        {
                            foreach (var p in Process.GetProcesses().Where(p => p.ProcessName.StartsWith("HWiNFO", StringComparison.OrdinalIgnoreCase)))
                                try { p.Kill(); p.WaitForExit(5000); } catch { }
                            Process.Start(new ProcessStartInfo(hwPath) { UseShellExecute = true });
                            return "HWiNFO neu gestartet.";
                        })
                    });
                res.Add(f);
            }
            else if (!hwRunning)
            {
                var f = new Finding
                {
                    Id = "hw:missing", Title = "Optional: HWiNFO für noch genauere Temperaturen", Severity = Severity.Info, Areas = Area.Monitor,
                    Detail = "Die App misst selbst (LibreHardwareMonitor). Läuft HWiNFO mit „Shared Memory Support“, übernimmt sie automatisch dessen Werte – z. B. für neue Hardware, die LibreHardwareMonitor noch nicht kennt.",
                    Advice = "HWiNFO (kostenlos) installieren, „Sensors-only“ starten, in den Einstellungen „Shared Memory Support“ anhaken."
                };
                f.Actions.Add(new FixAction { Label = "HWiNFO-Webseite", Icon = "", Run = () => { Env.Open("https://www.hwinfo.com/download/"); return Task.FromResult("Webseite geöffnet."); } });
                res.Add(f);
            }
        }

        // Windows 11 Dynamic Lighting
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting");
            if (k?.GetValue("AmbientLightingEnabled") is int al && al == 1)
            {
                var f = new Finding
                {
                    Id = "sys:dynlight", Title = "Windows „Dynamische Beleuchtung“ ist an", Severity = Severity.Warning, Areas = Area.Rgb,
                    Detail = "Windows 11 steuert damit selbst RGB-Geräte und kann die Farben der App überschreiben oder flackern lassen.",
                    Advice = "Ausschalten (Einstellungen → Personalisierung → Dynamische Beleuchtung)."
                };
                f.Actions.Add(new FixAction
                {
                    Label = "Ausschalten", Icon = "", Primary = true,
                    Run = () => Task.Run(() =>
                    {
                        using var w = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Lighting");
                        w.SetValue("AmbientLightingEnabled", 0, RegistryValueKind.DWord);
                        w.SetValue("ControlledByForegroundApp", 0, RegistryValueKind.DWord);
                        return "Dynamische Beleuchtung ausgeschaltet.";
                    })
                });
                f.Actions.Add(new FixAction { Label = "Einstellungen öffnen", Icon = "", Run = () => { Env.Open("ms-settings:personalization-lighting"); return Task.FromResult("Geöffnet."); } });
                res.Add(f);
            }
        }
        catch { }

        // Speicherintegrität (HVCI) – kostet 5–10 % in CPU-lastigen Spielen
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            if (k?.GetValue("Enabled") is int on && on == 1)
            {
                var f = new Finding
                {
                    Id = "sys:hvci", Title = "Speicherintegrität (Kernisolierung) ist an", Severity = Severity.Info, Areas = Area.Boost,
                    Detail = "Ein Sicherheitsfeature von Windows. Es kostet in manchen CPU-lastigen Spielen 5–10 % FPS. Bewusst kein automatisches Abschalten – das ist eine Sicherheitsentscheidung.",
                    Advice = "Nur abschalten, wenn du den Kompromiss bewusst eingehst: Windows-Sicherheit → Gerätesicherheit → Kernisolierung."
                };
                f.Actions.Add(new FixAction { Label = "Kernisolierung öffnen", Icon = "", Run = () => { Env.Open("windowsdefender://coreisolation"); return Task.FromResult("Geöffnet."); } });
                res.Add(f);
            }
        }
        catch { }

        // Arbeitsspeicher läuft unter Nenntakt (XMP/EXPO aus)
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            int rated = 0, conf = 0;
            foreach (ManagementObject o in s.Get())
                using (o) { rated = Math.Max(rated, Convert.ToInt32(o["Speed"] ?? 0)); conf = Math.Max(conf, Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0)); }
            if (rated > 0 && conf > 0 && conf < rated - 100)
                res.Add(new Finding
                {
                    Id = "sys:xmp", Title = $"Arbeitsspeicher läuft mit {conf} statt {rated} MT/s", Severity = Severity.Hint, Areas = Area.Boost,
                    Detail = "Das XMP-/EXPO-Profil ist im BIOS vermutlich aus. Mit vollem Takt gibt es oft spürbar mehr FPS (vor allem bei CPU-lastigen Spielen).",
                    Advice = "Im BIOS XMP (Intel) bzw. EXPO (AMD) aktivieren."
                });
        }
        catch { }

        // Wenig Speicherplatz
        try
        {
            var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            double free = sys.AvailableFreeSpace / (double)sys.TotalSize;
            if (free < 0.08)
                res.Add(new Finding
                {
                    Id = "sys:disk", Title = $"Laufwerk {sys.Name} fast voll ({Fmt.Size(sys.AvailableFreeSpace)} frei)", Severity = Severity.Warning, Areas = Area.Boost | Area.System,
                    Detail = "Unter ~8 % freiem Platz werden Windows und Spiele langsamer (Auslagerung, Shader-Cache).",
                    Advice = "Im FPS-Boost unter Werkzeuge Temp-Dateien und Shader-Cache löschen."
                });
        }
        catch { }

        // CPU im Leerlauf sehr heiß
        var snap = HardwareHub.Instance.Latest;
        if (SensorSnapshot.Plausible(snap.CpuTemp) && snap.CpuTemp > 80 && snap.CpuLoad < 20)
            res.Add(new Finding
            {
                Id = "sys:hotidle", Title = $"CPU ist ohne Last {snap.CpuTemp:0} °C warm", Severity = Severity.Warning, Areas = Area.Fans | Area.Monitor | Area.Stress,
                Detail = "Im Leerlauf sind 30–55 °C normal. Mögliche Ursachen: Kühler sitzt schlecht, Wärmeleitpaste alt, CPU-Lüfter/Pumpe steht oder Staub.",
                Advice = "Lüfterseite prüfen (dreht der CPU-Lüfter?), Kühler kontrollieren."
            });

        return res;
    }

    private void AddProgramActions(Finding f, KnownProgram prog, List<string> processNames, List<SvcInfo> svc, string? exePath)
    {
        if (svc.Count > 0) WithServiceActions(f, svc);
        else
            f.Actions.Add(new FixAction
            {
                Label = "Schließen", Icon = "", Primary = true,
                Confirm = prog.SafeToKill ? null : $"{prog.Name} wird geschlossen. Nicht gespeicherte Daten darin gehen verloren.",
                Run = () => Task.Run(() => SystemActions.CloseProcesses(processNames))
            });
        if (svc.Count > 0)
            f.Actions.Add(new FixAction { Label = "Programm schließen", Icon = "", Run = () => Task.Run(() => SystemActions.CloseProcesses(processNames)) });
        if (exePath != null && exePath.Length > 0)
            f.Actions.Add(new FixAction
            {
                Label = "Autostart entfernen", Icon = "",
                Confirm = $"Einträge entfernen, die {Path.GetFileName(exePath)} beim Anmelden starten (Registry „Run“, Autostart-Ordner, Aufgabenplanung). Die Einträge werden deaktiviert bzw. gesichert.",
                Run = () => Task.Run(() => SystemActions.RemoveAutostart(processNames, exePath))
            });
        if (prog.UninstallName != null && SystemActions.FindUninstall(prog.UninstallName) is { } un)
            f.Actions.Add(new FixAction
            {
                Label = "Deinstallieren", Icon = "",
                Confirm = $"Startet die Deinstallation von „{un.Display}“. Folge danach den Schritten des Herstellers.",
                Run = () => Task.Run(() => SystemActions.Uninstall(un))
            });
        if (exePath != null && exePath.Length > 0)
            f.Actions.Add(new FixAction { Label = "Ordner öffnen", Icon = "", Run = () => { Env.ShowInExplorer(exePath); return Task.FromResult("Ordner geöffnet."); } });
    }

    private static Finding WithServiceActions(Finding f, List<SvcInfo> svc)
    {
        var names = svc.Select(s => s.Name).Distinct().ToList();
        f.Actions.Add(new FixAction
        {
            Label = names.Count == 1 ? "Dienst beenden" : "Dienste beenden", Icon = "", Primary = true,
            Run = () => Task.Run(() => SystemActions.StopServices(names, disable: false))
        });
        f.Actions.Add(new FixAction
        {
            Label = "Dienst deaktivieren", Icon = "",
            Confirm = $"Dienst {string.Join(", ", names)} beenden und nicht mehr automatisch starten? Das lässt sich im Problemlöser unter „Deaktivierte Dienste“ rückgängig machen.",
            Run = () => Task.Run(() => SystemActions.StopServices(names, disable: true))
        });
        return f;
    }
}

/// <summary>Die eigentlichen Eingriffe – alle mit Protokoll.</summary>
public static class SystemActions
{
    public static string CloseProcesses(IEnumerable<string> names)
    {
        int closed = 0, left = 0;
        var list = names.SelectMany(n => Process.GetProcessesByName(n)).Where(p => p.Id != Environment.ProcessId).ToList();
        foreach (var p in list) { try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { } }
        Thread.Sleep(2500);
        foreach (var p in list)
        {
            try
            {
                p.Refresh();
                if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); }
                if (p.HasExited) closed++; else left++;
            }
            catch { left++; }
            finally { p.Dispose(); }
        }
        return left == 0 ? $"{closed} Prozess(e) beendet." : $"{closed} beendet, {left} lassen sich nicht beenden (läuft evtl. als Dienst → „Dienst beenden“).";
    }

    public static string StopServices(IEnumerable<string> names, bool disable)
    {
        var msgs = new List<string>();
        foreach (var n in names)
        {
            try
            {
                using var sc = new ServiceController(n);
                if (disable)
                {
                    var before = sc.StartType.ToString();
                    var r = Env.Run("sc.exe", $"config \"{n}\" start= disabled");
                    if (r.Code == 0)
                    {
                        AppSettings.Current.DisabledServices[n] = before;
                        AppSettings.Current.Save();
                    }
                    else msgs.Add($"{n}: Deaktivieren fehlgeschlagen ({r.Output.Trim()})");
                }
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
                msgs.Add($"{n}: {(disable ? "beendet und deaktiviert" : "beendet")}");
            }
            catch (Exception ex) { msgs.Add($"{n}: {ex.Message}"); }
        }
        return string.Join(" · ", msgs);
    }

    public static string EnableService(string name)
    {
        var start = AppSettings.Current.DisabledServices.TryGetValue(name, out var s) ? s : "Automatic";
        string mode = start switch { "Manual" => "demand", "Disabled" => "demand", _ => "auto" };
        var r = Env.Run("sc.exe", $"config \"{name}\" start= {mode}");
        if (r.Code != 0) return "Fehlgeschlagen: " + r.Output.Trim();
        AppSettings.Current.DisabledServices.Remove(name);
        AppSettings.Current.Save();
        try { using var sc = new ServiceController(name); if (mode == "auto") sc.Start(); } catch { }
        return $"Dienst {name} wieder aktiviert.";
    }

    /// <summary>Autostart-Einträge eines Programms deaktivieren (Run-Schlüssel sichern, Aufgaben deaktivieren, Autostart-Ordner verschieben).</summary>
    public static string RemoveAutostart(List<string> processNames, string exePath)
    {
        var exe = Path.GetFileName(exePath);
        var hits = new List<string>();
        bool Matches(string? cmd) => cmd != null && (cmd.Contains(exe, StringComparison.OrdinalIgnoreCase) ||
                                                      processNames.Any(n => cmd.Contains(n + ".exe", StringComparison.OrdinalIgnoreCase)));

        var backup = Path.Combine(JsonStore.Folder, "autostart-sicherung.txt");
        foreach (var (root, path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")
                 })
        {
            try
            {
                using var k = root.OpenSubKey(path, true);
                if (k == null) continue;
                foreach (var v in k.GetValueNames())
                {
                    var cmd = k.GetValue(v) as string;
                    if (!Matches(cmd)) continue;
                    Directory.CreateDirectory(JsonStore.Folder);
                    File.AppendAllText(backup, $"{DateTime.Now:g}\t{root.Name}\\{path}\t{v}\t{cmd}{Environment.NewLine}");
                    k.DeleteValue(v, false);
                    hits.Add($"Registry „{v}“");
                }
            }
            catch { }
        }

        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            try
            {
                foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (!processNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)) &&
                        !name.Contains(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(JsonStore.Folder, "Autostart-entfernt");
                    Directory.CreateDirectory(target);
                    File.Move(lnk, Path.Combine(target, Path.GetFileName(lnk)), true);
                    hits.Add($"Autostart-Ordner „{name}“");
                }
            }
            catch { }
        }

        // Aufgabenplanung: CSV ohne Kopfzeile, Spalte 2 = Aufgabenname, Spalte 9 = Auszuführende Aufgabe
        var (code, output) = Env.Run("schtasks.exe", "/Query /FO CSV /V /NH", 30000);
        if (code == 0)
        {
            foreach (var line in output.Split('\n'))
            {
                var cols = SplitCsv(line);
                if (cols.Count < 9) continue;
                if (!Matches(cols[8])) continue;
                var task = cols[1];
                if (task.Contains("NickControlCenter", StringComparison.OrdinalIgnoreCase)) continue;
                if (Env.Run("schtasks.exe", $"/Change /TN \"{task}\" /DISABLE").Code == 0) hits.Add($"Aufgabe „{task.TrimStart('\\')}“ deaktiviert");
            }
        }
        return hits.Count == 0 ? "Kein Autostart-Eintrag gefunden (startet evtl. als Dienst → „Dienst deaktivieren“)." : string.Join(" · ", hits.Distinct());
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool q = false;
        foreach (var c in line.TrimEnd('\r'))
        {
            if (c == '"') { q = !q; continue; }
            if (c == ',' && !q) { res.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }

    public sealed record UninstallEntry(string Display, string Command);

    public static UninstallEntry? FindUninstall(string nameContains)
    {
        foreach (var (root, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
                 })
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) continue;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var e = k.OpenSubKey(sub);
                    var dn = e?.GetValue("DisplayName") as string;
                    var cmd = e?.GetValue("UninstallString") as string;
                    if (dn != null && cmd != null && dn.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) return new UninstallEntry(dn, cmd);
                }
            }
            catch { }
        }
        return null;
    }

    public static string Uninstall(UninstallEntry e)
    {
        var cmd = e.Command.Trim();
        string file, args;
        if (cmd.StartsWith('"')) { int end = cmd.IndexOf('"', 1); file = cmd[1..end]; args = cmd[(end + 1)..].Trim(); }
        else if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase)) { file = "msiexec.exe"; args = cmd[cmd.IndexOf(' ')..].Trim().Replace("/I{", "/X{"); }
        else { int sp = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); file = sp > 0 ? cmd[..(sp + 4)] : cmd; args = sp > 0 ? cmd[(sp + 4)..].Trim() : ""; }
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        return $"Deinstallation von „{e.Display}“ gestartet.";
    }
}

/// <summary>Einheitliche, dunkle Rückfragen (statt grauer Windows-Standarddialoge).</summary>
public static class Dialogs
{
    public static bool Confirm(string title, string text, string yes = "Ja, ausführen", string no = "Abbrechen") =>
        ConfirmWindow.Show(title, text, yes, no) == true;

    public static void Info(string title, string text) => ConfirmWindow.Show(title, text, "OK", null);
}
