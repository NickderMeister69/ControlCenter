namespace NickControlCenter;

public sealed record CleanProgress(string CurrentPath, long FreedBytes, int Deleted, int Skipped);
public sealed record CleanResult(long FreedBytes, int Deleted, int Skipped, bool Cancelled, TimeSpan Duration);

/// <summary>
/// Löscht Dateien in Ordnern (Temp, Shader-Cache …) – im Hintergrund, abbrechbar, mit Fortschritt.
/// Der alte PowerShell-Booster hat alles im UI-Thread rekursiv aufgezählt und dann gelöscht,
/// dadurch hing das Fenster minutenlang. Hier: ein Durchlauf, gesperrte Dateien werden sofort übersprungen,
/// Verknüpfungen (Junctions) werden nie verfolgt.
/// </summary>
public static class TempCleaner
{
    public static Task<CleanResult> CleanAsync(IEnumerable<string> folders, IProgress<CleanProgress>? progress,
        CancellationToken ct, TimeSpan? minAge = null)
        => Task.Run(() => Clean(folders, progress, ct, minAge ?? TimeSpan.Zero), CancellationToken.None);

    public static CleanResult Clean(IEnumerable<string> folders, IProgress<CleanProgress>? progress, CancellationToken ct, TimeSpan minAge)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long freed = 0; int deleted = 0, skipped = 0;
        var lastReport = DateTime.MinValue;
        var cutoff = DateTime.UtcNow - minAge;

        void Report(string path, bool force = false)
        {
            if (progress == null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - lastReport).TotalMilliseconds < 120) return;
            lastReport = now;
            progress.Report(new CleanProgress(path, freed, deleted, skipped));
        }

        foreach (var root in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(root) || IsDangerous(root)) continue;

            var stack = new Stack<string>();
            var dirsBottomUp = new List<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                var dir = stack.Pop();
                if (!ReferenceEquals(dir, root) && dir != root) dirsBottomUp.Add(dir);

                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir); }
                catch { skipped++; continue; }

                foreach (var entry in entries)
                {
                    if (ct.IsCancellationRequested) break;
                    FileAttributes attr;
                    try { attr = File.GetAttributes(entry); } catch { skipped++; continue; }

                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        // Junctions/Symlinks nie betreten – nur den Link selbst entfernen
                        if ((attr & FileAttributes.ReparsePoint) != 0)
                        {
                            try { Directory.Delete(entry, false); } catch { skipped++; }
                            continue;
                        }
                        stack.Push(entry);
                        continue;
                    }

                    try
                    {
                        var fi = new FileInfo(entry);
                        if (minAge > TimeSpan.Zero && fi.LastWriteTimeUtc > cutoff) { skipped++; continue; }
                        long len = fi.Length;
                        if ((attr & FileAttributes.ReadOnly) != 0) fi.Attributes = attr & ~FileAttributes.ReadOnly;
                        fi.Delete();
                        freed += len; deleted++;
                    }
                    catch { skipped++; } // in Benutzung → überspringen, nicht warten
                    Report(entry);
                }
            }

            // leere Unterordner von unten nach oben entfernen
            for (int i = dirsBottomUp.Count - 1; i >= 0 && !ct.IsCancellationRequested; i--)
            {
                try { if (!Directory.EnumerateFileSystemEntries(dirsBottomUp[i]).Any()) Directory.Delete(dirsBottomUp[i]); }
                catch { }
            }
        }
        Report("", true);
        return new CleanResult(freed, deleted, skipped, ct.IsCancellationRequested, sw.Elapsed);
    }

    /// <summary>Schutz: niemals Laufwerkswurzel, Windows-, Programm- oder Benutzerordner selbst leeren.</summary>
    public static bool IsDangerous(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (full.Length <= 3) return true; // "C:" bzw. "/"
            if (Path.GetPathRoot(full)?.TrimEnd('\\', '/') == full) return true;
            string[] forbidden =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            return forbidden.Any(f => !string.IsNullOrEmpty(f) && string.Equals(f.TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; }
    }

    /// <summary>Größe eines Ordners (für die Anzeige "x MB können frei werden").</summary>
    public static long MeasureSafe(string folder, CancellationToken ct)
    {
        long sum = 0;
        if (!Directory.Exists(folder)) return 0;
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var d = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d)) { try { sum += new FileInfo(f).Length; } catch { } }
                foreach (var s in Directory.EnumerateDirectories(d))
                {
                    try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) stack.Push(s); } catch { }
                }
            }
            catch { }
        }
        return sum;
    }
}

public static class Fmt
{
    public static string Size(double bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (1L << 30):0.00} GB" :
        bytes >= 1L << 20 ? $"{bytes / (1L << 20):0} MB" :
        $"{bytes / 1024:0} KB";

    public static string Temp(double c, bool fahrenheit) =>
        double.IsNaN(c) ? "–" : fahrenheit ? $"{c * 9 / 5 + 32:0} °F" : $"{c:0} °C";
}
