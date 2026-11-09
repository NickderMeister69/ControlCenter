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
    // ARK
    public bool ArkGraphics { get; set; } = true;
    public int ArkPreset { get; set; } = 1;
    public int ArkFps { get; set; }
    public int ArkRes { get; set; }
    public bool ArkVsyncOff { get; set; } = true;
    public bool ArkPostFxOff { get; set; } = true;
    public bool ArkSkipIntros { get; set; } = true;
    public bool AsaVolumetricsOff { get; set; }
    public bool GameExeTweaks { get; set; } = true;
    public List<string> ManualGamePaths { get; set; } = new();
    // Auto-Boost
    public bool AutoBoost { get; set; } = true;
    public List<string> AutoBoostProcesses { get; set; } = new() { "ShooterGame", "ArkAscended" };
    public bool AutoCloseApps { get; set; }
    public bool Applied { get; set; }
    public DateTime? LastBoost { get; set; }
    /// <summary>Steam verbunden (Bibliothek wird gelesen).</summary>
    public bool SteamConnected { get; set; }
    /// <summary>Profil je Spiel (AppId oder Ordner): 0 = aus, 1 = Max-FPS, 2 = Ausgewogen.</summary>
    public Dictionary<string, int> GameProfiles { get; set; } = new();
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

public sealed class BoostState
{
    public List<RegBackupEntry> Registry { get; set; } = new();
    public string? PowerOriginal { get; set; }
    public string? PowerCreated { get; set; }
    public List<(string From, string To)> Renamed { get; set; } = new();
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
    public BgAppVM(BoostService svc, BackgroundApp a, double mb) { _svc = svc; App = a; Mb = mb; }
    public string Name => App.Name;
    public double Mb { get; }
    public string MbText => $"{Mb:0} MB RAM";
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

public sealed class GameVM : ObservableObject
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public string? Exe { get; init; }
    public string? ConfigDir { get; init; }
    public string Process { get; init; } = "";
    public int AppId { get; init; }
    public GameDef? Ark { get; init; }
    public bool IsArk => Ark != null;
    public string Info => IsArk ? $"{Ark!.Short} · {Root}" : Root;
    private bool _auto;
    public bool AutoBoost { get => _auto; set { if (Set(ref _auto, value)) AutoChanged?.Invoke(this); } }
    public Action<GameVM>? AutoChanged;
    public string Key => AppId > 0 ? AppId.ToString() : Root;
    public static string[] ProfileNames { get; } = { "Aus", "Max-FPS", "Ausgewogen" };
    private int _profile;
    /// <summary>Profil: 0 aus, 1 Max-FPS (hohe Priorität, Timer, Apps schließen), 2 Ausgewogen (über normal).</summary>
    public int Profile { get => _profile; set { if (Set(ref _profile, value)) { Raise(nameof(ProfileText)); ProfileChanged?.Invoke(this); } } }
    public Action<GameVM>? ProfileChanged;
    public string ProfileText => ProfileNames[Math.Clamp(_profile, 0, 2)];
    public string ExeName => Exe != null ? System.IO.Path.GetFileName(Exe) : "exe nicht gefunden";
}

