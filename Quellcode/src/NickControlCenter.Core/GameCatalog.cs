namespace NickControlCenter;

/// <summary>
/// Ein Spiel, das die App kennt: Prozessname(n), wo die Grafik-Config liegt, ob man Dateien anfassen darf
/// (Anti-Cheat / Cloud-Einstellungen), Startoptionen und Tipps.
/// </summary>
public sealed record KnownGame(
    string Id,
    string Name,
    string[] Processes,
    int SteamAppId = 0,
    /// <summary>"UE4", "UE5", "Source2", "Andere"</summary>
    string Engine = "",
    /// <summary>Pfad zur GameUserSettings.ini. Platzhalter: {root} = Spielordner, %LOCALAPPDATA%. null = keine Config-Änderungen.</summary>
    string? ConfigPath = null,
    /// <summary>Darf die Engine.ini (Bewegungsunschärfe usw.) geändert werden? Bei Anti-Cheat-Spielen nein.</summary>
    bool EngineIniOk = false,
    string? LaunchOptions = null,
    /// <summary>Ordner mit Intro-Videos (relativ zum Spielordner), die übersprungen werden können.</summary>
    string? IntroFolder = null,
    bool Competitive = false,
    string Hint = "");

public enum GamePreset { MaxFps = 0, Balanced = 1, Custom = 2, Off = 3 }

/// <summary>Einstellungen pro Spiel – was beim Spielstart passiert und was in die Spiel-Config geschrieben wird.</summary>
public sealed class GameProfile
{
    /// <summary>Eindeutig: Prozessname in Kleinbuchstaben.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Process { get; set; } = "";
    public string? Exe { get; set; }
    public string? Root { get; set; }
    public int SteamAppId { get; set; }
    /// <summary>Start-Link (steam://, com.epicgames.launcher:// oder Riot-Befehl), falls nicht über die exe.</summary>
    public string? LaunchUri { get; set; }
    public string Source { get; set; } = "Manuell";
    public string? KnownId { get; set; }
    /// <summary>Vom Nutzer entfernt (taucht bei der automatischen Suche nicht wieder auf).</summary>
    public bool Hidden { get; set; }

    public GamePreset Preset { get; set; } = GamePreset.Off;
    /// <summary>0 = normal, 1 = erhöht, 2 = hoch</summary>
    public int Priority { get; set; }
    public bool ExeTweaks { get; set; }
    public bool Timer { get; set; }
    public bool PowerPlan { get; set; }
    public bool CloseApps { get; set; }
    public bool CleanRam { get; set; }
    public bool PauseTiles { get; set; }

    // Grafik (nur wenn eine Unreal-Config gefunden wurde und Änderungen erlaubt sind)
    public bool Graphics { get; set; }
    /// <summary>0 = nicht ändern, 1 = max. FPS, 2 = ausgewogen, 3 = hohe Qualität</summary>
    public int GfxQuality { get; set; }
    /// <summary>Index in <see cref="GameCatalog.FpsLimits"/></summary>
    public int FpsLimit { get; set; }
    /// <summary>Index in <see cref="GameCatalog.ResolutionScales"/></summary>
    public int ResScale { get; set; }
    public bool VsyncOff { get; set; }
    public bool PostFxOff { get; set; }
    public bool VolumetricsOff { get; set; }
    public bool SkipIntros { get; set; }

    public bool Active => Preset != GamePreset.Off;
}

public static class GameCatalog
{
    public static readonly int[] FpsLimits = { -1, 0, 60, 75, 120, 144, 165, 240 };
    public static readonly string[] FpsLimitNames = { "Nicht ändern", "Unbegrenzt", "60", "75", "120", "144", "165", "240" };
    public static readonly int[] ResolutionScales = { -1, 100, 90, 80, 75, 70, 60 };
    public static readonly string[] ResolutionNames = { "Nicht ändern", "100 %", "90 %", "80 %", "75 %", "70 %", "60 %" };
    public static readonly string[] QualityNames = { "Nicht ändern", "Maximale FPS (niedrig)", "Ausgewogen (mittel)", "Schön (hoch)" };
    public static readonly string[] PriorityNames = { "Normal", "Erhöht", "Hoch" };
    public static readonly string[] PresetNames = { "Max-FPS", "Ausgewogen", "Eigene", "Aus" };

