using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace NickControlCenter;

/// <summary>
/// Das LED-Plugin von Wallpaper Engine (iCUE &amp; Chroma SDK, „ledextensions“) direkt aus dem Control Center an-/ausschalten.
/// Steht in wallpaper_engine\config.json unter &lt;Benutzer&gt;.general.user.plugins.ledextensions.enabled.
/// Wallpaper Engine schreibt die Datei beim Beenden selbst – deshalb: schließen, ändern, wieder starten.
/// </summary>
public static class WallpaperEngineLed
{
    private static readonly string[] Processes = { "wallpaper32", "wallpaper64", "wallpaperservice32", "webwallpaper32", "ui32" };

    public static string? FindFolder()
    {
        // 1) aus dem laufenden Prozess
        foreach (var n in new[] { "wallpaper64", "wallpaper32" })
            foreach (var p in Process.GetProcessesByName(n))
                try { var f = Path.GetDirectoryName(p.MainModule?.FileName); if (f != null && File.Exists(Path.Combine(f, "config.json"))) return f; } catch { }
        // 2) Deinstallations-Eintrag von Steam (App 431960) – zeigt direkt auf den Installationsordner
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 431960", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 431960" })
                try
                {
                    if (hive.OpenSubKey(key)?.GetValue("InstallLocation") is string loc && File.Exists(Path.Combine(loc, "config.json"))) return loc;
                }
                catch { }
        // 3) alle Steam-Bibliotheken (auch auf anderen Laufwerken) + übliche Orte
        var roots = new List<string>();
        try { if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string sp) roots.Add(sp.Replace('/', '\\')); } catch { }
        try { if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") is string ip) roots.Add(ip); } catch { }
        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Program Files\Steam");
        foreach (var d in DriveInfo.GetDrives())
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                foreach (var sub in new[] { "Steam", "SteamLibrary", @"Games\Steam", @"Games\SteamLibrary", @"Program Files (x86)\Steam" })
                    roots.Add(Path.Combine(d.RootDirectory.FullName, sub));
            }
            catch { }
        try
        {
            foreach (var lib in SteamLibrary.FindLibraries(roots).Concat(roots).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var f = Path.Combine(lib, "steamapps", "common", "wallpaper_engine");
                if (File.Exists(Path.Combine(f, "config.json"))) return f;
            }
        }
        catch { }
        return null;
    }

    /// <summary>null = Wallpaper Engine/Einstellung nicht gefunden, sonst: ist das LED-Plugin bei irgendeinem Benutzer an?</summary>
    public static bool? IsEnabled()
    {
        try
        {
            var dir = FindFolder();
            if (dir == null) return null;
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "config.json")))?.AsObject();
            if (root == null) return null;
            bool any = false, found = false;
            foreach (var (_, user) in root)
            {
                var en = user?["general"]?["user"]?["plugins"]?["ledextensions"]?["enabled"];
                if (en == null) continue;
                found = true;
                if (en.GetValueKind() == JsonValueKind.True) any = true;
            }
            return found ? any : null;
        }
        catch { return null; }
    }

    public static string SetEnabled(bool on)
    {
        var dir = FindFolder() ?? throw new InvalidOperationException("Wallpaper Engine wurde nicht gefunden.");
        var cfg = Path.Combine(dir, "config.json");
        bool wasRunning = Processes.Any(n => Process.GetProcessesByName(n).Length > 0);
        string? exe = null;
        foreach (var n in new[] { "wallpaper64", "wallpaper32" })
            foreach (var p in Process.GetProcessesByName(n))
                try { exe ??= p.MainModule?.FileName; } catch { }
        exe ??= File.Exists(Path.Combine(dir, "wallpaper64.exe")) ? Path.Combine(dir, "wallpaper64.exe") : Path.Combine(dir, "wallpaper32.exe");

        if (wasRunning)
        {
            SystemActions.CloseProcesses(Processes);
            for (int i = 0; i < 20 && Processes.Any(n => Process.GetProcessesByName(n).Length > 0); i++) Thread.Sleep(250);
        }

        var root = JsonNode.Parse(File.ReadAllText(cfg))?.AsObject() ?? throw new InvalidOperationException("config.json ist leer oder beschädigt.");
        File.Copy(cfg, cfg + ".controlcenter-sicherung", true);
        int changed = 0;
        foreach (var (key, user) in root.ToList())
        {
            if (key.StartsWith('?') || user is not JsonObject u) continue;
            var general = u["general"] as JsonObject; if (general == null) continue;
            var usr = general["user"] as JsonObject ?? (JsonObject)(general["user"] = new JsonObject());
            var plugins = usr["plugins"] as JsonObject ?? (JsonObject)(usr["plugins"] = new JsonObject());
            var led = plugins["ledextensions"] as JsonObject ?? (JsonObject)(plugins["ledextensions"] = new JsonObject());
            led["enabled"] = on;
            changed++;
        }
        var tmp = cfg + ".neu";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, cfg, true);
        Log.Info(LogModule.Diagnose, $"Wallpaper Engine: LED-Plugin {(on ? "eingeschaltet" : "ausgeschaltet")} ({changed} Benutzerprofile, Sicherung: config.json.controlcenter-sicherung)");

        if (wasRunning && File.Exists(exe)) { Thread.Sleep(500); Env.OpenAsUser(exe); }
        return on
            ? "LED-Plugin von Wallpaper Engine ist wieder an." + (wasRunning ? " Wallpaper Engine wurde neu gestartet." : "")
            : "LED-Plugin von Wallpaper Engine ist aus – es überschreibt deine RGB-Farben nicht mehr." + (wasRunning ? " Wallpaper Engine wurde neu gestartet." : "");
    }
}
