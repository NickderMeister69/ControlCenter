namespace NickControlCenter;

public enum RegKind { DWord, String }

/// <summary>Ein Registry-Wert, den eine Optimierung setzt. Key z. B. "HKCU\Software\Microsoft\GameBar".</summary>
public sealed record RegValue(string Key, string Name, object Value, RegKind Kind = RegKind.DWord);

/// <summary>Eine Windows-Optimierung im FPS-Boost. Jede Änderung wird vorher gesichert und ist rückgängig machbar.</summary>
public sealed record Tweak(
    string Id,
    string Group,
    string Title,
    string Description,
    bool DefaultOn,
    bool NeedsRestart,
    RegValue[] Values,
    /// <summary>Sonderfall statt (nur) Registry: "power", "nagle", "timer".</summary>
    string? Special = null,
    /// <summary>Experten-Option (standardmäßig aus, mit Hinweis).</summary>
    bool Expert = false);

public sealed record GameDef(string Short, string Name, string Folder, string ExeRel, string ConfigRel, string Process, int AppId);

/// <summary>Programme, die man vor dem Zocken schließen kann.</summary>
public sealed record BackgroundApp(string Process, string Name, bool Kill);

public static class BoostCatalog
{
    private const string MM = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string Games = MM + @"\Tasks\Games";
    private const string GCS = @"HKCU\System\GameConfigStore";

