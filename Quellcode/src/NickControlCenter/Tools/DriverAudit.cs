using System.Management;

namespace NickControlCenter;

public sealed class HwDriver : ObservableObject
{
    /// <summary>Hardware-IDs des Geräts (zum Zuordnen von Windows-Update-Treibern).</summary>
    public string HardwareId { get; init; } = "";
    /// <summary>Passendes Treiber-Update aus Windows Update (COM-Objekt IUpdate), falls vorhanden.</summary>
    public WuDriver? Update { get => _update; set { _update = value; RaiseAll(nameof(HasUpdate), nameof(ButtonText), nameof(UpdateText), nameof(HasUpdateText)); } }
    private WuDriver? _update;
    public bool HasUpdate => _update != null && !_update.Installed;
    public string UpdateText => _update == null ? "" : $"Neuer Treiber über Windows Update: {_update.DateText}" + (_update.Installed ? " – installiert ✔" : $"  ·  {InstallEstimate.Text(_update.Bytes, InstallEstimate.DriverInstallSeconds(_update.Bytes))}");
    public bool HasUpdateText => _update != null;
    public string ButtonText => HasUpdate ? "Installieren" : "Beim Hersteller laden";
    public bool HasButton => HasUpdate || Link.Length > 0;
    private string _status = "";
    public string Status { get => _status; set { Set(ref _status, value); Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    private bool _busy;
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;
    public void Refresh() => RaiseAll(nameof(HasUpdate), nameof(ButtonText), nameof(UpdateText), nameof(HasButton), nameof(ShownAdvice), nameof(HasShownAdvice), nameof(ShownBrush), nameof(ShowLink));

    private bool _checked;
    /// <summary>Windows Update wurde gefragt.</summary>
    public bool Checked { get => _checked; set { _checked = value; Refresh(); } }
    /// <summary>Nach der Prüfung: ohne Update = aktuell (grün), auch wenn das Datum alt ist – Chipsatz-Treiber z. B. ändern sich jahrelang nicht.</summary>
    public string ShownAdvice => _checked && !HasUpdate && _update == null
        ? (Level > 0 ? "Kein neuerer Treiber verfügbar – passt so. (Ältere Datumsangaben sind bei diesen Geräten normal.)" : "")
        : Advice;
    public bool HasShownAdvice => ShownAdvice.Length > 0;
    public System.Windows.Media.Brush ShownBrush => _checked && _update == null ? Palette.Good : HasUpdate ? Palette.Warn : LevelBrush;
    public bool ShowLink => !_checked && HasLink;
    public required string Category { get; init; }
    public required string Device { get; init; }
    public string Maker { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public bool Generic { get; init; }
    public string LinkText { get; init; } = "";
    public string Link { get; init; } = "";
    public bool HasLink => Link.Length > 0;
    public string Advice { get; init; } = "";
    public bool HasAdvice => Advice.Length > 0;
    /// <summary>0 ok, 1 Hinweis, 2 alt</summary>
    public int Level { get; init; }
    public System.Windows.Media.Brush LevelBrush => Level switch { 2 => Palette.Warn, 1 => Palette.Muted, _ => Palette.Good };
    public string AgeText => Date is { } d ? $"{d:dd.MM.yyyy} ({Age(d)})" : "Datum unbekannt";
    public string Line => $"{Maker} · Version {Version} · {AgeText}";
    private static string Age(DateTime d)
    {
        var m = (int)((DateTime.Now - d).TotalDays / 30.4);
        return m < 1 ? "neu" : m < 12 ? $"{m} Monate alt" : $"{m / 12.0:0.#} Jahre alt";
    }
}

/// <summary>
/// Prüft die Treiber für Chipsatz, Audio, Netzwerk/WLAN, Bluetooth, Speicher-Controller und USB.
/// Eine automatische Versionsprüfung ist nur bei Grafikkarten möglich (die Hersteller haben dafür Schnittstellen);
/// hier werden Alter und Standardtreiber erkannt und der passende Hersteller-Download verlinkt.
/// </summary>
public static class DriverAudit
{
    public static string Board { get; private set; } = "";
    public static string BoardUrl { get; private set; } = "";

    private static readonly (string Class, string Category)[] Classes =
    {
        ("SYSTEM", "Chipsatz"), ("MEDIA", "Audio"), ("NET", "Netzwerk / WLAN"), ("BLUETOOTH", "Bluetooth"),
        ("SCSIADAPTER", "Speicher-Controller"), ("HDC", "Speicher-Controller"), ("USB", "USB-Controller"),
    };

    // Chipsatz-Geräte, die wirklich einen Hersteller-Treiber haben (der Rest unter SYSTEM ist Windows-Kram)
    private static readonly string[] ChipsetWords =
    {
        "chipset", "management engine", "serial io", "smbus", "gpio", "psp", "pci express root", "dynamic tuning", "gna",
        "thermal", "pmf", "i2c", "ppm", "amd pci", "amd smbus", "amd gpio", "amd psp", "platform", "ipf", "dptf", "rapid storage"
    };

    public static List<HwDriver> Scan()
    {
        var list = new List<HwDriver>();
        string boardMaker = "", boardProduct = "";
        try
        {
            using var b = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject o in b.Get()) using (o) { boardMaker = o["Manufacturer"]?.ToString()?.Trim() ?? ""; boardProduct = o["Product"]?.ToString()?.Trim() ?? ""; }
        }
        catch { }
        Board = $"{boardMaker} {boardProduct}".Trim();
        BoardUrl = BoardLink(boardMaker, boardProduct);

        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceName, DeviceClass, HardWareID, Manufacturer, DriverProviderName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var cls = o["DeviceClass"]?.ToString()?.ToUpperInvariant() ?? "";
                    var cat = Classes.FirstOrDefault(c => c.Class == cls).Category;
                    if (cat == null) continue;
                    var name = o["DeviceName"]?.ToString()?.Trim() ?? "";
                    if (name.Length == 0) continue;
                    var lower = name.ToLowerInvariant();
                    if (cls == "SYSTEM" && !ChipsetWords.Any(lower.Contains)) continue;
                    if (cls == "MEDIA" && (lower.Contains("nvidia") && lower.Contains("high definition") || lower.Contains("virtual") || lower.Contains("steam streaming"))) continue;
                    if (cls == "NET" && (lower.Contains("wan miniport") || lower.Contains("virtual") || lower.Contains("kernel debug") || lower.Contains("vpn") || lower.Contains("tap-") || lower.Contains("bluetooth device (personal") || lower.Contains("wi-fi direct"))) continue;
                    if (cls == "BLUETOOTH" && !(lower.Contains("adapter") || lower.Contains("radio") || lower.Contains("wireless bluetooth") || lower.Contains("bluetooth(r)") || lower.Contains("bluetooth ®"))) continue;
                    if (cls == "USB" && !(lower.Contains("host controller") || lower.Contains("xhci") || lower.Contains("usb4") || lower.Contains("thunderbolt"))) continue;
                    var ver = o["DriverVersion"]?.ToString() ?? "";
                    if (!seen.Add(name + "|" + ver)) continue;
                    var provider = o["DriverProviderName"]?.ToString()?.Trim() ?? "";
                    var maker = o["Manufacturer"]?.ToString()?.Trim() ?? "";
                    DateTime? date = null;
                    try { if (o["DriverDate"] is string d && d.Length >= 8) date = ManagementDateTimeConverter.ToDateTime(d); } catch { }
                    list.Add(Make(cat, name, maker, provider, ver, date, boardMaker, boardProduct, o["HardWareID"]?.ToString() ?? ""));
                }
        }
        catch (Exception ex) { Log.Warn(LogModule.Diagnose, "Treiberliste: " + ex.Message); }
        return list.OrderBy(d => Array.FindIndex(Classes, c => c.Category == d.Category)).ThenByDescending(d => d.Level).ThenBy(d => d.Device).ToList();
    }

