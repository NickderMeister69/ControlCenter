using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NickControlCenter;

public sealed class BoostSettings
{
    public Dictionary<string, bool> Toggles { get; set; } = new();
    public List<string> SelectedApps { get; set; } = new();
    public bool CloseAppsOnBoost { get; set; } = true;
    public bool CleanOnBoost { get; set; } = true;
    public bool RamOnBoost { get; set; } = true;
    public bool RestorePoint { get; set; } = true;
    public List<string> ManualGamePaths { get; set; } = new();
    // Auto-Boost
    public bool AutoBoost { get; set; } = true;
    /// <summary>Alt (vor den Spielprofilen) – nur noch zum Übernehmen.</summary>
    public List<string>? AutoBoostProcesses { get; set; }
    /// <summary>Bekannte Spiele automatisch erkennen, sobald sie laufen.</summary>
    public bool DetectRunning { get; set; } = true;
    /// <summary>Auch unbekannte Spiele erkennen (Spieleordner oder Vollbild).</summary>
    public bool DetectAnyGame { get; set; } = true;
    /// <summary>Beim Spielen RAM automatisch freigeben, wenn er knapp wird.</summary>
    public bool RamWatchdog { get; set; } = true;
    public bool Applied { get; set; }
    public DateTime? LastBoost { get; set; }
    /// <summary>Was beim letzten Boost gemacht wurde (kurze Punkte für die Anzeige).</summary>
    public List<string> LastSummary { get; set; } = new();
    /// <summary>Steam verbunden (Bibliothek wird gelesen).</summary>
    public bool SteamConnected { get; set; }
    /// <summary>Ein Profil pro Spiel.</summary>
    public List<GameProfile> Games { get; set; } = new();
}

/// <summary>Ein gesicherter Registry-Originalwert (zum exakten Zurücksetzen).</summary>
public sealed class RegBackupEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string Kind { get; set; } = "";
    public string? Value { get; set; }
}

public sealed class RenamedFile { public string From { get; set; } = ""; public string To { get; set; } = ""; }

public sealed class BoostState
{
    public List<RegBackupEntry> Registry { get; set; } = new();
    public string? PowerOriginal { get; set; }
    public string? PowerCreated { get; set; }
    public List<RenamedFile> Renamed { get; set; } = new();
    public Dictionary<string, string?> IniBackups { get; set; } = new(); // Quelle → Sicherungsdatei (null = existierte nicht)
}

public sealed class TweakVM : ObservableObject
{
    private readonly BoostService _svc;
    public Tweak T { get; }
    public TweakVM(BoostService svc, Tweak t) { _svc = svc; T = t; }
    public string Title => T.Title;
    public string Description => T.Description;
    public string Group => T.Group;
    public bool Restart => T.NeedsRestart;
    public bool Expert => T.Expert;
    public bool IsOn
    {
        get => _svc.Settings.Toggles.TryGetValue(T.Id, out var v) ? v : T.DefaultOn;
        set { _svc.Settings.Toggles[T.Id] = value; _svc.Save(); Raise(); }
    }
    private bool _active;
    /// <summary>Ist die Einstellung gerade wirklich gesetzt (aus der Registry gelesen)?</summary>
    public bool Active { get => _active; set => Set(ref _active, value); }
}

public sealed class BgAppVM : ObservableObject
{
    private readonly BoostService _svc;
    public BackgroundApp App { get; }
    public BgAppVM(BoostService svc, BackgroundApp a, double mb, bool known, bool window) { _svc = svc; App = a; Mb = mb; Known = known; HasWindow = window; }
    public string Name => App.Name;
    public double Mb { get; }
    public bool Known { get; }
    public bool HasWindow { get; }
    public string MbText => $"{Mb:0} MB RAM" + (HasWindow ? " · Fenster offen" : " · im Hintergrund");
    public bool Selected
    {
        get => _svc.Settings.SelectedApps.Contains(App.Process, StringComparer.OrdinalIgnoreCase);
        set
        {
            _svc.Settings.SelectedApps.RemoveAll(x => x.Equals(App.Process, StringComparison.OrdinalIgnoreCase));
            if (value) _svc.Settings.SelectedApps.Add(App.Process);
            _svc.Save(); Raise();
        }
    }
}

