using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NickControlCenter;

/// <summary>Ein Programm, das mit Windows startet.</summary>
public sealed class StartupItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    /// <summary>Wo der Eintrag steht (für die Anzeige).</summary>
    public required string Location { get; init; }
    /// <summary>Registry-Pfad bzw. Ordner von StartupApproved (wie der Task-Manager es macht).</summary>
    internal RegistryKey ApprovedRoot { get; init; } = Registry.CurrentUser;
    internal string ApprovedKey { get; init; } = "";
    internal string ApprovedName { get; init; } = "";
    public string? ExePath { get; init; }
    public string Publisher { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>0 unbekannt, 1 gering, 2 mittel, 3 hoch (Schätzung).</summary>
    public int ImpactLevel { get; init; }
    public string Impact => ImpactLevel switch { 3 => "hohe Auswirkung (geschätzt)", 2 => "mittlere Auswirkung (geschätzt)", 1 => "geringe Auswirkung (geschätzt)", _ => "Auswirkung unbekannt" };
    public System.Windows.Media.Brush ImpactBrush => ImpactLevel switch { 3 => Palette.Bad, 2 => Palette.Warn, 1 => Palette.Good, _ => Palette.Muted };
    public string Advice { get; init; } = "";
    public bool HasAdvice => Advice.Length > 0;
    /// <summary>2 = kann aus, 1 = nach Bedarf, 0 = an lassen</summary>
    public int Verdict { get; init; } = 1;
    public string VerdictText => Verdict switch { 2 => "kann aus", 0 => "an lassen", _ => "nach Bedarf" };
    public System.Windows.Media.Brush VerdictBrush => Verdict switch { 2 => Palette.Good, 0 => Palette.Bad, _ => Palette.Warn };
    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; if (StartupManager.SetEnabled(this, value)) { _enabled = value; Raise(); Raise(nameof(StateText)); } else Raise(); }
    }
    public string StateText => _enabled ? "startet mit Windows" : "deaktiviert";
    internal void InitEnabled(bool v) => _enabled = v;
}

