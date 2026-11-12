using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NickControlCenter;

/// <summary>Ein Spiel in der Liste – Profil (gespeichert) plus Laufzeit-Infos (Config gefunden, läuft gerade …).</summary>
public sealed class GameVM : ObservableObject
{
    private readonly BoostService _svc;
    public GameProfile P { get; }
    public KnownGame? Known { get; private set; }

    public GameVM(BoostService svc, GameProfile p)
    {
        _svc = svc;
        P = p;
        Known = (p.KnownId != null ? GameCatalog.Games.FirstOrDefault(g => g.Id == p.KnownId) : null)
                ?? GameCatalog.Find(p.Process, p.SteamAppId, p.Name);
        if (Known != null) p.KnownId = Known.Id;
    }

    public string Key => P.Key;
    public string Name => P.Name;
    public string ExeName => P.Exe != null ? Path.GetFileName(P.Exe) : P.Process + ".exe";
    public string Sub => $"{P.Source} · {ExeName}";
    public bool IsKnown => Known != null;
    public string? Hint => Known?.Hint is { Length: > 0 } h ? h : null;
    public bool HasHint => Hint != null;
    public string? LaunchOptions => Known?.LaunchOptions;
    public bool HasLaunchOptions => LaunchOptions != null;
    public string? LastSession => P.LastSession;
    public bool HasSession => P.LastSession != null;
    public bool HasIntros => Known?.IntroFolder != null && P.Root != null;
    public bool IsUe5 => Known?.Engine == "UE5" || (Known == null && P.Process.Contains("-Win64-Shipping", StringComparison.OrdinalIgnoreCase));
    public bool CanLaunch => P.LaunchUri != null || P.Exe != null || P.SteamAppId > 0;

    // ---------- Grafik-Config ----------
    public string? ConfigPath { get; private set; }
    public bool EngineIniOk { get; private set; }
    public bool CanGraphics => ConfigPath != null;
    public string ConfigText { get; private set; } = "";

    public void RefreshConfig()
    {
        ConfigPath = BoostService.ResolveConfig(P, Known, out bool eng);
        EngineIniOk = eng;
        ConfigText = ConfigPath == null
            ? (Known != null && Known.ConfigPath == null
                ? "Bei diesem Spiel ändert die App absichtlich keine Spieldateien (Anti-Cheat bzw. Einstellungen liegen in der Cloud) – nur Windows wird optimiert."
                : "Keine Unreal-Grafik-Config gefunden – Grafik bitte im Spiel einstellen. Windows-Optimierungen funktionieren trotzdem.")
            : File.Exists(ConfigPath) ? ConfigPath
            : ConfigPath + "\n(noch nicht vorhanden – Spiel einmal starten und beenden)";
        RaiseAll(nameof(ConfigPath), nameof(CanGraphics), nameof(ConfigText), nameof(EngineIniOk));
    }

    // ---------- Laufzeit ----------
    private bool _running;
    public bool Running { get => _running; set { if (Set(ref _running, value)) Raise(nameof(StateText)); } }
    public string StateText => Running ? "LÄUFT · Profil aktiv" : P.Active ? GameCatalog.PresetNames[(int)P.Preset] : "Aus";

    public string Summary
    {
        get
        {
            if (!P.Active) return "Profil aus – beim Start dieses Spiels passiert nichts.";
            var l = new List<string>();
            if (P.Priority > 0) l.Add("Priorität " + GameCatalog.PriorityNames[P.Priority].ToLowerInvariant());
            if (P.Timer) l.Add("Timer 0,5 ms");
            if (P.PowerPlan) l.Add("Höchstleistung");
            if (P.CloseApps) l.Add("Hintergrund-Apps schließen");
            if (P.CleanRam) l.Add("RAM freigeben");
            if (P.PauseTiles) l.Add("Kacheln pausieren");
            if (P.ExeTweaks) l.Add("Vollbild-Optimierung aus + starke GPU");
            if (P.Graphics && CanGraphics) l.Add("Grafik: " + GameCatalog.QualityNames[Math.Clamp(P.GfxQuality, 0, 3)].Split(' ')[0] + (P.FpsLimit > 0 ? $", FPS {GameCatalog.FpsLimitNames[P.FpsLimit]}" : ""));
            return l.Count == 0 ? "Nichts ausgewählt." : "Beim Spielstart: " + string.Join(" · ", l);
        }
    }

    // ---------- Profil (bindbar) ----------
    public int PresetIndex
    {
        get => (int)P.Preset;
        set { if (value is >= 0 and <= 3 && value != (int)P.Preset) _svc.SetPreset(this, (GamePreset)value); }
    }

    public int Priority { get => P.Priority; set { P.Priority = value; Edited(); } }
    public bool ExeTweaks { get => P.ExeTweaks; set { P.ExeTweaks = value; Edited(); } }
    public bool Timer { get => P.Timer; set { P.Timer = value; Edited(); } }
    public bool PowerPlan { get => P.PowerPlan; set { P.PowerPlan = value; Edited(); } }
    public bool CloseApps { get => P.CloseApps; set { P.CloseApps = value; Edited(); } }
    public bool CleanRam { get => P.CleanRam; set { P.CleanRam = value; Edited(); } }
    public bool PauseTiles { get => P.PauseTiles; set { P.PauseTiles = value; Edited(); } }
    public bool Graphics { get => P.Graphics; set { P.Graphics = value; Edited(); } }
    public int GfxQuality { get => P.GfxQuality; set { P.GfxQuality = value; Edited(); } }
    public int FpsLimit { get => P.FpsLimit; set { P.FpsLimit = value; Edited(); } }
    public int ResScale { get => P.ResScale; set { P.ResScale = value; Edited(); } }
    public bool VsyncOff { get => P.VsyncOff; set { P.VsyncOff = value; Edited(); } }
    public bool PostFxOff { get => P.PostFxOff; set { P.PostFxOff = value; Edited(); } }
    public bool VolumetricsOff { get => P.VolumetricsOff; set { P.VolumetricsOff = value; Edited(); } }
    public bool SkipIntros { get => P.SkipIntros; set { P.SkipIntros = value; Edited(); } }

    // ---- Feineinstellungen (Unreal Engine)
    public string EngineName => Known?.Engine is "UE4" or "UE5" ? Known.Engine : IsUe5 ? "UE5" : "UE4";
    private List<SgItem>? _sgItems;
    public List<SgItem> SgItems => _sgItems ??= UnrealTweaks.Categories.Select(c => new SgItem(this, c)).ToList();
    private List<TweakItem>? _tweakItems;
    public List<TweakItem> TweakItems => _tweakItems ??= UnrealTweaks.For(EngineName).Select(t => new TweakItem(this, t)).ToList();
    public LookPreset[] LookPresets => UnrealTweaks.Presets(Known?.Id);
    public string LookPresetText => P.LookPreset != null ? $"Aktiv: „{P.LookPreset}“" : "Keine Kombination gewählt";
    public string ConsoleCommands => string.Join("\n", UnrealTweaks.All.Where(t => P.EngineTweaks.Contains(t.Id)).Select(t => t.Console));
    internal void FineEdited(string? preset = null)
    {
        P.LookPreset = preset;
        if (P.Preset != GamePreset.Custom) P.Preset = GamePreset.Custom;
        if (!P.Graphics) P.Graphics = true;
        _svc.ProfileEdited(this);
        RaiseAll(nameof(PresetIndex), nameof(StateText), nameof(Summary), nameof(LookPresetText), nameof(ConsoleCommands), nameof(Graphics));
    }
    public void ApplyLook(LookPreset lp)
    {
        P.SgOverride = new Dictionary<string, int>(lp.Sg);
        P.EngineTweaks = lp.Tweaks.Where(id => TweakItems.Any(t => t.Id == id)).ToList();
        P.PostFxOff = lp.PostFxOff; P.VolumetricsOff = lp.VolumetricsOff && IsUe5;
        P.GfxQuality = 0; P.VsyncOff = true;
        if (lp.FpsLimit >= 0) P.FpsLimit = lp.FpsLimit;
        if (lp.ResScale > 0) P.ResScale = lp.ResScale;
        foreach (var i in SgItems) i.Refresh();
        foreach (var t in TweakItems) t.Refresh();
        FineEdited(lp.Name);
        RaiseAll(nameof(PostFxOff), nameof(VolumetricsOff), nameof(GfxQuality), nameof(VsyncOff), nameof(FpsLimit), nameof(ResScale));
    }
    public void ClearFine()
    {
        P.SgOverride.Clear(); P.EngineTweaks.Clear();
        foreach (var i in SgItems) i.Refresh();
        foreach (var t in TweakItems) t.Refresh();
        FineEdited(null);
    }