    private static HwDriver Make(string cat, string name, string maker, string provider, string ver, DateTime? date, string boardMaker, string boardProduct, string hwid)
    {
        bool generic = provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);
        var hay = (name + " " + maker + " " + provider).ToLowerInvariant();
        string link = "", linkText = "", advice = "";
        if (hay.Contains("intel")) { link = "https://www.intel.de/content/www/de/de/support/detect.html"; linkText = "Intel Driver & Support Assistant"; }
        else if (hay.Contains("killer") || hay.Contains("rivet")) { link = "https://www.intel.de/content/www/de/de/download/19779/intel-killer-performance-suite.html"; linkText = "Intel Killer-Treiber"; }
        else if (hay.Contains("amd") || hay.Contains("advanced micro")) { link = "https://www.amd.com/de/support/download/drivers.html"; linkText = "AMD Chipsatz-Treiber"; }
        else if (hay.Contains("realtek") && cat == "Audio") { link = BoardLink(boardMaker, boardProduct); linkText = "Audio-Treiber vom Mainboard-Hersteller"; }
        else if (hay.Contains("realtek")) { link = "https://www.realtek.com/Download/List?cate_id=584"; linkText = "Realtek Netzwerk-Treiber"; }
        else if (hay.Contains("mediatek")) { link = BoardLink(boardMaker, boardProduct); linkText = "WLAN-Treiber vom Mainboard-Hersteller"; }
        else if (hay.Contains("qualcomm") || hay.Contains("atheros")) { link = BoardLink(boardMaker, boardProduct); linkText = "Treiber vom Mainboard-Hersteller"; }
        else if (hay.Contains("aquantia") || hay.Contains("marvell")) { link = "https://www.marvell.com/support/downloads.html"; linkText = "Marvell/Aquantia-Treiber"; }
        else if (hay.Contains("asmedia")) { link = BoardLink(boardMaker, boardProduct); linkText = "Treiber vom Mainboard-Hersteller"; }

