using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace NickControlCenter;

public sealed class PrivacySettings
{
    public bool AdBlockOn { get; set; }
    public string AdBlockProvider { get; set; } = "adguard";
    /// <summary>Vorherige DNS je Adapter (SettingID → "dhcp" oder "1.1.1.1,8.8.8.8").</summary>
    public Dictionary<string, string> PreviousDns { get; set; } = new();
    public string? ActiveVpn { get; set; }
}

public sealed record DnsProvider(string Id, string Name, string Description, string[] V4, string[] V6, string DohTemplate);

/// <summary>
/// Werbeblocker für den ganzen PC: stellt die DNS-Server der echten Netzwerkadapter (LAN/WLAN) auf einen
/// werbefilternden DNS-Dienst um. Werbe- und Tracker-Adressen werden dann gar nicht erst aufgelöst – in jedem Browser
/// und jeder App. Vorherige Einstellung wird gesichert und beim Ausschalten exakt wiederhergestellt.
/// </summary>
public static class AdBlocker
{
    public static PrivacySettings Settings { get; } = JsonStore.Load<PrivacySettings>("datenschutz.json");
    public static void Save() => JsonStore.Save("datenschutz.json", Settings);

    public static readonly DnsProvider[] Providers =
    {
        new("adguard", "AdGuard DNS – Werbung & Tracker", "Blockt Werbung, Tracker und Schadseiten. Empfehlung.",
            new[] { "94.140.14.14", "94.140.15.15" }, new[] { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" }, "https://dns.adguard-dns.com/dns-query"),
        new("adguardfamily", "AdGuard DNS Familie", "Wie oben, zusätzlich Erwachsenen-Inhalte und sichere Suche.",
            new[] { "94.140.14.15", "94.140.15.16" }, new[] { "2a10:50c0::bad1:ff", "2a10:50c0::bad2:ff" }, "https://family.adguard-dns.com/dns-query"),
        new("cloudflare", "Cloudflare – nur Schadseiten", "Blockt keine Werbung, nur Malware/Phishing. Sehr schnell.",
            new[] { "1.1.1.2", "1.0.0.2" }, new[] { "2606:4700:4700::1112", "2606:4700:4700::1002" }, "https://security.cloudflare-dns.com/dns-query"),
    };

    public static DnsProvider Current => Providers.FirstOrDefault(p => p.Id == Settings.AdBlockProvider) ?? Providers[0];

    private sealed record Adapter(string Guid, uint Index, string Name, string[] Dns);

    /// <summary>Echte Netzwerkadapter mit Verbindung (keine VPN-/virtuellen Adapter).</summary>
    private static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        using var s = new ManagementObjectSearcher("SELECT SettingID, InterfaceIndex, Description, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
        var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var a = new ManagementObjectSearcher("SELECT GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
            foreach (ManagementObject o in a.Get()) using (o) if (o["GUID"] is string g) physical.Add(g);
        }
        catch { }
        foreach (ManagementObject o in s.Get())
            using (o)
            {
                var guid = o["SettingID"]?.ToString() ?? "";
                var desc = o["Description"]?.ToString() ?? "";
                if (!physical.Contains(guid)) continue;
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) || desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new Adapter(guid, Convert.ToUInt32(o["InterfaceIndex"] ?? 0), desc, o["DNSServerSearchOrder"] as string[] ?? Array.Empty<string>()));
            }
        return list;
    }

