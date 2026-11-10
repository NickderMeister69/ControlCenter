using System.IO;
using System.Runtime.InteropServices;

namespace NickControlCenter;

/// <summary>Einschätzung, ob etwas gelöscht werden kann.</summary>
public enum SpaceVerdict { Important = 0, Check = 1, Safe = 2 }

public static class VerdictUi
{
    public static string Text(SpaceVerdict v) => v switch { SpaceVerdict.Safe => "kann weg", SpaceVerdict.Check => "selbst prüfen", _ => "wichtig – nicht löschen" };
    public static System.Windows.Media.Brush Brush(SpaceVerdict v) => v switch { SpaceVerdict.Safe => Palette.Good, SpaceVerdict.Check => Palette.Warn, _ => Palette.Bad };
}

/// <summary>Ein bekannter Platzfresser aus dem Schnell-Check.</summary>
public sealed class SpaceItem : ObservableObject
{
    public required string Title { get; init; }
    public required string Explain { get; init; }
    public string Path { get; init; } = "";
    public SpaceVerdict Verdict { get; init; }
    public string VerdictText => VerdictUi.Text(Verdict);
    public System.Windows.Media.Brush VerdictBrush => VerdictUi.Brush(Verdict);
    private long _bytes;
    public long Bytes { get => _bytes; set { Set(ref _bytes, value); Raise(nameof(SizeText)); } }
    public string SizeText => _bytes < 0 ? "" : Fmt.Bytes(_bytes);
    public string ActionLabel { get; init; } = "";
    public bool HasAction => ActionLabel.Length > 0 && Action != null;
    public string? Confirm { get; init; }
    internal Func<SpaceItem, string>? Action { get; init; }
    private string _status = "";
    public string Status { get => _status; set { Set(ref _status, value); Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    private bool _busy;
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;
}

/// <summary>
/// Sagt zu Ordnern und Dateien, ob man sie löschen kann, selbst prüfen sollte oder ob Windows sie braucht –
/// und findet bekannte Platzfresser, die man gefahrlos leeren kann.
/// </summary>
public static class SpaceAdvisor
{
    private static string L(string p) => p.TrimEnd('\\').ToLowerInvariant();
    private static readonly string Win = L(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string Users = L(System.IO.Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"c:\users");
    private static readonly string Local = L(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly string Roaming = L(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    private static readonly string SysDrive = L(System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"c:\");

    /// <summary>Einschätzung für einen Ordner oder eine Datei aus der Analyse.</summary>
    public static (SpaceVerdict Verdict, string Hint) Classify(string path, bool isDir)
    {
        var p = L(path);
        var name = System.IO.Path.GetFileName(p);
        bool In(string root) => p == root || p.StartsWith(root + "\\");

        // --- Dateien
        if (!isDir)
        {
            if (name == "pagefile.sys") return (SpaceVerdict.Important, "Auslagerungsdatei – Windows braucht sie. Nicht löschen.");
            if (name == "swapfile.sys") return (SpaceVerdict.Important, "Gehört zur Auslagerung von Windows. Nicht löschen.");
            if (name == "hiberfil.sys") return (SpaceVerdict.Check, "Ruhezustand-Datei. Lässt sich nur über „Ruhezustand ausschalten“ (Schnell-Check) entfernen – dann geht auch der Schnellstart aus.");
            if (name == "memory.dmp" || p.EndsWith(".dmp")) return (SpaceVerdict.Safe, "Absturzbericht – nur für Fehlersuche, kann weg.");
            if (p.EndsWith(".iso") || p.EndsWith(".zip") || p.EndsWith(".rar") || p.EndsWith(".7z")) return (SpaceVerdict.Check, "Archiv/Abbild – wenn schon entpackt bzw. installiert, kann es meist weg.");
            if (p.EndsWith(".exe") || p.EndsWith(".msi")) return (SpaceVerdict.Check, "Installer – nach der Installation meist nicht mehr nötig.");
            return (SpaceVerdict.Check, "Selbst entscheiden, ob du die Datei noch brauchst.");
        }

        // --- bekannte Ordner (spezifisch vor allgemein)
        if (name == "$recycle.bin") return (SpaceVerdict.Safe, "Papierkorb – leeren gibt den Platz frei.");
        if (name == "system volume information") return (SpaceVerdict.Important, "Wiederherstellungspunkte von Windows. Nicht löschen – verkleinern geht über „Systemschutz“ (Schnell-Check).");
        if (name == "windows.old") return (SpaceVerdict.Safe, "Altes Windows nach einem Upgrade. Kann weg, wenn alles läuft – über die Datenträgerbereinigung (Schnell-Check).");
        if (name == "$windows.~bt" || name == "$windows.~ws" || name == "$winreagent") return (SpaceVerdict.Safe, "Reste eines Windows-Upgrades – über die Datenträgerbereinigung entfernen.");
        if (name == "shadercache" || name == "dxcache" || name == "glcache" || name == "d3dscache" || name == "nv_cache") return (SpaceVerdict.Safe, "Shader-Cache – wird von Spielen neu angelegt. Kann geleert werden (erster Spielstart danach dauert etwas länger).");
        if (name == "temp" || name == "tmp") return (SpaceVerdict.Safe, "Temporäre Dateien – können weg (was gerade benutzt wird, bleibt automatisch liegen).");
        if (name == "crashdumps" || name == "minidump") return (SpaceVerdict.Safe, "Absturzberichte – nur für Fehlersuche, können weg.");
        if (p.EndsWith(@"\softwaredistribution\download")) return (SpaceVerdict.Safe, "Bereits installierte Windows-Updates (Zwischenspeicher) – kann weg.");
        if (p.EndsWith(@"\microsoft\windows\wer")) return (SpaceVerdict.Safe, "Windows-Fehlerberichte – können weg.");
        if (name == "cache" || name == "cache_data" || name == "code cache" || name == "gpucache") return (SpaceVerdict.Safe, "Cache eines Programms (z. B. Browser, Discord) – wird neu angelegt. Programm vorher schließen.");
        if (name == "winsxs") return (SpaceVerdict.Important, "Komponentenspeicher von Windows – NIE von Hand löschen. Sicher verkleinern: „Windows-Komponenten aufräumen“ im Schnell-Check.");
        if (name == "installer" && In(Win)) return (SpaceVerdict.Important, "Wird zum Reparieren/Deinstallieren von Programmen gebraucht. Nicht löschen.");
        if (name == "driverstore" || name == "system32" || name == "syswow64") return (SpaceVerdict.Important, "Kern von Windows. Nicht anfassen.");
        if (name == "steamapps" || name == "common" && p.Contains("steamapps")) return (SpaceVerdict.Check, "Steam-Spiele – nicht hier löschen, sondern in Steam deinstallieren.");
        if (p.Contains("steamapps\\common\\") || p.Contains("\\epic games\\")) return (SpaceVerdict.Check, "Ein Spiel – über Steam bzw. den Epic Launcher deinstallieren, wenn du es nicht mehr spielst.");
        if (p.Contains("\\steamapps\\downloading") || p.Contains("\\steamapps\\temp")) return (SpaceVerdict.Safe, "Abgebrochene Steam-Downloads – können weg (Steam vorher schließen).");
        if (name == "epic games") return (SpaceVerdict.Check, "Epic-Spiele – über den Epic Launcher deinstallieren.");
        if (name == "downloads") return (SpaceVerdict.Check, "Deine Downloads – oft viele alte Installer. „Downloads aufräumen“ oben hilft.");
        if (name is "videos" or "captures" or "nvidia" && In(Users)) return (SpaceVerdict.Check, "Videos und Aufnahmen (z. B. NVIDIA-Clips) – oft riesig. Selbst prüfen.");

        // --- große Bereiche
        if (In(Win)) return (SpaceVerdict.Important, "Gehört zu Windows. Nicht von Hand löschen – aufräumen nur über den Schnell-Check.");
        if (p == SysDrive.TrimEnd('\\') + "\\program files" || p == SysDrive.TrimEnd('\\') + "\\program files (x86)" || name is "program files" or "program files (x86)")
            return (SpaceVerdict.Important, "Installierte Programme. Nicht löschen – über Einstellungen → Apps deinstallieren.");
        if (p.Contains("\\program files\\") || p.Contains("\\program files (x86)\\"))
            return (SpaceVerdict.Check, "Ein installiertes Programm. Nicht den Ordner löschen, sondern über Einstellungen → Apps deinstallieren.");
        if (name == "programdata" || p.Contains("\\programdata\\")) return (SpaceVerdict.Important, "Daten von Programmen und Windows. Nicht von Hand löschen.");
        if (name == "recovery" || name == "boot" || name == "efi") return (SpaceVerdict.Important, "Start- und Wiederherstellungsdateien. Nicht löschen.");
        if (p == Local || p == Roaming || name == "appdata") return (SpaceVerdict.Check, "Einstellungen und Caches deiner Programme. Nicht komplett löschen – tiefer reinschauen, Caches darin können weg.");
        if (In(Local) || In(Roaming)) return (SpaceVerdict.Check, "Daten eines Programms. Nur löschen, wenn das Programm nicht mehr installiert ist.");
        if (p == Users) return (SpaceVerdict.Check, "Benutzerordner – hier liegen deine eigenen Dateien. Reinklicken.");
        if (In(Users)) return (SpaceVerdict.Check, "Deine eigenen Dateien – nur du weißt, ob du sie noch brauchst.");
        return (SpaceVerdict.Check, "Eigener Ordner – selbst entscheiden.");
    }

    // ================= Schnell-Check
    public static List<SpaceItem> QuickCheck(string drive, CancellationToken ct)
    {
        var root = L(drive);
        bool sys = root.TrimEnd('\\') == SysDrive.TrimEnd('\\');
        var list = new List<SpaceItem>();
        void Folder(string path, string title, string explain, SpaceVerdict v, string action = "Leeren", bool deleteSelf = false)
        {
            if (!L(path).StartsWith(root.TrimEnd('\\'))) return;
            if (!Directory.Exists(path)) return;
            list.Add(new SpaceItem
            {
                Title = title, Explain = explain, Path = path, Verdict = v, ActionLabel = action,
                Action = i => { long freed = DeleteContents(i.Path, deleteSelf); return $"{Fmt.Bytes(freed)} frei geworden"; }
            });
        }

        // Papierkorb
        var rb = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBin(drive, ref rb) == 0 && rb.i64Size > 0)
            list.Add(new SpaceItem
            {
                Title = "Papierkorb", Explain = $"{rb.i64NumItems:N0} gelöschte Dateien warten noch im Papierkorb.", Verdict = SpaceVerdict.Safe, Bytes = rb.i64Size,
                ActionLabel = "Leeren", Confirm = "Papierkorb auf diesem Laufwerk endgültig leeren?",
                Action = i => { SHEmptyRecycleBin(IntPtr.Zero, drive, 0x1 | 0x2 | 0x4); return "Papierkorb geleert"; }
            });

        if (sys)
        {
            Folder(System.IO.Path.GetTempPath(), "Temporäre Dateien (dein Benutzer)", "Reste von Installationen und Programmen. Was gerade benutzt wird, bleibt liegen.", SpaceVerdict.Safe);
            Folder(System.IO.Path.Combine(Win, "temp"), "Temporäre Dateien (Windows)", "Reste von Windows-Updates und Installationen.", SpaceVerdict.Safe);
            Folder(System.IO.Path.Combine(Win, @"softwaredistribution\download"), "Windows-Update-Zwischenspeicher", "Bereits installierte Updates. Windows lädt bei Bedarf neu.", SpaceVerdict.Safe);
            Folder(System.IO.Path.Combine(Win, @"serviceprofiles\networkservice\appdata\local\microsoft\windows\deliveryoptimization\cache"), "Übermittlungsoptimierung", "Update-Teile, die Windows an andere PCs weitergibt. Kann weg.", SpaceVerdict.Safe);
            Folder(System.IO.Path.Combine(Win, "minidump"), "Absturzberichte (Minidump)", "Nur für Fehlersuche nach Bluescreens.", SpaceVerdict.Safe);
            Folder(System.IO.Path.Combine(Local, "crashdumps"), "Absturzberichte von Programmen", "Nur für Fehlersuche.", SpaceVerdict.Safe);
            Folder(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"microsoft\windows\wer"), "Windows-Fehlerberichte", "Gesammelte Fehlerberichte. Können weg.", SpaceVerdict.Safe);
            foreach (var (sub, t) in new[] { (@"nvidia\dxcache", "NVIDIA Shader-Cache (DirectX)"), (@"nvidia\glcache", "NVIDIA Shader-Cache (OpenGL/Vulkan)"), ("d3dscache", "DirectX Shader-Cache"), (@"amd\dxcache", "AMD Shader-Cache"), (@"amd\dxccache", "AMD Shader-Cache"), (@"intel\shadercache", "Intel Shader-Cache") })
                Folder(System.IO.Path.Combine(Local, sub), t, "Wird von Spielen neu angelegt. Hilft auch bei Rucklern nach Treiber-Updates. Erster Spielstart danach dauert etwas länger.", SpaceVerdict.Safe);
            foreach (var (sub, t) in new[] { (@"google\chrome\user data\default\cache", "Chrome-Cache"), (@"microsoft\edge\user data\default\cache", "Edge-Cache"), (@"bravesoftware\brave-browser\user data\default\cache", "Brave-Cache") })
                Folder(System.IO.Path.Combine(Local, sub), t, "Zwischengespeicherte Webseiten. Browser vorher schließen – Passwörter und Verlauf bleiben.", SpaceVerdict.Safe);
            foreach (var sub in new[] { @"discord\cache", @"discord\code cache" })
                Folder(System.IO.Path.Combine(Roaming, sub), "Discord-Cache", "Bilder und Daten aus Chats. Discord vorher schließen.", SpaceVerdict.Safe);

            var dump = System.IO.Path.Combine(Win, "memory.dmp");
            if (File.Exists(dump))
                list.Add(new SpaceItem { Title = "Große Absturzdatei (MEMORY.DMP)", Explain = "Vom letzten Bluescreen. Nur für Fehlersuche.", Path = dump, Verdict = SpaceVerdict.Safe, Bytes = Size(dump), ActionLabel = "Löschen", Action = i => { File.Delete(i.Path); return "Gelöscht"; } });

            var old = System.IO.Path.Combine(drive, "Windows.old");
            if (Directory.Exists(old))
                list.Add(new SpaceItem
                {
                    Title = "Windows.old (altes Windows)", Explain = "Bleibt nach einem großen Windows-Update 10 Tage zum Zurückgehen. Läuft alles, kann es weg. Wird über die Windows-Datenträgerbereinigung entfernt (Haken bei „Vorherige Windows-Installationen“).",
                    Path = old, Verdict = SpaceVerdict.Safe, ActionLabel = "Datenträgerbereinigung öffnen",
                    Action = i => { Env.Run("cleanmgr.exe", "/d " + drive.TrimEnd('\\'), 1000); return "Datenträgerbereinigung geöffnet – „Systemdateien bereinigen“ klicken, dann „Vorherige Windows-Installationen“ anhaken."; }
                });

            var hib = System.IO.Path.Combine(drive, "hiberfil.sys");
            if (File.Exists(hib))
                list.Add(new SpaceItem
                {
                    Title = "Ruhezustand-Datei (hiberfil.sys)", Explain = "Brauchst du nur, wenn du den PC in den Ruhezustand schickst. Ausschalten löscht die Datei – aber auch der „Schnellstart“ von Windows ist dann aus (Start dauert ein paar Sekunden länger). Energiesparen geht weiterhin.",
                    Path = hib, Verdict = SpaceVerdict.Check, Bytes = Size(hib), ActionLabel = "Ruhezustand ausschalten",
                    Confirm = "Ruhezustand und Schnellstart ausschalten? Wieder einschalten geht mit „powercfg /h on“ in einer Admin-Eingabeaufforderung.",
                    Action = i => { var r = Env.Run("powercfg.exe", "/h off"); return r.Code == 0 ? "Ruhezustand aus – Datei ist weg" : "Fehlgeschlagen: " + r.Output.Trim(); }
                });

            var pf = System.IO.Path.Combine(drive, "pagefile.sys");
            if (File.Exists(pf))
                list.Add(new SpaceItem { Title = "Auslagerungsdatei (pagefile.sys)", Explain = "Windows braucht sie, wenn der RAM voll wird – ohne sie stürzen Spiele ab. Nicht löschen, Windows verwaltet die Größe selbst.", Path = pf, Verdict = SpaceVerdict.Important, Bytes = Size(pf) });

            list.Add(new SpaceItem
            {
                Title = "Windows-Komponenten (WinSxS)", Explain = "Der Komponentenspeicher sieht riesig aus, ist aber zum Großteil doppelt gezählt. NIE von Hand löschen! Sicher aufräumen geht nur mit dem Windows-eigenen Werkzeug (DISM) – entfernt alte Update-Versionen. Dauert 5–15 Minuten.",
                Path = System.IO.Path.Combine(Win, "winsxs"), Verdict = SpaceVerdict.Important, Bytes = -1, ActionLabel = "Sicher aufräumen (DISM)",
                Confirm = "Alte Komponenten-Versionen mit dem Windows-Werkzeug DISM entfernen? Dauert 5–15 Minuten, der PC kann dabei normal benutzt werden. Danach lassen sich bereits installierte Updates nicht mehr deinstallieren.",
                Action = i => { var r = Env.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 40 * 60_000); return r.Code == 0 ? "Komponentenspeicher aufgeräumt ✔" : $"DISM meldet Code {r.Code}"; }
            });

            list.Add(new SpaceItem
            {
                Title = "Wiederherstellungspunkte", Explain = "Sicherungen von Windows für den Notfall (System Volume Information). Wichtig – nicht löschen. Wenn sie sehr groß sind, kann man unter „Systemschutz“ den Speicher begrenzen (5 % reichen).",
                Verdict = SpaceVerdict.Important, Bytes = -1, ActionLabel = "Systemschutz öffnen",
                Action = i => { Env.Open("SystemPropertiesProtection.exe"); return "Systemschutz geöffnet – „Konfigurieren“ → Speicherplatz begrenzen."; }
            });
        }

        // Downloads (falls auf diesem Laufwerk)
        var dl = DownloadsCleaner.Folder;
        if (L(dl).StartsWith(root.TrimEnd('\\')) && Directory.Exists(dl))
            list.Add(new SpaceItem { Title = "Downloads", Explain = "Deine heruntergeladenen Dateien – oft alte Installer und Videos. Oben unter „Downloads aufräumen“ sortieren und alte Installer entfernen.", Path = dl, Verdict = SpaceVerdict.Check });
        var vids = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (L(vids).StartsWith(root.TrimEnd('\\')) && Directory.Exists(vids))
            list.Add(new SpaceItem { Title = "Videos & Aufnahmen", Explain = "Spiel-Clips (NVIDIA, Xbox Game Bar) landen hier und werden schnell riesig. Selbst prüfen.", Path = vids, Verdict = SpaceVerdict.Check, ActionLabel = "Ordner öffnen", Action = i => { Env.Open(i.Path); return ""; } });

        // Steam auf diesem Laufwerk: Shader-Cache und abgebrochene Downloads
        try
        {
            var roots = new List<string>();
            try { if (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string sp) roots.Add(sp.Replace('/', '\\')); } catch { }
            foreach (var lib in SteamLibrary.FindLibraries(roots).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!L(lib).StartsWith(root.TrimEnd('\\'))) continue;
                Folder(System.IO.Path.Combine(lib, @"steamapps\shadercache"), "Steam Shader-Cache", "Vorkompilierte Shader für Steam-Spiele. Kann geleert werden, Steam lädt sie bei Bedarf neu.", SpaceVerdict.Safe);
                Folder(System.IO.Path.Combine(lib, @"steamapps\downloading"), "Abgebrochene Steam-Downloads", "Reste von abgebrochenen Downloads/Updates. Steam vorher schließen.", SpaceVerdict.Safe);
                Folder(System.IO.Path.Combine(lib, @"steamapps\temp"), "Steam-Temp", "Temporäre Steam-Dateien. Steam vorher schließen.", SpaceVerdict.Safe);
                var common = System.IO.Path.Combine(lib, @"steamapps\common");
                if (Directory.Exists(common))
                    list.Add(new SpaceItem { Title = $"Steam-Spiele ({lib})", Explain = "Installierte Spiele. Nicht im Explorer löschen – in Steam per Rechtsklick → Verwalten → Deinstallieren.", Path = common, Verdict = SpaceVerdict.Check, ActionLabel = "Steam-Speicher öffnen", Action = i => { Env.Open("steam://open/storage"); return "Steam-Speicherverwaltung geöffnet"; } });
            }
        }
        catch { }

        // Größen messen (parallel)
        Parallel.ForEach(list.Where(i => i.Bytes == 0 && i.Path.Length > 0 && Directory.Exists(i.Path)), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            i => i.Bytes = DirSize(i.Path, ct));
        return list.Where(i => i.Bytes != 0 || i.Verdict == SpaceVerdict.Important)
                   .OrderBy(i => i.Bytes < 0 ? 1 : 0).ThenByDescending(i => i.Verdict == SpaceVerdict.Safe).ThenByDescending(i => i.Bytes).ToList();
    }

    private static long Size(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    private static readonly EnumerationOptions Deep = new() { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

    public static long DirSize(string dir, CancellationToken ct)
    {
        long s = 0; int n = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep)) { if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested(); try { s += f.Length; } catch { } } }
        catch (OperationCanceledException) { throw; }
        catch { }
        return s;
    }

    /// <summary>Inhalt eines Ordners löschen (gesperrte Dateien bleiben liegen). Gibt die freigegebenen Bytes zurück.</summary>
    public static long DeleteContents(string dir, bool deleteSelf = false)
    {
        long freed = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep))
                try { long len = f.Length; f.Attributes = FileAttributes.Normal; f.Delete(); freed += len; } catch { }
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                try { d.Delete(true); } catch { }
            if (deleteSelf) try { Directory.Delete(dir, true); } catch { }
        }
        catch { }
        Log.Info(LogModule.App, $"Speicherplatz: {dir} geleert – {Fmt.Bytes(freed)} frei");
        return freed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
}
