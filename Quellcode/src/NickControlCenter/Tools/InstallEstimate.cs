using System.Diagnostics;
using System.Net.Http;

namespace NickControlCenter;

/// <summary>
/// Größe und Dauer von Installationen abschätzen: Download (gemessene Internet-Geschwindigkeit) + typische Installationszeit.
/// </summary>
public static class InstallEstimate
{
    private static readonly HttpClient Http = Create();
    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ControlCenter/1.0");
        return c;
    }

    /// <summary>Download-Geschwindigkeit in Mbit/s (Standard 50, bis gemessen).</summary>
    public static double Mbit { get; private set; } = 50;
    public static bool Measured { get; private set; }
    private static DateTime _measuredAt = DateTime.MinValue;
    public static event Action? SpeedChanged;

    public static string SpeedText => Measured ? $"gemessen: {Mbit:0} Mbit/s" : $"angenommen: {Mbit:0} Mbit/s";

    /// <summary>Echte Downloads melden ihre Geschwindigkeit hier (macht die Schätzung genauer).</summary>
    public static void Report(long bytes, TimeSpan time)
    {
        if (bytes < (2L << 20) || time.TotalSeconds < 1) return;
        Set(bytes * 8 / time.TotalSeconds / 1e6);
    }

    private static void Set(double mbit)
    {
        if (mbit < 0.5 || mbit > 10_000) return;
        Mbit = Measured ? Mbit * 0.4 + mbit * 0.6 : mbit;
        Measured = true; _measuredAt = DateTime.Now;
        SpeedChanged?.Invoke();
    }

    /// <summary>Geschwindigkeit messen: bis zu 4 s bzw. 25 MB einer großen Datei laden (wird nicht gespeichert).</summary>
    public static async Task MeasureAsync(string? url = null, string? referer = null)
    {
        if (Measured && (DateTime.Now - _measuredAt).TotalMinutes < 30) return;
        url ??= "https://speed.cloudflare.com/__down?bytes=25000000";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (referer != null) req.Headers.Referrer = new Uri(referer);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return;
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[1 << 16];
            long total = 0; var sw = Stopwatch.StartNew();
            int n;
            while (sw.Elapsed.TotalSeconds < 4 && total < 25_000_000 && (n = await s.ReadAsync(buf, cts.Token)) > 0)
            {
                total += n;
                if (total < 512 * 1024) sw.Restart(); // Verbindungsaufbau nicht mitzählen
            }
            if (sw.Elapsed.TotalSeconds > 0.3) Set(total * 8 / sw.Elapsed.TotalSeconds / 1e6);
            Log.Info(LogModule.App, $"Internet-Geschwindigkeit für Schätzungen: {Mbit:0} Mbit/s");
        }
        catch { }
    }

    /// <summary>Dateigröße über den Server ermitteln (ohne Download).</summary>
    public static async Task<long> SizeOfAsync(string url, string? referer = null)
    {
        try
        {
            foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
            {
                using var req = new HttpRequestMessage(method, url);
                if (referer != null) req.Headers.Referrer = new Uri(referer);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is > 0 and var len) return len;
            }
        }
        catch { }
        return 0;
    }

    public static TimeSpan Duration(long bytes, double installSeconds) =>
        TimeSpan.FromSeconds((bytes > 0 ? bytes * 8 / (Mbit * 1e6) : 0) + installSeconds);

    /// <summary>„ca. 3 min“ – grob gerundet, weil es eine Schätzung ist.</summary>
    public static string DurationText(TimeSpan t) =>
        t.TotalSeconds < 45 ? "unter 1 min" :
        t.TotalMinutes < 60 ? $"ca. {Math.Max(1, Math.Round(t.TotalMinutes)):0} min" :
        $"ca. {(int)t.TotalHours} h {t.Minutes:00} min";

    /// <summary>„690 MB · ca. 4 min“</summary>
    public static string Text(long bytes, double installSeconds) =>
        (bytes > 0 ? Fmt.Bytes(bytes) : "Größe unbekannt") + " · " + DurationText(Duration(bytes, installSeconds));

    // typische Installationszeiten (ohne Download)
    public static double AppInstallSeconds(long bytes) => 20 + bytes / 1e6 * 0.12;
    public static double DriverInstallSeconds(long bytes) => 25 + bytes / 1e6 * 0.25;
    public const double GpuDriverInstallSeconds = 240;
}
