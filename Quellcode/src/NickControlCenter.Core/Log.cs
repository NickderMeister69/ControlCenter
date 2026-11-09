using System.Collections.Concurrent;
using System.Text;

namespace NickControlCenter;

public enum LogLevel { Info, Ok, Warn, Error }

/// <summary>Bereiche der App – jeder schreibt zusätzlich in eine eigene Log-Datei.</summary>
public static class LogModule
{
    public const string App = "Allgemein";
    public const string Fans = "Luefter";
    public const string Rgb = "Beleuchtung";
    public const string Boost = "Boost";
    public const string Monitor = "Monitor";
    public const string Stress = "Stresstest";
    public const string Tweaker = "Tweaker";
    public const string Diagnose = "Problemloeser";
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Module, string Message)
{
    public string LevelText => Level switch { LogLevel.Ok => "OK", LogLevel.Warn => "WARNUNG", LogLevel.Error => "FEHLER", _ => "INFO" };
    public string Line => $"{Time:yyyy-MM-dd HH:mm:ss}  {LevelText,-8} [{Module}] {Message}";
}

/// <summary>
/// Zentrales Protokoll. Schreibt in den Ordner "Logs" neben der exe (Fallback: %LocalAppData%):
///   Logs\Gesamt.log      – alles
///   Logs\&lt;Bereich&gt;.log – nur der jeweilige Bereich (Luefter, Beleuchtung, Boost …)
/// Dateien über 2 MB werden zu .old umbenannt. Logging wirft nie eine Ausnahme.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<LogEntry> Recent = new();
    private static string? _folder;

    /// <summary>Neuer Eintrag (aus beliebigem Thread!).</summary>
    public static event Action<LogEntry>? Written;

    public static string Folder
    {
        get
        {
            if (_folder != null) return _folder;
            Init(AppContext.BaseDirectory);
            return _folder!;
        }
    }

    /// <summary>Legt den Log-Ordner fest. Ist der exe-Ordner schreibgeschützt, wird %LocalAppData% verwendet.</summary>
    public static void Init(string baseDir)
    {
        string preferred = Path.Combine(baseDir, "Logs");
        try
        {
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, ".schreibtest");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            _folder = preferred;
        }
        catch
        {
            _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NickControlCenter", "Logs");
            try { Directory.CreateDirectory(_folder); } catch { }
        }
    }

    public static IReadOnlyList<LogEntry> RecentEntries => Recent.ToArray();

    public static void Info(string module, string msg) => Write(LogLevel.Info, module, msg);
    public static void Ok(string module, string msg) => Write(LogLevel.Ok, module, msg);
    public static void Warn(string module, string msg) => Write(LogLevel.Warn, module, msg);
    public static void Error(string module, string msg, Exception? ex = null) =>
        Write(LogLevel.Error, module, ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>Kompatibel zu den übernommenen Modulen (Lüfter/RGB).</summary>
    public static void Write(string module, string msg)
    {
        var lvl = msg.Contains("fehlgeschlagen", StringComparison.OrdinalIgnoreCase) || msg.Contains("Fehler", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Warn : LogLevel.Info;
        Write(lvl, module, msg);
    }

    public static void Write(LogLevel level, string module, string msg)
    {
        var e = new LogEntry(DateTime.Now, level, module, msg ?? "");
        Recent.Enqueue(e);
        while (Recent.Count > 3000 && Recent.TryDequeue(out _)) { }
        try
        {
            lock (Gate)
            {
                var folder = Folder;
                Append(Path.Combine(folder, "Gesamt.log"), e.Line);
                Append(Path.Combine(folder, Safe(module) + ".log"), e.Line);
            }
        }
        catch { /* Logging darf nie die App abstürzen lassen */ }
        try { Written?.Invoke(e); } catch { }
    }

    private static string Safe(string module)
    {
        var sb = new StringBuilder();
        foreach (var c in module) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "Allgemein" : sb.ToString();
    }

    private static void Append(string file, string line)
    {
        var fi = new FileInfo(file);
        if (fi.Exists && fi.Length > MaxBytes) File.Move(file, file + ".old", true);
        File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
    }

    /// <summary>Liest das Ende einer Log-Datei (für die Log-Seite).</summary>
    public static string ReadTail(string module, int maxChars = 200_000)
    {
        try
        {
            var file = Path.Combine(Folder, module == "Gesamt" ? "Gesamt.log" : Safe(module) + ".log");
            if (!File.Exists(file)) return "";
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxChars) fs.Seek(-maxChars, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch (Exception ex) { return "Log konnte nicht gelesen werden: " + ex.Message; }
    }
}