        int level = 0;
        double years = date is { } d ? (DateTime.Now - d).TotalDays / 365.25 : 0;
        if (generic)
        {
            // Windows-Standardtreiber: bei Audio und Netzwerk oft schlechter als der vom Hersteller
            if (cat is "Audio" && (name.Contains("High Definition Audio") || name.Contains("USB-Audio") || name.Contains("USB Audio")))
            { level = 1; advice = "Windows-Standardtreiber. Funktioniert, aber der Herstellertreiber (z. B. Realtek) bringt meist mehr Einstellungen und besseren Klang."; }
            else if (cat is "Netzwerk / WLAN" or "Bluetooth")
            { level = 1; advice = "Windows-Standardtreiber. Der Herstellertreiber ist oft stabiler und schneller."; }
            if (string.IsNullOrEmpty(link)) { link = BoardLink(boardMaker, boardProduct); linkText = "Treiber vom Mainboard-Hersteller"; }
        }
        else if (years >= 3)
        {
            level = 2;
            advice = $"Der Treiber ist über {Math.Floor(years):0} Jahre alt – beim Hersteller nach einer neueren Version schauen.";
        }
        else if (years >= 1.5) { level = 1; advice = "Etwas älter – ein kurzer Blick auf die Herstellerseite lohnt sich."; }

        return new HwDriver
        {
            HardwareId = hwid, Category = cat, Device = name, Maker = maker.Length > 0 ? maker : provider, Version = ver, Date = date, Generic = generic,
            Level = level, Advice = advice, Link = link, LinkText = linkText
        };
    }

    /// <summary>Support-Seite des Mainboards (Suche, weil die genaue Adresse je Hersteller und Modell anders ist).</summary>
    public static string BoardLink(string maker, string product)
    {
        var m = maker.ToLowerInvariant();
        var p = Uri.EscapeDataString(product);
        if (m.Contains("asus")) return $"https://www.asus.com/de/searchresult?searchType=support&searchKey={p}";
        if (m.Contains("micro-star") || m.Contains("msi")) return $"https://de.msi.com/search/{p}";
        if (m.Contains("gigabyte")) return $"https://www.gigabyte.com/de/Search?kw={p}";
        if (m.Contains("asrock")) return $"https://www.asrock.com/support/index.de.asp?cat=Drivers&Model={p}";
        return "https://www.google.com/search?q=" + Uri.EscapeDataString($"{maker} {product} treiber download");
    }
}

