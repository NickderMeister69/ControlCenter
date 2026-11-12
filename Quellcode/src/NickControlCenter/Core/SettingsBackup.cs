using System.IO;

namespace NickControlCenter;

/// <summary>
/// Sicherung aller Einstellungen (Lüfter, RGB, Boost, Spielprofile, Overlay, Tweaker …) aus %AppData%\NickControlCenter.
/// Automatisch einmal am Tag und vor jedem Update, die letzten 15 Sicherungen bleiben. Der Passwort-Tresor wird mitgesichert (bleibt verschlüsselt).
/// </summary>
public static class SettingsBackup
{
    public static string Folder => Path.Combine(JsonStore.Folder, "Sicherungen");

    public static List<string> List() =>
        Directory.Exists(Folder) ? Directory.GetDirectories(Folder).OrderByDescending(d => d).ToList() : new();

    public static string? Create(string reason)
    {
        try
        {
            var src = JsonStore.Folder;
            if (!Directory.Exists(src)) return null;
            var files = Directory.GetFiles(src).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vault", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count == 0) return null;
            var dest = Path.Combine(Folder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(dest);
            foreach (var f in files) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            File.WriteAllText(Path.Combine(dest, "grund.txt"), reason);
            foreach (var old in List().Skip(15)) try { Directory.Delete(old, true); } catch { }
            Log.Info(LogModule.App, $"Einstellungen gesichert ({reason}): {files.Count} Dateien");
            return dest;
        }
        catch (Exception ex) { Log.Warn(LogModule.App, "Einstellungen sichern: " + ex.Message); return null; }
    }

    /// <summary>Einmal am Tag automatisch.</summary>
    public static void Daily()
    {
        var last = List().FirstOrDefault();
        if (last == null || !Path.GetFileName(last).StartsWith(DateTime.Now.ToString("yyyy-MM-dd"))) Create("täglich");
    }

    /// <summary>Sicherung zurückspielen – danach startet die App neu, damit alles neu geladen wird.</summary>
    public static void Restore(string dir)
    {
        Create("vor dem Wiederherstellen");
        foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith("grund.txt")))
            File.Copy(f, Path.Combine(JsonStore.Folder, Path.GetFileName(f)), true);
        Log.Ok(LogModule.App, "Einstellungen wiederhergestellt aus " + Path.GetFileName(dir));
    }

    public static string Describe(string dir)
    {
        var name = Path.GetFileName(dir);
        string reason = "";
        try { reason = File.ReadAllText(Path.Combine(dir, "grund.txt")); } catch { }
        return DateTime.TryParseExact(name, "yyyy-MM-dd_HH-mm-ss", null, System.Globalization.DateTimeStyles.None, out var d)
            ? $"{d:dd.MM.yyyy HH:mm} ({reason})" : name;
    }
}
