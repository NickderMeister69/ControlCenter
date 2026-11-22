using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace NickControlCenter;

public sealed class SpeedResult
{
    public DateTime Time { get; set; }
    public double PingMs { get; set; }
    public double JitterMs { get; set; }
    public double DownMbit { get; set; }
    public double UpMbit { get; set; }
    public string Line => $"{Time:dd.MM. HH:mm}   ↓ {DownMbit:0} Mbit/s   ↑ {UpMbit:0} Mbit/s   Ping {PingMs:0} ms";
}

public sealed class SpeedHistory { public List<SpeedResult> Results { get; set; } = new(); }

/// <summary>
/// Internet-Geschwindigkeit messen (Server von Cloudflare, wie speed.cloudflare.com): Ping/Jitter, Download und Upload
/// mit mehreren parallelen Verbindungen. Ergebnisse werden für den Verlauf gespeichert.
/// </summary>
public static class SpeedTest
{
    private const string Base = "https://speed.cloudflare.com/";
    public static SpeedHistory History { get; } = JsonStore.Load<SpeedHistory>("speedtest.json");

    private static HttpClient NewClient() => new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1), MaxConnectionsPerServer = 8 }) { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<SpeedResult> RunAsync(IProgress<(string Phase, double Value)> progress, CancellationToken ct)
    {
        using var http = NewClient();
        var res = new SpeedResult { Time = DateTime.Now };

        // Ping: 12 kleine Anfragen, Median; Jitter = mittlere Abweichung
        progress.Report(("Ping", double.NaN));
        var pings = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            var sw = Stopwatch.StartNew();
            using var r = await http.GetAsync(Base + "__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            if (i > 1) pings.Add(sw.Elapsed.TotalMilliseconds); // erste Verbindungen (TLS-Aufbau) nicht zählen
        }
        pings.Sort();
        res.PingMs = pings[pings.Count / 2];
        res.JitterMs = pings.Zip(pings.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(0).Average();
        progress.Report(("Ping", res.PingMs));

        res.DownMbit = await Measure(http, down: true, progress, ct);
        res.UpMbit = await Measure(http, down: false, progress, ct);

        History.Results.Insert(0, res);
        if (History.Results.Count > 50) History.Results.RemoveRange(50, History.Results.Count - 50);
        JsonStore.Save("speedtest.json", History);
        InstallEstimate.Report((long)(res.DownMbit * 1e6 / 8 * 8), TimeSpan.FromSeconds(8));
        Log.Ok(LogModule.App, $"Speedtest: ↓ {res.DownMbit:0} Mbit/s, ↑ {res.UpMbit:0} Mbit/s, Ping {res.PingMs:0} ms, Jitter {res.JitterMs:0.0} ms");
        return res;
    }

    /// <summary>8 Sekunden lang mit 6 parallelen Verbindungen laden bzw. hochladen, die ersten 1,5 s (Anlauf) nicht werten.</summary>
    private static async Task<double> Measure(HttpClient http, bool down, IProgress<(string, double)> progress, CancellationToken ct)
    {
        string phase = down ? "Download" : "Upload";
        long total = 0;
        var sw = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(8);
        long warmBytes = -1; double warmT = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var chunk = new byte[4 << 20];
        Random.Shared.NextBytes(chunk);
        var workers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            var buf = new byte[1 << 16];
            while (sw.Elapsed < duration && !cts.IsCancellationRequested)
            {
                try
                {
                    if (down)
                    {
                        using var r = await http.GetAsync(Base + "__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        await using var s = await r.Content.ReadAsStreamAsync(cts.Token);
                        int n;
                        while ((n = await s.ReadAsync(buf, cts.Token)) > 0) { Interlocked.Add(ref total, n); if (sw.Elapsed >= duration) break; }
                    }
                    else
                    {
                        var content = new ProgressContent(chunk, n => Interlocked.Add(ref total, n));
                        using var r = await http.PostAsync(Base + "__up", content, cts.Token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200); }
            }
        }, cts.Token)).ToList();

        while (sw.Elapsed < duration)
        {
            await Task.Delay(250, ct);
            var t = sw.Elapsed.TotalSeconds;
            long b = Interlocked.Read(ref total);
            if (warmBytes < 0 && t >= 1.5) { warmBytes = b; warmT = t; }
            if (warmBytes >= 0 && t > warmT + 0.3) progress.Report((phase, (b - warmBytes) * 8 / (t - warmT) / 1e6));
        }
        double end = sw.Elapsed.TotalSeconds;
        long final = Interlocked.Read(ref total);
        cts.Cancel();
        try { await Task.WhenAll(workers); } catch { }
        double mbit = warmBytes >= 0 && end > warmT ? (final - warmBytes) * 8 / (end - warmT) / 1e6 : final * 8 / end / 1e6;
        progress.Report((phase, mbit));
        return mbit;
    }

    /// <summary>Upload-Inhalt, der mitzählt, wie viel schon gesendet wurde.</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly byte[] _data; private readonly Action<int> _sent;
        public ProgressContent(byte[] data, Action<int> sent) { _data = data; _sent = sent; Headers.ContentType = new("application/octet-stream"); }
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            for (int o = 0; o < _data.Length; o += 64 * 1024)
            {
                int n = Math.Min(64 * 1024, _data.Length - o);
                await stream.WriteAsync(_data.AsMemory(o, n));
                _sent(n);
            }
        }
        protected override bool TryComputeLength(out long length) { length = _data.Length; return true; }
    }
}
