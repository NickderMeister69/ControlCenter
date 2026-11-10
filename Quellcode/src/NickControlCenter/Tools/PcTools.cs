using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NickControlCenter;

// ======================================================================
//  Speicherplatz: größte Ordner finden
// ======================================================================

public sealed class DirSize
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public long Bytes { get; init; }
    public int Files { get; init; }
    public bool IsDir { get; init; }
    public double Share { get; set; }
    public string SizeText => Fmt.Bytes(Bytes);
    public string Info => IsDir ? $"{Files:N0} Dateien" : Files > 1 ? $"{Files:N0} Dateien direkt in diesem Ordner" : "Datei";
    public string Hint { get; init; } = "";
    public bool HasHint => Hint.Length > 0;
    public SpaceVerdict Verdict { get; init; } = SpaceVerdict.Check;
    public string VerdictText => VerdictUi.Text(Verdict);
    public System.Windows.Media.Brush VerdictBrush => VerdictUi.Brush(Verdict);
}

public static class DiskAnalyzer
{
    private static readonly EnumerationOptions Deep = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false
    };
    private static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Bekannte Platzfresser mit Tipp.</summary>
    private static string HintFor(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.EndsWith(@"\downloads")) return "Downloads – mit „Downloads aufräumen“ oben sortieren, alte Installer löschen.";
        if (p.EndsWith(@"\appdata\local\temp") || p.EndsWith(@"\windows\temp")) return "Temporäre Dateien – „Aufräumen“ unter FPS-Boost leert das sicher.";
        if (p.EndsWith(@"\softwaredistribution")) return "Windows-Update-Zwischenspeicher – Datenträgerbereinigung von Windows kann das leeren.";
        if (p.Contains(@"\nvidia\dxcache") || p.Contains(@"\d3dscache") || p.Contains("shadercache")) return "Shader-Cache – wird von Spielen neu angelegt, kann geleert werden (FPS-Boost → Aufräumen).";
        if (p.EndsWith(@"\steamapps") || p.EndsWith(@"\steamapps\common")) return "Steam-Spiele – nicht hier löschen, sondern in Steam deinstallieren.";
        if (p.Contains(@"\epic games")) return "Epic-Spiele – über den Epic Launcher deinstallieren.";
        if (p.EndsWith(@"\windows.old")) return "Altes Windows nach einem Upgrade – Datenträgerbereinigung („Vorherige Windows-Installationen“) entfernt es.";
        if (p.EndsWith(@"\hiberfil.sys")) return "Ruhezustand-Datei.";
        if (p.EndsWith(@"\$recycle.bin")) return "Papierkorb – leeren gibt den Platz frei.";
        return "";
    }

    public static List<DirSize> Scan(string root, CancellationToken ct, Action<string>? progress = null)
    {
        var result = new List<DirSize>();
        string[] dirs;
        try { dirs = Directory.GetDirectories(root, "*", Flat); } catch { dirs = Array.Empty<string>(); }
        long loose = 0; int looseN = 0;
        var bigFiles = new List<DirSize>();
        try
        {
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                long len; try { len = f.Length; } catch { continue; }
                if (len >= 300L << 20)
                {
                    var (v, hint) = SpaceAdvisor.Classify(f.FullName, false);
                    bigFiles.Add(new DirSize { Path = f.FullName, Name = f.Name, Bytes = len, Files = 1, IsDir = false, Hint = hint, Verdict = v });
                }
                else { loose += len; looseN++; }
            }
        }
        catch { }
        var bag = new System.Collections.Concurrent.ConcurrentBag<DirSize>();
        Parallel.ForEach(dirs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, d =>
        {
            progress?.Invoke(d);
            long size = 0; int n = 0;
            try
            {
                foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", Deep))
                {
                    if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
                    try { size += f.Length; n++; } catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            var (v, hint) = SpaceAdvisor.Classify(d, true);
            bag.Add(new DirSize { Path = d, Name = System.IO.Path.GetFileName(d), Bytes = size, Files = n, IsDir = true, Hint = hint, Verdict = v });
        });
        result.AddRange(bag);
        result.AddRange(bigFiles);
        if (looseN > 0) result.Add(new DirSize { Path = root, Name = "(kleinere Dateien direkt hier)", Bytes = loose, Files = looseN, IsDir = false, Hint = "Einzelne Dateien in diesem Ordner.", Verdict = SpaceVerdict.Check });
        result = result.Where(r => r.Bytes > 0).OrderByDescending(r => r.Bytes).ToList();
        long max = result.Count > 0 ? result[0].Bytes : 1;
        foreach (var r in result) r.Share = (double)r.Bytes / Math.Max(1, max);
        return result;
    }

    public static string DriveText(DriveInfo d)
    {
        try
        {
            double used = d.TotalSize - d.TotalFreeSpace;
            return $"{d.Name.TrimEnd('\\')} {(string.IsNullOrEmpty(d.VolumeLabel) ? "" : d.VolumeLabel + " ")}– {Fmt.Bytes(d.TotalFreeSpace)} frei von {Fmt.Bytes(d.TotalSize)} ({used / d.TotalSize * 100:0} % belegt)";
        }
        catch { return d.Name; }
    }
}

