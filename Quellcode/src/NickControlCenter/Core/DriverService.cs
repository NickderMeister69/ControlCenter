using System.Collections.ObjectModel;
using System.IO;
using System.Management;
using System.Net.Http;
using Microsoft.Win32;

namespace NickControlCenter;

public sealed class DriverRow : ObservableObject
{
    public required string Title { get; init; }
    public string Installed { get; set; } = "–";
    public string Latest { get; set; } = "–";
    public string Date { get; set; } = "";
    public DriverOffer? Offer { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool IsCurrent { get; set; }
    public string StateText => Offer == null ? "Nicht geprüft" : UpdateAvailable ? "Update verfügbar" : "Aktuell";
    public System.Windows.Media.Brush StateBrush => Offer == null ? Palette.Muted : UpdateAvailable ? Palette.Warn : Palette.Good;
    private double _progress;
    public double Progress { get => _progress; set { Set(ref _progress, value); Raise(nameof(Downloading)); } }
    public bool Downloading => _progress > 0 && _progress < 100;
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    public void Refresh() => RaiseAll(nameof(Installed), nameof(Latest), nameof(Date), nameof(UpdateAvailable), nameof(StateText), nameof(StateBrush), nameof(IsCurrent));
}

/// <summary>
/// Prüft Grafiktreiber: NVIDIA (Game Ready UND Studio, offizielle NVIDIA-Treibersuche), sonst AMD (Adrenalin) bzw. Intel.
/// Lädt auf Wunsch den Installer in "Downloads" und startet ihn.
/// </summary>
public sealed class DriverService : ObservableObject
{
    private static readonly HttpClient Http = CreateClient();
    public ObservableCollection<DriverRow> Rows { get; } = new();
    public string GpuName { get; private set; } = "";
    public GpuVendor Vendor { get; private set; }
    public string InstalledVersion { get; private set; } = "–";
    private string _status = "Noch nicht geprüft";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private bool _checking;
    public bool Checking { get => _checking; private set => Set(ref _checking, value); }
    public bool UpdateAvailable => Rows.Any(r => r.UpdateAvailable && r.IsCurrent);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NickControlCenter/1.0");
        return c;
    }