    private static bool IsStatic(string guid)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}");
            return k?.GetValue("NameServer") is string ns && ns.Trim().Length > 0;
        }
        catch { return false; }
    }

    private static (int Code, string Output) Ps(string script) =>
        Env.Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"", 30_000);

    /// <summary>Ist der Werbeblocker gerade wirksam (alle Adapter zeigen auf den gewählten Dienst)?</summary>
    public static bool IsActive()
    {
        try
        {
            var ad = Adapters();
            return ad.Count > 0 && ad.All(a => a.Dns.Length > 0 && Current.V4.Contains(a.Dns[0]));
        }
        catch { return false; }
    }

    public static string Enable()
    {
        var p = Current;
        var ad = Adapters();
        if (ad.Count == 0) throw new InvalidOperationException("Kein aktiver LAN-/WLAN-Adapter gefunden.");
        foreach (var a in ad)
        {
            // nur beim ersten Einschalten sichern – sonst würde man sich die eigenen Werbeblocker-Server merken
            if (!Settings.PreviousDns.ContainsKey(a.Guid))
                Settings.PreviousDns[a.Guid] = IsStatic(a.Guid) ? string.Join(",", a.Dns) : "dhcp";
            var all = string.Join(",", p.V4.Concat(p.V6).Select(x => $"'{x}'"));
            var r = Ps($"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses ({all})");
            if (r.Code != 0) Log.Warn(LogModule.App, $"Werbeblocker: {a.Name}: {r.Output.Trim()}");
        }
        // verschlüsseltes DNS (DoH), wenn Windows es kann – sonst normal
        foreach (var ip in p.V4.Concat(p.V6))
            Ps($"Add-DnsClientDohServerAddress -ServerAddress '{ip}' -DohTemplate '{p.DohTemplate}' -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction SilentlyContinue");
        Env.Run("ipconfig.exe", "/flushdns");
        // Browser mit eigenem „Sicheren DNS“ würden den Filter umgehen
        try { BrowserDnsFix.DisableAll(); } catch { }
        Settings.AdBlockOn = true; Save();
        Log.Ok(LogModule.App, $"Werbeblocker an ({p.Name}) auf {ad.Count} Adapter(n)");
        return $"Werbeblocker an – {p.Name}";
    }

    public static string Disable()
    {
        foreach (var a in Adapters())
        {
            Settings.PreviousDns.TryGetValue(a.Guid, out var prev);
            if (prev == null || prev == "dhcp") Ps($"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ResetServerAddresses");
            else Ps($"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses ({string.Join(",", prev.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => $"'{x}'"))})");
        }
        Env.Run("ipconfig.exe", "/flushdns");
        Settings.PreviousDns.Clear();
        try { BrowserDnsFix.RemovePolicies(); } catch { }
        Settings.AdBlockOn = false; Save();
        Log.Info(LogModule.App, "Werbeblocker aus – vorherige DNS-Einstellung wiederhergestellt");
        return "Werbeblocker aus – Internet wie vorher";
    }

    /// <summary>Test: wird eine bekannte Werbe-Adresse blockiert?</summary>
    public static async Task<bool?> TestAsync()
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync("doubleclick.net");
            return ips.Length == 0 || ips.All(i => IPAddress.Any.Equals(i) || IPAddress.IPv6Any.Equals(i) || i.ToString() is "0.0.0.0" or "::" or "94.140.14.33");
        }
        catch (System.Net.Sockets.SocketException) { return true; } // „nicht gefunden“ = geblockt
        catch { return null; }
    }
}

// ======================================================================

public sealed class VpnTunnel : ObservableObject
{
    public required string Name { get; init; }
    public required string ConfigPath { get; init; }
    public string Endpoint { get; init; } = "";
    private bool _connected;
    public bool Connected { get => _connected; set { Set(ref _connected, value); RaiseAll(nameof(StateText), nameof(ButtonText), nameof(StateBrush)); } }
    private bool _busy;
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;
    public string StateText => _connected ? "● Verbunden" : "Getrennt";
    public System.Windows.Media.Brush StateBrush => _connected ? Palette.Good : Palette.Muted;
    public string ButtonText => _connected ? "Trennen" : "Verbinden";
}

