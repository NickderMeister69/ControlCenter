namespace NickControlCenter;

/// <summary>Welche Bereiche der App ein anderes Programm stören kann.</summary>
[Flags]
public enum Area
{
    None = 0,
    Fans = 1,
    Rgb = 2,
    Monitor = 4,
    Stress = 8,
    Tweaker = 16,
    Boost = 32,
    System = 64,
    All = Fans | Rgb | Monitor | Stress | Tweaker | Boost | System
}

public enum Severity { Info, Hint, Warning, Problem }

/// <summary>Ein bekanntes Programm, das sich mit Teilen der App beißen kann.</summary>
public sealed record KnownProgram(
    string Id,
    string Name,
    string[] Processes,
    Area Areas,
    Severity Severity,
    string Why,
    string Advice,
    /// <summary>Teil des Anzeigenamens in "Programme und Features" (für Deinstallieren).</summary>
    string? UninstallName = null,
    /// <summary>Darf ohne Rückfrage hart beendet werden (keine ungespeicherten Daten zu erwarten).</summary>
    bool SafeToKill = true);

public static class ConflictCatalog
{
    public static readonly KnownProgram[] Programs =
    {
        // ---- eigene alte Einzel-Apps (doppelte Steuerung = Flackern / Lüfter springen)
        new("old-fancontrol", "Nick Fan Control (alte Einzel-App)", new[] { "NickFanControl" }, Area.Fans | Area.Rgb, Severity.Problem,
            "Die alte Fan-Control-App steuert Lüfter und Beleuchtung gleichzeitig mit dem Control Center. Zwei Programme senden abwechselnd Farben bzw. Lüfterwerte – das führt zu Flackern und springenden Drehzahlen.",
            "Beenden und den Autostart der alten App entfernen. Alle Funktionen sind jetzt im Control Center.", null),
        new("old-pcmonitor", "PC Monitor (alte Einzel-App)", new[] { "PcMonitor" }, Area.Monitor, Severity.Hint,
            "Die alte PC-Monitor-App liest die gleichen Sensoren – doppelte Last und manchmal springende Werte.",
            "Beenden – der Monitor ist jetzt im Control Center eingebaut."),
        new("old-tweaker", "Tweaker (alte Einzel-App)", new[] { "Tweaker" }, Area.Tweaker, Severity.Problem,
            "Der alte Tweaker setzt beim Start eigene GPU-/CPU-Werte und überschreibt damit die Einstellungen im Control Center.",
            "Beenden und den Autostart (Aufgabe „NickTweaker-Apply“) entfernen."),

        // ---- RGB-Software
        new("armoury", "ASUS Armoury Crate / Aura", new[] { "ArmouryCrate", "ArmouryCrate.Service", "ArmouryCrate.UserSessionHelper", "LightingService", "AuraService", "ArmourySocketServer", "ArmourySwAgent" },
            Area.Rgb | Area.Fans, Severity.Problem,
            "Armoury Crate steuert Beleuchtung und Lüfter des Mainboards selbst. Beide Programme schreiben abwechselnd auf den Controller – typische Ursache für Flackern.",
            "Dienst „LightingService“/„ArmouryCrateService“ beenden und deaktivieren oder Armoury Crate deinstallieren.", "Armoury Crate"),
        new("msicenter", "MSI Center / Mystic Light", new[] { "MSI.CentralServer", "MSI_Center", "MysticLight", "LEDKeeper2", "MSI_Central_Service", "Mystic_Light_Service" },
            Area.Rgb | Area.Fans, Severity.Problem,
            "MSI Center bzw. Mystic Light hält die Kontrolle über die Mainboard-Beleuchtung.",
            "Programm schließen und Dienst deaktivieren oder deinstallieren.", "MSI Center"),
        new("gigabyte", "Gigabyte Control Center / RGB Fusion", new[] { "GigabyteControlCenter", "RGBFusion", "RGBFusion2", "RGBFusion2Service", "GigabyteUpdateService", "AorusEngine", "EasyTuneEngineService", "GraphicsCardEngine", "GCCService", "GCC_Service" },
            Area.Rgb | Area.Fans, Severity.Problem,
            "Gigabyte-Software greift auf den gleichen ITE-RGB-Controller (048D:5702) zu wie das Control Center. Läuft beides, flackern einzelne Anschlüsse.",
            "Dienst beenden/deaktivieren oder Gigabyte Control Center deinstallieren.", "GIGABYTE Control Center"),
        new("asrock", "ASRock Polychrome", new[] { "PolychromeRGB", "AsrPolychromeRGB", "ASRPolychromeSync" }, Area.Rgb, Severity.Problem,
            "Polychrome steuert die Mainboard-Beleuchtung selbst.", "Schließen oder deinstallieren.", "Polychrome"),
        new("icue", "Corsair iCUE", new[] { "iCUE", "Corsair.Service", "CorsairDeviceControlService", "iCUEDevicePluginHost", "Corsair.Service.CpuIdRemote64", "CorsairCpuIdService" },
            Area.Rgb | Area.Fans | Area.Monitor, Severity.Problem,
            "iCUE belegt Corsair-Hubs und -Lüftercontroller exklusiv und liest dauerhaft Sensoren.",
            "iCUE schließen und den Dienst „Corsair Service“ beenden – oder deinstallieren.", "iCUE"),
        new("synapse", "Razer Synapse / Chroma", new[] { "RazerAppEngine", "Razer Synapse 3", "Razer Synapse Service", "RazerCentralService", "Razer Synapse Service Process", "RzSDKService", "RzChromaStreamServer", "RzChromaConnectServer" },
            Area.Rgb, Severity.Warning,
            "Synapse setzt die Razer-Beleuchtung immer wieder auf das eigene Profil zurück.",
            "Synapse schließen bzw. „Razer Chroma SDK“ deaktivieren.", "Razer Synapse"),
        new("signalrgb", "SignalRGB", new[] { "SignalRgb", "SignalRgbLauncher" }, Area.Rgb, Severity.Problem,
            "SignalRGB sendet bis zu 60× pro Sekunde eigene Farben – das Control Center und SignalRGB wechseln sich ab.",
            "SignalRGB schließen (Tray-Symbol → Beenden) oder deinstallieren.", "SignalRGB"),
        new("openrgb", "OpenRGB", new[] { "OpenRGB" }, Area.Rgb, Severity.Problem,
            "OpenRGB steuert dieselben Geräte.", "OpenRGB schließen."),
        new("cam", "NZXT CAM", new[] { "NZXT CAM", "NZXT.CAM.Service", "NZXTCAMService" }, Area.Rgb | Area.Fans | Area.Monitor, Severity.Problem,
            "CAM belegt NZXT-Controller und Kraken-Kühler.", "CAM schließen oder deinstallieren.", "NZXT CAM"),
        new("lconnect", "Lian Li L-Connect", new[] { "L-Connect 3", "L-Connect", "LConnectService" }, Area.Rgb | Area.Fans, Severity.Problem,
            "L-Connect steuert die Uni-Hubs selbst.", "L-Connect schließen oder deinstallieren.", "L-Connect"),
        new("ttrgb", "Thermaltake TT RGB Plus", new[] { "TT RGB Plus", "TTRGBPlus" }, Area.Rgb | Area.Fans, Severity.Problem,
            "TT RGB Plus steuert Thermaltake-Controller.", "Schließen oder deinstallieren.", "TT RGB Plus"),
        new("masterplus", "Cooler Master MasterPlus", new[] { "MasterPlus", "MasterPlusApp" }, Area.Rgb | Area.Fans, Severity.Warning,
            "MasterPlus steuert Cooler-Master-Beleuchtung.", "Schließen oder deinstallieren.", "MasterPlus"),
        new("ghub", "Logitech G HUB", new[] { "lghub", "lghub_agent", "lghub_system_tray" }, Area.Rgb, Severity.Hint,
            "G HUB steuert nur Logitech-Geräte. Stört meist nicht, kann aber bei „Bildschirm“-Effekten mitfärben.", "Nur bei Problemen schließen.", "G HUB", SafeToKill: true),
        new("steelseries", "SteelSeries GG", new[] { "SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesGGClient" }, Area.Rgb, Severity.Hint,
            "Steuert SteelSeries-Geräte.", "Nur bei Problemen schließen.", "SteelSeries GG"),
        new("wallpaperengine", "Wallpaper Engine", new[] { "wallpaper32", "wallpaper64", "wallpaperservice32", "webwallpaper32", "ui32" },
            Area.Rgb | Area.Stress | Area.Boost | Area.Monitor, Severity.Warning,
            "Wallpaper Engine rendert ständig im Hintergrund (GPU-Last, verfälscht Stresstest und Messwerte) und hat eigene RGB-Plugins (iCUE/Razer/LED), die Farben überschreiben können.",
            "Für Stresstest und Spiele schließen. In Wallpaper Engine unter Einstellungen → Plugins die LED-Plugins abschalten.", "Wallpaper Engine"),

        // ---- Lüfter-/Hardware-Software
        new("fancontrol-rem0o", "FanControl (Rem0o)", new[] { "FanControl" }, Area.Fans, Severity.Problem,
            "Zwei Lüftersteuerungen gleichzeitig: Die Lüfter springen zwischen beiden Werten.", "Eines der beiden Programme schließen."),
        new("argus", "Argus Monitor", new[] { "ArgusMonitor" }, Area.Fans | Area.Monitor, Severity.Problem,
            "Argus Monitor regelt Lüfter selbst.", "Schließen oder Lüftersteuerung dort abschalten.", "Argus Monitor"),
        new("speedfan", "SpeedFan", new[] { "speedfan" }, Area.Fans, Severity.Problem, "Zweite Lüftersteuerung.", "Schließen."),
        new("aida", "AIDA64", new[] { "aida64" }, Area.Monitor | Area.Stress | Area.Fans, Severity.Hint,
            "AIDA64 fragt die gleichen Sensor-Chips ab. Gleichzeitig mit dem Stresstest verfälscht es Ergebnisse.", "Für Messungen/Stresstest schließen."),
        new("hwinfo", "HWiNFO", new[] { "HWiNFO64", "HWiNFO32" }, Area.Monitor, Severity.Info,
            "HWiNFO darf parallel laufen (der Monitor kann sogar dessen Werte mitlesen). Bei sehr kurzen Abfrage-Intervallen können Sensorwerte kurz springen.", "Kein Handlungsbedarf."),
        new("afterburner", "MSI Afterburner / RivaTuner", new[] { "MSIAfterburner", "RTSS", "RTSSHooksLoader64", "EncoderServer" }, Area.Tweaker | Area.Fans | Area.Stress, Severity.Warning,
            "Afterburner setzt eigene GPU-Takt-Offsets und Lüfterkurven – das überschreibt den Tweaker. RTSS hängt sich in jedes 3D-Programm (auch in den GPU-Stresstest).",
            "Für den Tweaker Afterburner schließen bzw. dort „Beim Start anwenden“ abschalten."),
        new("precision", "EVGA Precision X1", new[] { "PrecisionX_x64", "PrecisionX" }, Area.Tweaker | Area.Fans, Severity.Warning,
            "Setzt eigene GPU-Werte.", "Schließen."),
        new("gputweak", "ASUS GPU Tweak", new[] { "GPUTweakIII", "GPUTweakII" }, Area.Tweaker | Area.Fans, Severity.Warning,
            "Setzt eigene GPU-Werte.", "Schließen."),
        new("xtu", "Intel XTU", new[] { "XtuService", "PerfTune", "XTU3SERVICE" }, Area.Tweaker, Severity.Warning,
            "Intel XTU setzt eigene CPU-Grenzen.", "XTU-Profile zurücksetzen oder XTU schließen.", "Intel(R) Extreme Tuning Utility"),
        new("ryzenmaster", "AMD Ryzen Master", new[] { "AMD Ryzen Master", "RyzenMasterSDK" }, Area.Tweaker, Severity.Hint,
            "Ryzen Master kann eigene CPU-Profile aktiv haben.", "Bei Problemen dort auf Standard zurücksetzen."),
        new("adrenalin-tune", "AMD Adrenalin (Leistungsoptimierung)", new[] { "RadeonSoftware" }, Area.Tweaker, Severity.Info,
            "Ist in Adrenalin unter „Leistung → Optimierung“ ein eigenes Profil aktiv, kann es die Werte aus dem Tweaker überschreiben.", "In Adrenalin die Optimierung auf „Standard“ stellen."),

        // ---- Leistung / Overlays
        new("overwolf", "Overwolf", new[] { "Overwolf", "OverwolfBrowser" }, Area.Boost | Area.Stress, Severity.Hint,
            "Overlay-Programm mit spürbarer Hintergrundlast.", "Beim Zocken schließen."),
        new("xboxbar", "Xbox Game Bar (Aufnahme)", new[] { "GameBarFTServer" }, Area.Boost, Severity.Info,
            "Die Hintergrundaufnahme kostet FPS.", "Im FPS-Boost „Game DVR aus“ aktivieren."),
    };

    public static KnownProgram? FindByProcess(string processName) =>
        Programs.FirstOrDefault(p => p.Processes.Any(x => string.Equals(x, processName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Alle Einträge, die zu den laufenden Prozessnamen passen.</summary>
    public static List<(KnownProgram Program, List<string> Running)> Match(IEnumerable<string> runningProcessNames)
    {
        var set = new HashSet<string>(runningProcessNames, StringComparer.OrdinalIgnoreCase);
        var res = new List<(KnownProgram, List<string>)>();
        foreach (var p in Programs)
        {
            var hits = p.Processes.Where(set.Contains).ToList();
            if (hits.Count > 0) res.Add((p, hits));
        }
        return res;
    }
}