/// <summary>
/// FPS-Boost: beide alten Booster (allgemein + ARK) zusammengeführt, in C# statt PowerShell.
/// Alles läuft im Hintergrund (kein Einfrieren mehr), jede Änderung wird vorher gesichert.
/// </summary>
public sealed partial class BoostService : ObservableObject
{
    public BoostSettings Settings { get; } = JsonStore.Load<BoostSettings>("boost.json");
    private BoostState _state = JsonStore.Load<BoostState>("boost-sicherung.json");
    public ObservableCollection<TweakVM> Tweaks { get; } = new();
    public ObservableCollection<BgAppVM> BackgroundApps { get; } = new();
    public ObservableCollection<GameVM> Games { get; } = new();
    public ObservableCollection<string> Output { get; } = new();
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<int> _boostedPids = new();
    private bool _timerActive;
    private CancellationTokenSource? _cleanCts;

    public BoostService()
    {
        foreach (var t in BoostCatalog.Tweaks) Tweaks.Add(new TweakVM(this, t));
    }

    public void Save() => JsonStore.Save("boost.json", Settings);
    private void SaveState() => JsonStore.Save("boost-sicherung.json", _state);

    private bool _busy;
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;
    private string _status = "Bereit";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private string _cleanStatus = "";
    public string CleanStatus { get => _cleanStatus; private set => Set(ref _cleanStatus, value); }
    private double _cleanProgress;
    public double CleanProgress { get => _cleanProgress; private set => Set(ref _cleanProgress, value); }
    public bool Applied => Settings.Applied;
    public string LastBoostText => Settings.LastBoost is { } d ? $"Zuletzt geboostet: {d:dd.MM. HH:mm}" : "Noch nicht geboostet";
    private string _autoStatus = "";
    public string AutoStatus { get => _autoStatus; private set => Set(ref _autoStatus, value); }

    public void Start()
    {
        RefreshActive();
        RefreshApps();
        LoadProfiles();
        _ = Task.Run(FindGames);
        _watch.Tick += (_, _) => WatchGames();
        _watch.Start();
        // Timer-Auflösung gilt nur solange die App läuft → nach dem Start wieder setzen, wenn geboostet
        if (Settings.Applied && IsOn("timer")) SetTimerResolution(true);
    }

    /// <summary>Ist diese Optimierung gerade wirklich gesetzt?</summary>
    public bool IsActive(string id) { RefreshActive(); return Tweaks.FirstOrDefault(t => t.T.Id == id)?.Active ?? false; }

    /// <summary>Eine einzelne Optimierung anwenden (mit Sicherung, rückgängig über „Alles zurücksetzen“).</summary>
    public void ApplySingle(string id)
    {
        var t = Tweaks.FirstOrDefault(x => x.T.Id == id);
        if (t == null) return;
        try
        {
            switch (t.T.Special)
            {
                case "power": SetPowerPlan(); break;
                case "timer": SetTimerResolution(true); break;
                case "nagle": foreach (var k in NagleKeys()) { SetTracked(new RegValue(k, "TcpAckFrequency", 1)); SetTracked(new RegValue(k, "TCPNoDelay", 1)); } break;
                default:
                    foreach (var v in t.T.Values) SetTracked(v);
                    if (t.T.Special == "mouse") ApplyMouse(0, 0, 0);
                    break;
            }
            t.IsOn = true;
            Native.BroadcastSettingChange("Policy");
            Say($"{t.Title}: an", LogLevel.Ok);
        }
        catch (Exception ex) { Say($"{t.Title}: {ex.Message}", LogLevel.Warn); }
        RefreshActive();
    }

    public bool IsOn(string id) => Tweaks.FirstOrDefault(t => t.T.Id == id)?.IsOn ?? false;