// ======================================================================
//  Downloads aufräumen
// ======================================================================

public sealed class SortMove
{
    public required string Source { get; init; }
    public required string Category { get; init; }
    public long Bytes { get; init; }
}

public static class DownloadsCleaner
{
    public static readonly (string Folder, string[] Ext)[] Categories =
    {
        ("Bilder", new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".bmp", ".svg", ".tif", ".tiff", ".ico" }),
        ("Videos", new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v" }),
        ("Musik", new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac", ".wma" }),
        ("Dokumente", new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".odt", ".ods", ".csv", ".rtf", ".md" }),
        ("Archive", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".xz", ".bz2" }),
        ("Installer", new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" }),
        ("Abbilder", new[] { ".iso", ".img" }),
        ("Torrents", new[] { ".torrent" }),
    };
    private static readonly string[] Partial = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

    public static string Folder
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? CategoryOf(string file)
    {
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        foreach (var (f, e) in Categories) if (e.Contains(ext)) return f;
        return null;
    }

    /// <summary>Was würde wohin verschoben? (nur Dateien direkt im Download-Ordner, nichts gerade Heruntergeladenes)</summary>
    public static List<SortMove> Plan()
    {
        var list = new List<SortMove>();
        var dir = Folder;
        if (!Directory.Exists(dir)) return list;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
        {
            try
            {
                if (Partial.Contains(f.Extension.ToLowerInvariant()) || f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if ((DateTime.Now - f.LastWriteTime).TotalMinutes < 10) continue; // läuft evtl. noch
                if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                var cat = CategoryOf(f.Name);
                if (cat != null) list.Add(new SortMove { Source = f.FullName, Category = cat, Bytes = f.Length });
            }
            catch { }
        }
        return list;
    }

    public static (int Moved, int Failed) Sort(List<SortMove> plan)
    {
        int ok = 0, fail = 0;
        foreach (var m in plan)
        {
            try
            {
                var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(m.Source)!, m.Category);
                Directory.CreateDirectory(target);
                File.Move(m.Source, FreeName(target, System.IO.Path.GetFileName(m.Source)));
                ok++;
            }
            catch { fail++; }
        }
        Log.Info(LogModule.App, $"Downloads sortiert: {ok} Dateien verschoben" + (fail > 0 ? $", {fail} übersprungen (in Benutzung)" : ""));
        return (ok, fail);
    }

    private static string FreeName(string dir, string name)
    {
        var p = System.IO.Path.Combine(dir, name);
        if (!File.Exists(p)) return p;
        var b = System.IO.Path.GetFileNameWithoutExtension(name); var e = System.IO.Path.GetExtension(name);
        for (int i = 2; ; i++) { p = System.IO.Path.Combine(dir, $"{b} ({i}){e}"); if (!File.Exists(p)) return p; }
    }

    /// <summary>Alte Installer (im Download-Ordner und im Unterordner „Installer“).</summary>
    public static List<FileInfo> OldInstallers(int days)
    {
        var res = new List<FileInfo>();
        var exts = Categories.First(c => c.Folder == "Installer").Ext;
        foreach (var d in new[] { Folder, System.IO.Path.Combine(Folder, "Installer") })
        {
            if (!Directory.Exists(d)) continue;
            try { res.AddRange(new DirectoryInfo(d).EnumerateFiles().Where(f => exts.Contains(f.Extension.ToLowerInvariant()) && (DateTime.Now - f.LastWriteTime).TotalDays > days)); } catch { }
        }
        return res;
    }

