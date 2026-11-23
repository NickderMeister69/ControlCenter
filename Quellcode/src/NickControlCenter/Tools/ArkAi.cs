using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NickControlCenter;

/// <summary>
/// KI für ARK-Fragen über Ollama – entweder auf dem eigenen PC oder auf dem PC eines Freundes, der seine KI freigegeben hat
/// (siehe <see cref="AiShare"/>). Nichts geht an fremde KI-Firmen. Für jede Frage sucht die App
/// passende Seiten im ARK-Wiki (ark.wiki.gg) und gibt sie zusammen mit den Astraeos-Daten als Wissen mit – so antwortet
/// das Modell mit echten Infos statt zu raten, und die Quellen werden angezeigt.
/// </summary>
public static class ArkAi
{
    public const string LocalOllama = "http://localhost:11434";
    /// <summary>true = die KI eines Freundes wird benutzt (Einladungs-Code).</summary>
    public static bool UsesFriend => ArkWeb.Settings.AiMode == "friend" && !string.IsNullOrEmpty(ArkWeb.Settings.FriendToken);
    private static string? _friendBase;
    private static string Ollama => UsesFriend ? (_friendBase ?? ArkWeb.Settings.FriendUrl ?? "").TrimEnd('/') : LocalOllama;

    /// <summary>Aktuelle Adresse der Freundes-KI holen (Tunnel-Adresse aus dem Repo, sonst die aus dem Code).</summary>
    private static async Task ResolveFriendAsync(bool force)
    {
        if (!UsesFriend) return;
        var pub = await AiShare.PublishedAddressAsync(force);
        _friendBase = pub ?? ArkWeb.Settings.FriendUrl;
    }
    /// <summary>Wo die KI läuft – für die Anzeige.</summary>
    public static string WhereText => UsesFriend ? $"KI läuft auf dem PC von {ArkWeb.Settings.FriendName}" : "KI läuft auf deinem PC";