    // ---- weitere Start-Optionen
    public bool PCoresOnly { get => P.PCoresOnly; set { P.PCoresOnly = value; Edited(); } }
    public bool QuietNotifications { get => P.QuietNotifications; set { P.QuietNotifications = value; Edited(); } }
    public bool ThrottleBackground { get => P.ThrottleBackground; set { P.ThrottleBackground = value; Edited(); } }
    public bool HybridCpu => CpuTopology.IsHybrid;

    // ---- Beleuchtung beim Spielen (gehört nicht zum Boost-Preset → kein Wechsel auf „Eigene“)
    public static string[] RgbModeNames { get; } = { "Nicht ändern", "Bildschirm-Farben (wie Ambilight)", "Feste Farbe", "Temperatur (blau → rot)", "Gespeichertes RGB-Profil", "Licht aus" };
    public int RgbMode { get => P.RgbMode; set { P.RgbMode = Math.Clamp(value, 0, 5); _svc.SaveGames(); RaiseAll(nameof(RgbMode), nameof(ShowRgbColor), nameof(ShowRgbProfile)); } }
    public string RgbColor { get => P.RgbColor; set { if (RgbF.TryParseHex(value, out var c)) { P.RgbColor = c.Hex; _svc.SaveGames(); } Raise(); Raise(nameof(RgbBrush)); } }
    public System.Windows.Media.Brush RgbBrush => ColorItem.Frozen(RgbF.FromHex(P.RgbColor));
    public string? RgbProfile { get => P.RgbProfile; set { P.RgbProfile = value; _svc.SaveGames(); Raise(); } }
    public bool ShowRgbColor => P.RgbMode == 2;
    public bool ShowRgbProfile => P.RgbMode == 4;
    public IEnumerable<string> RgbProfileNames => Services.Fans?.Rgb.Profiles.Select(p => p.Name) ?? Enumerable.Empty<string>();

    private void Edited([CallerMemberName] string? name = null)
    {
        if (P.Preset != GamePreset.Custom) P.Preset = GamePreset.Custom;
        _svc.ProfileEdited(this);
        Raise(name);
        RaiseAll(nameof(PresetIndex), nameof(StateText), nameof(Summary));
    }

    public void RaiseAllProps() => Raise(string.Empty);
    public void SetKnown(KnownGame? k) { Known = k; P.KnownId = k?.Id; }
}

/// <summary>Eine Grafik-Kategorie im Spielprofil.</summary>
public sealed class SgItem : ObservableObject
{
    private readonly GameVM _g;
    public SgCategory C { get; }
    public SgItem(GameVM g, SgCategory c) { _g = g; C = c; }
    public string Label => C.Label;
    public string Hint => C.Hint;
    /// <summary>0 = wie Voreinstellung, 1…4 = Niedrig…Episch</summary>
    public int Index
    {
        get => _g.P.SgOverride.TryGetValue(C.Key, out var v) ? v + 1 : 0;
        set
        {
            if (value <= 0) _g.P.SgOverride.Remove(C.Key); else _g.P.SgOverride[C.Key] = Math.Clamp(value - 1, 0, 3);
            Raise(); _g.FineEdited();
        }
    }
    public void Refresh() => Raise(nameof(Index));
}

/// <summary>Eine Feineinstellung (Konsolenvariable) im Spielprofil.</summary>
public sealed class TweakItem : ObservableObject
{
    private readonly GameVM _g;
    public UnrealTweak T { get; }
    public TweakItem(GameVM g, UnrealTweak t) { _g = g; T = t; }
    public string Id => T.Id;
    public string Title => T.Title;
    public string Description => T.Description + (T.PvpNote ? " Im PvP fair bleiben – am besten nur in PvE/Einzelspieler." : "");
    public string Gain => T.Gain;
    public string LookText => T.LookText;
    public System.Windows.Media.Brush LookBrush => T.Look switch { -1 => Palette.Good, 0 => Palette.Good, 1 => Palette.Warn, _ => Palette.Bad };
    public bool On
    {
        get => _g.P.EngineTweaks.Contains(T.Id);
        set
        {
            _g.P.EngineTweaks.Remove(T.Id);
            if (value) _g.P.EngineTweaks.Add(T.Id);
            Raise(); _g.FineEdited();
        }
    }
    public void Refresh() => Raise(nameof(On));
}

public sealed partial class BoostService
{
    private int? _notifyPrev;
    private readonly HashSet<int> _throttled = new();
    private readonly Dictionary<string, HashSet<int>> _activeGames = new(StringComparer.OrdinalIgnoreCase);
    private string? _gamePowerPrev;
    private bool _gameTimer;
    public static string[] KnownGameNames { get; } = GameCatalog.Games.Select(g => g.Name).OrderBy(n => n).ToArray();

    private GameVM? _selected;
    public GameVM? SelectedGame { get => _selected; set { if (Set(ref _selected, value)) value?.RefreshConfig(); } }

    public string GamesText => $"{Games.Count(g => !g.P.Hidden)} Spiele · {Games.Count(g => !g.P.Hidden && g.P.Active)} mit Profil";

    // ================================================================ Profile laden/speichern

    private void LoadProfiles()
    {
        // Übernahme aus der alten Version (Auto-Boost-Liste ohne Profile)
        if (Settings.AutoBoostProcesses is { Count: > 0 } old)
        {
            foreach (var proc in old.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (Settings.Games.Any(g => g.Key == proc.ToLowerInvariant())) continue;
                var k = GameCatalog.Find(proc);
                // ARK-Standardeinträge nur übernehmen, wenn ARK wirklich installiert ist (findet die Spielesuche)
                if (k is { Id: "ase" or "asa" }) continue;
                var p = NewProfile(k?.Name ?? proc, proc, null, null, "Manuell", k);
                GameCatalog.ApplyPreset(p, GamePreset.Balanced, k, false, false);
                Settings.Games.Add(p);
            }
            Settings.AutoBoostProcesses = null;
            Save();
        }
        Games.Clear();
        // fälschlich als Spiel erkannte Programme (z. B. Wallpaper Engine) wieder entfernen
        Settings.Games.RemoveAll(p => NoGame.Contains(p.Process) || p.SteamAppId == 431960);
        foreach (var p in Settings.Games.OrderBy(p => p.Name)) Games.Add(new GameVM(this, p));
        Raise(nameof(GamesText));
    }

    private static GameProfile NewProfile(string name, string process, string? exe, string? root, string source, KnownGame? k) => new()
    {
        Key = process.ToLowerInvariant(), Name = name, Process = process, Exe = exe, Root = root, Source = source, KnownId = k?.Id,
        SteamAppId = k?.SteamAppId ?? 0
    };

    public void SaveGames()
    {
        Settings.Games = Games.Select(g => g.P).ToList();
        Save();
        Raise(nameof(GamesText));
    }

    public void ProfileEdited(GameVM g)
    {
        SaveGames();
        Log.Info(LogModule.Boost, $"{g.Name}: Profil geändert → {g.Summary}");
    }

    public void SetPreset(GameVM g, GamePreset preset)
    {
        g.RefreshConfig();
        if (preset != GamePreset.Custom) GameCatalog.ApplyPreset(g.P, preset, g.Known, g.CanGraphics, g.EngineIniOk);
        else g.P.Preset = GamePreset.Custom;
        if (!g.IsUe5) g.P.VolumetricsOff = false;
        g.RaiseAllProps();
        SaveGames();
        Say($"{g.Name}: Profil „{GameCatalog.PresetNames[(int)preset]}“ – {g.Summary}", LogLevel.Ok);
        // Datei-Optimierungen gleich vorbereiten (wirken beim nächsten Spielstart)
        if (g.P.Active && !g.Running) Task.Run(() => { if (g.P.ExeTweaks || g.P.Priority > 0) GameExeTweaks(g); if (g.P.Graphics || g.P.SkipIntros) ApplyGameFiles(g, false); });
    }

    // ================================================================ Spiele finden

