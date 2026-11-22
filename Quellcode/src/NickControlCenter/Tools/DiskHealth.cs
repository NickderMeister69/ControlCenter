using System.Text.Json;

namespace NickControlCenter;

/// <summary>Zustand einer SSD/Festplatte (aus Windows: Get-PhysicalDisk + Zuverlässigkeitszähler/SMART).</summary>
public sealed class DiskHealthInfo
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public long Size { get; init; }
    public string WindowsHealth { get; init; } = "";
    public double Temp { get; init; } = double.NaN;
    public double TempMax { get; init; } = double.NaN;
    /// <summary>Verschleiß in % (0 = neu, 100 = Lebensdauer laut Hersteller erreicht). NaN = meldet die Platte nicht.</summary>
    public double Wear { get; init; } = double.NaN;
    public double PowerOnHours { get; init; } = double.NaN;
    public long ReadErrors { get; init; }
    public long WriteErrors { get; init; }

    /// <summary>0 gut, 1 beobachten, 2 kritisch</summary>
    public int Level => WindowsHealth is "Unhealthy" || Wear >= 90 || (ReadErrors + WriteErrors) > 50 ? 2
                      : WindowsHealth is "Warning" || Wear >= 70 || (ReadErrors + WriteErrors) > 0 || Temp >= 70 ? 1 : 0;
    public System.Windows.Media.Brush LevelBrush => Level switch { 2 => Palette.Bad, 1 => Palette.Warn, _ => Palette.Good };
    public string LevelText => Level switch { 2 => "Kritisch – Daten sichern!", 1 => "Beobachten", _ => "Gesund" };
    public string Life => double.IsNaN(Wear) ? "Restlebensdauer: meldet die Platte nicht" : $"Restlebensdauer ≈ {Math.Max(0, 100 - Wear):0} %";
    public double LifeShare => double.IsNaN(Wear) ? 0 : Math.Clamp((100 - Wear) / 100, 0, 1);
    public bool HasLife => !double.IsNaN(Wear);
    public string Details
    {
        get
        {
            var p = new List<string> { Kind, Fmt.Bytes(Size) };
            if (!double.IsNaN(Temp)) p.Add($"{Temp:0} °C" + (double.IsNaN(TempMax) || TempMax <= 0 ? "" : $" (max. {TempMax:0} °C)"));
            if (!double.IsNaN(PowerOnHours)) p.Add($"{PowerOnHours:N0} Betriebsstunden (≈ {PowerOnHours / 24 / 365:0.#} Jahre)");
            p.Add(ReadErrors + WriteErrors == 0 ? "keine Lese-/Schreibfehler" : $"{ReadErrors} Lese- / {WriteErrors} Schreibfehler");
            return string.Join(" · ", p);
        }
    }
    public string Advice => Level switch
    {
        2 => "Die Platte zeigt ernste Probleme. Wichtige Daten SOFORT sichern und die Platte bald ersetzen.",
        1 when !double.IsNaN(Wear) && Wear >= 70 => "Ein großer Teil der Lebensdauer ist verbraucht – kein Notfall, aber Backups machen und Ersatz einplanen.",
        1 when Temp >= 70 => "Läuft sehr warm – Kühlkörper/Belüftung prüfen (NVMe-SSDs drosseln ab ca. 70–80 °C).",
        1 => "Es gab Lese-/Schreibfehler. Im Auge behalten und wichtige Daten sichern.",
        _ => "Alles in Ordnung."
    };
    public bool HasAdvice => Level > 0;
}

public static class DiskHealth
{
    public static List<DiskHealthInfo> Read()
    {
        const string ps = "$ErrorActionPreference='SilentlyContinue'; Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter; " +
                          "[pscustomobject]@{ Name=$_.FriendlyName; Media=[string]$_.MediaType; Bus=[string]$_.BusType; Size=$_.Size; Health=[string]$_.HealthStatus; " +
                          "Temp=$r.Temperature; TempMax=$r.TemperatureMax; Wear=$r.Wear; Hours=$r.PowerOnHours; RErr=$r.ReadErrorsTotal; WErr=$r.WriteErrorsTotal } } | ConvertTo-Json -Compress";
        var r = Env.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"", 30_000);
        var list = new List<DiskHealthInfo>();
        var txt = r.Output.Trim();
        if (txt.Length == 0) return list;
        if (txt.StartsWith('{')) txt = "[" + txt + "]";
        try
        {
            using var d = JsonDocument.Parse(txt);
            foreach (var e in d.RootElement.EnumerateArray())
            {
                string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                double N(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
                var bus = S("Bus"); var media = S("Media");
                string kind = bus == "NVMe" ? "NVMe-SSD" : media == "SSD" ? "SATA-SSD" : media == "HDD" ? "Festplatte (HDD)" : bus == "USB" ? "USB-Laufwerk" : "Laufwerk";
                double temp = N("Temp"), tmax = N("TempMax");
                list.Add(new DiskHealthInfo
                {
                    Name = S("Name"), Kind = kind, Size = (long)(double.IsNaN(N("Size")) ? 0 : N("Size")), WindowsHealth = S("Health"),
                    Temp = temp > 0 ? temp : double.NaN, TempMax = tmax, Wear = N("Wear"), PowerOnHours = N("Hours"),
                    ReadErrors = double.IsNaN(N("RErr")) ? 0 : (long)N("RErr"), WriteErrors = double.IsNaN(N("WErr")) ? 0 : (long)N("WErr")
                });
            }
        }
        catch (Exception ex) { Log.Warn(LogModule.Diagnose, "Laufwerks-Gesundheit: " + ex.Message); }
        Log.Info(LogModule.Diagnose, "Laufwerks-Gesundheit: " + string.Join(" · ", list.Select(l => $"{l.Name}: {l.LevelText}" + (double.IsNaN(l.Wear) ? "" : $", Verschleiß {l.Wear:0} %"))));
        return list;
    }
}
