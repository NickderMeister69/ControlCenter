using System.Text.RegularExpressions;

namespace NickControlCenter;

public sealed record SteamGame(int AppId, string Name, string InstallDir, string Library);

/// <summary>Findet Steam-Bibliotheken (libraryfolders.vdf) und installierte Spiele (appmanifest_*.acf).</summary>
public static class SteamLibrary
{
    private static readonly Regex PathRx = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Liest alle Bibliothekspfade aus dem Inhalt einer libraryfolders.vdf.</summary>
    public static List<string> ParseLibraryFolders(string vdf)
    {
        var list = new List<string>();
        foreach (Match m in PathRx.Matches(vdf))
        {
            var p = m.Groups[1].Value.Replace(@"\\", @"\").TrimEnd('\\', '/');
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        return list;
    }

    /// <summary>Wert eines Schlüssels aus einer .acf/.vdf (erste Ebene reicht für appmanifest).</summary>
    public static string? ParseValue(string text, string key)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static List<string> FindLibraries(IEnumerable<string> steamRoots)
    {
        var libs = new List<string>();
        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var r = root.Replace('/', '\\').TrimEnd('\\');
            if (!Directory.Exists(r)) continue;
            if (!libs.Contains(r, StringComparer.OrdinalIgnoreCase)) libs.Add(r);
            var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (var p in ParseLibraryFolders(File.ReadAllText(vdf)))
                        if (Directory.Exists(p) && !libs.Contains(p, StringComparer.OrdinalIgnoreCase)) libs.Add(p);
            }
            catch { }
        }
        return libs;
    }

    public static List<SteamGame> FindGames(IEnumerable<string> libraries)
    {
        var games = new List<SteamGame>();
        foreach (var lib in libraries)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            string[] files;
            try { files = Directory.GetFiles(apps, "appmanifest_*.acf"); } catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    var txt = File.ReadAllText(f);
                    var name = ParseValue(txt, "name");
                    var dir = ParseValue(txt, "installdir");
                    if (!int.TryParse(ParseValue(txt, "appid"), out int id) || name == null || dir == null) continue;
                    // Werkzeuge/Redistributables ausblenden
                    if (name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.Contains("Redistributable") ||
                        name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) || id == 228980) continue;
                    var full = Path.Combine(apps, "common", dir);
                    if (Directory.Exists(full) && games.All(g => g.AppId != id)) games.Add(new SteamGame(id, name, full, lib));
                }
                catch { }
            }
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>Update-Zustand eines installierten Steam-Spiels (aus appmanifest_*.acf).</summary>
public sealed record SteamGameState(int AppId, string Name, string Library, long SizeOnDisk, bool NeedsUpdate, bool Updating, bool Paused,
                                    long BytesToDownload, long BytesDownloaded, DateTime? LastUpdated, long BuildId = 0)
{
    /// <summary>Neueste Version laut Steam-Server (0 = nicht geprüft).</summary>
    public long LatestBuild { get; init; }
    /// <summary>Update nur online erkannt (Steam lief nicht) – Größe kennt erst Steam.</summary>
    public bool OnlineOnly { get; init; }
    public double Progress => BytesToDownload > 0 ? Math.Clamp(BytesDownloaded * 100.0 / BytesToDownload, 0, 100) : 0;
    public long Remaining => Math.Max(0, BytesToDownload - BytesDownloaded);
}

public static class SteamUpdates
{
    /// <summary>Build-Nummer des öffentlichen Zweigs aus einer App-Info (JSON von api.steamcmd.net) – sucht „branches“ → „public“ → „buildid“.</summary>
    public static long ParsePublicBuild(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return Find(doc.RootElement);
        }
        catch { return 0; }
        static long Find(System.Text.Json.JsonElement e)
        {
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals("branches") && p.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    p.Value.TryGetProperty("public", out var pub) && pub.TryGetProperty("buildid", out var b))
                    return long.TryParse(b.ValueKind == System.Text.Json.JsonValueKind.String ? b.GetString() : b.GetRawText(), out var v) ? v : 0;
                var r = Find(p.Value);
                if (r > 0) return r;
            }
            return 0;
        }
    }

    // StateFlags von Steam
    private const int UpdateRequired = 2, FullyInstalled = 4, UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024;

    public static SteamGameState? ParseManifest(string txt, string library)
    {
        var name = SteamLibrary.ParseValue(txt, "name");
        if (!int.TryParse(SteamLibrary.ParseValue(txt, "appid"), out int id) || name == null) return null;
        if (name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.Contains("Redistributable") || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) || id == 228980) return null;
        long L(string k) => long.TryParse(SteamLibrary.ParseValue(txt, k), out var v) ? v : 0;
        int flags = (int)L("StateFlags");
        long build = L("buildid"), target = L("TargetBuildID");
        long toDl = L("BytesToDownload"), done = L("BytesDownloaded");
        bool running = (flags & (UpdateRunning | UpdateStarted)) != 0;
        bool paused = (flags & UpdatePaused) != 0;
        bool needs = (flags & UpdateRequired) != 0 || running || paused || (target > 0 && target != build) || (toDl > 0 && done < toDl);
        long last = L("LastUpdated");
        return new SteamGameState(id, name, library, L("SizeOnDisk"), needs, running, paused, toDl, done,
                                  last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime : null, build);
    }

    public static List<SteamGameState> Scan(IEnumerable<string> libraries)
    {
        var list = new List<SteamGameState>();
        foreach (var lib in libraries)
        {
            var apps = Path.Combine(lib, "steamapps");
            string[] files;
            try { files = Directory.GetFiles(apps, "appmanifest_*.acf"); } catch { continue; }
            foreach (var f in files)
                try { if (ParseManifest(File.ReadAllText(f), lib) is { } g && list.All(x => x.AppId != g.AppId)) list.Add(g); } catch { }
        }
        return list.OrderByDescending(g => g.NeedsUpdate).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