    /// <summary>In den Papierkorb (wiederherstellbar).</summary>
    public static int ToRecycleBin(IEnumerable<string> files)
    {
        var arr = files.ToList();
        if (arr.Count == 0) return 0;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = string.Join("\0", arr) + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0400 | 0x0004 // ALLOWUNDO | NOCONFIRMATION | NOERRORUI | SILENT
        };
        int r = SHFileOperation(ref op);
        Log.Info(LogModule.App, $"{arr.Count} alte Installer in den Papierkorb verschoben (Ergebnis {r})");
        return r == 0 ? arr.Count : arr.Count(f => !File.Exists(f));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}

// ======================================================================
//  Automatisch ausschalten
// ======================================================================

public enum ShutdownWhen { Off, Timer, DownloadDone, Idle }
public enum ShutdownAction { Shutdown, Sleep, Hibernate }

public sealed class AutoShutdown : ObservableObject
{
    public static AutoShutdown Instance { get; } = new();
    private readonly System.Windows.Threading.DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(5) };

    public static string[] WhenNames { get; } = { "Aus", "Nach Zeit", "Wenn der Download fertig ist", "Wenn der PC nicht benutzt wird" };
    public static string[] ActionNames { get; } = { "Herunterfahren", "Energiesparen", "Ruhezustand" };

    private ShutdownWhen _when;
    public int WhenIndex { get => (int)_when; set { _when = (ShutdownWhen)value; Reset(); RaiseAll(nameof(WhenIndex), nameof(Active), nameof(ShowMinutes)); } }
    private ShutdownAction _action;
    public int ActionIndex { get => (int)_action; set { _action = (ShutdownAction)value; Raise(); } }
    private int _minutes = 60;
    public int Minutes { get => _minutes; set { _minutes = Math.Clamp(value, 1, 24 * 60); Reset(); Raise(); } }
    public bool Active => _when != ShutdownWhen.Off;
    public bool ShowMinutes => _when is ShutdownWhen.Timer or ShutdownWhen.Idle;
    private string _status = "Aus";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private DateTime _start;
    private bool _sawDownload;
    private DateTime _quietSince = DateTime.MaxValue;
    private int _fastTicks;
    private CountdownWindow? _cd;

    private AutoShutdown() { _t.Tick += (_, _) => Tick(); }

    private void Reset()
    {
        _start = DateTime.Now; _sawDownload = false; _quietSince = DateTime.MaxValue; _fastTicks = 0;
        if (Active) { _t.Start(); Tick(); } else { _t.Stop(); Status = "Aus"; }
        if (Active) Log.Info(LogModule.App, $"Automatisch ausschalten: {WhenNames[(int)_when]} → {ActionNames[(int)_action]}");
    }

    private void Tick()
    {
        if (_cd != null) return;
        var s = Services.Hub.Latest;
        switch (_when)
        {
            case ShutdownWhen.Timer:
            {
                var left = _start.AddMinutes(_minutes) - DateTime.Now;
                Status = $"{ActionNames[(int)_action]} in {Fmt.Duration(left)}";
                if (left <= TimeSpan.Zero) Fire();
                break;
            }
            case ShutdownWhen.DownloadDone:
            {
                double mbit = s.NetDownBps * 8 / 1e6;
                if (mbit > 8) { if (++_fastTicks >= 4) _sawDownload = true; _quietSince = DateTime.MaxValue; }
                else { _fastTicks = 0; if (_sawDownload && _quietSince == DateTime.MaxValue) _quietSince = DateTime.Now; }
                if (!_sawDownload) Status = $"Warte auf einen Download … (gerade {mbit:0.0} Mbit/s)";
                else if (_quietSince == DateTime.MaxValue) Status = $"Download läuft ({mbit:0} Mbit/s) – danach: {ActionNames[(int)_action]}";
                else
                {
                    var left = TimeSpan.FromMinutes(3) - (DateTime.Now - _quietSince);
                    Status = $"Download scheint fertig – {ActionNames[(int)_action]} in {Fmt.Duration(left)}, falls nichts mehr kommt";
                    if (left <= TimeSpan.Zero) Fire();
                }
                break;
            }
            case ShutdownWhen.Idle:
            {
                var idle = IdleTime();
                double mbit = s.NetDownBps * 8 / 1e6;
                bool busy = s.CpuLoad > 30 || mbit > 8 || (!double.IsNaN(s.GpuLoad) && s.GpuLoad > 40);
                if (busy) { Status = $"PC arbeitet noch (CPU {s.CpuLoad:0} %, Netz {mbit:0} Mbit/s) – warte"; break; }
                var left = TimeSpan.FromMinutes(_minutes) - idle;
                Status = $"Keine Eingabe seit {Fmt.Duration(idle)} – {ActionNames[(int)_action]} nach {_minutes} min ohne Benutzung";
                if (left <= TimeSpan.Zero) Fire();
                break;
            }
        }
    }

    private void Fire()
    {
        _cd = new CountdownWindow($"{ActionNames[(int)_action]} in", 60);
        _cd.Closed += (_, _) =>
        {
            bool go = _cd!.Completed;
            _cd = null;
            if (!go) { Log.Info(LogModule.App, "Automatisch ausschalten abgebrochen"); WhenIndex = 0; return; }
            WhenIndex = 0;
            Execute(_action);
        };
        _cd.Show();
    }

    public static void Execute(ShutdownAction a)
    {
        Log.Info(LogModule.App, "Automatisch ausschalten: " + ActionNames[(int)a]);
        try { AppSettings.Current.Save(); } catch { }
        switch (a)
        {
            case ShutdownAction.Shutdown: Env.Run("shutdown.exe", "/s /t 0 /d p:0:0"); break;
            case ShutdownAction.Sleep: SetSuspendState(false, false, false); break;
            case ShutdownAction.Hibernate: SetSuspendState(true, false, false); break;
        }
    }

    public static TimeSpan IdleTime()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO li);
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
}