/// <summary>
/// Autostart-Manager: listet Autostart-Einträge (Registry „Run“ und Autostart-Ordner) und schaltet sie
/// genauso ab wie der Task-Manager (StartupApproved) – nichts wird gelöscht, alles ist umkehrbar.
/// </summary>
public static class StartupManager
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Bekannte Programme: Einschätzung und Rat (Schätzung, keine Messung).</summary>
    /// <summary>Bekannte Programme: Einschätzung (Schätzung, keine Messung). Verdict: 2 = kann aus, 1 = nach Bedarf, 0 = an lassen.</summary>
    private static readonly (string Match, string Impact, int Verdict, string Advice)[] Known =
    {
        // --- kann aus
        ("steam", "hoch", 2, "Startet Steam samt Browser-Komponente. Kann aus – Steam startet, wenn du es oder ein Spiel öffnest."),
        ("epicgameslauncher", "hoch", 2, "Kann aus – startet beim Öffnen eines Epic-Spiels von selbst."),
        ("spotify", "mittel", 2, "Kann aus – Spotify startet schnell, wenn du es öffnest."),
        ("ms-teams", "hoch", 2, "Kann aus, wenn du Teams privat nicht brauchst."), ("teams", "hoch", 2, "Kann aus, wenn du Teams privat nicht brauchst."),
        ("riotclient", "mittel", 2, "Kann aus – VALORANT/LoL starten den Client selbst. Vanguard (vgc) bleibt davon unberührt."),
        ("eadesktop", "hoch", 2, "EA App – kann aus, startet mit dem Spiel."), ("upc", "hoch", 2, "Ubisoft Connect – kann aus."), ("ubisoft", "hoch", 2, "Ubisoft Connect – kann aus."),
        ("galaxyclient", "mittel", 2, "GOG Galaxy – kann aus."), ("battle.net", "hoch", 2, "Kann aus – startet, wenn du es öffnest."), ("rockstar", "mittel", 2, "Rockstar Launcher – kann aus."),
        ("adobe", "hoch", 2, "Adobe-Hintergrunddienste – meistens unnötig, kann aus."), ("creative cloud", "hoch", 2, "Kann aus."),
        ("msedge", "mittel", 2, "Edge-Vorstart – kann aus, Edge öffnet trotzdem normal."), ("opera", "mittel", 2, "Browser-Vorstart – kann aus."),
        ("skype", "mittel", 2, "Kann aus, wenn du Skype nicht nutzt."), ("zoom", "mittel", 2, "Kann aus – startet beim Beitreten eines Meetings."),
        ("whatsapp", "mittel", 2, "Kann aus – öffnest du bei Bedarf."), ("overwolf", "hoch", 2, "Overwolf – kostet FPS, kann aus."), ("medal", "hoch", 2, "Medal (Clips) – kann aus, wenn du nicht dauerhaft aufnimmst."),
        ("ccleaner", "mittel", 2, "Kann aus – Aufräumen macht auch das Control Center."), ("jusched", "niedrig", 2, "Java-Updater – kann aus."), ("ituneshelper", "niedrig", 2, "iTunes-Helfer – kann aus."),
        ("msiafterburner", "mittel", 2, "MSI Afterburner – überschreibt die Grafikkarten-Einstellungen des Tweakers. Aus lassen."), ("rtss", "mittel", 2, "RivaTuner – hängt sich in jedes Spiel. Kann aus."),
        ("virtualbox", "mittel", 2, "VirtualBox – kann aus."), ("docker", "hoch", 2, "Docker – startet viele Dienste, kann aus."), ("bluestacks", "hoch", 2, "BlueStacks – kann aus."),
        ("nickfancontrol", "mittel", 2, "Alte Einzel-App – ist jetzt im Control Center enthalten. Abschalten!"),
        ("pcmonitor", "niedrig", 2, "Alte Einzel-App – ist jetzt im Control Center enthalten. Abschalten."),
        ("hwinfo", "niedrig", 2, "HWiNFO – startet das Control Center bei Bedarf selbst. Kann aus."),
        // --- nach Bedarf
        ("discord", "mittel", 1, "Nach Bedarf: an lassen, wenn du direkt mit Freunden schreiben willst – sonst kann es aus."),
        ("onedrive", "mittel", 1, "An lassen, wenn du OneDrive zum Synchronisieren nutzt – sonst kann es aus."),
        ("dropbox", "mittel", 1, "An lassen, wenn du Dropbox zum Synchronisieren nutzt."), ("googledrive", "mittel", 1, "An lassen, wenn du Google Drive synchronisierst."),
        ("razer", "mittel", 1, "Razer Synapse – das Control Center steuert die Beleuchtung selbst. Nur an lassen, wenn du Synapse für Maus-DPI/Makros brauchst."),
        ("icue", "mittel", 1, "Corsair iCUE – stört die RGB-Steuerung der App, wenn beide laufen. Nur an, wenn du es für Corsair-Funktionen brauchst."),
        ("armoury", "hoch", 1, "Armoury Crate – stört Lüfter/RGB der App, wenn beide laufen."), ("lghub", "mittel", 1, "Logitech G HUB – an lassen, wenn du Profile/DPI deiner Logitech-Maus brauchst."),
        ("logi", "niedrig", 1, "Logitech-Software – an lassen, wenn du die Tastenbelegung nutzt."), ("steelseries", "mittel", 1, "SteelSeries GG – an lassen, wenn du Geräte-Einstellungen brauchst."),
        ("wallpaper", "mittel", 1, "Wallpaper Engine – an lassen, wenn du bewegte Hintergründe willst. Kostet im Hintergrund etwas Leistung."),
        ("tailscale", "niedrig", 1, "VPN – an lassen, wenn du dich immer verbinden willst."), ("nahimic", "niedrig", 1, "Audio-Software – an lassen, wenn du ihre Klang-Effekte nutzt."),
        ("sonic studio", "niedrig", 1, "Audio-Software – an lassen, wenn du ihre Effekte nutzt."), ("broadcast", "niedrig", 1, "NVIDIA Broadcast – an, wenn du Rauschfilter fürs Mikro nutzt."),
        ("obs64", "mittel", 1, "OBS – nur an, wenn du immer direkt aufnimmst."), ("gigabyte", "mittel", 1, "Gigabyte-Tool – meist nicht nötig, Lüfter/RGB macht das Control Center."),
        // --- an lassen
        ("securityhealth", "niedrig", 0, "Windows-Sicherheit – AN lassen."), ("defender", "niedrig", 0, "Virenschutz – AN lassen."),
        ("avast", "mittel", 0, "Virenschutz – an lassen."), ("bitdefender", "mittel", 0, "Virenschutz – an lassen."), ("kaspersky", "mittel", 0, "Virenschutz – an lassen."),
        ("norton", "mittel", 0, "Virenschutz – an lassen."), ("mcafee", "mittel", 0, "Virenschutz – an lassen."),
        ("rtkaud", "niedrig", 0, "Realtek-Audio – an lassen (sonst evtl. kein Ton oder keine Kopfhörer-Erkennung)."), ("realtek", "niedrig", 0, "Realtek-Treiberkomponente – an lassen."),
        ("waves", "niedrig", 0, "Audio-Treiberkomponente – an lassen."), ("igfx", "niedrig", 0, "Intel-Grafiktreiber – an lassen."),
        ("nvidia", "niedrig", 0, "NVIDIA-Treiberkomponente – an lassen."), ("amd", "niedrig", 0, "AMD-Treiberkomponente – an lassen."),
        ("synaptics", "niedrig", 0, "Touchpad-Treiber – an lassen."), ("elantech", "niedrig", 0, "Touchpad-Treiber – an lassen."), ("bluetooth", "niedrig", 0, "Bluetooth – an lassen."),
        ("controlcenter", "niedrig", 0, "Das Control Center selbst – an lassen, damit Lüfter und RGB nach dem Start laufen."),
        ("update", "niedrig", 2, "Ein Update-Helfer – kann aus, das Programm sucht beim Öffnen selbst nach Updates."),
    };

    private static readonly string[] SystemPublishers = { "microsoft", "intel", "realtek", "nvidia", "advanced micro devices", "amd", "synaptics", "elan" };

    public static List<StartupItem> Load()
    {
        var list = new List<StartupItem>();
        void FromRun(RegistryKey root, string runKey, string approvedSub, string loc)
        {
            try
            {
                using var k = root.OpenSubKey(runKey);
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var cmd = k.GetValue(name)?.ToString() ?? "";
                    list.Add(Make(name, cmd, loc, root, Approved + approvedSub, name));
                }
            }
            catch { }
        }
        FromRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (nur du)");
        FromRun(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (alle Benutzer)");
        FromRun(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Registry (alle Benutzer, 32 Bit)");
        void FromFolder(string dir, RegistryKey root, string loc)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                {
                    string target = f;
                    if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) target = ResolveLnk(f) ?? f;
                    list.Add(Make(Path.GetFileNameWithoutExtension(f), "\"" + target + "\"", loc, root, Approved + "StartupFolder", Path.GetFileName(f)));
                }
            }
            catch { }
        }
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Autostart-Ordner (nur du)");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Autostart-Ordner (alle Benutzer)");
        return list.OrderByDescending(i => i.Enabled).ThenByDescending(i => i.Verdict).ThenByDescending(i => i.ImpactLevel).ThenBy(i => i.Name).ToList();
    }

    private static StartupItem Make(string name, string cmd, string loc, RegistryKey root, string approvedKey, string approvedName)
    {
        var exe = ExeFromCommand(cmd);
        string pub = "", desc = "";
        if (exe != null && File.Exists(exe))
            try { var v = FileVersionInfo.GetVersionInfo(exe); pub = v.CompanyName?.Trim() ?? ""; desc = v.FileDescription?.Trim() ?? ""; } catch { }
        var hay = (name + " " + exe + " " + desc).ToLowerInvariant();
        // „an lassen“ zuerst prüfen (z. B. „NVIDIA … Update“ ist ein Treiberteil), dann der Rest
        var known = Known.Where(k => k.Verdict == 0 && k.Match != "update").Concat(Known.Where(k => k.Verdict != 0 || k.Match == "update")).FirstOrDefault(k => hay.Contains(k.Match));
        int level = known.Match == null ? 0 : known.Impact switch { "hoch" => 3, "mittel" => 2, _ => 1 };
        bool missing = exe != null && !File.Exists(exe);
        int verdict; string advice;
        if (missing) { verdict = 2; advice = "Das Programm existiert nicht mehr – der Eintrag kann aus."; }
        else if (known.Match != null) { verdict = known.Verdict; advice = known.Advice; }
        else if (SystemPublishers.Any(sp => pub.ToLowerInvariant().Contains(sp))) { verdict = 0; advice = $"Systemkomponente von {pub} – lieber an lassen."; }
        else { verdict = 1; advice = "Unbekanntes Programm: Brauchst du es nicht direkt nach dem Hochfahren, kann es aus. Ausschalten ist jederzeit umkehrbar."; }
        var item = new StartupItem
        {
            Name = desc.Length > 2 && !desc.Equals(name, StringComparison.OrdinalIgnoreCase) ? $"{name} – {desc}" : name,
            Command = cmd, Location = loc, ExePath = exe, Publisher = pub,
            ImpactLevel = level,
            Advice = advice, Verdict = verdict,
            ApprovedRoot = root, ApprovedKey = approvedKey, ApprovedName = approvedName
        };
        item.InitEnabled(IsEnabled(root, approvedKey, approvedName));
        return item;
    }

    private static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        var m = Regex.Match(cmd, "^\"([^\"]+)\"");
        if (m.Success) return m.Groups[1].Value;
        var e = Regex.Match(cmd, @"^(.+?\.exe)", RegexOptions.IgnoreCase);
        return e.Success ? e.Groups[1].Value : cmd.Split(' ').FirstOrDefault();
    }

    private static string? ResolveLnk(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic sh = Activator.CreateInstance(t)!;
            dynamic l = sh.CreateShortcut(lnk);
            string target = l.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }

    private static bool IsEnabled(RegistryKey root, string key, string name)
    {
        try
        {
            using var k = root.OpenSubKey(key);
            if (k?.GetValue(name) is byte[] b && b.Length > 0) return (b[0] & 1) == 0; // 02/06 = an, 03/07 = aus
        }
        catch { }
        return true;
    }

    /// <summary>An/aus wie im Task-Manager (Eintrag bleibt erhalten).</summary>
    internal static bool SetEnabled(StartupItem i, bool on)
    {
        try
        {
            using var k = i.ApprovedRoot.CreateSubKey(i.ApprovedKey, true);
            var data = new byte[12];
            data[0] = on ? (byte)2 : (byte)3;
            if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            k.SetValue(i.ApprovedName, data, RegistryValueKind.Binary);
            Log.Info(LogModule.Diagnose, $"Autostart „{i.Name}“: {(on ? "an" : "aus")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogModule.Diagnose, $"Autostart „{i.Name}“: {ex.Message}");
            Ui.Toast("Konnte nicht geändert werden: " + ex.Message, true);
            return false;
        }
    }
}
