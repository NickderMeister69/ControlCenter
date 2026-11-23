using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NickControlCenter;

/// <summary>
/// Lokale KI für ARK-Fragen: Ollama (läuft auf deinem PC, nichts geht an fremde KI-Server). Für jede Frage sucht die App
/// passende Seiten im ARK-Wiki (ark.wiki.gg) und gibt sie zusammen mit den Astraeos-Daten als Wissen mit – so antwortet
/// das Modell mit echten Infos statt zu raten, und die Quellen werden angezeigt.
/// </summary>
public static class ArkAi
{
    public const string Ollama = "http://localhost:11434";
    public const string Recommended = "qwen2.5:7b";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly HttpClient Web = Create();
    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ControlCenter-ArkHelper/1.0 (github.com/NickderMeister69/ControlCenter)");
        return c;
    }

    /// <summary>Installierte Modelle (leer = Ollama läuft nicht).</summary>
    public static async Task<List<string>?> ModelsAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(Ollama + "/api/tags");
            using var d = JsonDocument.Parse(json);
            return d.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0).ToList();
        }
        catch { return null; }
    }

    public static string? OllamaExe()
    {
        foreach (var p in new[] { @"%LOCALAPPDATA%\Programs\Ollama\ollama app.exe", @"%LOCALAPPDATA%\Programs\Ollama\ollama.exe", @"%ProgramFiles%\Ollama\ollama app.exe" })
        {
            var f = Environment.ExpandEnvironmentVariables(p);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Ollama starten, falls installiert aber nicht aktiv.</summary>
    public static async Task<bool> EnsureRunningAsync()
    {
        if (await ModelsAsync() != null) return true;
        var exe = OllamaExe();
        if (exe == null) return false;
        Env.OpenAsUser(exe);
        for (int i = 0; i < 20; i++) { await Task.Delay(1000); if (await ModelsAsync() != null) return true; }
        return false;
    }

    public static async Task<bool> InstallOllamaAsync()
    {
        var r = await Task.Run(() => Env.Run("cmd.exe", "/d /c winget install --id Ollama.Ollama --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity", 15 * 60_000));
        return await EnsureRunningAsync();
    }

    /// <summary>Modell herunterladen (Fortschritt 0–100).</summary>
    public static async Task<bool> PullAsync(string model, IProgress<(double Percent, string Text)> progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Ollama + "/api/pull") { Content = new StringContent(JsonSerializer.Serialize(new { name = model, stream = true }), Encoding.UTF8, "application/json") };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var sr = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0) continue;
            try
            {
                var n = JsonNode.Parse(line);
                var status = n?["status"]?.GetValue<string>() ?? "";
                double total = n?["total"]?.GetValue<double>() ?? 0, done = n?["completed"]?.GetValue<double>() ?? 0;
                progress.Report((total > 0 ? done / total * 100 : -1, total > 0 ? $"{status} – {Fmt.Bytes(done)} von {Fmt.Bytes(total)}" : status));
                if (status == "success") return true;
                if (n?["error"] != null) throw new InvalidOperationException(n["error"]!.GetValue<string>());
            }
            catch (JsonException) { }
        }
        return true;
    }

    public sealed record Source(string Title, string Url);

    /// <summary>Wiki-Seiten zur Frage suchen und als Text holen (englisches ARK-Wiki).</summary>
    private static async Task<List<(Source Src, string Text)>> WikiAsync(string query, CancellationToken ct)
    {
        var res = new List<(Source, string)>();
        try
        {
            var url = "https://ark.wiki.gg/api.php?action=query&list=search&format=json&srlimit=3&srsearch=" + Uri.EscapeDataString(query);
            using var d = JsonDocument.Parse(await Web.GetStringAsync(url, ct));
            var titles = d.RootElement.GetProperty("query").GetProperty("search").EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToList();
            foreach (var t in titles)
            {
                try
                {
                    var purl = "https://ark.wiki.gg/api.php?action=parse&format=json&formatversion=2&prop=wikitext&page=" + Uri.EscapeDataString(t);
                    using var pd = JsonDocument.Parse(await Web.GetStringAsync(purl, ct));
                    var wt = pd.RootElement.GetProperty("parse").GetProperty("wikitext").GetString() ?? "";
                    res.Add((new Source(t, "https://ark.wiki.gg/wiki/" + Uri.EscapeDataString(t.Replace(' ', '_'))), CleanWikitext(wt)));
                }
                catch { }
            }
        }
        catch { }
        return res;
    }

    /// <summary>Wikitext grob in lesbaren Text verwandeln und kürzen.</summary>
    public static string CleanWikitext(string wt)
    {
        var t = Regex.Replace(wt, @"<ref[^>]*>.*?</ref>|<ref[^>]*/>|<!--.*?-->", "", RegexOptions.Singleline);
        t = Regex.Replace(t, @"\[\[(?:File|Datei|Image|Category):[^\]]*\]\]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\[\[([^\]|]*\|)?([^\]]*)\]\]", "$2");
        t = Regex.Replace(t, @"'{2,}", "");
        t = Regex.Replace(t, @"<[^>]+>", "");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Length > 3500 ? t[..3500] : t;
    }

    /// <summary>Frage stellen – Antwort kommt Stück für Stück (onToken). Gibt die verwendeten Quellen zurück.</summary>
    public static async Task<List<Source>> AskAsync(string model, string question, Action<string> onToken, Action<string> onStatus, CancellationToken ct)
    {
        onStatus("Suche im ARK-Wiki …");
        // deutsche Frage → englische Suchbegriffe (das Wiki ist englisch)
        string search = question;
        try
        {
            var q = await ChatAsync(model, new[] { ("system", "Gib NUR 2 bis 5 englische Suchbegriffe für das ARK: Survival Ascended Wiki zurück, ohne Erklärung."), ("user", question) }, null, ct);
            if (!string.IsNullOrWhiteSpace(q) && q.Length < 120) search = q.Trim().Trim('"');
        }
        catch { }
        var wiki = await WikiAsync(search, ct);
        if (wiki.Count == 0 && search != question) wiki = await WikiAsync(question, ct);
        onStatus(wiki.Count > 0 ? $"{wiki.Count} Wiki-Seiten gefunden – KI antwortet …" : "Nichts im Wiki gefunden – KI antwortet mit eigenem Wissen …");
        var ctx = new StringBuilder();
        ctx.AppendLine("EIGENE DATEN (Map Astraeos):").AppendLine(ArkData.AsContext());
        foreach (var (src, text) in wiki) ctx.AppendLine($"WIKI-SEITE „{src.Title}“:").AppendLine(text).AppendLine();
        var system = "Du bist ein hilfreicher Experte für ARK: Survival Ascended. Der Spieler spielt hauptsächlich auf der Map Astraeos (offizielle Server, bald eigener Server). " +
                     "Antworte auf Deutsch, kurz und praktisch (Stichpunkte erlaubt). Nutze vor allem das folgende Wissen. Wenn etwas dort nicht steht und du unsicher bist, sag das ehrlich statt zu raten. " +
                     "Koordinaten immer als Breite / Länge angeben.\n\n" + ctx;
        await ChatAsync(model, new[] { ("system", system), ("user", question) }, onToken, ct);
        return wiki.Select(w => w.Src).ToList();
    }

    private static async Task<string> ChatAsync(string model, (string Role, string Content)[] msgs, Action<string>? onToken, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model, stream = onToken != null,
            messages = msgs.Select(m => new { role = m.Role, content = m.Content }),
            options = new { temperature = 0.3, num_ctx = 8192 }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, Ollama + "/api/chat") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (onToken == null)
        {
            var n = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            return n?["message"]?["content"]?.GetValue<string>() ?? "";
        }
        var all = new StringBuilder();
        using var sr = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0) continue;
            var n = JsonNode.Parse(line);
            var tok = n?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(tok)) { all.Append(tok); onToken(tok); }
            if (n?["done"]?.GetValue<bool>() == true) break;
        }
        return all.ToString();
    }
}