    private static HttpRequestMessage Req(HttpMethod m, string path, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, Ollama + path) { Content = body };
        if (UsesFriend && !string.IsNullOrEmpty(ArkWeb.Settings.FriendToken)) r.Headers.Add(AiShare.TokenHeader, ArkWeb.Settings.FriendToken);
        return r;
    }
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
        if (UsesFriend)
        {
            await ResolveFriendAsync(false);
            var r = await TryModelsAsync();
            if (r != null) return r;
            await ResolveFriendAsync(true);   // Adresse evtl. neu (PC des Freundes neu gestartet)
        }
        return await TryModelsAsync();
    }

    private static async Task<List<string>?> TryModelsAsync()
    {
        if (UsesFriend && string.IsNullOrEmpty(Ollama)) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(UsesFriend ? 8 : 4));
            using var resp = await Http.SendAsync(Req(HttpMethod.Get, "/api/tags"), cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
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
        if (UsesFriend) return false;
        var exe = OllamaExe();
        if (exe == null) return false;
        Env.OpenAsUser(exe);
        for (int i = 0; i < 20; i++) { await Task.Delay(1000); if (await ModelsAsync() != null) return true; }
        return false;
    }

    /// <summary>Ollama komplett beenden – gibt Grafikkarten-Speicher und RAM sofort frei.</summary>
    public static int StopOllama()
    {
        if (ArkWeb.Settings.ShareEnabled) { ArkWeb.Settings.ShareEnabled = false; ArkWeb.Save(); AiShare.Stop(); }
        int n = 0;
        foreach (var name in new[] { "ollama app", "ollama", "ollama_llama_server" })
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(true); n++; } catch { } finally { p.Dispose(); }
        return n;
    }

    public static async Task<bool> InstallOllamaAsync()
    {
        var r = await Task.Run(() => Env.Run("cmd.exe", "/d /c winget install --id Ollama.Ollama --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity", 15 * 60_000));
        return await EnsureRunningAsync();
    }

    /// <summary>Modell herunterladen (Fortschritt 0–100).</summary>
    public static async Task<bool> PullAsync(string model, IProgress<(double Percent, string Text)> progress, CancellationToken ct)
    {
        if (UsesFriend) throw new InvalidOperationException("Modelle laden geht nur bei deiner eigenen KI.");
        using var req = new HttpRequestMessage(HttpMethod.Post, LocalOllama + "/api/pull") { Content = new StringContent(JsonSerializer.Serialize(new { name = model, stream = true }), Encoding.UTF8, "application/json") };
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

    // Deutsche ARK-Begriffe → englische Wiki-Begriffe (das Wiki ist englisch). Hilft, auch wenn die KI schlechte Suchbegriffe liefert.
    private static readonly (string De, string En)[] Glossary =
    {
        ("metall", "Metal"), ("kristall", "Crystal"), ("obsidian", "Obsidian"), ("öl", "Oil"), ("oel", "Oil"), ("schwefel", "Sulfur"),
        ("perle", "Black Pearl"), ("silica", "Silica Pearls"), ("polymer", "Organic Polymer"), ("zement", "Cement Paste"), ("chitin", "Chitin"),
        ("stein", "Stone"), ("holz", "Wood"), ("stroh", "Thatch"), ("faser", "Fiber"), ("beeren", "Berry"), ("fleisch", "Raw Meat"),
        ("leder", "Hide"), ("fell", "Pelt"), ("feuerstein", "Flint"), ("element", "Element"), ("honig", "Giant Bee Honey"), ("salz", "Salt"),
        ("zähm", "Taming"), ("zaehm", "Taming"), ("betäub", "Narcotic"), ("kibble", "Kibble"), ("züchte", "Breeding"), ("zucht", "Breeding"), ("ei", "Egg"),
        ("sattel", "Saddle"), ("höhle", "Caves"), ("artefakt", "Artifact"), ("boss", "Bosses"), ("tribut", "Tribute"), ("obelisk", "Obelisk"),
        ("schmiede", "Refining Forge"), ("industrie", "Industrial Forge"), ("waffe", "Weapons"), ("rüstung", "Armor"), ("tek", "TEK Tier"),
    };

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    { "the","a","an","of","for","in","on","to","and","or","is","best","ark","survival","ascended","how","what","where","which","dino","creature","good" };

    private static async Task<List<(string Title, int Size)>> SearchAsync(string q, int limit, CancellationToken ct)
    {
        try
        {
            var url = $"https://ark.wiki.gg/api.php?action=query&list=search&format=json&srlimit={limit}&srsearch=" + Uri.EscapeDataString(q);
            using var d = JsonDocument.Parse(await Web.GetStringAsync(url, ct));
            return d.RootElement.GetProperty("query").GetProperty("search").EnumerateArray()
                    .Select(x => (x.GetProperty("title").GetString()!, x.TryGetProperty("size", out var z) ? z.GetInt32() : 0)).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn(LogModule.App, "ARK-Wiki-Suche: " + ex.Message); return new(); }
    }

    /// <summary>Mehrere Suchen (KI-Begriffe + Wörterbuch), Treffer zusammenzählen, die besten 3 Seiten holen.</summary>
    private static async Task<List<(Source Src, string Text)>> WikiAsync(IEnumerable<string> queries, string[] focus, CancellationToken ct)
    {
        var score = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queries.Where(q => q.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
        {
            var hits = await SearchAsync(q, 4, ct);
            for (int i = 0; i < hits.Count; i++)
            {
                var t = hits[i].Title;
                if (t.Contains("(Mobile)") || t.Contains("(Survival Evolved)") || t.StartsWith("Mod:")) continue;
                double sc = 4 - i;
                if (string.Equals(t, q, StringComparison.OrdinalIgnoreCase)) sc += 6;   // Seite heißt genau wie der Begriff
                score[t] = score.GetValueOrDefault(t) + sc;
            }
        }
        var res = new List<(Source, string)>();
        foreach (var t in score.OrderByDescending(x => x.Value).Select(x => x.Key).Take(3))
        {
            try
            {
                var purl = "https://ark.wiki.gg/api.php?action=parse&format=json&formatversion=2&prop=wikitext&redirects=1&page=" + Uri.EscapeDataString(t);
                using var pd = JsonDocument.Parse(await Web.GetStringAsync(purl, ct));
                var wt = pd.RootElement.GetProperty("parse").GetProperty("wikitext").GetString() ?? "";
                var text = Relevant(CleanWikitext(wt), focus.Where(f => !t.Contains(f, StringComparison.OrdinalIgnoreCase)).ToArray(), 2600);
                if (text.Length > 80) res.Add((new Source(t, "https://ark.wiki.gg/wiki/" + Uri.EscapeDataString(t.Replace(' ', '_'))), text));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return res;
    }

    /// <summary>Wikitext lesbar machen: Vorlagen/Infoboxen, Tabellen, Bilder und Referenzen entfernen.</summary>
    public static string CleanWikitext(string wt)
    {
        var t = Regex.Replace(wt, @"<ref[^>]*>.*?</ref>|<ref[^>]*/>|<!--.*?-->", "", RegexOptions.Singleline);
        // {{ItemLink|X}} / {{ItemLink|X|Text}} → Text behalten, danach alle übrigen Vorlagen (auch verschachtelt) entfernen
        t = Regex.Replace(t, @"\{\{(?:ItemLink|Link|il)\|([^}|]*)(?:\|([^}|]*))?[^}]*\}\}", m => m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value, RegexOptions.IgnoreCase);
        for (int i = 0; i < 6; i++) t = Regex.Replace(t, @"\{\{[^{}]*\}\}", "");
        t = Regex.Replace(t, @"\{\|.*?\|\}", "", RegexOptions.Singleline);   // Tabellen
        t = Regex.Replace(t, @"\[\[(?:File|Datei|Image|Category):[^\]]*\]\]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\[\[([^\]|]*\|)?([^\]]*)\]\]", "$2");
        t = Regex.Replace(t, @"'{2,}", "");
        t = Regex.Replace(t, @"<[^>]+>", "");
        t = Regex.Replace(t, @"[ \t]+\n", "\n");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Trim();
    }

    /// <summary>Aus einem Artikel die Abschnitte nehmen, die am besten zur Frage passen (statt nur den Anfang).</summary>
    private static string Relevant(string text, string[] focus, int max)
    {
        var parts = Regex.Split(text, @"\n(?===+[^=])");
        if (parts.Length <= 1 || text.Length <= max) return text.Length > max ? text[..max] : text;
        var intro = parts[0].Length > 700 ? parts[0][..700] : parts[0];
        var ranked = parts.Skip(1)
            .Select((p, i) => (p, i, sc: focus.Sum(f => Regex.Matches(p, Regex.Escape(f), RegexOptions.IgnoreCase).Count) * 3
                                         + (Regex.IsMatch(p, @"^==+\s*(Gathering|Taming|Obtaining|Strategy|Combat|Utility|Roles|Basic Info|Location|Spawn)", RegexOptions.IgnoreCase) ? 4 : 0)))
            .OrderByDescending(x => x.sc).ThenBy(x => x.i).ToList();
        var sb = new StringBuilder(intro);
        foreach (var x in ranked)
        {
            if (sb.Length + x.p.Length > max) { if (sb.Length < max - 300) sb.Append('\n').Append(x.p[..Math.Min(x.p.Length, max - sb.Length)]); break; }
            sb.Append('\n').Append(x.p);
        }
        return sb.ToString();
    }

    /// <summary>Frage stellen – Antwort kommt Stück für Stück (onToken). Gibt die verwendeten Quellen zurück.</summary>
    public static async Task<List<Source>> AskAsync(string model, string question, Action<string> onToken, Action<string> onStatus, CancellationToken ct)
    {
        if (UsesFriend && _friendBase == null) await ResolveFriendAsync(false);
        onStatus("Suche im ARK-Wiki …");
        // 1) Suchbegriffe: KI liefert 2–3 kurze englische Begriffe (je Zeile einer), dazu das eingebaute Wörterbuch
        var queries = new List<string>();
        try
        {
            var q = await ChatAsync(model, new[]
            {
                ("system", "You turn ARK: Survival Ascended questions into wiki search terms. Reply with 2 to 3 lines, each 1 to 3 English words: " +
                           "the exact wiki page names that answer it (creature, resource, item or mechanic names). No numbering, no explanation.\n" +
                           "Example: 'welcher dino ist der beste metall farmer' → Metal\nAnkylosaurus\nGathering\n" +
                           "Example: 'wie zähme ich einen rex' → Rex\nTaming"),
                ("user", question)
            }, null, ct, 0.1);
            queries.AddRange(q.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Select(l => Regex.Replace(l, @"^[\-\*\d\.\)\s""']+|[""'.]+$", "").Trim()).Where(l => l.Length is > 1 and < 40).Take(3));
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        var lower = question.ToLowerInvariant();
        foreach (var (de, en) in Glossary) if (lower.Contains(de)) queries.Add(en);
        if (queries.Count == 0) queries.Add(question);
        var focus = queries.SelectMany(x => x.Split(' ')).Where(w => w.Length > 2 && !Stop.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var wiki = await WikiAsync(queries, focus, ct);
        onStatus(wiki.Count > 0 ? $"Gefunden: {string.Join(", ", wiki.Select(w => w.Src.Title))} – KI antwortet …" : "Nichts Passendes im Wiki gefunden – KI antwortet mit eigenem Wissen …");

        // 2) Antwort
        var ctx = new StringBuilder();
        foreach (var (src, text) in wiki) ctx.AppendLine($"### WIKI: {src.Title}").AppendLine(text).AppendLine();
        var map = ArkWeb.Settings.Map;
        if (map == ArkData.MapName && Regex.IsMatch(lower, @"wo |wo$|finde|koordinat|höhle|hoehle|boss|artefakt|ort|spawn|map|karte|astraeos|metall|kristall|öl|obsidian|silica|perle|zement|polymer|schwefel"))
            ctx.AppendLine("### ORTE AUF ASTRAEOS (Community-Daten, Koordinaten Breite / Länge)").AppendLine(ArkData.AsContext());
        var system =
            $"Du bist ein erfahrener ARK: Survival Ascended Spieler und hilfst einem Mitspieler. Er spielt auf der Map {map}.\n" +
            "Regeln:\n" +
            "- Beantworte genau die gestellte Frage. Stütze dich vor allem auf die WIKI-Abschnitte unten; die sind verlässlicher als dein eigenes Wissen.\n" +
            "- Bei „bester Dino für X“: nenne zuerst den Dino, den das Wiki als besten/effizientesten nennt, und warum (z. B. Sammel-Bonus, Gewichtsreduktion). Danach 1–2 Alternativen.\n" +
            "- Orte/Koordinaten nur nennen, wenn danach gefragt wurde oder sie direkt helfen (dann als Breite / Länge).\n" +
            "- Wenn die Infos unten nicht reichen und du unsicher bist: sag das ehrlich, statt zu raten.\n" +
            "- Antworte auf Deutsch, kurz und übersichtlich in Markdown: eine Zeile mit der direkten Antwort in **fett**, dann 2–5 Stichpunkte mit „- “, optional eine Überschrift mit „### “. Keine Einleitung, kein Fazit-Absatz. Spielbegriffe (Dino-/Item-Namen) auf Englisch lassen.\n\n" +
            (ctx.Length > 0 ? ctx.ToString() : "(Keine Wiki-Infos gefunden.)");
        await ChatAsync(model, new[] { ("system", system), ("user", question) }, onToken, ct);
        return wiki.Select(w => w.Src).ToList();
    }

    private static async Task<string> ChatAsync(string model, (string Role, string Content)[] msgs, Action<string>? onToken, CancellationToken ct, double temperature = 0.3)
    {
        var body = JsonSerializer.Serialize(new
        {
            model, stream = onToken != null,
            messages = msgs.Select(m => new { role = m.Role, content = m.Content }),
            options = new { temperature, num_ctx = 8192 }
        });
        using var req = Req(HttpMethod.Post, "/api/chat", new StringContent(body, Encoding.UTF8, "application/json"));
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
