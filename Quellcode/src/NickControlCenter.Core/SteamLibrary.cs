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