    private const string LA = "%LOCALAPPDATA%";

    public static readonly KnownGame[] Games =
    {
        new("valorant", "VALORANT", new[] { "VALORANT-Win64-Shipping" }, 0, "UE4", Competitive: true,
            Hint: "VALORANT speichert die Grafik in deinem Riot-Konto und hat den Anti-Cheat Vanguard – deshalb ändert die App hier keine Spieldateien, nur Windows-Seite (Priorität, Timer, Energieplan …). " +
                  "Im Spiel für max. FPS: NVIDIA Reflex „An + Boost“, Multithreaded Rendering an, Material-/Textur-/Detailqualität niedrig, Vignette, Bloom, Verzerrung und Schatten aus, V-Sync aus."),
        new("fortnite", "Fortnite", new[] { "FortniteClient-Win64-Shipping" }, 0, "UE5", $@"{LA}\FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini", Competitive: true,
            Hint: "Größter FPS-Gewinn: im Spiel Rendermodus „Leistung“ wählen. Dazu NVIDIA Reflex „An + Boost“. Fortnite muss geschlossen sein, wenn die Grafik-Config geschrieben wird."),
        new("cs2", "Counter-Strike 2", new[] { "cs2" }, 730, "Source2", LaunchOptions: "+fps_max 0 -fullscreen", Competitive: true,
            Hint: "CS2 speichert die Grafik selbst – die App ändert nur Windows-Seite. Im Spiel: „Spielerkontrast verstärken“ an, Shader-Details niedrig, NVIDIA Reflex „An + Boost“."),
        new("dota2", "Dota 2", new[] { "dota2" }, 570, "Source2", LaunchOptions: "-high +fps_max 0", Competitive: true),
        new("apex", "Apex Legends", new[] { "r5apex", "r5apex_dx12" }, 1172470, "Source", LaunchOptions: "-novid +fps_max unlimited", Competitive: true,
            Hint: "Anti-Cheat-Spiel: keine Datei-Änderungen. Im Spiel Adaptive Auflösung aus, Umgebungsverdeckung und Volumetrisches Licht aus."),
        new("ase", "ARK: Survival Evolved", new[] { "ShooterGame" }, 346110, "UE4", @"{root}\ShooterGame\Saved\Config\WindowsNoEditor\GameUserSettings.ini",
            EngineIniOk: true, LaunchOptions: "-USEALLAVAILABLECORES -nosplash -high", IntroFolder: @"ShooterGame\Content\Movies",
            Hint: "ARK ist sehr CPU-lastig – viele FPS bringt vor allem: Schatten und Gras niedrig, Sichtweite mittel. Spiel muss beim Schreiben der Config geschlossen sein."),
        new("asa", "ARK: Survival Ascended", new[] { "ArkAscended" }, 2399830, "UE5", @"{root}\ShooterGame\Saved\Config\Windows\GameUserSettings.ini",
            EngineIniOk: true, LaunchOptions: "-USEALLAVAILABLECORES -nosplash",
            Hint: "Unreal Engine 5: Globale Beleuchtung und volumetrische Wolken kosten am meisten. DLSS/FSR im Spiel auf „Qualität“ bringt zusätzlich viel."),
        new("rocketleague", "Rocket League", new[] { "RocketLeague" }, 252950, "UE3", Competitive: true,
            Hint: "Im Spiel: Rendering-Qualität „High Performance“, Weltdetail „Performance“, Max FPS 240/360 und V-Sync aus."),
        new("lol", "League of Legends", new[] { "League of Legends" }, 0, "Andere", Competitive: true,
            Hint: "Riot Vanguard läuft mit – keine Datei-Änderungen. Im Spiel Bildrate auf „Unbegrenzt“ oder 240, Schatten aus."),
        new("overwatch", "Overwatch 2", new[] { "Overwatch" }, 2357570, "Andere", Competitive: true,
            Hint: "Im Spiel: Reflex „An + Boost“, Renderskalierung 100 %, Schattendetails niedrig, Dynamische Reflexionen aus."),
        new("r6", "Rainbow Six Siege", new[] { "RainbowSix", "RainbowSix_Vulkan", "RainbowSixGame" }, 359550, "Andere", Competitive: true,
            Hint: "Vulkan-Version läuft oft schneller als DirectX. Reflex „An + Boost“."),
        new("cod", "Call of Duty", new[] { "cod" }, 1938090, "Andere", Competitive: true,
            Hint: "Anti-Cheat Ricochet – keine Datei-Änderungen. Im Spiel „On-Demand-Texturstreaming“ aus spart Bandbreite, Reflex „An + Boost“."),
        new("pubg", "PUBG: Battlegrounds", new[] { "TslGame" }, 578080, "UE4", Competitive: true,
            Hint: "BattlEye – keine Datei-Änderungen. Im Spiel Schatten/Laub/Effekte sehr niedrig, Sichtweite mittel."),
        new("rust", "Rust", new[] { "RustClient" }, 252490, "Unity", Competitive: true),
        new("tarkov", "Escape from Tarkov", new[] { "EscapeFromTarkov" }, 0, "Unity", Competitive: true),
        new("finals", "The Finals", new[] { "Discovery" }, 2073850, "UE5", Competitive: true),
        new("rivals", "Marvel Rivals", new[] { "Marvel-Win64-Shipping" }, 2767030, "UE5", Competitive: true),
        new("dbd", "Dead by Daylight", new[] { "DeadByDaylight-Win64-Shipping", "DeadByDaylight-EGS-Shipping" }, 381210, "UE4"),
        new("destiny2", "Destiny 2", new[] { "destiny2" }, 1085660, "Andere", Competitive: true),
        new("bf2042", "Battlefield 2042", new[] { "BF2042" }, 1517290, "Andere", Competitive: true),
        new("helldivers2", "Helldivers 2", new[] { "helldivers2" }, 553850, "Andere"),
        new("warframe", "Warframe", new[] { "Warframe.x64" }, 230410, "Andere"),
        new("gta5", "GTA V", new[] { "GTA5", "GTA5_Enhanced" }, 271590, "Andere"),
        new("cyberpunk", "Cyberpunk 2077", new[] { "Cyberpunk2077" }, 1091500, "Andere",
            Hint: "DLSS/FSR „Qualität“ + Frame Generation bringen hier am meisten. Pfadverfolgung (Path Tracing) kostet extrem viel."),
        new("eldenring", "Elden Ring", new[] { "eldenring" }, 1245620, "Andere",
            Hint: "Elden Ring ist auf 60 FPS begrenzt – Priorität und Timer sorgen hier vor allem für gleichmäßigere Bilder."),
        new("palworld", "Palworld", new[] { "Palworld-Win64-Shipping", "Palworld-WinGDK-Shipping" }, 1623730, "UE5", $@"{LA}\Pal\Saved\Config\Windows\GameUserSettings.ini", EngineIniOk: true),
        new("satisfactory", "Satisfactory", new[] { "FactoryGameSteam-Win64-Shipping", "FactoryGameEGS-Win64-Shipping", "FactoryGame-Win64-Shipping" }, 526870, "UE5",
            $@"{LA}\FactoryGame\Saved\Config\Windows\GameUserSettings.ini", EngineIniOk: true),
        new("ets2", "Euro Truck Simulator 2", new[] { "eurotrucks2" }, 227300, "Andere"),
        new("minecraft", "Minecraft (Bedrock)", new[] { "Minecraft.Windows" }, 0, "Andere",
            Hint: "Java-Edition: im Launcher mehr RAM zuweisen bringt oft mehr als Windows-Tweaks."),
    };