/// <summary>Kleines Fenster oben rechts: „Herunterfahren in 60 s“ mit Abbrechen.</summary>
public sealed class CountdownWindow : System.Windows.Window
{
    public bool Completed { get; private set; }
    private int _left;
    private readonly System.Windows.Controls.TextBlock _text = new() { FontSize = 20, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White };
    private readonly System.Windows.Threading.DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(1) };

    public CountdownWindow(string what, int seconds)
    {
        _left = seconds;
        WindowStyle = System.Windows.WindowStyle.None; ResizeMode = System.Windows.ResizeMode.NoResize; Topmost = true; ShowInTaskbar = true;
        SizeToContent = System.Windows.SizeToContent.WidthAndHeight; Title = "Control Center – " + what;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 20, 26));
        var cancel = new System.Windows.Controls.Button { Content = "Abbrechen", Padding = new System.Windows.Thickness(22, 8, 22, 8), Margin = new System.Windows.Thickness(0, 14, 0, 0), FontSize = 14, IsDefault = true, IsCancel = true };
        cancel.Click += (_, _) => Close();
        var now = new System.Windows.Controls.Button { Content = "Sofort", Padding = new System.Windows.Thickness(14, 8, 14, 8), Margin = new System.Windows.Thickness(8, 14, 0, 0), FontSize = 13 };
        now.Click += (_, _) => { Completed = true; Close(); };
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(cancel); row.Children.Add(now);
        var p = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(24, 18, 24, 18) };
        p.Children.Add(new System.Windows.Controls.TextBlock { Text = "CONTROL CENTER", FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray });
        p.Children.Add(_text); p.Children.Add(row);
        Content = new System.Windows.Controls.Border { BorderBrush = System.Windows.Media.Brushes.OrangeRed, BorderThickness = new System.Windows.Thickness(2), Child = p };
        void Upd() => _text.Text = $"{what} {_left} s";
        Upd();
        _t.Tick += (_, _) => { if (--_left <= 0) { Completed = true; Close(); } else Upd(); };
        Loaded += (_, _) =>
        {
            var wa = System.Windows.SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 24; Top = wa.Top + 24;
            _t.Start();
            System.Media.SystemSounds.Exclamation.Play();
        };
        Closed += (_, _) => _t.Stop();
    }
}
