using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NickControlCenter;

public enum FanMode
{
    Auto = 0,     // BIOS / Mainboard regelt selbst (z. B. CPU-Lüfter, Pumpe)
    Minimum = 1,  // App hält den Lüfter auf der niedrigsten Stufe, bei der er sich noch dreht
    Pump = 2,     // Wasserkühlungs-Pumpe: feste Leistung (Standard 30 %)
    Manual = 3,   // Feste Leistung, vom Nutzer eingestellt
    Curve = 4     // App regelt nach CPU-Temperatur (kühl = leise, warm = schnell)
}

public sealed class FanSetting
{
    public string? CustomName { get; set; }
    public FanMode Mode { get; set; } = FanMode.Auto;
    /// <summary>Kalibrierter Mindestwert in %. -1 = noch nicht kalibriert.</summary>
    public int MinPercent { get; set; } = -1;
    /// <summary>Feste Leistung für Pumpen in %.</summary>
    public int PumpPercent { get; set; } = 30;
    /// <summary>Feste Leistung im Modus "Manuell" in %.</summary>
    public int ManualPercent { get; set; } = 50;
    /// <summary>Temperatur-Kurve: Mindestleistung in %.</summary>
    public int CurveMinPercent { get; set; } = 30;
    /// <summary>Temperatur-Kurve: bis hierhin (°C) Mindestleistung.</summary>
    public int CurveLowC { get; set; } = 50;
    /// <summary>Temperatur-Kurve: ab hier (°C) 100 %.</summary>
    public int CurveHighC { get; set; } = 80;
    /// <summary>true, wenn der Nutzer den Modus dieses Lüfters selbst gewählt hat.</summary>
    public bool UserChosen { get; set; }
}

/// <summary>Einstellungen für Lüfter und Beleuchtung (luefter.json). Übernimmt beim ersten Start die Daten der alten Fan-Control-App.</summary>
public sealed class AppConfig
{
    public Dictionary<string, FanSetting> Fans { get; set; } = new();
    public RgbConfig Rgb { get; set; } = new();
    public bool Paused { get; set; }
    /// <summary>Ab dieser CPU-/GPU-Temperatur laufen alle geregelten Lüfter auf 100 %.</summary>
    public int SafetyTempC { get; set; } = 85;
    /// <summary>Unter dieser Temperatur übernimmt die App wieder.</summary>
    public int SafetyResumeC { get; set; } = 75;
    /// <summary>Ab dieser Temperatur drehen auch die Minimum-Lüfter stufenlos hoch.</summary>
    public int RampStartC { get; set; } = 70;
    public bool FirstRunDone { get; set; }
    public int ConfigVersion { get; set; }
    /// <summary>Erst wenn der Nutzer die Lüfter zugeordnet hat, wird automatisch kalibriert.</summary>
    public bool FanSetupConfirmed { get; set; }
    /// <summary>Auch Anschlüsse ohne Lüfter anzeigen.</summary>
    public bool ShowEmptyFans { get; set; }
    /// <summary>Gewählter Sensor für die CPU-Anzeige (null = automatisch: Ø P-Kerne, HWiNFO falls aktiv).</summary>
    public string? CpuSensorId { get; set; }
    /// <summary>Gewählter Sensor für die GPU-Anzeige (null = automatisch: GPU Core).</summary>
    public string? GpuSensorId { get; set; }
    /// <summary>Lüfterprofil: "Leise", "Normal", "Leistung" (nur Anzeige/Schnellwahl).</summary>
    public string Profile { get; set; } = "Normal";

    [JsonIgnore] public static string Folder => JsonStore.Folder;
    private const string FileName = "luefter.json";

    public static AppConfig Load()
    {
        var file = Path.Combine(JsonStore.Folder, FileName);
        AppConfig? cfg = null;
        if (File.Exists(file)) cfg = JsonStore.Load<AppConfig>(FileName);
        else
        {
            // Übernahme aus der alten Einzel-App
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NickFanControl", "config.json");
            try
            {
                if (File.Exists(old))
                {
                    cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(old), JsonStore.Options);
                    Log.Ok(LogModule.Fans, "Einstellungen der alten Fan-Control-App übernommen (Lüfter, Namen, Beleuchtung).");
                }
            }
            catch (Exception ex) { Log.Warn(LogModule.Fans, "Alte Fan-Control-Einstellungen nicht lesbar: " + ex.Message); }
        }
        cfg ??= new AppConfig();
        cfg.Fans ??= new();
        cfg.Rgb ??= new();
        cfg.Rgb.Zones ??= new();
        cfg.Rgb.Global ??= new();
        cfg.Rgb.IgnoredConflicts ??= new();
        cfg.Rgb.SavedColors ??= new();
        cfg.Rgb.Profiles ??= new();
        return cfg;
    }

    public void Save() => JsonStore.Save(FileName, this);

    public FanSetting Get(string id)
    {
        if (!Fans.TryGetValue(id, out var s))
        {
            s = new FanSetting();
            Fans[id] = s;
        }
        return s;
    }
}