    /// <summary>Bekanntes Spiel zu einem Prozess-/exe-Namen, einer Steam-AppId oder einem Spielnamen finden.</summary>
    public static KnownGame? Find(string? process = null, int steamAppId = 0, string? name = null)
    {
        if (steamAppId > 0)
        {
            var bySteam = Games.FirstOrDefault(g => g.SteamAppId == steamAppId);
            if (bySteam != null) return bySteam;
        }
        if (!string.IsNullOrWhiteSpace(process))
        {
            var p = System.IO.Path.GetFileNameWithoutExtension(process.Trim());
            var byProc = Games.FirstOrDefault(g => g.Processes.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)));
            if (byProc != null) return byProc;
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            var n = Norm(name);
            return Games.FirstOrDefault(g => Norm(g.Name) == n) ?? Games.FirstOrDefault(g => n.Length >= 4 && Norm(g.Name).StartsWith(n));
        }
        return null;
    }

    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Unreal-Spiele: der Projektname steckt im exe-Namen ("Palworld-Win64-Shipping" → "Palworld").</summary>
    public static string? UnrealProjectName(string exeOrProcess)
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(exeOrProcess);
        int i = n.IndexOf("-Win64-", StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = n.IndexOf("-WinGDK-", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? n[..i] : null;
    }

    /// <summary>Profil auf eine Voreinstellung setzen – abhängig vom Spiel (Wettkampf-Shooter vs. Einzelspieler, Config verfügbar).</summary>
    public static void ApplyPreset(GameProfile p, GamePreset preset, KnownGame? k, bool configAvailable, bool engineIniOk)
    {
        p.Preset = preset;
        switch (preset)
        {
            case GamePreset.MaxFps:
                p.Priority = 2; p.ExeTweaks = true; p.Timer = true; p.PowerPlan = true; p.CloseApps = true; p.CleanRam = true; p.PauseTiles = true;
                p.Graphics = configAvailable; p.GfxQuality = configAvailable ? 1 : 0; p.VsyncOff = true; p.PostFxOff = engineIniOk; p.VolumetricsOff = false;
                p.FpsLimit = 1; p.ResScale = 0; p.SkipIntros = k?.IntroFolder != null;
                break;
            case GamePreset.Balanced:
                p.Priority = 1; p.ExeTweaks = true; p.Timer = true; p.PowerPlan = true; p.CloseApps = false; p.CleanRam = true; p.PauseTiles = false;
                p.Graphics = configAvailable; p.GfxQuality = configAvailable ? 2 : 0; p.VsyncOff = true; p.PostFxOff = engineIniOk; p.VolumetricsOff = false;
                p.FpsLimit = 0; p.ResScale = 0; p.SkipIntros = k?.IntroFolder != null;
                break;
            case GamePreset.Off:
                p.Priority = 0; p.ExeTweaks = false; p.Timer = false; p.PowerPlan = false; p.CloseApps = false; p.CleanRam = false; p.PauseTiles = false;
                p.Graphics = false; p.SkipIntros = false;
                break;
        }
    }

    /// <summary>Empfohlene Voreinstellung für ein Spiel.</summary>
    public static GamePreset Recommended(KnownGame? k) => k == null ? GamePreset.Off : k.Competitive ? GamePreset.MaxFps : GamePreset.Balanced;

    /// <summary>Unreal-Scalability-Werte (sg.*) für eine Qualitätsstufe. 0 = nichts ändern.</summary>
    public static Dictionary<string, string> Scalability(int quality)
    {
        var sg = new Dictionary<string, string>();
        string? v = quality switch { 1 => "0", 2 => "1", 3 => "3", _ => null };
        if (v == null) return sg;
        foreach (var k in new[] { "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.PostProcessQuality", "sg.EffectsQuality", "sg.FoliageQuality",
                                  "sg.ShadingQuality", "sg.GlobalIlluminationQuality", "sg.ReflectionQuality" })
            sg[k] = v;
        // Sichtweite und Texturen nicht ganz runter – kostet kaum FPS, sieht sonst sehr schlecht aus
        sg["sg.ViewDistanceQuality"] = quality switch { 1 => "1", 2 => "2", _ => "3" };
        sg["sg.TextureQuality"] = quality switch { 1 => "1", 2 => "2", _ => "3" };
        return sg;
    }

    /// <summary>Abschnitt der Benutzereinstellungen in einer GameUserSettings.ini finden (je Spiel anders benannt).</summary>
    public static string UserSettingsSection(IniFile ini)
    {
        string? found = null;
        foreach (var line in ini.Lines)
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) { found = t[1..^1]; continue; }
            if (found != null && found.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) &&
                (t.StartsWith("bUseVSync", StringComparison.OrdinalIgnoreCase) || t.StartsWith("FrameRateLimit", StringComparison.OrdinalIgnoreCase)))
                return found;
        }
        return "/Script/Engine.GameUserSettings";
    }
}