/// <summary>
/// VPN über WireGuard (offizieller Client). Eine .conf-Datei eines VPN-Anbieters (z. B. Proton VPN – auch kostenlos)
/// wird eingelesen; Verbinden/Trennen läuft über den WireGuard-Tunneldienst, ganz ohne WireGuard-Fenster.
/// </summary>
public static class VpnManager
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ControlCenter", "vpn");
    public static string? WireGuardExe
    {
        get
        {
            foreach (var f in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" })
            { var p = Path.Combine(f, "WireGuard", "wireguard.exe"); if (File.Exists(p)) return p; }
            return null;
        }
    }
    public static bool Installed => WireGuardExe != null;

    public static async Task<bool> InstallAsync()
    {
        var r = await Task.Run(() => Env.Run("cmd.exe", "/d /c winget install --id WireGuard.WireGuard --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity", 10 * 60_000));
        // WireGuard öffnet nach der Installation sein Fenster – schließen, die App steuert alles selbst
        await Task.Delay(1500);
        foreach (var p in Process.GetProcessesByName("wireguard")) try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
        Log.Write(Installed ? LogLevel.Ok : LogLevel.Warn, LogModule.App, "WireGuard-Installation: " + (Installed ? "ok" : "fehlgeschlagen " + r.Output.Trim()));
        return Installed;
    }

    public static List<VpnTunnel> Load()
    {
        Directory.CreateDirectory(Folder);
        return Directory.GetFiles(Folder, "*.conf").Select(f => new VpnTunnel
        {
            Name = Path.GetFileNameWithoutExtension(f), ConfigPath = f, Endpoint = EndpointOf(f), Connected = IsConnected(Path.GetFileNameWithoutExtension(f))
        }).OrderBy(t => t.Name).ToList();
    }

    private static string EndpointOf(string conf)
    {
        try { return File.ReadLines(conf).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1].Trim() ?? ""; }
        catch { return ""; }
    }

    /// <summary>Konfiguration übernehmen. Name nur aus Buchstaben/Ziffern (WireGuard-Vorgabe, max. 32 Zeichen).</summary>
    public static VpnTunnel Import(string file)
    {
        var text = File.ReadAllText(file);
        if (!text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Das ist keine WireGuard-Konfiguration (.conf mit [Interface] und [Peer]).");
        var name = new string(Path.GetFileNameWithoutExtension(file).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (name.Length == 0) name = "VPN";
        if (name.Length > 32) name = name[..32];
        Directory.CreateDirectory(Folder);
        var dest = Path.Combine(Folder, name + ".conf");
        File.WriteAllText(dest, text);
        Log.Info(LogModule.App, $"VPN-Konfiguration „{name}“ übernommen");
        return new VpnTunnel { Name = name, ConfigPath = dest, Endpoint = EndpointOf(dest) };
    }

    public static bool IsConnected(string name)
    {
        try { using var sc = new ServiceController("WireGuardTunnel$" + name); return sc.Status == ServiceControllerStatus.Running; }
        catch { return false; }
    }

    public static string Connect(VpnTunnel t)
    {
        var exe = WireGuardExe ?? throw new InvalidOperationException("WireGuard ist nicht installiert.");
        foreach (var other in Load().Where(x => x.Connected && x.Name != t.Name)) Disconnect(other); // nur ein Tunnel gleichzeitig
        var r = Env.Run(exe, $"/installtunnelservice \"{t.ConfigPath}\"", 30_000);
        for (int i = 0; i < 20 && !IsConnected(t.Name); i++) Thread.Sleep(500);
        bool ok = IsConnected(t.Name);
        AdBlocker.Settings.ActiveVpn = ok ? t.Name : null; AdBlocker.Save();
        Log.Write(ok ? LogLevel.Ok : LogLevel.Warn, LogModule.App, ok ? $"VPN „{t.Name}“ verbunden" : $"VPN „{t.Name}“: {r.Output.Trim()}");
        return ok ? "VPN verbunden" : "Verbinden fehlgeschlagen: " + r.Output.Trim();
    }

    public static string Disconnect(VpnTunnel t)
    {
        var exe = WireGuardExe ?? throw new InvalidOperationException("WireGuard ist nicht installiert.");
        Env.Run(exe, $"/uninstalltunnelservice {t.Name}", 30_000);
        for (int i = 0; i < 20 && IsConnected(t.Name); i++) Thread.Sleep(500);
        AdBlocker.Settings.ActiveVpn = null; AdBlocker.Save();
        Log.Info(LogModule.App, $"VPN „{t.Name}“ getrennt");
        return "VPN getrennt";
    }

    public static void Delete(VpnTunnel t)
    {
        if (t.Connected) Disconnect(t);
        File.Delete(t.ConfigPath);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Öffentliche IP und Ort – so sieht man, ob das VPN wirkt.</summary>
    public static async Task<string> PublicIpAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("https://ipinfo.io/json");
            using var d = JsonDocument.Parse(json);
            string G(string k) => d.RootElement.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            return $"{G("ip")} · {G("city")}, {G("country")} · {G("org")}";
        }
        catch
        {
            try { return (await Http.GetStringAsync("https://api.ipify.org")).Trim(); } catch { return "nicht ermittelbar"; }
        }
    }
}

// ======================================================================

public sealed record BrowserDns(string Name, string Mode, bool Bypasses, bool Running);