    private void Say(string msg, LogLevel lvl = LogLevel.Info)
    {
        Log.Write(lvl, LogModule.Boost, msg);
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            Output.Insert(0, $"{DateTime.Now:HH:mm:ss}  {(lvl == LogLevel.Warn ? "⚠ " : lvl == LogLevel.Error ? "✖ " : lvl == LogLevel.Ok ? "✔ " : "")}{msg}");
            while (Output.Count > 300) Output.RemoveAt(Output.Count - 1);
        });
    }

    // ================================================================ Registry mit Sicherung

    public static (RegistryKey Root, string Path) SplitKey(string key)
    {
        var i = key.IndexOf('\\');
        var hive = key[..i].ToUpperInvariant();
        var root = hive switch { "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser, "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine, _ => throw new ArgumentException(key) };
        return (root, key[(i + 1)..]);
    }

    private void Remember(string key, string name)
    {
        if (_state.Registry.Any(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        var (root, path) = SplitKey(key);
        using var k = root.OpenSubKey(path);
        var v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        _state.Registry.Add(new RegBackupEntry
        {
            Key = key, Name = name, Existed = v != null,
            Kind = v == null ? "" : k!.GetValueKind(name).ToString(),
            Value = v switch { null => null, int i => i.ToString(), _ => v.ToString() }
        });
        SaveState();
    }

    public void SetTracked(RegValue v)
    {
        Remember(v.Key, v.Name);
        var (root, path) = SplitKey(v.Key);
        using var k = root.CreateSubKey(path, true);
        if (v.Kind == RegKind.String) k.SetValue(v.Name, v.Value.ToString()!, RegistryValueKind.String);
        else k.SetValue(v.Name, Convert.ToInt32(v.Value), RegistryValueKind.DWord);
    }

    private static object? Read(string key, string name)
    {
        try { var (root, path) = SplitKey(key); using var k = root.OpenSubKey(path); return k?.GetValue(name); }
        catch { return null; }
    }

    /// <summary>Liest aus, welche Optimierungen gerade wirklich aktiv sind.</summary>
    public void RefreshActive()
    {
        foreach (var t in Tweaks)
        {
            if (t.T.Special == "power") t.Active = _state.PowerCreated != null && ActivePowerScheme() == _state.PowerCreated;
            else if (t.T.Special == "timer") t.Active = _timerActive;
            else if (t.T.Special == "nagle") t.Active = NagleKeys().Any() && NagleKeys().All(k => Read(k, "TCPNoDelay") is int n && n == 1);
            else t.Active = t.T.Values.Length > 0 && t.T.Values.All(v => Equals(Read(v.Key, v.Name)?.ToString(), v.Value.ToString()));
        }
        Raise(nameof(Applied));
    }

    // ================================================================ BOOST

    public async Task BoostAsync()
    {
        if (Busy) return;
        Busy = true;
        Status = "Boost läuft …";
        try
        {
            Say("1-Klick-Boost gestartet");
            bool reboot = false;
            int ok = 0, failed = 0, rebootCount = 0, games = 0;
            bool restore = false;
            var gameNames = new List<string>();
            await Task.Run(() =>
            {
                if (Settings.RestorePoint) { try { CreateRestorePoint(); restore = true; } catch { } }
                foreach (var t in Tweaks.Where(t => t.IsOn).ToList())
                {
                    try
                    {
                        switch (t.T.Special)
                        {
                            case "power": SetPowerPlan(); break;
                            case "nagle": foreach (var k in NagleKeys()) { SetTracked(new RegValue(k, "TcpAckFrequency", 1)); SetTracked(new RegValue(k, "TCPNoDelay", 1)); } break;
                            case "timer": break; // wird unten im UI-Thread gesetzt
                            default:
                                foreach (var v in t.T.Values) SetTracked(v);
                                if (t.T.Special == "mouse") ApplyMouse(0, 0, 0);
                                break;
                        }
                        if (t.T.NeedsRestart) { reboot = true; rebootCount++; }
                        ok++;
                        Say($"{t.Title}: an", LogLevel.Ok);
                    }
                    catch (Exception ex) { failed++; Say($"{t.Title}: fehlgeschlagen – {ex.Message}", LogLevel.Warn); }
                }
                // Spielprofile: exe-Optimierungen und Grafik-Configs vorbereiten
                foreach (var g in Games.Where(g => g.P.Active && !g.P.Hidden).ToList())
                {
                    bool any = false;
                    if (g.P.ExeTweaks || g.P.Priority > 0) { GameExeTweaks(g); any = true; }
                    if (g.P.Graphics || g.P.SkipIntros) { ApplyGameFiles(g, fromBoost: true); any = true; }
                    if (any) { games++; gameNames.Add(g.Name); }
                }
            });
            if (IsOn("timer")) SetTimerResolution(true);
            Native.BroadcastSettingChange("Policy");
            int closed = Settings.CloseAppsOnBoost ? await CloseSelectedAppsAsync() : 0;
            LastCleanFreed = 0;
            if (Settings.CleanOnBoost) await CleanAsync(includeShader: false);
            double ram = Settings.RamOnBoost ? await CleanRamAsync() : 0;

            var sum = new List<string>();
            if (ok > 0) sum.Add($"{ok} Boost-Einstellung{(ok == 1 ? "" : "en")} angewendet");
            if (games > 0) sum.Add($"{games} Spielprofil{(games == 1 ? "" : "e")} optimiert ({string.Join(", ", gameNames.Take(3))}{(gameNames.Count > 3 ? " …" : "")})");
            if (closed > 0) sum.Add($"{closed} Hintergrund-App{(closed == 1 ? "" : "s")} geschlossen");
            if (LastCleanFreed > 1 << 20) sum.Add($"{Fmt.Size(LastCleanFreed)} Temp-Dateien gelöscht");
            if (ram > 10) sum.Add(ram >= 1024 ? $"{ram / 1024:0.0} GB Arbeitsspeicher freigegeben" : $"{ram:0} MB Arbeitsspeicher freigegeben");
            else if (Settings.RamOnBoost) sum.Add("Arbeitsspeicher aufgeräumt");
            if (IsOn("timer")) sum.Add("Windows-Timer auf 0,5 ms (weniger Input-Lag)");
            if (restore) sum.Add("Wiederherstellungspunkt erstellt");
            if (failed > 0) sum.Add($"⚠ {failed} Einstellung{(failed == 1 ? "" : "en")} fehlgeschlagen (siehe Protokoll)");
            if (rebootCount > 0) sum.Add($"↻ {rebootCount} Punkt{(rebootCount == 1 ? " wirkt" : "e wirken")} erst nach einem Neustart");
            Settings.LastSummary = sum.Select(x => x.StartsWith("⚠") || x.StartsWith("↻") ? x : "✔ " + x).ToList();
            RaiseAll(nameof(Summary), nameof(HasSummary));

            Settings.Applied = true;
            Settings.LastBoost = DateTime.Now;
            Save();
            RefreshActive();
            Raise(nameof(LastBoostText));
            Status = reboot ? "Fertig – einige Punkte wirken nach einem Neustart" : "Fertig – viel Spaß beim Zocken!";
            Say(Status, LogLevel.Ok);
            Ui.Toast(Status);
        }
        catch (Exception ex) { Status = "Fehler: " + ex.Message; Say("Unerwarteter Fehler: " + ex.Message, LogLevel.Error); }
        finally { Busy = false; }
    }

    public async Task UndoAsync()
    {
        if (Busy) return;
        Busy = true;
        Status = "Setze zurück …";
        try
        {
            await Task.Run(() =>
            {
                int ok = 0;
                foreach (var e in _state.Registry.ToList())
                {
                    try
                    {
                        var (root, path) = SplitKey(e.Key);
                        using var k = root.CreateSubKey(path, true);
                        if (!e.Existed) k.DeleteValue(e.Name, false);
                        else if (Enum.TryParse<RegistryValueKind>(e.Kind, out var kind))
                            k.SetValue(e.Name, kind == RegistryValueKind.DWord ? unchecked(int.Parse(e.Value ?? "0")) : (object)(e.Value ?? ""), kind);
                        ok++;
                    }
                    catch (Exception ex) { Say($"{e.Key}\\{e.Name}: {ex.Message}", LogLevel.Warn); }
                }
                _state.Registry.Clear();
                Say($"{ok} Registry-Werte wiederhergestellt", LogLevel.Ok);

                if (_state.PowerOriginal != null)
                {
                    Env.Run("powercfg.exe", "/setactive " + _state.PowerOriginal);
                    if (_state.PowerCreated != null) Env.Run("powercfg.exe", "/delete " + _state.PowerCreated);
                    Say("Ursprünglicher Energieplan aktiv", LogLevel.Ok);
                    _state.PowerOriginal = null; _state.PowerCreated = null;
                }
                foreach (var r in _state.Renamed.ToList())
                    try { if (File.Exists(r.To)) File.Move(r.To, r.From, true); } catch { }
                if (_state.Renamed.Count > 0) Say("Intro-Videos wiederhergestellt", LogLevel.Ok);
                _state.Renamed.Clear();
                foreach (var (src, bk) in _state.IniBackups.ToList())
                {
                    try
                    {
                        if (bk == null) { if (File.Exists(src)) File.Delete(src); }
                        else if (File.Exists(bk)) File.Copy(bk, src, true);
                    }
                    catch (Exception ex) { Say($"{Path.GetFileName(src)}: {ex.Message}", LogLevel.Warn); }
                }
                if (_state.IniBackups.Count > 0) Say("Spiel-Configs wiederhergestellt", LogLevel.Ok);
                _state.IniBackups.Clear();
                SaveState();
                ApplyMouseFromRegistry();
            });
            SetTimerResolution(false);
            Native.BroadcastSettingChange("Policy");
            Settings.Applied = false;
            Save();
            RefreshActive();
            Status = "Alles zurückgesetzt";
            Ui.Toast("Alle Änderungen des FPS-Boosts wurden zurückgesetzt.");
            foreach (var g in Games) g.RefreshConfig();
        }
        finally { Busy = false; }
    }

    // ================================================================ Einzelteile

    private void CreateRestorePoint()
    {
        Say("Erstelle Wiederherstellungspunkt (kann etwas dauern) …");
        var r = Env.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'Control Center Boost' -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue\"", 120000);
        Say(r.Code == 0 ? "Wiederherstellungspunkt erstellt (oder Windows erlaubt nur einen pro Tag)" : "Wiederherstellungspunkt nicht möglich (Systemschutz aus?) – eigene Sicherung ist trotzdem aktiv", r.Code == 0 ? LogLevel.Ok : LogLevel.Warn);
    }

    private static string? ActivePowerScheme()
    {
        var m = Regex.Match(Env.Run("powercfg.exe", "/getactivescheme").Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private void SetPowerPlan()
    {
        var cur = ActivePowerScheme();
        if (_state.PowerOriginal == null && cur != null && cur != _state.PowerCreated) _state.PowerOriginal = cur;
        string? target = _state.PowerCreated;
        if (target == null || !Env.Run("powercfg.exe", "/list").Output.Contains(target, StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(Env.Run("powercfg.exe", "-duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c").Output,
                                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (!m.Success) throw new InvalidOperationException("Energieplan konnte nicht angelegt werden (vom Hersteller gesperrt?)");
            target = m.Value.ToLowerInvariant();
            Env.Run("powercfg.exe", $"/changename {target} \"Control Center Gaming\" \"Höchstleistung, erstellt vom Control Center\"");
            _state.PowerCreated = target;
        }
        SaveState();
        if (Env.Run("powercfg.exe", "/setactive " + target).Code != 0) throw new InvalidOperationException("Energieplan konnte nicht aktiviert werden");
    }

    private static IEnumerable<string> NagleKeys()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        using var k = Registry.LocalMachine.OpenSubKey(root);
        if (k == null) yield break;
        foreach (var sub in k.GetSubKeyNames())
        {
            using var s = k.OpenSubKey(sub);
            var dhcp = s?.GetValue("DhcpIPAddress") as string;
            var ip = s?.GetValue("IPAddress") as string[];
            bool has = (!string.IsNullOrEmpty(dhcp) && dhcp != "0.0.0.0") || (ip != null && ip.Any(x => x != "0.0.0.0" && x.Length > 0));
            if (has) yield return $@"HKLM\{root}\{sub}";
        }
    }

    private static void ApplyMouse(int t1, int t2, int speed)
    {
        try { Native.SystemParametersInfo(0x0004, 0, new[] { t1, t2, speed }, 3); } catch { }
    }

    private static void ApplyMouseFromRegistry()
    {
        int G(string n, int d) => int.TryParse(Read(@"HKCU\Control Panel\Mouse", n) as string, out var v) ? v : d;
        ApplyMouse(G("MouseThreshold1", 6), G("MouseThreshold2", 10), G("MouseSpeed", 1));
    }

    /// <summary>Windows-Zeitgeber auf 0,5 ms (gilt, solange die App läuft).</summary>
    public void SetTimerResolution(bool on)
    {
        try
        {
            if (on && !_timerActive)
            {
                Native.NtSetTimerResolution(5000, true, out var cur);
                _timerActive = true;
                Say($"Timer-Auflösung aktiv: {cur / 10000.0:0.00} ms", LogLevel.Ok);
            }
            else if (!on && _timerActive)
            {
                Native.NtSetTimerResolution(5000, false, out _);
                _timerActive = false;
                Say("Timer-Auflösung zurück auf Windows-Standard");
            }
        }
        catch (Exception ex) { Say("Timer-Auflösung: " + ex.Message, LogLevel.Warn); }
        RefreshActive();
    }

    // ================================================================ Werkzeuge

    public static string[] TempFolders => new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };

    public static string[] ShaderFolders
    {
        get
        {
            var l = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var ll = Path.Combine(Path.GetDirectoryName(l) ?? l, "LocalLow");
            return new[]
            {
                Path.Combine(l, "NVIDIA", "DXCache"), Path.Combine(l, "NVIDIA", "GLCache"), Path.Combine(ll, "NVIDIA", "PerDriverVersion", "DXCache"),
                Path.Combine(l, "D3DSCache"), Path.Combine(l, "AMD", "DxCache"), Path.Combine(l, "AMD", "DxcCache"), Path.Combine(l, "AMD", "VkCache"),
                Path.Combine(l, "Intel", "ShaderCache")
            };
        }
    }

    /// <summary>Temp-Dateien (und optional Shader-Cache) löschen – im Hintergrund, mit Fortschritt, abbrechbar.</summary>
    public async Task CleanAsync(bool includeShader, bool shaderOnly = false)
    {
        _cleanCts?.Cancel();
        _cleanCts = new CancellationTokenSource();
        var ct = _cleanCts.Token;
        var folders = shaderOnly ? ShaderFolders : includeShader ? TempFolders.Concat(ShaderFolders).ToArray() : TempFolders;
        CleanStatus = "Suche Dateien …";
        CleanProgress = 0;
        int lastCount = 0;
        var prog = new Progress<CleanProgress>(p =>
        {
            CleanStatus = $"{p.Deleted} Dateien gelöscht · {Fmt.Size(p.FreedBytes)} frei · {p.Skipped} in Benutzung übersprungen";
            CleanProgress = Math.Min(95, CleanProgress + (p.Deleted - lastCount > 0 ? 0.5 : 0));
            lastCount = p.Deleted;
        });
        try
        {
            // nur Dateien älter als 1 Stunde – laufende Installer/Programme brauchen ihre frischen Temp-Dateien
            var r = await TempCleaner.CleanAsync(folders, prog, ct, shaderOnly ? TimeSpan.Zero : TimeSpan.FromHours(1));
            LastCleanFreed = r.FreedBytes;
            CleanProgress = 100;
            CleanStatus = (r.Cancelled ? "Abgebrochen – " : "Fertig – ") + $"{Fmt.Size(r.FreedBytes)} frei, {r.Deleted} Dateien gelöscht, {r.Skipped} übersprungen ({r.Duration.TotalSeconds:0.0} s)";
            Say((shaderOnly ? "Shader-Cache: " : "Temp-Dateien: ") + CleanStatus, LogLevel.Ok);
        }
        catch (Exception ex) { CleanStatus = "Fehler: " + ex.Message; Say("Aufräumen: " + ex.Message, LogLevel.Warn); }
    }

    public void CancelClean() => _cleanCts?.Cancel();
    private double LastCleanFreed;

    /// <summary>Zusammenfassung des letzten Boosts (für die Anzeige oben).</summary>
    public List<string> Summary => Settings.LastSummary;
    public bool HasSummary => Settings.LastSummary.Count > 0;

    public async Task<double> CleanRamAsync()
    {
        var before = Native.Memory();
        var skip = new HashSet<string>(BoostCatalog.NeverTouch, StringComparer.OrdinalIgnoreCase);
        foreach (var g in Games) if (g.P.Process.Length > 0) skip.Add(g.P.Process);
        foreach (var k in GameCatalog.Games) foreach (var pr in k.Processes) skip.Add(pr);
        await Task.Run(() =>
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try { if (!skip.Contains(p.ProcessName)) Native.EmptyWorkingSet(p.Handle); }
                catch { }
                finally { p.Dispose(); }
            }
            // Standby-Liste leeren (wie ISLC) – braucht SeProfileSingleProcessPrivilege
            if (Native.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            {
                int cmd = 4; // MemoryPurgeStandbyList
                int r = Native.NtSetSystemInformation(0x50, ref cmd, sizeof(int));
                if (r != 0) Say($"Standby-Liste: Windows meldet 0x{r:X8}", LogLevel.Warn);
            }
            Thread.Sleep(600);
        });
        var after = Native.Memory();
        double freed = (before.UsedGb - after.UsedGb) * 1024;
        Say($"Arbeitsspeicher aufgeräumt{(freed > 10 ? $" – {freed:0} MB frei geworden" : "")} (jetzt {after.UsedGb:0.0} von {after.TotalGb:0.0} GB belegt)", LogLevel.Ok);
        Ui.Toast("Arbeitsspeicher aufgeräumt" + (freed > 10 ? $" – {freed:0} MB frei" : ""));
        return freed;
    }

    public async Task FlushDnsAsync()
    {
        var r = await Task.Run(() => Env.Run("ipconfig.exe", "/flushdns"));
        Say(r.Code == 0 ? "DNS-Cache geleert" : "DNS leeren fehlgeschlagen", r.Code == 0 ? LogLevel.Ok : LogLevel.Warn);
    }

    public async Task RestorePointAsync() => await Task.Run(CreateRestorePoint);

    public async Task<string> PingAsync()
    {
        var targets = new[] { ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Steam", "steamcommunity.com"), ("Epic", "epicgames.com") };
        var lines = new List<string>();
        foreach (var (n, host) in targets)
        {
            try
            {
                using var p = new Ping();
                var times = new List<long>();
                int lost = 0;
                for (int i = 0; i < 5; i++)
                {
                    var r = await p.SendPingAsync(host, 1500);
                    if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else lost++;
                }
                lines.Add(times.Count == 0 ? $"{n}: keine Antwort" :
                    $"{n}: Ø {times.Average():0} ms · Schwankung {(times.Max() - times.Min())} ms" + (lost > 0 ? $" · {lost}/5 verloren" : ""));
            }
            catch (Exception ex) { lines.Add($"{n}: {ex.Message}"); }
        }
        var txt = string.Join("\n", lines);
        Say("Ping-Test: " + txt.Replace("\n", " | "));
        return txt;
    }

    /// <summary>Prozesse, die nie in der Liste auftauchen (Windows selbst, Treiber, diese App, Spiele-Schutz).</summary>
    private static readonly HashSet<string> SystemProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "RuntimeBroker", "sihost",
        "ctfmon", "taskhostw", "dllhost", "conhost", "smartscreen", "SecurityHealthSystray", "SecurityHealthService", "ApplicationFrameHost",
        "LockApp", "fontdrvhost", "svchost", "WmiPrvSE", "spoolsv", "SystemSettings", "SystemSettingsBroker", "UserOOBEBroker", "Widgets",
        "WidgetService", "PhoneExperienceHost", "CrossDeviceResume", "backgroundTaskHost", "WindowsTerminal", "OpenConsole", "cmd", "powershell",
        "NVDisplay.Container", "nvcontainer", "NVIDIA Overlay", "NVIDIA Share", "NVIDIA app", "nvsphelper64", "RtkAudUService64", "igfxEM",
        "AMDRSServ", "RadeonSoftware", "atieclxx", "HWiNFO64", "HWiNFO32", "NickControlCenter", "ControlCenter", "MsMpEng", "NisSrv", "SgrmBroker",
        "msedgewebview2", "Taskmgr", "mmc", "dasHost", "WUDFHost", "CompPkgSrv", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "audiodg",
        "Claude", "claude", "GameBar", "GameBarFTServer", "XboxPcAppFT", "LsaIso", "Secure System", "unsecapp", "wlanext", "AggregatorHost",
    };

    /// <summary>
    /// Alle laufenden Programme des Benutzers (nicht nur bekannte), sortiert nach RAM.
    /// Bekannte Programme (Browser, Launcher …) haben schöne Namen; unbekannte zeigen die Programmbeschreibung.
    /// </summary>
    public void RefreshApps()
    {
        var list = new List<BgAppVM>();
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            var skip = new HashSet<string>(SystemProcs, StringComparer.OrdinalIgnoreCase);
            foreach (var n in BoostCatalog.NeverTouch) skip.Add(n);
            foreach (var g in Games) skip.Add(g.P.Process);
            foreach (var k in GameCatalog.Games) foreach (var pr in k.Processes) skip.Add(pr);

            foreach (var grp in Process.GetProcesses().GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                var ps = grp.ToList();
                try
                {
                    if (skip.Contains(grp.Key) || ps.All(p => { try { return p.SessionId != session; } catch { return true; } })) continue;
                    double mb = 0; bool window = false; string? desc = null;
                    foreach (var p in ps)
                    {
                        try { mb += p.WorkingSet64 / 1048576.0; } catch { }
                        try { if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)) window = true; } catch { }
                        if (desc == null) try { desc = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
                    }
                    var known = BoostCatalog.BackgroundApps.FirstOrDefault(a => a.Process.Equals(grp.Key, StringComparison.OrdinalIgnoreCase));
                    // unbekannte Kleinigkeiten ausblenden (Helfer-Prozesse unter 40 MB ohne Fenster)
                    if (known == null && !window && mb < 40) continue;
                    var name = known?.Name ?? (string.IsNullOrWhiteSpace(desc) ? grp.Key : $"{desc.Trim()} ({grp.Key})");
                    // unbekannte Programme mit Fenster nur sanft schließen (ungespeicherte Daten!), Hintergrund-Programme dürfen beendet werden
                    var app = known ?? new BackgroundApp(grp.Key, name, !window);
                    list.Add(new BgAppVM(this, app, mb, known != null, window));
                }
                finally { foreach (var p in ps) p.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Warn(LogModule.Boost, "Programmliste: " + ex.Message); }
        BackgroundApps.Clear();
        foreach (var x in list.OrderByDescending(x => x.Known && x.Selected).ThenByDescending(x => x.Mb)) BackgroundApps.Add(x);
        _lastApps = list.Select(x => x.App).ToList();
    }

    private List<BackgroundApp> _lastApps = new();

    public async Task<int> CloseSelectedAppsAsync()
    {
        var sel = Settings.SelectedApps.ToList();
        if (sel.Count == 0) return 0;
        int closedCount = 0;
        await Task.Run(() =>
        {
            foreach (var name in sel)
            {
                var def = BoostCatalog.BackgroundApps.FirstOrDefault(a => a.Process.Equals(name, StringComparison.OrdinalIgnoreCase))
                          ?? _lastApps.FirstOrDefault(a => a.Process.Equals(name, StringComparison.OrdinalIgnoreCase));
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                }
                Thread.Sleep(1500);
                var rest = System.Diagnostics.Process.GetProcessesByName(name);
                if (rest.Length > 0 && def is { Kill: true }) foreach (var p in rest) { try { p.Kill(); } catch { } }
                bool gone = System.Diagnostics.Process.GetProcessesByName(name).Length == 0;
                if (gone) closedCount++;
                Say($"{def?.Name ?? name}: {(gone ? "geschlossen" : "läuft noch (ungespeicherte Daten?)")}", gone ? LogLevel.Ok : LogLevel.Warn);
            }
        });
        RefreshApps();
        return closedCount;
    }

    public void OnExit()
    {
        if (_timerActive) try { Native.NtSetTimerResolution(5000, false, out _); } catch { }
        _timerActive = false;
    }
}
