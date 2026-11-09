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

public sealed partial class BoostService
{
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
        // ARK (UE4) hat eigene Schalter für Bloom/Lichtstrahlen
        if (g.Known?.Id == "ase" && g.P.GfxQuality == 1) { ini.Set(us, "bDisableBloom", "True"); ini.Set(us, "bDisableLightShafts", "True"); }
        int res = GameCatalog.ResolutionScales[Math.Clamp(g.P.ResScale, 0, GameCatalog.ResolutionScales.Length - 1)];
        if (res > 0) { ini.Set("ScalabilityGroups", "sg.ResolutionQuality", res.ToString()); parts.Add($"Auflösung {res} %"); }
        ini.Save(gus);

        if (g.EngineIniOk && (g.P.PostFxOff || g.P.VolumetricsOff))
        {
            BackupIni(eng, g.Key);
            var e = IniFile.Load(eng);
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
        // exe-Optimierungen für den nächsten Start sicherstellen
        if (g.P.ExeTweaks || g.P.Priority > 0) Task.Run(() => GameExeTweaks(g));
        Say($"🎮 {g.Name} gestartet → {(done.Count == 0 ? "nichts zu tun" : string.Join(", ", done))}", LogLevel.Ok);
        Sort();
    }

    private void GameStopped(GameVM g)
    {
        _activeGames.Remove(g.Key);
        g.Running = false;
        Say($"{g.Name} beendet");
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
        Sort();
    }

    private void StopAll()
    {
        foreach (var key in _activeGames.Keys.ToList())
            if (Games.FirstOrDefault(x => x.Key == key) is { } g) GameStopped(g); else _activeGames.Remove(key);
    }
}