/// <summary>
/// Browser mit eigenem „Sicheren DNS“ (DNS-over-HTTPS) fragen direkt bei Google/Cloudflare nach und umgehen damit den
/// Werbeblocker. Chrome/Edge/Brave/Firefox: per Richtlinie abschalten (wirkt sofort nach Browser-Neustart, wird beim
/// Ausschalten des Werbeblockers wieder entfernt). Opera/Opera GX: Einstellung in „Local State“ (Browser muss zu sein).
/// </summary>
public static class BrowserDnsFix
{
    private static readonly (string Name, string Proc, string LocalState, string? PolicyKey)[] Browsers =
    {
        ("Google Chrome", "chrome", @"%LOCALAPPDATA%\Google\Chrome\User Data\Local State", @"SOFTWARE\Policies\Google\Chrome"),
        ("Microsoft Edge", "msedge", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Local State", @"SOFTWARE\Policies\Microsoft\Edge"),
        ("Brave", "brave", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\Local State", @"SOFTWARE\Policies\BraveSoftware\Brave"),
        ("Opera GX", "opera", @"%APPDATA%\Opera Software\Opera GX Stable\Local State", null),
        ("Opera", "opera", @"%APPDATA%\Opera Software\Opera Stable\Local State", null),
        ("Vivaldi", "vivaldi", @"%LOCALAPPDATA%\Vivaldi\User Data\Local State", null),
    };

    private static string Expand(string p) => Environment.ExpandEnvironmentVariables(p);

    /// <summary>Welche installierten Browser umgehen den Filter gerade?</summary>
    public static List<BrowserDns> Scan()
    {
        var list = new List<BrowserDns>();
        foreach (var b in Browsers)
        {
            var ls = Expand(b.LocalState);
            if (!File.Exists(ls)) continue;
            string mode = "automatic"; // Chromium-Standard: nutzt DoH, wenn der System-DNS-Anbieter es kann
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(ls));
                mode = root?["dns_over_https"]?["mode"]?.GetValue<string>() ?? mode;
            }
            catch { }
            bool policyOff = false;
            if (b.PolicyKey != null)
                try { policyOff = (Registry.LocalMachine.OpenSubKey(b.PolicyKey)?.GetValue("DnsOverHttpsMode") as string) == "off"; } catch { }
            // „automatic“ ist harmlos, solange ein eigener Anbieter (z. B. Google) nicht fest eingestellt ist – Opera GX stellt oft „secure“ + Cloudflare ein
            bool bypass = !policyOff && mode == "secure";
            list.Add(new BrowserDns(b.Name, policyOff ? "aus (vom Control Center)" : mode switch { "secure" => "eigener DNS-Anbieter (umgeht den Filter)", "off" => "aus", _ => "automatisch (ok)" },
                                    bypass, Process.GetProcessesByName(b.Proc).Length > 0));
        }
        // Firefox
        var ff = Expand(@"%APPDATA%\Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
        {
            bool policyOff = false;
            try { policyOff = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS")?.GetValue("Enabled") is int i && i == 0; } catch { }
            bool trr = Directory.GetDirectories(ff).Any(d => { try { var p = Path.Combine(d, "prefs.js"); return File.Exists(p) && File.ReadAllText(p).Contains("\"network.trr.mode\", 3"); } catch { return false; } });
            list.Add(new BrowserDns("Firefox", policyOff ? "aus (vom Control Center)" : trr ? "eigener DNS-Anbieter (umgeht den Filter)" : "automatisch (ok)", trr && !policyOff, Process.GetProcessesByName("firefox").Length > 0));
        }
        return list;
    }

    /// <summary>Sicheres DNS in allen Browsern abschalten, damit der Werbeblocker greift. Gibt eine kurze Meldung zurück.</summary>
    public static string DisableAll()
    {
        var done = new List<string>();
        foreach (var b in Browsers)
        {
            var ls = Expand(b.LocalState);
            if (!File.Exists(ls)) continue;
            if (b.PolicyKey != null)
            {
                try
                {
                    using var k = Registry.LocalMachine.CreateSubKey(b.PolicyKey, true);
                    k.SetValue("DnsOverHttpsMode", "off", RegistryValueKind.String);
                    done.Add(b.Name);
                }
                catch { }
                continue;
            }
            // Opera & Co.: Einstellung direkt ändern – geht nur, wenn der Browser zu ist (sonst überschreibt er sie)
            if (Process.GetProcessesByName(b.Proc).Length > 0) { done.Add(b.Name + " (erst schließen, dann nochmal)"); continue; }
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(ls))?.AsObject();
                if (root == null) continue;
                File.Copy(ls, ls + ".controlcenter-sicherung", true);
                var dns = root["dns_over_https"] as JsonObject ?? (JsonObject)(root["dns_over_https"] = new JsonObject());
                dns["mode"] = "off";
                File.WriteAllText(ls, root.ToJsonString());
                done.Add(b.Name);
            }
            catch { }
        }
        if (Directory.Exists(Expand(@"%APPDATA%\Mozilla\Firefox\Profiles")))
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", true);
                k.SetValue("Enabled", 0, RegistryValueKind.DWord);
                done.Add("Firefox");
            }
            catch { }
        Log.Ok(LogModule.App, "Sicheres DNS in Browsern ausgeschaltet: " + string.Join(", ", done));
        return done.Count == 0 ? "Keine Browser gefunden" : "Sicheres DNS aus in: " + string.Join(", ", done) + ". Browser einmal neu starten.";
    }

    /// <summary>Beim Ausschalten des Werbeblockers die Richtlinien wieder entfernen.</summary>
    public static void RemovePolicies()
    {
        foreach (var b in Browsers.Where(b => b.PolicyKey != null))
            try { using var k = Registry.LocalMachine.OpenSubKey(b.PolicyKey!, true); k?.DeleteValue("DnsOverHttpsMode", false); } catch { }
        try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", false); } catch { }
    }
}