/// <summary>
/// FPS-Boost: beide alten Booster (allgemein + ARK) zusammengeführt, in C# statt PowerShell.
/// Alles läuft im Hintergrund (kein Einfrieren mehr), jede Änderung wird vorher gesichert.
/// </summary>
public sealed class BoostService : ObservableObject
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
        _ = Task.Run(FindGames);
        _watch.Tick += (_, _) => WatchGames();
        _watch.Start();
        // Timer-Auflösung gilt nur solange die App läuft → nach dem Start wieder setzen, wenn geboostet
        if (Settings.Applied && IsOn("timer")) SetTimerResolution(true);
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
            await Task.Run(() =>
            {
                if (Settings.RestorePoint) CreateRestorePoint();
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
                        if (t.T.NeedsRestart) reboot = true;
                        Say($"{t.Title}: an", LogLevel.Ok);
                    }
                    catch (Exception ex) { Say($"{t.Title}: fehlgeschlagen – {ex.Message}", LogLevel.Warn); }
                }
                foreach (var g in Games.ToList())
                {
                    if (Settings.GameExeTweaks && g.Exe != null && g.Profile > 0) GameExeTweaks(g);
                    if (g.IsArk)
                    {
                        if (Settings.ArkGraphics) ArkGraphics(g);
                        if (Settings.ArkSkipIntros) SkipIntros(g);
                    }
                }
                if (!Games.Any(g => g.IsArk)) Say("Kein ARK gefunden – ARK-Optimierungen übersprungen (unter „Spiele“ den Ordner wählen).", LogLevel.Info);
            });
            if (IsOn("timer")) SetTimerResolution(true);
            Native.BroadcastSettingChange("Policy");
            if (Settings.CloseAppsOnBoost) await CloseSelectedAppsAsync();
            if (Settings.CleanOnBoost) await CleanAsync(includeShader: false);
            if (Settings.RamOnBoost) await CleanRamAsync();

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
                foreach (var (from, to) in _state.Renamed.ToList())
                    try { if (File.Exists(to)) File.Move(to, from, true); } catch { }
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
        }
        finally { Busy = false; }
    }

    // ================================================================ Einzelteile

    private void CreateRestorePoint()
    {
        Say("Erstelle Wiederherstellungspunkt (kann etwas dauern) …");
        var r = Env.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'Nick Control Center Boost' -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue\"", 120000);
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
            Env.Run("powercfg.exe", $"/changename {target} \"Nick Gaming\" \"Höchstleistung, erstellt vom Nick Control Center\"");
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

    private void GameExeTweaks(GameVM g)
    {
        try
        {
            var exe = g.Exe!;
            const string layers = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
            var cur = Read(layers, exe) as string ?? "";
            if (!cur.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE"))
                SetTracked(new RegValue(layers, exe, string.IsNullOrWhiteSpace(cur) ? "~ DISABLEDXMAXIMIZEDWINDOWEDMODE" : cur.TrimEnd() + " DISABLEDXMAXIMIZEDWINDOWEDMODE", RegKind.String));
            const string gpu = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
            var gp = Read(gpu, exe) as string ?? "";
            var ngp = Regex.IsMatch(gp, @"GpuPreference=\d+;") ? Regex.Replace(gp, @"GpuPreference=\d+;", "GpuPreference=2;") : "GpuPreference=2;" + gp;
            if (ngp != gp) SetTracked(new RegValue(gpu, exe, ngp, RegKind.String));
            var name = Path.GetFileName(exe);
            SetTracked(new RegValue($@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{name}\PerfOptions", "CpuPriorityClass", 3));
            Say($"{g.Name}: Vollbildoptimierung aus, starke Grafikkarte, hohe CPU-Priorität", LogLevel.Ok);
        }
        catch (Exception ex) { Say($"{g.Name}: {ex.Message}", LogLevel.Warn); }
    }

    private void BackupIni(string path, string tag)
    {
        if (_state.IniBackups.ContainsKey(path)) return;
        string? bk = null;
        if (File.Exists(path))
        {
            var dir = Path.Combine(JsonStore.Folder, "Boost-Sicherung");
            Directory.CreateDirectory(dir);
            bk = Path.Combine(dir, $"{tag}_{Path.GetFileName(path)}");
            File.Copy(path, bk, true);
        }
        _state.IniBackups[path] = bk;
        SaveState();
    }

    private void ArkGraphics(GameVM g)
    {
        var ark = g.Ark!;
        if (System.Diagnostics.Process.GetProcessesByName(ark.Process).Length > 0) { Say($"{ark.Short} läuft gerade – Grafik-Config übersprungen. Spiel schließen und nochmal boosten.", LogLevel.Warn); return; }
        var gus = Path.Combine(g.ConfigDir!, "GameUserSettings.ini");
        var eng = Path.Combine(g.ConfigDir!, "Engine.ini");
        if (!File.Exists(gus)) { Say($"{ark.Short}: keine Config – ARK einmal starten und beenden, dann nochmal boosten.", LogLevel.Warn); return; }
        BackupIni(gus, ark.Short);
        BackupIni(eng, ark.Short);
        bool asa = ark.Short == "ASA";
        var ini = IniFile.Load(gus);
        const string us = "/Script/ShooterGame.ShooterGameUserSettings";
        var parts = new List<string>();
        if (Settings.ArkVsyncOff) { ini.Set(us, "bUseVSync", "False"); parts.Add("V-Sync aus"); }
        int fps = BoostCatalog.FpsLimits[Math.Clamp(Settings.ArkFps, 0, BoostCatalog.FpsLimits.Length - 1)];
        if (fps >= 0) { ini.Set(us, "FrameRateLimit", $"{fps}.000000"); parts.Add(fps == 0 ? "FPS unbegrenzt" : $"FPS-Limit {fps}"); }
        foreach (var (k, v) in BoostCatalog.ArkScalability(Settings.ArkPreset, asa)) ini.Set("ScalabilityGroups", k, v);
        if (Settings.ArkPreset == 1 && !asa) { ini.Set(us, "bDisableBloom", "True"); ini.Set(us, "bDisableLightShafts", "True"); }
        if (Settings.ArkPreset > 0) parts.Add(Settings.ArkPreset == 1 ? "Preset Max-FPS" : "Preset Ausgewogen");
        int res = BoostCatalog.ResolutionScales[Math.Clamp(Settings.ArkRes, 0, BoostCatalog.ResolutionScales.Length - 1)];
        if (res > 0) { ini.Set("ScalabilityGroups", "sg.ResolutionQuality", res.ToString()); parts.Add($"Auflösung {res} %"); }
        ini.Save(gus);

        var e = IniFile.Load(eng);
        if (Settings.ArkPostFxOff)
        {
            e.Set("SystemSettings", "r.MotionBlurQuality", "0"); e.Set("SystemSettings", "r.DepthOfFieldQuality", "0");
            e.Set("SystemSettings", "r.LensFlareQuality", "0"); e.Set("SystemSettings", "r.SceneColorFringeQuality", "0");
            parts.Add("Bewegungsunschärfe/Tiefenschärfe aus");
        }
        if (asa && Settings.AsaVolumetricsOff)
        {
            e.Set("SystemSettings", "r.VolumetricCloud", "0"); e.Set("SystemSettings", "r.VolumetricFog", "0");
            parts.Add("Volumetrische Wolken/Nebel aus");
        }
        if (!asa && fps >= 0) e.Set("/Script/Engine.Engine", "bSmoothFrameRate", "False");
        e.Save(eng);
        Say($"{ark.Short}: Grafik angepasst ({(parts.Count == 0 ? "nichts geändert" : string.Join(", ", parts))})", LogLevel.Ok);
    }

    private void SkipIntros(GameVM g)
    {
        if (g.Ark!.Short != "ASE") return;
        var dir = Path.Combine(g.Root, @"ShooterGame\Content\Movies");
        if (!Directory.Exists(dir)) return;
        int n = 0;
        foreach (var f in Directory.GetFiles(dir))
        {
            if (!new[] { ".mp4", ".bik", ".webm", ".wmv" }.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            var to = f + ".nccboost";
            try { File.Move(f, to); _state.Renamed.Add((f, to)); n++; } catch { }
        }
        SaveState();
        Say(n > 0 ? $"ASE: {n} Intro-Videos übersprungen" : "ASE: Intro-Videos waren schon aus", LogLevel.Ok);
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
            CleanProgress = 100;
            CleanStatus = (r.Cancelled ? "Abgebrochen – " : "Fertig – ") + $"{Fmt.Size(r.FreedBytes)} frei, {r.Deleted} Dateien gelöscht, {r.Skipped} übersprungen ({r.Duration.TotalSeconds:0.0} s)";
            Say((shaderOnly ? "Shader-Cache: " : "Temp-Dateien: ") + CleanStatus, LogLevel.Ok);
        }
        catch (Exception ex) { CleanStatus = "Fehler: " + ex.Message; Say("Aufräumen: " + ex.Message, LogLevel.Warn); }
    }

    public void CancelClean() => _cleanCts?.Cancel();

    public async Task CleanRamAsync()
    {
        var before = Native.Memory();
        await Task.Run(() =>
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try { if (!BoostCatalog.NeverTouch.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) Native.EmptyWorkingSet(p.Handle); }
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

    public void RefreshApps()
    {
        var list = new List<BgAppVM>();
        foreach (var a in BoostCatalog.BackgroundApps)
        {
            var ps = System.Diagnostics.Process.GetProcessesByName(a.Process);
            if (ps.Length == 0) continue;
            double mb = 0;
            foreach (var p in ps) { try { mb += p.WorkingSet64 / 1048576.0; } catch { } p.Dispose(); }
            list.Add(new BgAppVM(this, a, mb));
        }
        BackgroundApps.Clear();
        foreach (var x in list.OrderByDescending(x => x.Mb)) BackgroundApps.Add(x);
    }

    public async Task CloseSelectedAppsAsync()
    {
        var sel = Settings.SelectedApps.ToList();
        if (sel.Count == 0) return;
        await Task.Run(() =>
        {
            foreach (var name in sel)
            {
                var def = BoostCatalog.BackgroundApps.FirstOrDefault(a => a.Process.Equals(name, StringComparison.OrdinalIgnoreCase));
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                }
                Thread.Sleep(1500);
                var rest = System.Diagnostics.Process.GetProcessesByName(name);
                if (rest.Length > 0 && def is { Kill: true }) foreach (var p in rest) { try { p.Kill(); } catch { } }
                bool gone = System.Diagnostics.Process.GetProcessesByName(name).Length == 0;
                Say($"{def?.Name ?? name}: {(gone ? "geschlossen" : "läuft noch (ungespeicherte Daten?)")}", gone ? LogLevel.Ok : LogLevel.Warn);
            }
        });
        RefreshApps();
    }

    // ================================================================ Spiele

    private void FindGames()
    {
        var found = new List<GameVM>();
        try
        {
            var roots = new List<string>();
            try { if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string sp) roots.Add(sp); } catch { }
            try { if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") is string ip) roots.Add(ip); } catch { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            var libs = SteamLibrary.FindLibraries(roots);
            foreach (var g in SteamLibrary.FindGames(libs).Where(x => Settings.SteamConnected || BoostCatalog.ArkGames.Any(a => a.AppId == x.AppId)))
            {
                var ark = BoostCatalog.ArkGames.FirstOrDefault(a => a.AppId == g.AppId);
                string? exe = ark != null ? Path.Combine(g.InstallDir, ark.ExeRel) : GuessExe(g.InstallDir);
                if (exe != null && !File.Exists(exe)) exe = null;
                found.Add(new GameVM
                {
                    Name = g.Name, Root = g.InstallDir, AppId = g.AppId, Ark = ark, Exe = exe,
                    ConfigDir = ark != null ? Path.Combine(g.InstallDir, ark.ConfigRel) : null,
                    Process = ark?.Process ?? (exe != null ? Path.GetFileNameWithoutExtension(exe) : "")
                });
            }
            foreach (var mp in Settings.ManualGamePaths)
                foreach (var a in BoostCatalog.ArkGames)
                    if (File.Exists(Path.Combine(mp, a.ExeRel)) && found.All(f => f.Ark?.Short != a.Short))
                        found.Add(new GameVM { Name = a.Name, Root = mp, Ark = a, Exe = Path.Combine(mp, a.ExeRel), ConfigDir = Path.Combine(mp, a.ConfigRel), Process = a.Process, AppId = a.AppId });
            SteamAccount = ReadSteamAccount(roots);
            Log.Info(LogModule.Boost, $"{found.Count} Spiele gefunden ({libs.Count} Steam-Bibliotheken), ARK: {string.Join(", ", found.Where(f => f.IsArk).Select(f => f.Ark!.Short))}");
        }
        catch (Exception ex) { Log.Warn(LogModule.Boost, "Spielesuche: " + ex.Message); }
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            Games.Clear();
            foreach (var g in found.OrderByDescending(g => g.IsArk).ThenBy(g => g.Name))
            {
                if (g.Process.Length > 0) g.AutoBoost = Settings.AutoBoostProcesses.Contains(g.Process, StringComparer.OrdinalIgnoreCase);
                g.Profile = Settings.GameProfiles.TryGetValue(g.Key, out var pr) ? pr : g.IsArk ? 1 : 0;
                g.AutoChanged = OnGameAutoChanged;
                g.ProfileChanged = OnGameProfileChanged;
                Games.Add(g);
            }
            Raise(nameof(ArkFound));
            RaiseAll(nameof(SteamText), nameof(SteamConnected));
        });
    }

    // ================================================================ Steam
    public bool SteamConnected => Settings.SteamConnected;
    public string SteamAccount { get; private set; } = "";
    public string SteamText => !Settings.SteamConnected ? "Nicht verbunden – nur ARK wird gesucht"
        : $"Verbunden{(SteamAccount.Length > 0 ? $" mit „{SteamAccount}“" : "")} · {Games.Count} Spiele erkannt";

    /// <summary>„Mit Steam verbinden“: liest die lokale Steam-Bibliothek (kein Passwort nötig, nichts verlässt den PC).</summary>
    public void ConnectSteam(bool on)
    {
        Settings.SteamConnected = on;
        Save();
        Log.Info(LogModule.Boost, on ? "Mit Steam verbunden – Spiele werden erkannt" : "Steam-Verbindung getrennt");
        Task.Run(FindGames);
        RaiseAll(nameof(SteamText), nameof(SteamConnected));
    }

    private static string ReadSteamAccount(List<string> roots)
    {
        foreach (var r in roots)
        {
            try
            {
                var f = Path.Combine(r.Replace('/', '\\'), "config", "loginusers.vdf");
                if (!File.Exists(f)) continue;
                var txt = File.ReadAllText(f);
                // Block mit "MostRecent" "1" bevorzugen
                var blocks = Regex.Matches(txt, "\"(\\d{17})\"\\s*\\{([^}]*)\\}");
                foreach (Match b in blocks)
                    if (Regex.IsMatch(b.Groups[2].Value, "\"MostRecent\"\\s*\"1\"", RegexOptions.IgnoreCase))
                        return SteamLibrary.ParseValue(b.Groups[2].Value, "PersonaName") ?? "";
                if (blocks.Count > 0) return SteamLibrary.ParseValue(blocks[0].Groups[2].Value, "PersonaName") ?? "";
            }
            catch { }
        }
        return "";
    }

    /// <summary>Haupt-exe eines Spiels raten: größte exe (ohne Installer/Crash-Reporter/Launcher-Helfer).</summary>
    public static string? GuessExe(string dir)
    {
        try
        {
            string[] bad = { "unins", "crash", "report", "setup", "install", "redist", "vc_", "dxsetup", "dotnet", "ue4prereq", "uninstall", "helper", "update", "cefprocess", "easyanticheat", "be_service", "battleye" };
            var exes = Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                                .Where(f => !bad.Any(b => Path.GetFileName(f).Contains(b, StringComparison.OrdinalIgnoreCase)) &&
                                            !f.Contains(@"\_CommonRedist\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\Engine\Binaries\ThirdParty", StringComparison.OrdinalIgnoreCase))
                                .Select(f => new FileInfo(f)).OrderByDescending(f => f.Length).ToList();
            // Unreal-Spiele: die echte exe liegt unter Binaries\Win64 (Shipping)
            var shipping = exes.FirstOrDefault(f => f.FullName.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase));
            return (shipping ?? exes.FirstOrDefault())?.FullName;
        }
        catch { return null; }
    }

    private void OnGameProfileChanged(GameVM g)
    {
        Settings.GameProfiles[g.Key] = g.Profile;
        if (g.Process.Length > 0)
        {
            Settings.AutoBoostProcesses.RemoveAll(x => x.Equals(g.Process, StringComparison.OrdinalIgnoreCase));
            if (g.Profile > 0) Settings.AutoBoostProcesses.Add(g.Process);
            g.AutoBoost = g.Profile > 0;
        }
        Save();
        Raise(nameof(AutoBoostList));
        Log.Info(LogModule.Boost, $"{g.Name}: Profil „{g.ProfileText}“");
        // exe-Optimierungen sofort anwenden, wenn ein Profil gewählt ist
        if (g.Profile > 0 && g.Exe != null && Settings.GameExeTweaks) Task.Run(() => GameExeTweaks(g));
    }

    public bool ArkFound => Games.Any(g => g.IsArk);

    public void AddGameFolder(string folder)
    {
        if (!Settings.ManualGamePaths.Contains(folder, StringComparer.OrdinalIgnoreCase)) Settings.ManualGamePaths.Add(folder);
        Save();
        Task.Run(FindGames);
    }

    public void AddAutoBoostExe(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        if (!Settings.AutoBoostProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) Settings.AutoBoostProcesses.Add(name);
        Save();
        Raise(nameof(AutoBoostList));
        Log.Info(LogModule.Boost, $"Auto-Boost für {name}.exe aktiviert");
    }

    public void RemoveAutoBoost(string name)
    {
        Settings.AutoBoostProcesses.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        Save();
        Raise(nameof(AutoBoostList));
    }

    public IEnumerable<string> AutoBoostList => Settings.AutoBoostProcesses.ToList();

    private void OnGameAutoChanged(GameVM g)
    {
        if (g.Process.Length == 0) return;
        if (g.AutoBoost) { if (!Settings.AutoBoostProcesses.Contains(g.Process, StringComparer.OrdinalIgnoreCase)) Settings.AutoBoostProcesses.Add(g.Process); }
        else Settings.AutoBoostProcesses.RemoveAll(x => x.Equals(g.Process, StringComparison.OrdinalIgnoreCase));
        Save();
        Raise(nameof(AutoBoostList));
    }

    public static void Launch(GameVM g)
    {
        if (g.AppId > 0) Env.Open($"steam://rungameid/{g.AppId}");
        else if (g.Exe != null) Env.Open(g.Exe);
    }

    /// <summary>Auto-Boost: startet ein Spiel aus der Liste, bekommt es hohe Priorität (+ Timer, Apps schließen).</summary>
    private void WatchGames()
    {
        if (!Settings.AutoBoost || Settings.AutoBoostProcesses.Count == 0) { AutoStatus = Settings.AutoBoost ? "Keine Spiele für Auto-Boost gewählt" : "Auto-Boost aus"; return; }
        var running = new List<string>();
        foreach (var name in Settings.AutoBoostProcesses)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                running.Add(name);
                if (_boostedPids.Add(p.Id))
                {
                    var game = Games.FirstOrDefault(g => g.Process.Equals(name, StringComparison.OrdinalIgnoreCase));
                    bool max = game == null || game.Profile != 2;
                    try { p.PriorityClass = max ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal; } catch { }
                    Say($"Auto-Boost: {game?.Name ?? name} gestartet → Profil {(max ? "Max-FPS: hohe Priorität" : "Ausgewogen: erhöhte Priorität")}" + (max && IsOn("timer") ? ", Timer 0,5 ms" : ""), LogLevel.Ok);
                    if (max && IsOn("timer")) SetTimerResolution(true);
                    if (max && Settings.AutoCloseApps) _ = CloseSelectedAppsAsync();
                }
                p.Dispose();
            }
        }
        // beendete Spiele aus der Liste nehmen
        _boostedPids.RemoveWhere(pid => { try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.HasExited; } catch { return true; } });
        AutoStatus = running.Count > 0 ? $"Aktiv für: {string.Join(", ", running.Distinct())}" : "Wartet auf Spielstart …";
    }

    public void OnExit()
    {
        if (_timerActive) try { Native.NtSetTimerResolution(5000, false, out _); } catch { }
        _timerActive = false;
    }
}
