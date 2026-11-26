using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace NickControlCenter;

/// <summary>Eine Datei im Download Center eines Freundes.</summary>
public sealed class RemoteFile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Category { get; set; } = "";
    public string Note { get; set; } = "";
    public string Uploader { get; set; } = "";
    public long UploadedAt { get; set; }
    public int Downloads { get; set; }
    public string Info => $"{ShareServer.Fmt(Size)} · {Category} · von {Uploader} · {DateTimeOffset.FromUnixTimeMilliseconds(UploadedAt).LocalDateTime:dd.MM.yyyy HH:mm}";
    public bool HasNote => Note.Length > 0;
    /// <summary>Selbst hochgeladen → darf gelöscht werden.</summary>
    public bool Mine { get; set; }
    public Visibility DeleteVisibility => Mine ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Ein laufender Download/Upload (für die Liste auf der Seite).</summary>
public sealed class Transfer : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public bool IsUpload { get; init; }
    public string Direction => IsUpload ? "↑" : "↓";
    public CancellationTokenSource Cts { get; } = new();
    private double _progress; private string _status = "Startet …"; private bool _running = true;
    public double Progress { get => _progress; set { _progress = value; On(); } }
    private string _speed = "", _amount = "";
    /// <summary>Tempo, z. B. „12,4 MB/s“.</summary>
    public string Speed { get => _speed; set { _speed = value; On(); } }
    /// <summary>Fortschritt in Größe, z. B. „3,2 GB von 40,0 GB“.</summary>
    public string Amount { get => _amount; set { _amount = value; On(); } }
    public string Status { get => _status; set { _status = value; On(); } }
    public bool Running
    {
        get => _running;
        set
        {
            if (_running && !value) Interlocked.Decrement(ref ShareServer.LocalTransfers);
            _running = value; if (!value) Speed = ""; On(); On(nameof(ButtonText));
        }
    }
    public Transfer() { Interlocked.Increment(ref ShareServer.LocalTransfers); }
    public Visibility OpenVisibility => IsUpload ? Visibility.Collapsed : Visibility.Visible;
    public string ButtonText => Running ? (IsUpload ? "Pausieren" : "Abbrechen") : "Entfernen";
    public string? ResultPath { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Download Center eines Freundes direkt im Control Center nutzen – ohne Browser.
/// Spricht dieselbe Schnittstelle wie die Webseite: Anmelden mit dem Passwort aus dem Einladungs-Code,
/// Downloads fortsetzbar (Range), Uploads in Teilen (3 gleichzeitig, fortsetzbar).
/// </summary>
public static class ShareClient
{
    public sealed record Invite(string n, string u, string p, bool r);

    private static CookieContainer _cookies = new();
    private static HttpClient _http = NewClient();
    public static string? BaseUrl { get; private set; }
    public static bool Connected { get; private set; }

    private static HttpClient NewClient() => new(new SocketsHttpHandler
    {
        CookieContainer = _cookies,
        UseCookies = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 8
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static ShareSettings S => ShareServer.Settings;
    public static bool HasFriend => !string.IsNullOrEmpty(S.FriendUrl) && (!string.IsNullOrEmpty(S.FriendPassword) || !string.IsNullOrEmpty(S.FriendKey));

    public static Invite? ParseInvite(string code)
    {
        try
        {
            code = code.Trim();
            if (!code.StartsWith("CCDC-")) return null;
            var b = code[5..].Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var inv = JsonSerializer.Deserialize<Invite>(Encoding.UTF8.GetString(Convert.FromBase64String(b)));
            return inv is { u.Length: > 0, p.Length: > 0 } ? inv : null;
        }
        catch { return null; }
    }

    public static void SetFriend(Invite inv)
    {
        S.FriendName = inv.n; S.FriendUrl = inv.u.TrimEnd('/'); S.FriendPassword = inv.p; S.FriendPublishes = inv.r; S.FriendKey = null;
        ShareServer.Save();
        Reset();
    }

    public static void Disconnect() => Connected = false;

    public static void Forget()
    {
        S.FriendName = S.FriendUrl = S.FriendPassword = S.FriendKey = null; S.FriendPublishes = false;
        ShareServer.Save();
        Reset();
    }

    private static void Reset()
    {
        Connected = false; BaseUrl = null;
        _cookies = new CookieContainer();
        try { _http.Dispose(); } catch { }
        _http = NewClient();
    }

    public static string MyName => string.IsNullOrWhiteSpace(S.MyName) ? Environment.UserName : S.MyName!.Trim();

    // ---------- Verbinden ----------
    /// <summary>Verbinden und anmelden. Ist die gespeicherte Adresse tot (neuer Tunnel), wird die veröffentlichte Adresse geholt.</summary>
    public static async Task<string?> ConnectAsync()
    {
        if (!HasFriend) return "Noch kein Einladungs-Code eingegeben.";
        var tried = new List<string>();
        foreach (var candidate in await CandidatesAsync())
        {
            if (tried.Contains(candidate)) continue;
            tried.Add(candidate);
            if (!await ReachableAsync(candidate)) continue;
            BaseUrl = candidate;
            var err = await LoginAsync();
            if (err != null) { Connected = false; return err; }
            if (candidate != S.FriendUrl) { S.FriendUrl = candidate; ShareServer.Save(); }
            Connected = true;
            return null;
        }
        Connected = false;
        return $"{S.FriendName ?? "Dein Freund"} ist gerade nicht erreichbar – sein PC oder das Control Center ist aus.";
    }

    private static async Task<List<string>> CandidatesAsync()
    {
        var list = new List<string> { S.FriendUrl! };
        if (S.FriendPublishes)
        {
            var pub = await ShareServer.PublishedAddressAsync();
            if (pub != null) list.Add(pub.TrimEnd('/'));
        }
        return list;
    }

    private static async Task<bool> ReachableAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var r = await _http.GetAsync(url + "/api/me", cts.Token);
            return r.IsSuccessStatusCode && (await r.Content.ReadAsStringAsync(cts.Token)).Contains("\"title\"");
        }
        catch { return false; }
    }

    private static async Task<string?> LoginAsync()
    {
        object body = !string.IsNullOrEmpty(S.FriendKey) ? new { name = MyName, key = S.FriendKey } : new { name = MyName, password = S.FriendPassword };
        var (code, json) = await SendAsync(HttpMethod.Post, "/api/login", body, relogin: false);
        if (code == 200) return null;
        if (code == 401 && !string.IsNullOrEmpty(S.FriendKey))
        {
            var who = S.FriendName ?? "Dein Freund";
            Forget();
            return $"{who} hat deinen Zugang entfernt. Du kannst eine neue Anfrage schicken.";
        }
        if (code == 401) return "Das Passwort im Code stimmt nicht mehr – dein Freund hat es geändert. Lass dir einen neuen Code schicken.";
        return Err(json, code);
    }

    private static string Err(JsonElement json, int code) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() ?? $"Fehler {code}" : $"Fehler {code}";

    private sealed class HttpStatusError(int code, string msg) : Exception(msg) { public int Code { get; } = code; }

    private static async Task<(int Code, JsonElement Json)> SendAsync(HttpMethod m, string path, object? body = null, bool relogin = true, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(m, BaseUrl + path);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var res = await _http.SendAsync(req, cts.Token);
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            JsonElement json = default;
            try { if (text.Length > 0) json = JsonDocument.Parse(text).RootElement.Clone(); } catch { }
            if ((int)res.StatusCode == 401 && relogin && attempt == 0 && await LoginAsync() == null) continue;
            return ((int)res.StatusCode, json);
        }
    }

    // ---------- Automatisch finden & Anfrage schicken ----------
    public sealed record Found(string Url, string Owner, string Title);

    /// <summary>Das Download Center aus dem Repo finden (veröffentlichte Tunnel-Adresse).</summary>
    public static async Task<Found?> DiscoverAsync()
    {
        var url = (await ShareServer.PublishedAddressAsync())?.TrimEnd('/');
        if (url == null) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var text = await _http.GetStringAsync(url + "/api/me", cts.Token);
            var j = JsonDocument.Parse(text).RootElement;
            var owner = j.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "";
            var title = j.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            return owner.Length == 0 ? null : new Found(url, owner, title); // ohne owner: alte Version ohne Anfragen
        }
        catch { return null; }
    }

    /// <summary>Anfrage schicken und warten, bis der Freund sie annimmt. Rückgabe: null = drin, sonst Meldung.</summary>
    public static async Task<string?> RequestJoinAsync(Found f, Action<string> status, CancellationToken ct)
    {
        string id, secret;
        using (var req = new HttpRequestMessage(HttpMethod.Post, f.Url + "/api/join") { Content = new StringContent(JsonSerializer.Serialize(new { name = MyName }), Encoding.UTF8, "application/json") })
        using (var res = await _http.SendAsync(req, ct))
        {
            var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
            if (!res.IsSuccessStatusCode) return Err(j, (int)res.StatusCode);
            id = j.GetProperty("id").GetString()!; secret = j.GetProperty("secret").GetString()!;
        }
        status($"Anfrage geschickt – warte, bis {f.Owner} auf „Erlauben“ klickt …");
        var until = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < until)
        {
            await Task.Delay(3000, ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{f.Url}/api/join/{id}");
                req.Headers.Add("X-Join-Secret", secret);
                using var res = await _http.SendAsync(req, ct);
                var j = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
                if (res.StatusCode == HttpStatusCode.NotFound) return "Die Anfrage ist abgelaufen – schick einfach eine neue.";
                var st = j.GetProperty("status").GetString();
                if (st == "denied") return $"{f.Owner} hat die Anfrage abgelehnt.";
                if (st == "approved")
                {
                    S.FriendUrl = f.Url; S.FriendKey = j.GetProperty("key").GetString(); S.FriendName = f.Owner;
                    S.FriendPassword = null; S.FriendPublishes = true;
                    ShareServer.Save();
                    Reset();
                    return await ConnectAsync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { status($"Anfrage geschickt – {f.Owner} ist gerade kurz nicht erreichbar, warte weiter …"); }
        }
        return $"{f.Owner} hat nicht reagiert – versuch es später nochmal.";
    }

    // ---------- Dateiliste ----------
    public sealed record Listing(string Title, List<RemoteFile> Files, long Free, long MaxUpload);

    public static async Task<Listing> ListAsync()
    {
        var (code, json) = await SendAsync(HttpMethod.Get, "/api/files");
        if (code != 200) throw new HttpStatusError(code, Err(json, code));
        var files = JsonSerializer.Deserialize<List<RemoteFile>>(json.GetProperty("files").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        long free = json.GetProperty("limit").GetInt64() - json.GetProperty("used").GetInt64();
        var (_, me) = await SendAsync(HttpMethod.Get, "/api/me");
        var title = me.ValueKind == JsonValueKind.Object && me.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var myName = me.ValueKind == JsonValueKind.Object && me.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u.GetProperty("name").GetString() ?? MyName : MyName;
        foreach (var f in files) f.Mine = f.Uploader.Equals(myName, StringComparison.OrdinalIgnoreCase);
        return new Listing(title, files, free, json.GetProperty("maxUploadBytes").GetInt64());
    }

    // ---------- Download (fortsetzbar) ----------
    public static async Task<string> DownloadAsync(RemoteFile f, string folder, Action<long, long, string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"{f.Name}.{f.Id[..6]}.teil");
        int fails = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long have = File.Exists(temp) ? new FileInfo(temp).Length : 0;
            if (have == f.Size) break;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/download/{f.Id}");
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.StatusCode == HttpStatusCode.Unauthorized) { if (await LoginAsync() != null) throw new HttpStatusError(401, "Anmeldung abgelehnt"); continue; }
                if (res.StatusCode == HttpStatusCode.NotFound) throw new HttpStatusError(404, "Die Datei gibt es nicht mehr.");
                if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(temp); continue; }
                res.EnsureSuccessStatusCode();
                if (have > 0 && res.StatusCode == HttpStatusCode.OK) have = 0; // Server konnte nicht fortsetzen → von vorn
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(temp, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, true);
                var buf = new byte[1 << 20];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    have += n;
                    fails = 0;
                    progress(have, f.Size, "");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpStatusError) { throw; }
            catch (Exception)
            {
                fails++;
                await WaitRetry(fails, progress, have, f.Size, ct);
                if (fails % 3 == 0) await ConnectAsync(); // evtl. neue Tunnel-Adresse
            }
        }
        var final = UniquePath(folder, f.Name);
        File.Move(temp, final);
        return final;
    }

    private static async Task WaitRetry(int fails, Action<long, long, string> progress, long done, long total, CancellationToken ct)
    {
        int wait = Math.Min(30, 1 << Math.Min(fails, 5));
        for (int s = wait; s > 0; s--)
        {
            progress(done, total, $"Verbindung weg – neuer Versuch in {s} s …");
            await Task.Delay(1000, ct);
        }
    }

    public static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        for (int i = 2; ; i++) { path = Path.Combine(folder, $"{stem} ({i}){ext}"); if (!File.Exists(path)) return path; }
    }

    // ---------- Ordner ----------
    /// <summary>
    /// Ordner (mit Unterordnern) als zip packen – ohne Komprimierung, damit es schnell geht (Spiele/Videos lassen sich eh kaum packen).
    /// Gesperrte Dateien werden übersprungen.
    /// </summary>
    public static async Task ZipFolderAsync(string folder, string zipPath, Action<long, long> progress, CancellationToken ct)
    {
        var root = new DirectoryInfo(folder);
        var files = root.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                        .ToList();
        long total = files.Sum(f => f.Length), done = 0;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(zipPath))!);
        if (drive.AvailableFreeSpace < total + (512L << 20))
            throw new IOException($"Zum Packen fehlt Platz auf {drive.Name} – nötig: {ShareServer.Fmt(total)}, frei: {ShareServer.Fmt(drive.AvailableFreeSpace)}.");
        await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 22, true);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
        var buf = new byte[4 << 20];
        var emptyDirs = root.EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                            .Where(d => !d.EnumerateFileSystemInfos().Any());
        foreach (var d in emptyDirs) zip.CreateEntry(root.Name + "/" + Path.GetRelativePath(root.FullName, d.FullName).Replace('\\', '/') + "/");
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = root.Name + "/" + Path.GetRelativePath(root.FullName, f.FullName).Replace('\\', '/');
            try
            {
                await using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
                var entry = zip.CreateEntry(rel, CompressionLevel.NoCompression);
                entry.LastWriteTime = f.LastWriteTime;
                await using var es = entry.Open();
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await es.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress(done, total);
                }
            }
            catch (IOException) when (!ct.IsCancellationRequested) { done += f.Length; } // gesperrt – überspringen
            catch (UnauthorizedAccessException) { done += f.Length; }
        }
    }

    /// <summary>Ordner für den Upload packen – das zip bleibt im Temp-Ordner, bis der Upload fertig ist (so geht Fortsetzen).</summary>
    private static async Task<string> PrepareFolderAsync(string folder, Action<long, long, string> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ControlCenter-Upload");
        Directory.CreateDirectory(dir);
        var info = new DirectoryInfo(folder);
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(info.FullName.ToLowerInvariant())))[..8];
        var zip = Path.Combine(dir, $"{info.Name}.zip");
        var sub = Path.Combine(dir, tag);
        Directory.CreateDirectory(sub);
        zip = Path.Combine(sub, $"{info.Name}.zip");
        var ok = zip + ".fertig";
        if (File.Exists(zip) && File.Exists(ok)) return zip; // von einem abgebrochenen Upload – weiterverwenden
        File.Delete(ok);
        await ZipFolderAsync(folder, zip, (d, t) => progress(d, t, $"Packe Ordner … {(t > 0 ? d * 100.0 / t : 100):0} % ({ShareServer.Fmt(d)} / {ShareServer.Fmt(t)})"), ct);
        File.WriteAllText(ok, "");
        return zip;
    }

    // ---------- Upload in Teilen (fortsetzbar) ----------
    private static string ResumeKey(FileInfo fi) => $"{S.FriendUrl}|{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";

    public static async Task UploadAsync(string path, string category, string note, Action<long, long, string> progress, CancellationToken ct)
    {
        string? folderZip = null;
        if (Directory.Exists(path)) path = folderZip = await PrepareFolderAsync(path, progress, ct);
        var fi = new FileInfo(path);
        if (fi.Length == 0) throw new HttpStatusError(400, "Leere Dateien werden nicht hochgeladen.");
        var key = ResumeKey(fi);
        string? id = S.ResumeUploads.TryGetValue(key, out var saved) ? saved : null;
        long partSize = 0;
        var done = new HashSet<int>();

        if (id != null)
        {
            var (c, j) = await SendAsync(HttpMethod.Get, $"/api/upload/{id}", ct: ct);
            if (c == 200 && j.TryGetProperty("done", out var d) && d.GetBoolean()) { Forget(key); return; }
            if (c == 200 && j.GetProperty("size").GetInt64() == fi.Length)
            {
                partSize = j.GetProperty("partSize").GetInt64();
                foreach (var p in j.GetProperty("parts").EnumerateArray()) done.Add(p.GetInt32());
            }
            else id = null;
        }
        if (id == null)
        {
            var (c, j) = await SendAsync(HttpMethod.Post, "/api/upload/init", new { name = fi.Name, size = fi.Length, category, note }, ct: ct);
            if (c != 200) throw new HttpStatusError(c, Err(j, c));
            id = j.GetProperty("id").GetString()!;
            partSize = j.GetProperty("partSize").GetInt64();
            S.ResumeUploads[key] = id;
            ShareServer.Save();
        }

        int total = (int)((fi.Length + partSize - 1) / partSize);
        long PartLen(int n) => Math.Min(partSize, fi.Length - (n - 1) * partSize);
        var queue = new Queue<int>(Enumerable.Range(1, total).Where(n => !done.Contains(n)));
        var inFlight = new Dictionary<int, long>();
        var gate = new object();
        long Bytes() { lock (gate) return done.Sum(PartLen) + inFlight.Values.Sum(); }
        progress(Bytes(), fi.Length, "");

        async Task Worker()
        {
            while (true)
            {
                int n;
                lock (gate) { if (queue.Count == 0) return; n = queue.Dequeue(); inFlight[n] = 0; }
                int fails = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}/api/upload/{id}/part/{n}")
                        {
                            Content = new PartContent(path, (n - 1) * partSize, PartLen(n), sent => { lock (gate) inFlight[n] = sent; progress(Bytes(), fi.Length, ""); })
                        };
                        using var res = await _http.SendAsync(req, ct);
                        if (res.StatusCode == HttpStatusCode.Unauthorized) { await LoginAsync(); throw new IOException("neu angemeldet"); }
                        if (res.StatusCode == HttpStatusCode.NotFound) { Forget(key); throw new HttpStatusError(404, "Upload auf dem PC deines Freundes nicht mehr da – bitte neu starten."); }
                        if (res.StatusCode == HttpStatusCode.Forbidden) throw new HttpStatusError(403, "Nicht dein Upload.");
                        res.EnsureSuccessStatusCode();
                        lock (gate) { inFlight.Remove(n); done.Add(n); }
                        progress(Bytes(), fi.Length, "");
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (HttpStatusError) { throw; }
                    catch (Exception)
                    {
                        lock (gate) inFlight[n] = 0;
                        fails++;
                        await WaitRetry(fails, progress, Bytes(), fi.Length, ct);
                        if (fails % 3 == 0) await ConnectAsync();
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(Worker, ct)));
        progress(fi.Length, fi.Length, "Wird zusammengesetzt …");
        for (int i = 1; ; i++)
        {
            try
            {
                var (c, j) = await SendAsync(HttpMethod.Post, $"/api/upload/{id}/complete", new { }, ct: ct);
                if (c == 200) break;
                if (i >= 5) throw new HttpStatusError(c, Err(j, c));
            }
            catch (HttpStatusError) { throw; }
            catch (Exception) when (!ct.IsCancellationRequested && i < 5) { }
            await Task.Delay(2000 * i, ct);
        }
        Forget(key);
        if (folderZip != null)
        {
            try { File.Delete(folderZip + ".fertig"); File.Delete(folderZip); Directory.Delete(Path.GetDirectoryName(folderZip)!); } catch { }
        }
    }

    /// <summary>Eine eigene Datei beim Freund löschen.</summary>
    public static async Task DeleteAsync(RemoteFile f)
    {
        var (code, json) = await SendAsync(HttpMethod.Delete, "/api/files/" + f.Id);
        if (code != 200) throw new HttpStatusError(code, Err(json, code));
    }

    /// <summary>Alles löschen, was ich beim Freund hochgeladen habe. Rückgabe: Anzahl.</summary>
    public static async Task<int> DeleteMineAsync()
    {
        var (code, json) = await SendAsync(HttpMethod.Post, "/api/files/delete-mine", new { });
        if (code != 200) throw new HttpStatusError(code, Err(json, code));
        return json.GetProperty("deleted").GetInt32();
    }

    private static void Forget(string key) { if (S.ResumeUploads.Remove(key)) ShareServer.Save(); }

    /// <summary>Ein Teil der Datei als Upload-Inhalt – liest direkt von der Platte, ohne die 64 MB in den RAM zu holen.</summary>
    private sealed class PartContent : HttpContent
    {
        private readonly string _path; private readonly long _offset, _length; private readonly Action<long> _sent;
        public PartContent(string path, long offset, long length, Action<long> sent)
        {
            _path = path; _offset = offset; _length = length; _sent = sent;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Headers.ContentLength = length;
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
            fs.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[256 * 1024];
            long left = _length, sent = 0;
            while (left > 0)
            {
                int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)));
                if (n <= 0) throw new IOException("Datei wurde während des Uploads verändert");
                await stream.WriteAsync(buf.AsMemory(0, n));
                left -= n; sent += n;
                _sent(sent);
            }
        }
        protected override bool TryComputeLength(out long length) { length = _length; return true; }
    }
}