/// <summary>Ein Treiber, den Windows Update für diesen PC anbietet.</summary>
public sealed class WuDriver
{
    internal dynamic Com { get; init; } = null!;
    public string Title { get; init; } = "";
    public string Model { get; init; } = "";
    public string Maker { get; init; } = "";
    public string Class { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public DateTime? Date { get; init; }
    public long Bytes { get; init; }
    public bool Installed { get; set; }
    public string DateText => Date is { } d ? $"Version vom {d:dd.MM.yyyy}" : Title;
}

/// <summary>
/// Treiber über Windows Update suchen und installieren (dieselbe Quelle wie „Optionale Updates“ in den Einstellungen,
/// die Treiber sind von Microsoft geprüft und signiert). Braucht Adminrechte – hat die App.
/// </summary>
public static class WindowsDriverUpdates
{
    public static List<WuDriver> Search()
    {
        var list = new List<WuDriver>();
        var t = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update ist nicht verfügbar");
        dynamic session = Activator.CreateInstance(t)!;
        session.ClientApplicationID = "Control Center";
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.ServerSelection = 2; // Windows Update (nicht WSUS)
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        int n = result.Updates.Count;
        for (int i = 0; i < n; i++)
        {
            dynamic u = result.Updates.Item(i);
            DateTime? date = null;
            try { date = (DateTime)u.DriverVerDate; } catch { }
            long bytes = 0;
            try { bytes = (long)Convert.ToDecimal(u.MaxDownloadSize); } catch { }
            string Str(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }
            list.Add(new WuDriver
            {
                Com = u, Title = Str(() => (string)u.Title), Model = Str(() => (string)u.DriverModel), Maker = Str(() => (string)u.DriverManufacturer),
                Class = Str(() => (string)u.DriverClass), HardwareId = Str(() => (string)u.DriverHardwareID), Date = date, Bytes = bytes
            });
        }
        Log.Info(LogModule.Diagnose, $"Windows Update: {list.Count} Treiber-Updates angeboten" + (list.Count > 0 ? ": " + string.Join(", ", list.Select(l => l.Title)) : ""));
        return list;
    }

    /// <summary>Update einem Gerät zuordnen (Hardware-ID, sonst Gerätename).</summary>
    public static WuDriver? Match(HwDriver d, List<WuDriver> updates) =>
        updates.FirstOrDefault(u => u.HardwareId.Length > 0 && d.HardwareId.Length > 0 &&
                                    (d.HardwareId.StartsWith(u.HardwareId, StringComparison.OrdinalIgnoreCase) || u.HardwareId.StartsWith(d.HardwareId, StringComparison.OrdinalIgnoreCase)))
        ?? updates.FirstOrDefault(u => u.Model.Length > 3 && u.Model.Equals(d.Device, StringComparison.OrdinalIgnoreCase));

    /// <summary>Laden und installieren. Gibt (Erfolg, Neustart nötig, Meldung) zurück.</summary>
    public static (bool Ok, bool Reboot, string Message) Install(IEnumerable<WuDriver> drivers)
    {
        var arr = drivers.ToList();
        if (arr.Count == 0) return (true, false, "Nichts zu tun");
        var t = Type.GetTypeFromProgID("Microsoft.Update.Session")!;
        dynamic session = Activator.CreateInstance(t)!;
        session.ClientApplicationID = "Control Center";
        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        foreach (var d in arr)
        {
            try { if (!(bool)d.Com.EulaAccepted) d.Com.AcceptEula(); } catch { }
            coll.Add(d.Com);
        }
        dynamic dl = session.CreateUpdateDownloader();
        dl.Updates = coll;
        dynamic dr = dl.Download();
        if ((int)dr.ResultCode is not (2 or 3)) return (false, false, $"Download fehlgeschlagen (Code {(int)dr.ResultCode})");
        dynamic inst = session.CreateUpdateInstaller();
        inst.Updates = coll;
        dynamic ir = inst.Install();
        int code = (int)ir.ResultCode;
        bool reboot = false; try { reboot = (bool)ir.RebootRequired; } catch { }
        for (int i = 0; i < arr.Count; i++)
            try { if ((int)ir.GetUpdateResult(i).ResultCode is 2 or 3) arr[i].Installed = true; } catch { }
        bool ok = code is 2 or 3;
        Log.Write(ok ? LogLevel.Ok : LogLevel.Warn, LogModule.Diagnose, $"Treiber über Windows Update installiert: {string.Join(", ", arr.Select(a => a.Title))} → Code {code}" + (reboot ? ", Neustart nötig" : ""));
        return (ok, reboot, ok ? (reboot ? "Installiert – nach dem nächsten Neustart aktiv" : "Installiert ✔") : $"Installation fehlgeschlagen (Code {code})");
    }
}
