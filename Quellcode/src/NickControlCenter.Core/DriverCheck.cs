using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NickControlCenter;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

public sealed record DriverOffer(string Kind, string Version, string ReleaseDate, string DownloadUrl, string DetailsUrl);

/// <summary>
/// Hilfsfunktionen für die Treiber-Prüfung (Netzwerkzugriff passiert in der App, hier nur das Auswerten – damit testbar).
/// NVIDIA: offizielle Treiber-Suche (dieselbe, die nvidia.com/Download benutzt) – Game Ready UND Studio.
/// AMD: Download-Seite von amd.com → aktueller Adrenalin-Installer (erkennt die Karte selbst).
/// </summary>
public static class DriverCheck
{
    public const string NvidiaProductList = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    public const string AmdDriverPage = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelAssistant = "https://www.intel.com/content/www/us/en/support/detect.html";
    public const string NvidiaAppPage = "https://www.nvidia.com/de-de/software/nvidia-app/";

    public static GpuVendor VendorFromName(string? name)
    {
        var n = (name ?? "").ToUpperInvariant();
        if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("RTX") || n.Contains("GTX") || n.Contains("QUADRO")) return GpuVendor.Nvidia;
        if (n.Contains("AMD") || n.Contains("RADEON") || n.Contains(" RX ")) return GpuVendor.Amd;
        if (n.Contains("INTEL") || n.Contains("ARC")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    /// <summary>Windows-Treiberversion (z. B. 32.0.15.6094) → NVIDIA-Version (560.94).</summary>
    public static string? NvidiaVersionFromWindows(string? winVersion)
    {
        if (string.IsNullOrWhiteSpace(winVersion)) return null;
        var parts = winVersion.Split('.');
        if (parts.Length < 4) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3], CultureInfo.InvariantCulture)}.{last5[3..]}";
    }

    /// <summary>Vergleicht Versionen wie "621.14" oder "26.10.2". >0 = a ist neuer.</summary>
    public static int Compare(string? a, string? b)
    {
        static int[] P(string? s) => (s ?? "").Split('.', '-').Select(x => int.TryParse(new string(x.TakeWhile(char.IsDigit).ToArray()), out var v) ? v : 0).ToArray();
        var x = P(a); var y = P(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int u = i < x.Length ? x[i] : 0, v = i < y.Length ? y[i] : 0;
            if (u != v) return u.CompareTo(v);
        }
        return 0;
    }

    /// <summary>NVIDIA-Produktliste (XML) → (psid, pfid) für den Kartennamen.</summary>
    public static (int psid, int pfid)? FindNvidiaProduct(string xml, string gpuName)
    {
        var wanted = Normalize(gpuName);
        (int, int)? best = null;
        int bestLen = 0;
        foreach (Match m in Regex.Matches(xml, @"<LookupValue ParentID=""(\d+)""[^>]*>\s*<Name>([^<]*)</Name>\s*<Value>(\d+)</Value>"))
        {
            var name = Normalize(System.Net.WebUtility.HtmlDecode(m.Groups[2].Value));
            if (name.Length == 0) continue;
            bool exact = name == wanted;
            // "RTX 4060" darf nicht auf "RTX 4060 Ti" passen und umgekehrt → nur exakter Treffer oder Endung
            if (exact || (wanted.EndsWith(name) && name.Length > bestLen))
            {
                best = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[3].Value));
                bestLen = name.Length;
                if (exact) break;
            }
        }
        return best;
    }

    private static string Normalize(string s)
    {
        s = s.ToUpperInvariant().Replace("NVIDIA", "").Replace("GEFORCE", "").Replace("LAPTOP GPU", "").Replace("(NOTEBOOKS)", "").Replace("  ", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public static string NvidiaLookupUrl(int psid, int pfid, bool studio, bool notebook) =>
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup" +
        $"&psid={psid}&pfid={pfid}&osID=57&languageCode=1031&dch=1&upCRD={(studio ? 1 : 0)}&sort1=0&numberOfResults=1";

    /// <summary>Antwort der NVIDIA-Treibersuche auswerten.</summary>
    public static DriverOffer? ParseNvidiaLookup(string json, string kind)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("Success").GetString() != "1") return null;
            var info = root.GetProperty("IDS")[0].GetProperty("downloadInfo");
            string Get(string p) => info.TryGetProperty(p, out var v) ? Uri.UnescapeDataString(v.GetString() ?? "") : "";
            var ver = Get("Version");
            if (ver.Length == 0) return null;
            return new DriverOffer(kind, ver, Get("ReleaseDateTime"), Get("DownloadURL"), Get("DetailsURL"));
        }
        catch { return null; }
    }

    /// <summary>AMD-Downloadseite → neuester Adrenalin-Installer (Version, URL).</summary>
    public static DriverOffer? ParseAmdPage(string html)
    {
        var m = Regex.Matches(html, @"https://drivers\.amd\.com/drivers/installer/[^""'\s]*?amd-software-adrenalin-edition-(\d+\.\d+\.\d+)-minimalsetup-\d+(?:_web)?\.exe")
                     .Cast<Match>()
                     .OrderByDescending(x => x.Groups[1].Value, Comparer<string>.Create(Compare))
                     .FirstOrDefault();
        if (m == null) return null;
        return new DriverOffer("AMD Software: Adrenalin Edition", m.Groups[1].Value, "", m.Value, AmdDriverPage);
    }
}