    private static (string Name, string WinVersion, DateTime? Date)? DetectGpu()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, AdapterCompatibility FROM Win32_VideoController");
            var list = new List<(string, string, DateTime?)>();
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var name = o["Name"]?.ToString() ?? "";
                    DateTime? date = null;
                    try { if (o["DriverDate"] is string d && d.Length >= 8) date = ManagementDateTimeConverter.ToDateTime(d); } catch { }
                    list.Add((name, o["DriverVersion"]?.ToString() ?? "", date));
                }
            // dedizierte Karte bevorzugen (NVIDIA/AMD vor Intel)
            return list.OrderBy(x => DriverCheck.VendorFromName(x.Item1) switch { GpuVendor.Nvidia => 0, GpuVendor.Amd => 1, GpuVendor.Intel => 2, _ => 3 }).FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? AmdInstalledAdrenalin()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AMD\CN");
            return (k?.GetValue("RadeonSoftwareVersion") ?? k?.GetValue("DriverVersion")) as string;
        }
        catch { return null; }
    }

    public async Task CheckAsync(bool silent)
    {
        if (Checking) return;
        Checking = true;
        Status = "Prüfe …";
        try
        {
            var gpu = await Task.Run(DetectGpu);
            if (gpu == null) { Status = "Keine Grafikkarte gefunden."; return; }
            GpuName = gpu.Value.Name;
            Vendor = DriverCheck.VendorFromName(GpuName);
            var rows = new List<DriverRow>();

            if (Vendor == GpuVendor.Nvidia)
            {
                InstalledVersion = DriverCheck.NvidiaVersionFromWindows(gpu.Value.WinVersion) ?? gpu.Value.WinVersion;
                var xml = await Http.GetStringAsync(DriverCheck.NvidiaProductList);
                var prod = DriverCheck.FindNvidiaProduct(xml, GpuName);
                if (prod == null) { Status = $"„{GpuName}“ ist in der NVIDIA-Treiberliste nicht zu finden."; }
                else
                {
                    foreach (var (studio, title) in new[] { (false, "GeForce Game Ready-Treiber"), (true, "NVIDIA Studio-Treiber") })
                    {
                        var json = await Http.GetStringAsync(DriverCheck.NvidiaLookupUrl(prod.Value.psid, prod.Value.pfid, studio, GpuName.Contains("Laptop")));
                        var offer = DriverCheck.ParseNvidiaLookup(json, title);
                        var row = new DriverRow { Title = title, Installed = InstalledVersion, Offer = offer, Latest = offer?.Version ?? "–", Date = offer?.ReleaseDate ?? "" };
                        row.UpdateAvailable = offer != null && DriverCheck.Compare(offer.Version, InstalledVersion) > 0;
                        // Game Ready gilt als "aktuell" für die Warnung, Studio ist eine Alternative
                        row.IsCurrent = !studio;
                        rows.Add(row);
                    }
                    Status = $"{GpuName} · installiert {InstalledVersion}";
                }
            }
            else if (Vendor == GpuVendor.Amd)
            {
                InstalledVersion = AmdInstalledAdrenalin() ?? gpu.Value.WinVersion;
                var html = await Http.GetStringAsync(DriverCheck.AmdDriverPage);
                var offer = DriverCheck.ParseAmdPage(html);
                var row = new DriverRow { Title = "AMD Software: Adrenalin Edition", Installed = InstalledVersion, Offer = offer, Latest = offer?.Version ?? "–", IsCurrent = true };
                // Adrenalin-Versionen (26.10.2) mit Registry-Wert vergleichen; ist nur die Windows-Treiberversion bekannt, kein Vergleich möglich
                row.UpdateAvailable = offer != null && InstalledVersion.Count(c => c == '.') == 2 && DriverCheck.Compare(offer.Version, InstalledVersion) > 0;
                if (offer != null && InstalledVersion.Count(c => c == '.') != 2) row.Status = "Installierte Adrenalin-Version unbekannt – der AMD-Installer erkennt selbst, ob ein Update nötig ist.";
                rows.Add(row);
                Status = $"{GpuName} · installiert {InstalledVersion}";
            }
            else
            {
                InstalledVersion = gpu.Value.WinVersion;
                rows.Add(new DriverRow { Title = "Intel / andere Grafik", Installed = InstalledVersion, Latest = "über Intel-Assistent", IsCurrent = true });
                Status = $"{GpuName} · Treiber {InstalledVersion} – Intel-Treiber über den „Intel Driver & Support Assistant“.";
            }

            Rows.Clear();
            foreach (var r in rows) { r.Refresh(); Rows.Add(r); }
            Raise(nameof(UpdateAvailable)); RaiseAll(nameof(GpuName), nameof(InstalledVersion), nameof(Vendor));
            Log.Info(LogModule.App, "Treiberprüfung: " + Status + " · " + string.Join(", ", rows.Select(r => $"{r.Title}: {r.Latest} ({r.StateText})")));

            var main = rows.FirstOrDefault(r => r.IsCurrent && r.UpdateAvailable);
            if (main != null)
            {
                var f = new Finding
                {
                    Id = "drv:update", Title = $"Grafiktreiber-Update: {main.Latest} (installiert {main.Installed})", Severity = Severity.Hint, Areas = Area.Boost | Area.System,
                    Detail = $"Für {GpuName} gibt es einen neueren {main.Title}. Neue Treiber bringen oft mehr FPS und Fehlerbehebungen für aktuelle Spiele.",
                    Advice = "Auf der Seite „Treiber“ herunterladen und installieren."
                };
                f.Actions.Add(new FixAction { Label = "Zur Treiber-Seite", Icon = "", Primary = true, Run = () => { Ui.Go("Treiber"); return Task.FromResult("Geöffnet."); } });
                DiagnosticsService.Instance.Report(f);
                if (!silent) Ui.Toast("Treiber-Update verfügbar: " + main.Latest);
            }
            else DiagnosticsService.Instance.Clear("drv:update");
        }
        catch (Exception ex)
        {
            Status = "Prüfung fehlgeschlagen (keine Internetverbindung?): " + ex.Message;
            Log.Warn(LogModule.App, "Treiberprüfung: " + ex.Message);
        }
        finally { Checking = false; }
    }

    /// <summary>Installer in den Download-Ordner laden und starten.</summary>
    public async Task DownloadAndInstallAsync(DriverRow row)
    {
        if (row.Offer == null || string.IsNullOrEmpty(row.Offer.DownloadUrl)) { Env.Open(row.Offer?.DetailsUrl ?? DriverCheck.AmdDriverPage); return; }
        try
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var file = Path.Combine(downloads, Path.GetFileName(new Uri(row.Offer.DownloadUrl).LocalPath));
            row.Status = "Lade herunter …";
            row.Progress = 0.1;
            using var req = new HttpRequestMessage(HttpMethod.Get, row.Offer.DownloadUrl);
            req.Headers.Referrer = new Uri(Vendor == GpuVendor.Amd ? "https://www.amd.com/" : "https://www.nvidia.com/");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(file))
            {
                var buf = new byte[1 << 16];
                long done = 0; int n; var last = DateTime.MinValue;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if ((DateTime.Now - last).TotalMilliseconds > 250)
                    {
                        last = DateTime.Now;
                        row.Progress = total > 0 ? Math.Max(0.1, done * 100.0 / total) : 50;
                        row.Status = total > 0 ? $"{Fmt.Size(done)} von {Fmt.Size(total)}" : Fmt.Size(done);
                    }
                }
            }
            row.Progress = 100;
            row.Status = "Heruntergeladen – Installer wird gestartet …";
            Log.Ok(LogModule.App, $"Treiber geladen: {file}");
            Env.Open(file);
            row.Status = $"Installer gestartet ({Path.GetFileName(file)} im Download-Ordner).";
        }
        catch (Exception ex)
        {
            row.Progress = 0;
            row.Status = "Download fehlgeschlagen: " + ex.Message + " – öffne die Download-Seite.";
            Log.Warn(LogModule.App, "Treiber-Download: " + ex.Message);
            Env.Open(row.Offer.DetailsUrl.Length > 0 ? row.Offer.DetailsUrl : DriverCheck.AmdDriverPage);
        }
    }
}
