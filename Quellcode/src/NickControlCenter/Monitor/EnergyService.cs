namespace NickControlCenter;

public sealed class EnergySettings
{
    /// <summary>Strompreis in Euro pro kWh.</summary>
    public double PricePerKwh { get; set; } = 0.35;
    /// <summary>Grundverbrauch für Mainboard, RAM, SSDs, Lüfter, Pumpe, RGB (W).</summary>
    public double BaseWatts { get; set; } = 45;
    /// <summary>Netzteil-Wirkungsgrad (0,85 = 85 %).</summary>
    public double PsuEfficiency { get; set; } = 0.87;
    /// <summary>Wh je Tag (yyyy-MM-dd → Wh).</summary>
    public Dictionary<string, double> Days { get; set; } = new();
}

/// <summary>
/// Stromverbrauch des PCs: CPU- und GPU-Leistung kommen von den Sensoren, der Rest (Mainboard, RAM, Laufwerke, Lüfter)
/// ist ein einstellbarer Grundwert. Dazu kommen die Verluste des Netzteils. Ergebnis = Schätzung „aus der Steckdose“.
/// </summary>
public sealed class EnergyService : ObservableObject
{
    public static EnergyService Instance { get; } = new();
    public EnergySettings Settings { get; } = JsonStore.Load<EnergySettings>("strom.json");
    private DateTime _last = DateTime.MinValue, _lastSave = DateTime.Now;

    public double NowWatts { get; private set; } = double.NaN;
    public double CpuWatts { get; private set; } = double.NaN;
    public double GpuWatts { get; private set; } = double.NaN;
    /// <summary>Gesamte gezählte Energie seit App-Start (Wh) – für Spielsitzungen.</summary>
    public double TotalWh { get; private set; }

    private EnergyService() { HardwareHub.Instance.Updated += OnHub; }

    public void Save() => JsonStore.Save("strom.json", Settings);

    public double Cost(double wh) => wh / 1000 * Settings.PricePerKwh;

    private void OnHub(SensorSnapshot s)
    {
        var now = DateTime.Now;
        double cpu = s.CpuPower is > 0 and < 500 ? s.CpuPower : double.NaN;
        double gpu = s.GpuPower is > 0 and < 800 ? s.GpuPower : double.NaN;
        CpuWatts = cpu; GpuWatts = gpu;
        double dc = (double.IsNaN(cpu) ? 25 : cpu) + (double.IsNaN(gpu) ? 15 : gpu) + Settings.BaseWatts;
        NowWatts = dc / Math.Clamp(Settings.PsuEfficiency, 0.5, 1);
        if (_last != DateTime.MinValue)
        {
            double h = (now - _last).TotalHours;
            if (h > 0 && h < 0.05) // Lücken (Standby) nicht mitzählen
            {
                double wh = NowWatts * h;
                TotalWh += wh;
                var key = now.ToString("yyyy-MM-dd");
                lock (Settings) Settings.Days[key] = (Settings.Days.TryGetValue(key, out var v) ? v : 0) + wh;
            }
        }
        _last = now;
        if ((now - _lastSave).TotalMinutes >= 2)
        {
            _lastSave = now;
            lock (Settings)
            {
                // nur die letzten 400 Tage behalten
                foreach (var k in Settings.Days.Keys.Where(k => string.CompareOrdinal(k, now.AddDays(-400).ToString("yyyy-MM-dd")) < 0).ToList()) Settings.Days.Remove(k);
                Save();
            }
        }
    }

    public double TodayWh => Day(DateTime.Now);
    public double Day(DateTime d) { lock (Settings) return Settings.Days.TryGetValue(d.ToString("yyyy-MM-dd"), out var v) ? v : 0; }

    public double MonthWh
    {
        get
        {
            var p = DateTime.Now.ToString("yyyy-MM");
            lock (Settings) return Settings.Days.Where(kv => kv.Key.StartsWith(p)).Sum(kv => kv.Value);
        }
    }

    /// <summary>Hochrechnung auf 30 Tage aus dem Schnitt der letzten (bis zu) 14 Tage mit Daten.</summary>
    public double MonthForecastWh
    {
        get
        {
            lock (Settings)
            {
                var days = Settings.Days.OrderByDescending(k => k.Key).Skip(1).Take(14).Select(k => k.Value).ToList(); // heute (unvollständig) weglassen
                if (days.Count == 0) return TodayWh / Math.Max(0.05, DateTime.Now.TimeOfDay.TotalDays) * 30;
                return days.Average() * 30;
            }
        }
    }

    public string Summary =>
        double.IsNaN(NowWatts) ? "Wird gemessen …" :
        $"Jetzt ≈ {NowWatts:0} W (CPU {Fmt0(CpuWatts)} · GPU {Fmt0(GpuWatts)} · Rest {Settings.BaseWatts:0} W)\n" +
        $"Heute {TodayWh / 1000:0.00} kWh ≈ {Cost(TodayWh):0.00} € · Diesen Monat {MonthWh / 1000:0.0} kWh ≈ {Cost(MonthWh):0.00} €\n" +
        $"Hochrechnung 30 Tage: {MonthForecastWh / 1000:0} kWh ≈ {Cost(MonthForecastWh):0.00} €";

    private static string Fmt0(double w) => double.IsNaN(w) ? "–" : $"{w:0} W";
}