    private void FindGames()
    {
        var found = new List<(string Name, string Process, string? Exe, string? Root, string Source, KnownGame? K, int AppId, string? Uri)>();
        var roots = new List<string>();
        try { if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string sp) roots.Add(sp); } catch { }
        try { if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") is string ip) roots.Add(ip); } catch { }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

        // Steam (nur wenn verbunden)
        int steamLibs = 0;
        if (Settings.SteamConnected)
        {
            try
            {
                var libs = SteamLibrary.FindLibraries(roots);
                steamLibs = libs.Count;
                foreach (var g in SteamLibrary.FindGames(libs))
                {
                    if (g.AppId == 228980 || g.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) || g.Name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase)) continue;
                    var k = GameCatalog.Find(null, g.AppId, g.Name);
                    var exe = FindMainExe(g.InstallDir, k);
                    var proc = k?.Processes[0] ?? (exe != null ? Path.GetFileNameWithoutExtension(exe) : "");
                    if (proc.Length == 0) continue;
                    found.Add((k?.Name ?? g.Name, proc, exe, g.InstallDir, "Steam", k, g.AppId, $"steam://rungameid/{g.AppId}"));
                }
            }
            catch (Exception ex) { Log.Warn(LogModule.Boost, "Steam-Suche: " + ex.Message); }
            SteamAccount = ReadSteamAccount(roots);
        }

        // Epic Games (lokale Manifeste, kein Login nötig)
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.item"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        var r = doc.RootElement;
                        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        bool isGame = !r.TryGetProperty("AppCategories", out var cats) || cats.EnumerateArray().Any(c => c.GetString() == "games");
                        if (!isGame) continue;
                        var name = S("DisplayName"); var loc = S("InstallLocation"); var launch = S("LaunchExecutable");
                        if (name.Length == 0 || loc.Length == 0) continue;
                        var k = GameCatalog.Find(Path.GetFileNameWithoutExtension(launch), 0, name);
                        string? exe = launch.Length > 0 ? Path.Combine(loc, launch.Replace('/', '\\')) : null;
                        var shipping = FindShippingExe(loc);
                        var proc = k?.Processes[0] ?? (shipping != null ? Path.GetFileNameWithoutExtension(shipping) : exe != null ? Path.GetFileNameWithoutExtension(exe) : "");
                        if (proc.Length == 0) continue;
                        var uri = $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{S("AppName")}?action=launch&silent=true";
                        found.Add((k?.Name ?? name, proc, shipping ?? exe, loc, "Epic Games", k, 0, uri));
                    }
                    catch { }
                }
        }
        catch (Exception ex) { Log.Warn(LogModule.Boost, "Epic-Suche: " + ex.Message); }

        // Riot (VALORANT, League of Legends)
        foreach (var (product, id) in new[] { ("valorant", "valorant"), ("league_of_legends", "lol") })
        {
            try
            {
                var k = GameCatalog.Games.First(g => g.Id == id);
                var yaml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), $@"Riot Games\Metadata\{product}.live\{product}.live.product_settings.yaml");
                string? dir = null;
                if (File.Exists(yaml))
                {
                    var m = Regex.Match(File.ReadAllText(yaml), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)");
                    if (m.Success) dir = m.Groups[1].Value.Trim().Replace('/', '\\');
                }
                dir ??= id == "valorant" ? @"C:\Riot Games\VALORANT\live" : @"C:\Riot Games\League of Legends";
                if (!Directory.Exists(dir)) continue;
                var client = new[] { @"C:\Riot Games\Riot Client\RiotClientServices.exe", Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\')) ?? "", @"Riot Client\RiotClientServices.exe") }.FirstOrDefault(File.Exists);
                var uri = client != null ? $"\"{client}\" --launch-product={product} --launch-patchline=live" : null;
                found.Add((k.Name, k.Processes[0], null, dir, "Riot", k, 0, uri));
            }
            catch { }
        }

        // Spielordner, die früher von Hand gewählt wurden
        foreach (var mp in Settings.ManualGamePaths)
        {
            var exe = FindShippingExe(mp) ?? GuessExe(mp);
            if (exe == null) continue;
            var k = GameCatalog.Find(Path.GetFileNameWithoutExtension(exe), 0, Path.GetFileName(mp));
            found.Add((k?.Name ?? Path.GetFileName(mp), k?.Processes[0] ?? Path.GetFileNameWithoutExtension(exe), exe, mp, "Manuell", k, k?.SteamAppId ?? 0, null));
        }

        Log.Info(LogModule.Boost, $"Spielesuche: {found.Count} Spiele gefunden (Steam {(Settings.SteamConnected ? $"verbunden, {steamLibs} Bibliotheken" : "nicht verbunden")}, Epic, Riot)");

        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var f in found)
            {
                var key = f.Process.ToLowerInvariant();
                var vm = Games.FirstOrDefault(g => g.Key == key);
                if (vm != null)
                {
                    // vorhandenes Profil behalten, nur Pfade auffrischen
                    vm.P.Exe ??= f.Exe; vm.P.Root ??= f.Root; vm.P.LaunchUri ??= f.Uri;
                    if (vm.P.SteamAppId == 0) vm.P.SteamAppId = f.AppId;
                    if (vm.P.Source is "Erkannt" or "Manuell" && f.Source != "Manuell") vm.P.Source = f.Source;
                    continue;
                }
                var p = NewProfile(f.Name, f.Process, f.Exe, f.Root, f.Source, f.K);
                p.SteamAppId = f.AppId; p.LaunchUri = f.Uri;
                var g = new GameVM(this, p);
                g.RefreshConfig();
                // bekannte Spiele bekommen gleich ihr passendes Profil, unbekannte bleiben aus, bis du eins wählst
                GameCatalog.ApplyPreset(p, GameCatalog.Recommended(g.Known), g.Known, g.CanGraphics, g.EngineIniOk);
                Games.Add(g);
            }
            Sort();
            SaveGames();
            RaiseAll(nameof(SteamText), nameof(SteamConnected), nameof(GamesText));
        });
    }

    private void Sort()
    {
        var sel = SelectedGame;
        var sorted = Games.OrderByDescending(g => g.Running).ThenByDescending(g => g.P.Active).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Games.IndexOf(sorted[i]);
            if (cur != i) Games.Move(cur, i);
        }
        SelectedGame = sel;
    }

    /// <summary>Haupt-exe: bei Unreal-Spielen die "…-Win64-Shipping.exe", sonst die größte exe.</summary>
    private static string? FindMainExe(string dir, KnownGame? k)
    {
        try
        {
            if (k != null)
                foreach (var proc in k.Processes)
                {
                    var hit = Directory.EnumerateFiles(dir, proc + ".exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true }).FirstOrDefault();
                    if (hit != null) return hit;
                }
        }
        catch { }
        return FindShippingExe(dir) ?? GuessExe(dir);
    }

    private static string? FindShippingExe(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*-Shipping.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                            .Where(f => f.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\Binaries\WinGDK\", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch { return null; }
    }

    // ================================================================ Steam
    public bool SteamConnected => Settings.SteamConnected;
    public string SteamAccount { get; private set; } = "";
    public string SteamText => !Settings.SteamConnected ? "Steam nicht verbunden – Epic- und Riot-Spiele werden trotzdem erkannt"
        : $"Steam verbunden{(SteamAccount.Length > 0 ? $" mit „{SteamAccount}“" : "")} · {Games.Count(g => g.P.Source == "Steam")} Steam-Spiele";

    /// <summary>„Mit Steam verbinden“: liest die lokale Steam-Bibliothek (kein Passwort nötig, nichts verlässt den PC).</summary>
    public void ConnectSteam(bool on)
    {
        Settings.SteamConnected = on;
        Save();
        Log.Info(LogModule.Boost, on ? "Mit Steam verbunden – Spiele werden erkannt" : "Steam-Verbindung getrennt");
        if (!on)
        {
            // Steam-Spiele ohne eigenes Profil wieder aus der Liste nehmen
            foreach (var g in Games.Where(g => g.P.Source == "Steam" && !g.P.Active && !g.Running).ToList()) Games.Remove(g);
            SaveGames();
        }
        Task.Run(FindGames);
        RaiseAll(nameof(SteamText), nameof(SteamConnected));
    }

    public void Rescan() => Task.Run(FindGames);

    private static string ReadSteamAccount(List<string> roots)
    {
        foreach (var r in roots)
        {
            try
            {
                var f = Path.Combine(r.Replace('/', '\\'), "config", "loginusers.vdf");
                if (!File.Exists(f)) continue;
                var txt = File.ReadAllText(f);
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
            var shipping = exes.FirstOrDefault(f => f.FullName.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase));
            return (shipping ?? exes.FirstOrDefault())?.FullName;
        }
        catch { return null; }
    }

    // ================================================================ Spiele hinzufügen / entfernen

    /// <summary>Beliebiges Spiel über seine exe hinzufügen (auch Launcher-exe – die echte Spiel-exe wird gesucht).</summary>
    public GameVM? AddGameExe(string exe)
    {
        string name;
        try { name = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim() ?? ""; } catch { name = ""; }
        if (name.Length < 2 || name.Contains("Unreal", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(exe);
        var dir = Path.GetDirectoryName(exe)!;
        var k = GameCatalog.Find(Path.GetFileNameWithoutExtension(exe), 0, name) ?? GameCatalog.Find(null, 0, Path.GetFileName(dir));
        // Launcher gewählt? → echte Unreal-Spiel-exe im selben Ordner suchen
        var shipping = Path.GetFileName(exe).Contains("-Shipping", StringComparison.OrdinalIgnoreCase) ? exe : FindShippingExe(dir);
        var process = k?.Processes[0] ?? Path.GetFileNameWithoutExtension(shipping ?? exe);
        string root = dir;
        var bi = exe.IndexOf(@"\Binaries\", StringComparison.OrdinalIgnoreCase);
        if (bi > 0) root = Path.GetDirectoryName(exe[..bi]) ?? dir;
        return AddProfile(k?.Name ?? name, process, shipping ?? exe, root, "Manuell", k, launchUri: null, exeForLaunch: exe);
    }

    /// <summary>Bekanntes Spiel aus der Liste hinzufügen (ohne exe – wird beim ersten Start erkannt).</summary>
    public GameVM? AddKnownGame(string name)
    {
        var k = GameCatalog.Games.FirstOrDefault(g => g.Name == name);
        return k == null ? null : AddProfile(k.Name, k.Processes[0], null, null, "Manuell", k, null, null);
    }

    private GameVM AddProfile(string name, string process, string? exe, string? root, string source, KnownGame? k, string? launchUri, string? exeForLaunch)
    {
        var key = process.ToLowerInvariant();
        var vm = Games.FirstOrDefault(g => g.Key == key);
        if (vm != null)
        {
            vm.P.Hidden = false;
            vm.P.Exe ??= exe; vm.P.Root ??= root;
            if (exeForLaunch != null && exeForLaunch != exe) vm.P.LaunchUri ??= $"\"{exeForLaunch}\"";
        }
        else
        {
            var p = NewProfile(name, process, exe, root, source, k);
            if (exeForLaunch != null && exeForLaunch != exe) p.LaunchUri = $"\"{exeForLaunch}\"";
            else p.LaunchUri = launchUri;
            vm = new GameVM(this, p);
            vm.RefreshConfig();
            // auch unbekannte, selbst eingetragene Spiele bekommen ein Profil – du willst es ja boosten
            GameCatalog.ApplyPreset(p, k != null ? GameCatalog.Recommended(k) : GamePreset.Balanced, k, vm.CanGraphics, vm.EngineIniOk);
            Games.Add(vm);
        }
        Sort();
        SaveGames();
        SelectedGame = vm;
        vm.RaiseAllProps();
        Say($"{vm.Name} hinzugefügt – Profil „{GameCatalog.PresetNames[(int)vm.P.Preset]}“{(vm.IsKnown ? " (an das Spiel angepasst)" : "")}", LogLevel.Ok);
        return vm;
    }

    public void RemoveGame(GameVM g)
    {
        if (g.P.Source == "Manuell" || g.P.Source == "Erkannt") Games.Remove(g);
        else { g.P.Hidden = true; GameCatalog.ApplyPreset(g.P, GamePreset.Off, g.Known, false, false); }
        if (SelectedGame == g) SelectedGame = Games.FirstOrDefault(x => !x.P.Hidden);
        SaveGames();
        Say($"{g.Name} aus der Liste entfernt");
    }

    public void Launch(GameVM g)
    {
        Log.Info(LogModule.Boost, "Starte " + g.Name);
        // Grafik vorher schreiben, falls das Spiel geschlossen ist
        if (g.P.Active && (g.P.Graphics || g.P.SkipIntros)) ApplyGameFiles(g, false);
        if (g.P.SteamAppId > 0 && g.P.Source == "Steam") Env.OpenAsUser($"steam://rungameid/{g.P.SteamAppId}");
        else if (g.P.LaunchUri is { } u)
        {
            if (u.StartsWith('"')) { var end = u.IndexOf('"', 1); var file = u[1..end]; var args = u[(end + 1)..].Trim(); Env.OpenAsUser(file, args); }
            else Env.OpenAsUser(u);
        }
        else if (g.P.Exe != null) Env.OpenAsUser(g.P.Exe);
        else Ui.Toast("Keine exe bekannt – starte das Spiel einmal normal, dann merkt sich die App den Pfad.", true);
    }

    // ================================================================ Datei-Optimierungen

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Wo liegt die GameUserSettings.ini dieses Spiels? null = keine Änderungen erlaubt/möglich.</summary>
    public static string? ResolveConfig(GameProfile p, KnownGame? k, out bool engineIniOk)
    {
        engineIniOk = false;
        try
        {
            if (k != null)
            {
                if (k.ConfigPath == null) return null;
                if (k.ConfigPath.Contains("{root}") && p.Root == null) return null;
                engineIniOk = k.EngineIniOk;
                return k.ConfigPath.Replace("{root}", p.Root ?? "").Replace("%LOCALAPPDATA%", LocalAppData);
            }
            // Unbekanntes Spiel: Unreal-Config im Spielordner oder unter %LocalAppData%\<Projekt>
            var candidates = new List<string>();
            if (p.Root != null && Directory.Exists(p.Root))
                candidates.AddRange(Directory.EnumerateFiles(p.Root, "GameUserSettings.ini", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true })
                                             .Where(f => f.Contains(@"\Saved\Config\", StringComparison.OrdinalIgnoreCase)));
            var proj = GameCatalog.UnrealProjectName(p.Exe ?? p.Process);
            if (proj != null)
            {
                var la = Path.Combine(LocalAppData, proj, "Saved", "Config");
                if (Directory.Exists(la))
                    candidates.AddRange(Directory.EnumerateFiles(la, "GameUserSettings.ini", SearchOption.AllDirectories));
            }
            var hit = candidates.OrderByDescending(f => f.Contains(@"\Windows", StringComparison.OrdinalIgnoreCase)).ThenByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (hit == null) return null;
            // Anti-Cheat im Spielordner? Dann keine Engine.ini-Änderungen
            engineIniOk = p.Root == null || !(Directory.Exists(Path.Combine(p.Root, "EasyAntiCheat")) || Directory.Exists(Path.Combine(p.Root, "BattlEye")) ||
                                              Directory.EnumerateDirectories(p.Root, "*AntiCheat*", SearchOption.TopDirectoryOnly).Any());
            return hit;
        }
        catch { return null; }
    }

    private void BackupIni(string path, string tag)
    {
        if (_state.IniBackups.ContainsKey(path)) return;
        string? bk = null;
        if (File.Exists(path))
        {
            var dir = Path.Combine(JsonStore.Folder, "Boost-Sicherung");
            Directory.CreateDirectory(dir);
            bk = Path.Combine(dir, $"{Safe(tag)}_{Path.GetFileName(path)}");
            File.Copy(path, bk, true);
        }
        _state.IniBackups[path] = bk;
        SaveState();
    }

    private static string Safe(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    private bool IsRunning(GameVM g) => Process.GetProcessesByName(g.P.Process).Any(p => { p.Dispose(); return true; });

    /// <summary>Grafik-Config und Intro-Videos des Spiels anpassen (Spiel muss geschlossen sein).</summary>
    public void ApplyGameFiles(GameVM g, bool fromBoost)
    {
        try
        {
            if (IsRunning(g)) { Say($"{g.Name} läuft gerade – Grafik-Config wird beim nächsten Mal geschrieben (Spiel vorher schließen).", LogLevel.Warn); return; }
            if (g.P.Graphics) WriteGraphics(g);
            if (g.P.SkipIntros) SkipIntros(g);
        }
        catch (Exception ex) { Say($"{g.Name}: {ex.Message}", LogLevel.Warn); }
    }

    private void WriteGraphics(GameVM g)
    {
        g.RefreshConfig();
        var gus = g.ConfigPath;
        if (gus == null) return;
        if (!File.Exists(gus)) { Say($"{g.Name}: noch keine Grafik-Config – Spiel einmal starten und beenden, dann wird sie angepasst.", LogLevel.Info); return; }
        var eng = Path.Combine(Path.GetDirectoryName(gus)!, "Engine.ini");
        BackupIni(gus, g.Key);
        var ini = IniFile.Load(gus);
        var us = GameCatalog.UserSettingsSection(ini);
        var parts = new List<string>();
        if (g.P.VsyncOff) { ini.Set(us, "bUseVSync", "False"); parts.Add("V-Sync aus"); }
        int fps = GameCatalog.FpsLimits[Math.Clamp(g.P.FpsLimit, 0, GameCatalog.FpsLimits.Length - 1)];
        if (fps >= 0) { ini.Set(us, "FrameRateLimit", $"{fps}.000000"); parts.Add(fps == 0 ? "FPS unbegrenzt" : $"FPS-Limit {fps}"); }
        var sg = GameCatalog.Scalability(g.P.GfxQuality);
        foreach (var (k, v) in sg) ini.Set("ScalabilityGroups", k, v);
        if (sg.Count > 0) parts.Add("Qualität " + GameCatalog.QualityNames[g.P.GfxQuality].Split(' ')[0]);
        foreach (var (k, v) in g.P.SgOverride) ini.Set("ScalabilityGroups", k, Math.Clamp(v, 0, 3).ToString());
        if (g.P.SgOverride.Count > 0) parts.Add($"{g.P.SgOverride.Count} Kategorien einzeln");
        // ARK (UE4) hat eigene Schalter für Bloom/Lichtstrahlen
        if (g.Known?.Id == "ase" && g.P.GfxQuality == 1) { ini.Set(us, "bDisableBloom", "True"); ini.Set(us, "bDisableLightShafts", "True"); }
        int res = GameCatalog.ResolutionScales[Math.Clamp(g.P.ResScale, 0, GameCatalog.ResolutionScales.Length - 1)];
        if (res > 0) { ini.Set("ScalabilityGroups", "sg.ResolutionQuality", res.ToString()); parts.Add($"Auflösung {res} %"); }
        ini.Save(gus);

        if (g.EngineIniOk)
        {
            BackupIni(eng, g.Key);
            var e = IniFile.Load(eng);
            // Feineinstellungen: gewählte setzen, abgewählte (von der App verwaltete) wieder entfernen
            var chosen = UnrealTweaks.All.Where(t => g.P.EngineTweaks.Contains(t.Id)).ToList();
            var keep = chosen.SelectMany(t => t.Lines.Select(l => l.Cvar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cv in UnrealTweaks.ManagedCvars.Where(c => !keep.Contains(c)))
                if (!(cv is "r.VolumetricCloud" or "r.VolumetricFog" && g.P.VolumetricsOff)) e.Remove("SystemSettings", cv);
            foreach (var t in chosen) foreach (var (cv, v) in t.Lines) e.Set("SystemSettings", cv, v);
            if (chosen.Count > 0) parts.Add($"{chosen.Count} Feineinstellungen");
            if (g.P.PostFxOff)
            {
                e.Set("SystemSettings", "r.MotionBlurQuality", "0"); e.Set("SystemSettings", "r.DepthOfFieldQuality", "0");
                e.Set("SystemSettings", "r.LensFlareQuality", "0"); e.Set("SystemSettings", "r.SceneColorFringeQuality", "0");
                parts.Add("Bewegungs-/Tiefenunschärfe aus");
            }
            if (g.P.VolumetricsOff && g.IsUe5)
            {
                e.Set("SystemSettings", "r.VolumetricCloud", "0"); e.Set("SystemSettings", "r.VolumetricFog", "0");
                parts.Add("Volumetrische Wolken/Nebel aus");
            }
            e.Save(eng);
        }
        Say($"{g.Name}: Grafik-Config angepasst ({(parts.Count == 0 ? "nichts zu ändern" : string.Join(", ", parts))})", LogLevel.Ok);
    }

    private void SkipIntros(GameVM g)
    {
        if (g.Known?.IntroFolder == null || g.P.Root == null) return;
        var dir = Path.Combine(g.P.Root, g.Known.IntroFolder);
        if (!Directory.Exists(dir)) return;
        int n = 0;
        foreach (var f in Directory.GetFiles(dir))
        {
            if (!new[] { ".mp4", ".bik", ".webm", ".wmv" }.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            var to = f + ".nccboost";
            try { File.Move(f, to); _state.Renamed.Add(new RenamedFile { From = f, To = to }); n++; } catch { }
        }
        SaveState();
        if (n > 0) Say($"{g.Name}: {n} Intro-Videos übersprungen", LogLevel.Ok);
    }

    /// <summary>Nur die Grafik-Config dieses Spiels auf das Original zurücksetzen.</summary>
    public void RestoreGameFiles(GameVM g)
    {
        g.RefreshConfig();
        var dir = g.ConfigPath != null ? Path.GetDirectoryName(g.ConfigPath) : null;
        int n = 0;
        foreach (var (src, bk) in _state.IniBackups.ToList())
        {
            if (dir == null || !src.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (bk == null) { if (File.Exists(src)) File.Delete(src); }
                else if (File.Exists(bk)) File.Copy(bk, src, true);
                _state.IniBackups.Remove(src);
                n++;
            }
            catch (Exception ex) { Say($"{Path.GetFileName(src)}: {ex.Message}", LogLevel.Warn); }
        }
        if (g.P.Root != null)
            foreach (var r in _state.Renamed.Where(r => r.From.StartsWith(g.P.Root, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { if (File.Exists(r.To)) File.Move(r.To, r.From, true); _state.Renamed.Remove(r); n++; } catch { }
            }
        SaveState();
        Say(n > 0 ? $"{g.Name}: Original-Grafikeinstellungen wiederhergestellt" : $"{g.Name}: keine gesicherten Dateien vorhanden", n > 0 ? LogLevel.Ok : LogLevel.Info);
    }

    /// <summary>Windows-Einträge für die Spiel-exe: Vollbild-Optimierung aus, starke GPU, CPU-Priorität (gilt auch für Spiele mit Schutz vor Prioritätsänderung).</summary>
    private void GameExeTweaks(GameVM g)
    {
        try
        {
            var parts = new List<string>();
            if (g.P.ExeTweaks && g.P.Exe != null && File.Exists(g.P.Exe))
            {
                var exe = g.P.Exe;
                const string layers = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
                var cur = Read(layers, exe) as string ?? "";
                if (!cur.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE"))
                    SetTracked(new RegValue(layers, exe, string.IsNullOrWhiteSpace(cur) ? "~ DISABLEDXMAXIMIZEDWINDOWEDMODE" : cur.TrimEnd() + " DISABLEDXMAXIMIZEDWINDOWEDMODE", RegKind.String));
                const string gpu = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
                var gp = Read(gpu, exe) as string ?? "";
                var ngp = Regex.IsMatch(gp, @"GpuPreference=\d+;") ? Regex.Replace(gp, @"GpuPreference=\d+;", "GpuPreference=2;") : "GpuPreference=2;" + gp;
                if (ngp != gp) SetTracked(new RegValue(gpu, exe, ngp, RegKind.String));
                parts.Add("Vollbild-Optimierung aus, starke Grafikkarte");
            }
            if (g.P.Priority > 0 && g.P.Process.Length > 0)
            {
                SetTracked(new RegValue($@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{g.P.Process}.exe\PerfOptions", "CpuPriorityClass", g.P.Priority == 2 ? 3 : 6));
                parts.Add("Priorität " + GameCatalog.PriorityNames[g.P.Priority].ToLowerInvariant());
            }
            if (parts.Count > 0) Log.Ok(LogModule.Boost, $"{g.Name}: {string.Join(", ", parts)}");
        }
        catch (Exception ex) { Say($"{g.Name}: {ex.Message}", LogLevel.Warn); }
    }

    // ================================================================ Empfehlung für diesen PC

    public static PcInfo GetPcInfo()
    {
        int w = 0, h = 0, hz = 0;
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
            foreach (System.Management.ManagementObject o in q.Get())
                using (o)
                {
                    int ww = Convert.ToInt32(o["CurrentHorizontalResolution"] ?? 0), hh = Convert.ToInt32(o["CurrentVerticalResolution"] ?? 0), rr = Convert.ToInt32(o["CurrentRefreshRate"] ?? 0);
                    if (ww * hh > w * h) { w = ww; h = hh; hz = rr; }
                }
        }
        catch { }
        if (w == 0) { w = (int)System.Windows.SystemParameters.PrimaryScreenWidth; h = (int)System.Windows.SystemParameters.PrimaryScreenHeight; }
        var snap = HardwareHub.Instance.Latest;
        var tw = Services.Tweaker;
        string gpu = !string.IsNullOrEmpty(snap.GpuName) ? snap.GpuName : tw.Gpu.Name;
        double vram = double.IsNaN(snap.GpuVramTotalGb) ? 0 : snap.GpuVramTotalGb;
        var mem = Native.Memory();
        return new PcInfo(gpu, vram, tw.CpuName, tw.Cores, tw.Threads, mem.TotalGb, w, h, hz);
    }

    /// <summary>Empfehlung berechnen und als Text beschreiben.</summary>
    public (Recommendation R, string Text) Recommend(GameVM g)
    {
        g.RefreshConfig();
        var pc = GetPcInfo();
        var r = HardwareAdvisor.Recommend(pc, g.Known, g.CanGraphics);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Dein PC: {pc.CpuName} ({pc.Cores} Kerne) · {pc.RamGb:0} GB RAM · {pc.GpuName} · {pc.Width}×{pc.Height} @ {pc.RefreshHz} Hz");
        sb.AppendLine();
        sb.AppendLine("Warum:");
        foreach (var x in r.Reasons) sb.AppendLine("• " + x);
        sb.AppendLine();
        sb.AppendLine($"Empfohlen für {g.Name}:");
        sb.AppendLine($"• CPU-Priorität: {GameCatalog.PriorityNames[r.Priority]} · Timer 0,5 ms · Höchstleistung während des Spiels");
        sb.AppendLine($"• Grafik: {GameCatalog.QualityNames[r.GfxQuality]} · FPS-Limit: {GameCatalog.FpsLimitNames[r.FpsLimit]} · Auflösung: {GameCatalog.ResolutionNames[r.ResScale]} · V-Sync aus");
        if (r.CloseApps || r.CleanRam) sb.AppendLine($"• {(r.CloseApps ? "Hintergrund-Apps schließen" : "")}{(r.CloseApps && r.CleanRam ? " · " : "")}{(r.CleanRam ? "RAM freigeben" : "")}");
        if (r.InGame.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Im Spiel / Treiber zusätzlich:");
            foreach (var x in r.InGame) sb.AppendLine("• " + x);
        }
        return (r, sb.ToString().TrimEnd());
    }

    public void ApplyRecommendation(GameVM g, Recommendation r)
    {
        HardwareAdvisor.ApplyTo(g.P, r, g.CanGraphics);
        g.RaiseAllProps();
        SaveGames();
        Say($"{g.Name}: empfohlene Einstellungen für diesen PC übernommen – {g.Summary}", LogLevel.Ok);
        if (!g.Running) Task.Run(() => { GameExeTweaks(g); if (g.P.Graphics) ApplyGameFiles(g, false); });
    }

    // ================================================================ Auto-Boost beim Spielstart

    private void WatchGames()
    {
        Dictionary<string, List<Process>> running;
        try
        {
            running = Process.GetProcesses().GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        }
        catch { return; }
        try
        {
            // bekannte Spiele, die noch nicht in der Liste sind, automatisch aufnehmen
            if (Settings.DetectRunning)
                foreach (var k in GameCatalog.Games)
                    foreach (var proc in k.Processes)
                        if (running.TryGetValue(proc, out var ps) && Games.All(g => g.Key != proc.ToLowerInvariant() && g.Known?.Id != k.Id))
                        {
                            string? exe = null;
                            try { exe = ps[0].MainModule?.FileName; } catch { }
                            var vm = AddProfile(k.Name, proc, exe, null, "Erkannt", k, null, null);
                            Ui.Toast($"{k.Name} erkannt – Profil „{GameCatalog.PresetNames[(int)vm.P.Preset]}“ angelegt");
                        }

            if (Settings.DetectAnyGame) DetectUnknownGame(running);

            if (!Settings.AutoBoost) { AutoStatus = "Auto-Boost aus"; StopAll(); return; }

            var now = new List<string>();
            foreach (var g in Games.Where(g => g.P.Active && !g.P.Hidden && g.P.Process.Length > 0).ToList())
            {
                // gleicher Prozess unter anderem Namen (z. B. r5apex_dx12)? → alle bekannten Namen prüfen
                var names = g.Known?.Processes ?? new[] { g.P.Process };
                var ps = names.SelectMany(n => running.TryGetValue(n, out var l) ? l : new List<Process>()).ToList();
                if (ps.Count == 0) { if (_activeGames.ContainsKey(g.Key)) GameStopped(g); continue; }
                now.Add(g.Name);
                if (!_activeGames.TryGetValue(g.Key, out var pids)) { pids = new HashSet<int>(); _activeGames[g.Key] = pids; GameStarted(g, ps); }
                foreach (var p in ps.Where(p => pids.Add(p.Id))) SetPriority(g, p);
            }
            // Spiele, deren Profil inzwischen aus ist
            foreach (var key in _activeGames.Keys.ToList())
            {
                var gg = Games.FirstOrDefault(x => x.Key == key);
                if (gg == null) _activeGames.Remove(key);
                else if (!gg.P.Active || gg.P.Hidden) GameStopped(gg);
            }
            AutoStatus = now.Count > 0 ? $"Aktiv für: {string.Join(", ", now)}" : "Wartet auf Spielstart …";
            if (_activeGames.Count > 0) { TrackSession(running); RamWatchdog(); }
        }
        finally
        {
            foreach (var l in running.Values) foreach (var p in l) p.Dispose();
        }
    }

    private void SetPriority(GameVM g, Process p)
    {
        if (g.P.Priority <= 0) return;
        try { p.PriorityClass = g.P.Priority == 2 ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal; }
        catch { /* Anti-Cheat-geschützt → die Windows-Priorität aus den exe-Optimierungen greift beim nächsten Start */ }
    }

    private void GameStarted(GameVM g, List<Process> ps)
    {
        g.Running = true;
        // exe-Pfad merken, wenn er noch fehlt (für Vollbild-Optimierung und „Spielen“-Knopf)
        if (g.P.Exe == null)
            try { g.P.Exe = ps[0].MainModule?.FileName; if (g.P.Exe != null) { g.P.Root ??= Path.GetDirectoryName(g.P.Exe); SaveGames(); g.RefreshConfig(); } } catch { }
        var done = new List<string>();
        if (g.P.Priority > 0) done.Add("Priorität " + GameCatalog.PriorityNames[g.P.Priority].ToLowerInvariant());
        if (g.P.Timer && !_timerActive) { SetTimerResolution(true); _gameTimer = true; }
        if (g.P.Timer) done.Add("Timer 0,5 ms");
        if (g.P.PowerPlan)
        {
            try
            {
                var cur = ActivePowerScheme();
                if (cur != null && cur != _state.PowerCreated)
                {
                    _gamePowerPrev ??= cur;
                    SetPowerPlan();
                }
                done.Add("Höchstleistung");
            }
            catch (Exception ex) { Say("Energieplan: " + ex.Message, LogLevel.Warn); }
        }
        if (g.P.CloseApps) { _ = CloseSelectedAppsAsync(); done.Add("Hintergrund-Apps schließen"); }
        if (g.P.CleanRam) { _ = CleanRamAsync(); done.Add("RAM freigeben"); }
        if (g.P.PauseTiles) { try { Services.Tiles?.SetGamePause(true); done.Add("Kacheln pausiert"); } catch { } }
        if (g.P.PCoresOnly && CpuTopology.IsHybrid)
        {
            int ok = 0;
            foreach (var p in ps) try { p.ProcessorAffinity = (IntPtr)CpuTopology.PerformanceMask; ok++; } catch { }
            done.Add(ok > 0 ? "nur P-Kerne" : "P-Kerne (vom Spiel blockiert)");
        }
        if (g.P.QuietNotifications && _notifyPrev == null)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", true);
                _notifyPrev = k.GetValue("ToastEnabled") as int? ?? 1;
                k.SetValue("ToastEnabled", 0, RegistryValueKind.DWord);
                done.Add("Benachrichtigungen aus");
            }
            catch { }
        }
        if (g.P.ThrottleBackground)
        {
            int n = 0;
            foreach (var name in Settings.SelectedApps)
                foreach (var p in Process.GetProcessesByName(name))
                    using (p) try { if (p.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal) { p.PriorityClass = ProcessPriorityClass.BelowNormal; _throttled.Add(p.Id); n++; } } catch { }
            if (n > 0) done.Add($"{n} Hintergrund-Programme ausgebremst");
        }
        // exe-Optimierungen für den nächsten Start sicherstellen
        if (g.P.ExeTweaks || g.P.Priority > 0) Task.Run(() => GameExeTweaks(g));
        Say($"🎮 {g.Name} gestartet → {(done.Count == 0 ? "nichts zu tun" : string.Join(", ", done))}", LogLevel.Ok);
        if (done.Count > 0)
        {
            Settings.LastSummary = new List<string> { $"🎮 Auto-Boost für {g.Name}" }.Concat(done.Select(d => "✔ " + d)).ToList();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => RaiseAll(nameof(Summary), nameof(HasSummary)));
        }
        _sessions[g.Key] = new Session { Start = DateTime.Now, Pids = ps.Select(p => p.Id).ToHashSet(), EnergyStart = EnergyService.Instance.TotalWh };
        ApplyGameRgb(g);
        if (_sessions.Count == 1) FpsCounter.Instance.Acquire();
        Sort();
    }

    private void GameStopped(GameVM g)
    {
        _activeGames.Remove(g.Key);
        g.Running = false;
        Say($"{g.Name} beendet");
        EndSession(g);
        if (_activeGames.Count > 0) { Sort(); return; }
        // letztes Spiel zu → vorübergehende Änderungen zurück
        if (_gameTimer && !(Settings.Applied && IsOn("timer"))) SetTimerResolution(false);
        _gameTimer = false;
        if (_gamePowerPrev != null && !(Settings.Applied && IsOn("power")))
        {
            Env.Run("powercfg.exe", "/setactive " + _gamePowerPrev);
            Say("Energieplan wieder wie vorher");
        }
        _gamePowerPrev = null;
        try { Services.Tiles?.SetGamePause(false); } catch { }
        try { Services.Fans?.Rgb.PopGameEffect(); } catch { }
        if (_notifyPrev is int np)
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", true); k.SetValue("ToastEnabled", np, RegistryValueKind.DWord); } catch { }
            _notifyPrev = null;
        }
        foreach (var pid in _throttled) try { using var p = Process.GetProcessById(pid); p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        _throttled.Clear();
        Sort();
    }

    /// <summary>Beleuchtung fürs Spiel umschalten (nach dem Spiel kommt die alte zurück).</summary>
    private void ApplyGameRgb(GameVM g)
    {
        var rgb = Services.Fans?.Rgb;
        if (rgb == null || g.P.RgbMode == 0) return;
        try
        {
            switch (g.P.RgbMode)
            {
                case 1: rgb.PushGameEffect(new EffectConfig { Type = LightEffect.Screen }); break;
                case 2: rgb.PushGameEffect(new EffectConfig { Type = LightEffect.Static, Colors = new() { g.P.RgbColor } }); break;
                case 3: rgb.PushGameEffect(new EffectConfig { Type = LightEffect.Temperature, Colors = new() { "#0050FF", "#00FF60", "#FFB000", "#FF0000" } }); break;
                case 4:
                    var pr = rgb.Profiles.FirstOrDefault(x => x.Name == g.P.RgbProfile);
                    if (pr != null) rgb.PushGameEffect(pr.Effect.Clone(), pr.Brightness);
                    break;
                case 5: rgb.PushGameEffect(new EffectConfig { Type = LightEffect.Off }); break;
            }
            Log.Info(LogModule.Rgb, $"{g.Name}: Beleuchtung fürs Spiel – {GameVM.RgbModeNames[g.P.RgbMode]}");
        }
        catch (Exception ex) { Log.Warn(LogModule.Rgb, "Spiel-Beleuchtung: " + ex.Message); }
    }

    private void StopAll()
    {
        foreach (var key in _activeGames.Keys.ToList())
            if (Games.FirstOrDefault(x => x.Key == key) is { } g) GameStopped(g); else _activeGames.Remove(key);
    }

    // ================================================================ Jedes Spiel erkennen (auch unbekannte)

    private static readonly string[] GameFolders =
    {
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\XboxGames\", @"\GOG Galaxy\Games\", @"\GOG Games\",
        @"\EA Games\", @"\Ubisoft Game Launcher\games\", @"\Battle.net\", @"\Rockstar Games\", @"\Games\"
    };
    private static readonly HashSet<string> NoGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "RiotClientServices", "RiotClientUx", "Battle.net", "Agent",
        "EADesktop", "upc", "UbisoftConnect", "GalaxyClient", "Discord", "chrome", "msedge", "firefox", "opera", "brave", "vlc", "mpc-hc64",
        "CrashReportClient", "UnrealCEFSubProcess", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "Launcher", "ApplicationFrameHost",
        "ControlCenter", "NickControlCenter", "ControlCenter.update", "obs64",
        "wallpaper32", "wallpaper64", "webwallpaper32", "wallpaperservice32", "ui32", "Lively", "Livelywpf", "Rainmeter", "mpv", "PotPlayerMini64", "Netflix", "Plex", "PowerPnt", "Kodi", "Spotify", "Teams", "ms-teams", "devenv", "Code", "WindowsTerminal", "Taskmgr", "mmc"
    };
    private string? _fsCandidate; private DateTime _fsSince;

    /// <summary>Kein Spiel (Browser, Launcher, Windows-Teile …)?</summary>
    public static bool IsNonGame(string processName) =>
        NoGame.Contains(processName) || SystemProcs.Contains(processName) ||
        processName is "dwm" or "csrss" or "ShellExperienceHost" or "StartMenuExperienceHost" or "SearchHost" or "TextInputHost" or "LockApp" or "SystemSettings";

    /// <summary>Gehört der Prozess zu einem Spiel aus der Spieleliste?</summary>
    public bool IsGameProcess(string processName)
    {
        var key = processName.ToLowerInvariant();
        return Games.Any(g => g.Key == key || (g.Known?.Processes.Any(x => x.Equals(processName, StringComparison.OrdinalIgnoreCase)) ?? false));
    }

    /// <summary>
    /// Läuft ein Spiel, das noch nicht in der Liste ist? Erkennung über den Ordner (Steam, Epic, Riot, Xbox, GOG …)
    /// oder: Vollbild-Fenster im Vordergrund, das länger als 15 s offen ist und nicht zu einem normalen Programm gehört.
    /// </summary>
    private void DetectUnknownGame(Dictionary<string, List<Process>> running)
    {
        try
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            Process? p = null;
            foreach (var l in running.Values) { p = l.FirstOrDefault(x => x.Id == pid); if (p != null) break; }
            if (p == null || NoGame.Contains(p.ProcessName) || SystemProcs.Contains(p.ProcessName)) return;
            var key = p.ProcessName.ToLowerInvariant();
            if (Games.Any(g => g.Key == key || (g.Known?.Processes.Any(x => x.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase)) ?? false))) return;
            string? exe = null;
            try { exe = p.MainModule?.FileName; } catch { }
            bool inGameFolder = exe != null && GameFolders.Any(f => exe.Contains(f, StringComparison.OrdinalIgnoreCase));
            bool fullscreen = Native.IsFullscreen(fg);
            if (!inGameFolder && !fullscreen) { _fsCandidate = null; return; }
            if (!inGameFolder)
            {
                // Vollbild allein reicht erst nach 15 s (Videos, Präsentationen …)
                if (_fsCandidate != key) { _fsCandidate = key; _fsSince = DateTime.Now; return; }
                if ((DateTime.Now - _fsSince).TotalSeconds < 15) return;
            }
            string name;
            try { name = p.MainModule?.FileVersionInfo.ProductName?.Trim() ?? ""; } catch { name = ""; }
            if (name.Length < 2 || name.Contains("Unreal", StringComparison.OrdinalIgnoreCase)) name = p.MainWindowTitle is { Length: > 1 } t ? t : p.ProcessName;
            var root = exe == null ? null : Path.GetDirectoryName(exe);
            var bi = exe?.IndexOf(@"\Binaries\", StringComparison.OrdinalIgnoreCase) ?? -1;
            if (bi > 0) root = Path.GetDirectoryName(exe![..bi]);
            var vm = AddProfile(name, p.ProcessName, exe, root, "Erkannt", GameCatalog.Find(p.ProcessName, 0, name), null, null);
            _fsCandidate = null;
            Ui.Toast($"Spiel erkannt: {vm.Name} – Profil „{GameCatalog.PresetNames[(int)vm.P.Preset]}“ ist aktiv (boostet + gibt RAM frei)");
            Log.Info(LogModule.Boost, $"Unbekanntes Spiel erkannt ({(inGameFolder ? "Spieleordner" : "Vollbild")}): {vm.Name} · {exe}");
        }
        catch { }
    }

    // ================================================================ Sitzungen (FPS, Temperaturen) und Absturz-Erkennung

    private sealed class Session
    {
        public DateTime Start;
        public HashSet<int> Pids = new();
        public List<double> Fps = new();
        public double MaxCpu = double.NaN, MaxGpu = double.NaN;
        public bool CrashReporter;
        public double EnergyStart;
    }
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRamClean = DateTime.MinValue;

    private void TrackSession(Dictionary<string, List<Process>> running)
    {
        var snap = HardwareHub.Instance.Latest;
        bool crashUi = running.ContainsKey("CrashReportClient") || running.ContainsKey("UnrealCEFSubProcess") && running.ContainsKey("CrashReportClient")
                       || running.Keys.Any(k => k.Contains("CrashReport", StringComparison.OrdinalIgnoreCase) || k.Equals("WerFault", StringComparison.OrdinalIgnoreCase));
        foreach (var (key, se) in _sessions)
        {
            if (!_activeGames.TryGetValue(key, out var pids)) continue;
            foreach (var pid in pids)
            {
                var f = FpsCounter.Instance.Get(pid);
                if (!double.IsNaN(f.Fps) && f.Fps > 1) { se.Fps.Add(f.Fps); break; }
            }
            if (SensorSnapshot.Plausible(snap.CpuTemp)) se.MaxCpu = double.IsNaN(se.MaxCpu) ? snap.CpuTemp : Math.Max(se.MaxCpu, snap.CpuTemp);
            if (SensorSnapshot.Plausible(snap.GpuTemp)) se.MaxGpu = double.IsNaN(se.MaxGpu) ? snap.GpuTemp : Math.Max(se.MaxGpu, snap.GpuTemp);
            if (crashUi) se.CrashReporter = true;
        }
    }

    /// <summary>Während des Spielens: RAM über 85 % belegt → automatisch freigeben (höchstens alle 3 Minuten).</summary>
    private void RamWatchdog()
    {
        if (!Settings.RamWatchdog || (DateTime.Now - _lastRamClean).TotalMinutes < 3) return;
        var m = Native.Memory();
        if (m.LoadPct < 85) return;
        _lastRamClean = DateTime.Now;
        Say($"RAM zu {m.LoadPct:0} % voll – gebe Arbeitsspeicher frei");
        _ = CleanRamAsync();
    }

    private void EndSession(GameVM g)
    {
        if (!_sessions.Remove(g.Key, out var se)) return;
        if (_sessions.Count == 0) FpsCounter.Instance.Release();
        var dur = DateTime.Now - se.Start;
        if (dur.TotalSeconds < 20) { CheckCrashAsync(g, se, dur); return; }
        string fps = "";
        if (se.Fps.Count >= 5)
        {
            var sorted = se.Fps.OrderBy(x => x).ToList();
            double low = sorted.Take(Math.Max(1, sorted.Count / 100)).Average();
            fps = $" · Ø {se.Fps.Average():0} FPS · schlechteste 1 % {low:0} FPS";
        }
        var temps = (double.IsNaN(se.MaxCpu) ? "" : $" · CPU max. {se.MaxCpu:0} °C") + (double.IsNaN(se.MaxGpu) ? "" : $" · GPU max. {se.MaxGpu:0} °C");
        double wh = EnergyService.Instance.TotalWh - se.EnergyStart;
        if (wh > 1) temps += $" · {wh / 1000:0.00} kWh ≈ {EnergyService.Instance.Cost(wh):0.00} €";
        g.P.LastSession = $"{se.Start:dd.MM. HH:mm} · {(dur.TotalHours >= 1 ? $"{(int)dur.TotalHours} h {dur.Minutes} min" : $"{(int)dur.TotalMinutes} min")}{fps}{temps}";
        g.RaiseAllProps();
        SaveGames();
        Say($"{g.Name}: Sitzung {g.P.LastSession}", LogLevel.Ok);
        CheckCrashAsync(g, se, dur);
    }

    /// <summary>
    /// Ist das Spiel abgestürzt (Windows-Fehlerbericht, Grafiktreiber-Reset oder Crash-Reporter)?
    /// Bei übertakteter Grafikkarte wird die Übertaktung dann automatisch gesenkt.
    /// </summary>
    private void CheckCrashAsync(GameVM g, Session se, TimeSpan dur)
    {
        var proc = g.P.Process;
        var since = DateTime.Now - dur - TimeSpan.FromSeconds(30);
        Task.Run(() =>
        {
            string? why = se.CrashReporter ? "Absturzmeldung des Spiels" : null;
            try
            {
                var from = since.ToUniversalTime().ToString("o");
                // Grafiktreiber hat sich zurückgesetzt (TDR) oder ist abgestürzt
                var qSys = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                    $"*[System[(Provider[@Name='nvlddmkm'] or Provider[@Name='Display'] or Provider[@Name='amdkmdag'] or Provider[@Name='amdwddmg']) and (Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime>='{from}']]]");
                using (var r = new System.Diagnostics.Eventing.Reader.EventLogReader(qSys))
                    if (r.ReadEvent() is { } ev) { why ??= $"Grafiktreiber-Fehler (Ereignis {ev.Id})"; ev.Dispose(); }
                // Windows-Fehlerbericht für die Spiel-exe
                var qApp = new System.Diagnostics.Eventing.Reader.EventLogQuery("Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
                    $"*[System[(EventID=1000 or EventID=1002) and TimeCreated[@SystemTime>='{from}']]]");
                using var ra = new System.Diagnostics.Eventing.Reader.EventLogReader(qApp);
                for (var ev = ra.ReadEvent(); ev != null; ev = ra.ReadEvent())
                    using (ev)
                    {
                        string text = "";
                        try { text = string.Join(" ", ev.Properties.Select(x => x.Value?.ToString())); } catch { }
                        if (text.Contains(proc, StringComparison.OrdinalIgnoreCase)) { why ??= ev.Id == 1002 ? "Spiel hat nicht mehr reagiert" : "Windows-Fehlerbericht (Absturz)"; break; }
                    }
            }
            catch (Exception ex) { Log.Write(LogModule.Boost, "Absturz-Prüfung: " + ex.Message); }
            if (why == null) return;
            Log.Warn(LogModule.Boost, $"{g.Name} ist offenbar abgestürzt: {why}");
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                var change = Services.Tweaker.StepDownAfterCrash(g.Name);
                var f = new Finding
                {
                    Id = "crash:" + g.Key, Severity = change != null ? Severity.Warning : Severity.Hint, Areas = Area.Boost | Area.Tweaker,
                    Title = $"{g.Name} ist abgestürzt",
                    Detail = $"Erkannt: {why}." + (change != null ? " " + change : " Die Grafikkarte war nicht übertaktet."),
                    Advice = change != null ? "Im Tweaker mit dem Stabilitätstest prüfen. Unreal-Engine-5-Spiele wie ARK Ascended vertragen oft weniger Übertaktung als andere Spiele."
                                            : "Grafiktreiber aktuell? Spieldateien in Steam überprüfen lassen. Im Problemlöser nach Konflikten schauen."
                };
                f.Actions.Add(new FixAction { Label = "Zum Tweaker", Primary = true, Run = () => { Ui.Go("Tweaker"); return Task.FromResult("Geöffnet."); } });
                DiagnosticsService.Instance.Report(f);
                Ui.Toast(change != null ? "Absturz erkannt – Grafikkarten-Übertaktung wurde automatisch gesenkt" : $"{g.Name} ist abgestürzt ({why})", true);
            });
        });
    }
}