    public static readonly Tweak[] Tweaks =
    {
        new("power", "Windows", "Energieplan: Höchstleistung",
            "CPU taktet nicht mehr herunter, keine Energiespar-Verzögerungen. Auf Laptops der größte Effekt, am Desktop für gleichmäßigere Frametimes.",
            true, false, Array.Empty<RegValue>(), "power"),
        new("gamemode", "Windows", "Windows-Spielmodus an",
            "Windows priorisiert das laufende Spiel und verschiebt Updates/Hintergrundaufgaben.",
            true, false, new[] { new RegValue(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1), new RegValue(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 1) }),
        new("gamedvr", "Windows", "Xbox-Hintergrundaufnahme (Game DVR) aus",
            "Die ständige Hintergrund-Videoaufnahme kostet spürbar FPS. Die Game Bar selbst bleibt nutzbar.",
            true, false, new[] { new RegValue(GCS, "GameDVR_Enabled", 0), new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0) }),
        new("hags", "Grafik", "Hardwarebeschleunigte GPU-Planung",
            "Die Grafikkarte verwaltet ihren Speicher selbst – etwas weniger Latenz (NVIDIA, AMD und Intel). Wirkt nach Neustart.",
            true, true, new[] { new RegValue(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2) }),
        new("priority", "Windows", "Spiele-Priorität (CPU & GPU)",
            "Gibt Spielen im Windows-Multimedia-Planer höchste CPU- und GPU-Priorität.",
            true, false, new[] { new RegValue(MM, "SystemResponsiveness", 10), new RegValue(Games, "GPU Priority", 8), new RegValue(Games, "Priority", 6),
                                 new RegValue(Games, "Scheduling Category", "High", RegKind.String), new RegValue(Games, "SFIO Priority", "High", RegKind.String) }),
        new("powerthrottle", "Windows", "Energiedrosselung (Power Throttling) aus",
            "Windows bremst Hintergrundprozesse nicht mehr künstlich – gut, wenn Spiel-Launcher oder Discord im Hintergrund laufen.",
            true, false, new[] { new RegValue(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1) }),
        new("netthrottle", "Netzwerk", "Netzwerk-Drosselung aus",
            "Windows bremst Netzwerkpakete nicht mehr bei Multimedia-Last. Wirkt nach Neustart.",
            true, true, new[] { new RegValue(MM, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF)) }),
        new("nagle", "Netzwerk", "Niedriger Ping (Nagle-Algorithmus aus)",
            "Netzwerkpakete werden sofort gesendet statt gesammelt – hilft bei manchen Online-Spielen. Wirkt nach Neustart.",
            true, true, Array.Empty<RegValue>(), "nagle"),
        new("mouse", "Eingabe", "Mausbeschleunigung aus",
            "1:1-Mausbewegung für präzises Zielen („Zeigerbeschleunigung verbessern“ aus).",
            true, false, new[] { new RegValue(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0", RegKind.String), new RegValue(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0", RegKind.String),
                                 new RegValue(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0", RegKind.String) }, "mouse"),
        new("stickykeys", "Eingabe", "Einrastfunktion-Popups aus",
            "Kein „Einrastfunktion aktivieren?“-Fenster mehr, wenn man im Spiel 5× Shift drückt (ab nächster Anmeldung).",
            true, false, new[] { new RegValue(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "506", RegKind.String),
                                 new RegValue(@"HKCU\Control Panel\Accessibility\ToggleKeys", "Flags", "58", RegKind.String),
                                 new RegValue(@"HKCU\Control Panel\Accessibility\Keyboard Response", "Flags", "122", RegKind.String) }),
        new("visual", "Windows", "Visuelle Effekte reduzieren",
            "Transparenz und Animationen aus – entlastet Desktop und GPU. Teilweise erst nach Neuanmeldung.",
            false, false, new[] { new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2),
                                  new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0),
                                  new RegValue(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "0", RegKind.String) }),
        new("bgapps", "Windows", "Hintergrund-Apps blockieren",
            "Store-Apps dürfen nicht mehr im Hintergrund laufen (spart CPU und RAM).",
            true, false, new[] { new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1) }),
        new("notify", "Windows", "Benachrichtigungen aus",
            "Keine Windows-Popups mehr, die dich aus dem Spiel reißen.",
            false, false, new[] { new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0) }),
        new("fso", "Grafik", "Vollbild-Optimierungen aus (Experte)",
            "Echtes Exklusiv-Vollbild für alle Spiele. Hilft bei manchen älteren Spielen, bei neuen meist ohne Effekt – lieber testen.",
            false, false, new[] { new RegValue(GCS, "GameDVR_FSEBehaviorMode", 2), new RegValue(GCS, "GameDVR_HonorUserFSEBehaviorMode", 1),
                                  new RegValue(GCS, "GameDVR_FSEBehavior", 2), new RegValue(GCS, "GameDVR_DXGIHonorFSEWindowsCompatible", 1) }, null, true),
        new("timer", "Neu", "Timer-Auflösung 0,5 ms beim Zocken",
            "Windows-Zeitgeber läuft während das Control Center offen ist mit 0,5 ms statt 15,6 ms – gleichmäßigere Frametimes und weniger Input-Lag. Wird beim Beenden automatisch zurückgesetzt.",
            true, false, Array.Empty<RegValue>(), "timer"),
        new("globaltimer", "Neu", "Timer-Auflösung systemweit erlauben (Windows 11)",
            "Ab Windows 11 gilt eine gesetzte Timer-Auflösung nur noch für das eigene Fenster. Dieser Schalter erlaubt sie wieder systemweit (für das Spiel). Wirkt nach Neustart.",
            true, true, new[] { new RegValue(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1) }),
    };

    public static readonly GameDef[] ArkGames =
    {
        new("ASE", "ARK: Survival Evolved", "ARK", @"ShooterGame\Binaries\Win64\ShooterGame.exe", @"ShooterGame\Saved\Config\WindowsNoEditor", "ShooterGame", 346110),
        new("ASA", "ARK: Survival Ascended", "ARK Survival Ascended", @"ShooterGame\Binaries\Win64\ArkAscended.exe", @"ShooterGame\Saved\Config\Windows", "ArkAscended", 2399830),
    };

    public static readonly BackgroundApp[] BackgroundApps =
    {
        new("chrome", "Google Chrome", false), new("msedge", "Microsoft Edge", false), new("firefox", "Firefox", false),
        new("opera", "Opera / Opera GX", false), new("brave", "Brave", false),
        new("EpicGamesLauncher", "Epic Games Launcher", true), new("EADesktop", "EA App", true), new("upc", "Ubisoft Connect", true),
        new("Battle.net", "Battle.net", true), new("GalaxyClient", "GOG Galaxy", true), new("RiotClientServices", "Riot Client", true),
        new("OneDrive", "OneDrive (Synchronisierung)", true), new("Dropbox", "Dropbox", true), new("ms-teams", "Microsoft Teams", true),
        new("Teams", "Microsoft Teams (klassisch)", true), new("Spotify", "Spotify", true), new("WhatsApp", "WhatsApp", true),
        new("Telegram", "Telegram", true), new("Overwolf", "Overwolf", true), new("Creative Cloud", "Adobe Creative Cloud", true),
        new("wallpaper32", "Wallpaper Engine", true), new("wallpaper64", "Wallpaper Engine (64 Bit)", true),
        new("Discord", "Discord (Achtung: Sprachchat)", true),
    };

    /// <summary>Prozesse, die beim RAM-Aufräumen nie angefasst werden (Spiele, Anti-Cheat, System).</summary>
    public static readonly string[] NeverTouch =
    {
        "ShooterGame", "ShooterGame_BE", "ArkAscended", "ArkAscended_BE", "BEService", "BEService_x64", "EasyAntiCheat", "EasyAntiCheat_EOS",
        "vgc", "vgtray", "FACEIT", "faceitservice", "Idle", "System", "Registry", "audiodg", "dwm", "csrss", "smss", "wininit", "winlogon",
        "lsass", "services", "MsMpEng", "Memory Compression"
    };

    /// <summary>ARK-Grafik: Werte je Preset (Index 1 = max. FPS, 2 = ausgewogen).</summary>
    public static Dictionary<string, string> ArkScalability(int preset, bool asa)
    {
        var sg = new Dictionary<string, string>();
        if (preset == 1)
        {
            sg["sg.ViewDistanceQuality"] = "1"; sg["sg.AntiAliasingQuality"] = "0"; sg["sg.ShadowQuality"] = "0";
            sg["sg.PostProcessQuality"] = "0"; sg["sg.TextureQuality"] = "1"; sg["sg.EffectsQuality"] = "0"; sg["sg.FoliageQuality"] = "0";
            if (asa) { sg["sg.ShadingQuality"] = "0"; sg["sg.GlobalIlluminationQuality"] = "0"; sg["sg.ReflectionQuality"] = "0"; }
        }
        else if (preset == 2)
        {
            sg["sg.ViewDistanceQuality"] = "2"; sg["sg.AntiAliasingQuality"] = "2"; sg["sg.ShadowQuality"] = "1";
            sg["sg.PostProcessQuality"] = "1"; sg["sg.TextureQuality"] = "2"; sg["sg.EffectsQuality"] = "1"; sg["sg.FoliageQuality"] = "1";
            if (asa) { sg["sg.ShadingQuality"] = "1"; sg["sg.GlobalIlluminationQuality"] = "1"; sg["sg.ReflectionQuality"] = "1"; }
        }
        return sg;
    }

    public static readonly int[] FpsLimits = { -1, 0, 60, 75, 120, 144, 165, 240 };
    public static readonly int[] ResolutionScales = { -1, 100, 90, 80, 75, 70, 60 };
}
