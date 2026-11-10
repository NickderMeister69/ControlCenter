namespace NickControlCenter;

/// <summary>Ein Spiel mit seinem Drehwinkel pro Maus-Zählschritt („Yaw“) bei Empfindlichkeit 1.</summary>
public sealed record SensGame(string Name, double Yaw, string Hint = "");

/// <summary>
/// Maus-Empfindlichkeit zwischen Spielen umrechnen. Grundlage: Grad pro Maus-Zählschritt = Empfindlichkeit × Yaw.
/// Gleiche cm/360° = gleiches Gefühl beim Zielen.
/// </summary>
public static class AimTools
{
    public static readonly SensGame[] Games =
    {
        new("VALORANT", 0.07),
        new("Counter-Strike 2", 0.022),
        new("Apex Legends", 0.022, "Gleiche Werte wie CS2"),
        new("Overwatch 2", 0.0066),
        new("Call of Duty (MW/BO, Legacy)", 0.0066, "Nur mit „Monitor Distance Coefficient“ 0 bzw. Legacy-Empfindlichkeit"),
        new("Fortnite (Wert in %)", 0.005555, "Fortnite zeigt die Empfindlichkeit in % – hier den %-Wert eingeben"),
        new("Team Fortress 2 / Source", 0.022),
        new("Quake Champions", 0.022),
    };

    /// <summary>Empfindlichkeit von Spiel A in Spiel B umrechnen (gleiche Drehung pro Mausweg).</summary>
    public static double Convert(double sens, SensGame from, SensGame to) => sens * from.Yaw / to.Yaw;

    /// <summary>Zentimeter Mausweg für eine volle 360°-Drehung.</summary>
    public static double CmPer360(double sens, int dpi, SensGame game)
    {
        if (sens <= 0 || dpi <= 0) return double.NaN;
        double counts = 360 / (sens * game.Yaw);
        return counts / dpi * 2.54;
    }

    /// <summary>Einordnung der cm/360° – grobe Richtwerte aus der Szene.</summary>
    public static string Judge(double cm) => double.IsNaN(cm) ? "" :
        cm < 20 ? "sehr schnell – typisch für Fortnite/Apex-Tracking, für taktische Shooter meist zu hektisch" :
        cm < 35 ? "schnell – gut für Apex, Overwatch, Fortnite" :
        cm < 60 ? "mittel – das nutzen viele VALORANT- und CS2-Profis" :
        "langsam – sehr präzise, braucht viel Platz auf dem Mauspad";

    /// <summary>Gemessene Ereignisse/s auf die nächste übliche Abtastrate runden.</summary>
    public static int NearestPollingRate(double eventsPerSecond)
    {
        int[] rates = { 125, 250, 500, 1000, 2000, 4000, 8000 };
        if (eventsPerSecond <= 0) return 0;
        return rates.OrderBy(r => Math.Abs(Math.Log(r / eventsPerSecond))).First();
    }
}
