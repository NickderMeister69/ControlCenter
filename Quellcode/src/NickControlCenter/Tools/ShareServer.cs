using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NickControlCenter;

/// <summary>Einstellungen der Dateifreigabe (dateifreigabe.json).</summary>
public sealed class ShareSettings
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "Download Center";
    public string? Password { get; set; }
    public string? AdminPassword { get; set; }
    public int Port { get; set; } = 8080;
    public string StoragePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Download Center Dateien");
    public int MaxUploadGB { get; set; } = 100;
    /// <summary>Zusätzlich übers Internet per Cloudflare-Tunnel erreichbar (Freunde brauchen nichts installieren).</summary>
    public bool Internet { get; set; } = true;
    public string? SessionSecret { get; set; }
    /// <summary>Dein Name, so wie ihn deine Freunde sehen („Nicks Download Center“).</summary>
    public string? OwnerName { get; set; }
    /// <summary>Freunde, die du per Anfrage reingelassen hast.</summary>
    public List<ShareMember> Members { get; set; } = new();
    /// <summary>Eigener Zugangsschlüssel beim Freund (nach angenommener Anfrage).</summary>
    public string? FriendKey { get; set; }

    // ----- Download Center eines Freundes (Client) -----
    public string? FriendName { get; set; }
    public string? FriendUrl { get; set; }
    public string? FriendPassword { get; set; }
    /// <summary>Der Freund veröffentlicht seine wechselnde Tunnel-Adresse im GitHub-Repo.</summary>
    public bool FriendPublishes { get; set; }
    /// <summary>Name, mit dem man beim Freund angemeldet ist.</summary>
    public string? MyName { get; set; }
    public string DownloadFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    /// <summary>Angefangene Uploads (Datei → Upload-Id), damit sie nach einem Abbruch weiterlaufen.</summary>
    public Dictionary<string, string> ResumeUploads { get; set; } = new();
    /// <summary>Zuletzt offener Bereich der Seite: "host" oder "friend".</summary>
    public string Tab { get; set; } = "host";
    /// <summary>Links für den Browser anzeigen (für Freunde ohne Control Center).</summary>
    public bool ShowBrowserLinks { get; set; }
}

/// <summary>Ein Freund, dem du per Anfrage Zugang gegeben hast.</summary>
public sealed class ShareMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SHA-256 des Zugangsschlüssels (der Schlüssel selbst liegt nur beim Freund).</summary>
    public string KeyHash { get; set; } = "";
    public DateTime Added { get; set; }
    public DateTime LastSeen { get; set; }
    public string LastSeenText => LastSeen == default ? "noch nie da" : $"zuletzt da: {LastSeen:dd.MM.yyyy HH:mm}";
}

/// <summary>Eine offene Anfrage „darf ich mich verbinden?“ (nur im Speicher, 15 Minuten gültig).</summary>
public sealed class JoinRequest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Ip { get; init; } = "";
    public string SecretHash { get; init; } = "";
    public DateTime Created { get; init; } = DateTime.Now;
    public string Status { get; set; } = "pending";
    public string? IssuedKey { get; set; }
    public string Info => $"um {Created:HH:mm} Uhr";
}

/// <summary>Eine geteilte Datei – gleiches Format wie die eigenständige FileShareApp (index.json).</summary>
public sealed class ShareFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Category { get; set; } = "Sonstiges";
    public string Note { get; set; } = "";
    public string Uploader { get; set; } = "";
    public long UploadedAt { get; set; }
    public int Downloads { get; set; }
}

/// <summary>Ein laufender Upload in Teilen (tmp/uploads.json).</summary>
public sealed class ShareUpload
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long PartSize { get; set; }
    public string Category { get; set; } = "Sonstiges";
    public string Note { get; set; } = "";
    public string Uploader { get; set; } = "";
    public long Started { get; set; }
    public long Updated { get; set; }
    public List<int> Done { get; set; } = new();
    [JsonIgnore] public HashSet<int> Busy { get; } = new();
    [JsonIgnore] public int TotalParts => (int)((Size + PartSize - 1) / PartSize);
    public long PartLength(int n) => Math.Min(PartSize, Size - (n - 1) * PartSize);
}

/// <summary>
/// Dateifreigabe für Freunde („Download Center“): kleine Webseite auf diesem PC, auf der Freunde mit einem
/// gemeinsamen Passwort Dateien hoch- und herunterladen.
///  • Große Dateien (bis 100 GB) kommen in 64-MB-Teilen und werden direkt an die richtige Stelle der Datei geschrieben –
///    kaum RAM, und ein abgebrochener Upload macht dort weiter, wo er aufgehört hat.
///  • Downloads mit Range-Unterstützung (fortsetzbar).
///  • Erreichbar im Heimnetz, über Tailscale und – auf Wunsch – übers Internet per Cloudflare-Tunnel (https, keine Router-Einstellung).
///  • Gleiche Ordnerstruktur wie die eigenständige FileShareApp (files\, tmp\, index.json) – vorhandene Dateien werden übernommen.
/// </summary>
public static class ShareServer
{
    private const string RuleName = "ControlCenter Dateifreigabe";
    private const long PartSize = 64L * 1024 * 1024;
    private const int SessionDays = 30;
    private static readonly Regex IdRe = new("^[a-f0-9]{24}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static ShareSettings Settings { get; private set; } = JsonStore.Load<ShareSettings>("dateifreigabe.json");
    public static void Save() => JsonStore.Save("dateifreigabe.json", Settings);

    private static HttpListener? _listener;
    private static readonly object Gate = new();
    private static readonly object FileGate = new();
    private static Dictionary<string, ShareFile> _index = new();
    private static Dictionary<string, ShareUpload> _uploads = new();
    private static readonly ConcurrentDictionary<string, (int Count, DateTime LockedUntil)> Failed = new();
    private static string _pwVersion = "";
    private static System.Threading.Timer? _cleanupTimer;
    private static volatile bool _indexDirty;

    public static bool Running => _listener?.IsListening == true;
    public static string? LastError { get; private set; }
    public static string? PublicUrl { get; private set; }
    public static string TunnelState { get; private set; } = "";
    /// <summary>Status hat sich geändert (aus beliebigem Thread).</summary>
    public static event Action? Changed;
    /// <summary>Neuer Eintrag im Aktivitäts-Protokoll (aus beliebigem Thread).</summary>
    public static event Action<string>? Activity;
    private static readonly ConcurrentQueue<string> RecentActivity = new();
    public static IEnumerable<string> Recent => RecentActivity.ToArray();

    // Live-Werte für die Seite
    private static long _bytesSent, _bytesReceived;
    private static int _activeDownloads, _activeUploads;
    public static long BytesSent => Interlocked.Read(ref _bytesSent);
    public static long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public static int ActiveDownloads => _activeDownloads;
    public static int ActiveUploads => _activeUploads;
    /// <summary>Laufende Übertragungen auf der Seite (eigene Downloads/Uploads, auch beim Freund).</summary>
    public static int LocalTransfers;

    /// <summary>Gerade wird etwas übertragen – dann soll z. B. ein Update warten.</summary>
    public static bool Busy
    {
        get
        {
            if (_activeDownloads > 0 || _activeUploads > 0 || LocalTransfers > 0) return true;
            var now = NowMs();
            lock (Gate) return _uploads.Values.Any(u => now - u.Updated < 60_000); // Freund lädt gerade Teile hoch
        }
    }

    private static string FilesDir => Path.Combine(Settings.StoragePath, "files");
    private static string TmpDir => Path.Combine(Settings.StoragePath, "tmp");
    private static string IndexPath => Path.Combine(Settings.StoragePath, "index.json");
    private static string UploadsPath => Path.Combine(TmpDir, "uploads.json");
    private static string PartPath(string id) => Path.Combine(TmpDir, id + ".part");
    private static long MaxBytes => Math.Max(1, Settings.MaxUploadGB) * 1024L * 1024 * 1024;

    private static void Notify() => Changed?.Invoke();

    private static void Act(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        RecentActivity.Enqueue(line);
        while (RecentActivity.Count > 200) RecentActivity.TryDequeue(out _);
        Log.Info(LogModule.Share, text);
        Activity?.Invoke(line);
    }

    // ---------- Anfragen & Freunde mit Zugang ----------
    private static readonly ConcurrentDictionary<string, JoinRequest> Requests = new();
    /// <summary>Ein Freund möchte sich verbinden (aus beliebigem Thread).</summary>
    public static event Action<JoinRequest>? JoinRequested;
    public static string OwnerName => string.IsNullOrWhiteSpace(Settings.OwnerName) ? Environment.UserName : Settings.OwnerName!.Trim();

    public static List<JoinRequest> PendingRequests()
    {
        foreach (var r in Requests.Values.Where(r => DateTime.Now - r.Created > TimeSpan.FromMinutes(15)).ToList()) Requests.TryRemove(r.Id, out _);
        return Requests.Values.Where(r => r.Status == "pending").OrderBy(r => r.Created).ToList();
    }

    public static void Approve(string requestId)
    {
        if (!Requests.TryGetValue(requestId, out var r) || r.Status != "pending") return;
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        lock (Gate)
        {
            Settings.Members.RemoveAll(m => m.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase));
            Settings.Members.Add(new ShareMember
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                Name = r.Name, KeyHash = Hash(key), Added = DateTime.Now
            });
        }
        Save();
        r.IssuedKey = key;
        r.Status = "approved";
        Act($"{r.Name} hat jetzt Zugang (Anfrage angenommen)");
        Notify();
    }

    public static void Deny(string requestId)
    {
        if (!Requests.TryGetValue(requestId, out var r) || r.Status != "pending") return;
        r.Status = "denied";
        Act($"Anfrage von {r.Name} abgelehnt");
        Notify();
    }

    public static void RemoveMember(string memberId)
    {
        ShareMember? m;
        lock (Gate) { m = Settings.Members.FirstOrDefault(x => x.Id == memberId); if (m != null) Settings.Members.Remove(m); }
        if (m == null) return;
        Save();
        Act($"Zugang von {m.Name} entfernt");
        Notify();
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Was an den Einstellungen fehlt (leer = alles gut).</summary>
    public static List<string> Problems(ShareSettings s)
    {
        var p = new List<string>();
        if (string.IsNullOrEmpty(s.Password) || s.Password.Length < 8) p.Add("Passwort für Freunde fehlt (mind. 8 Zeichen)");
        if (string.IsNullOrEmpty(s.AdminPassword) || s.AdminPassword.Length < 8) p.Add("Admin-Passwort fehlt (mind. 8 Zeichen)");
        if (!string.IsNullOrEmpty(s.Password) && s.Password == s.AdminPassword) p.Add("Die beiden Passwörter müssen unterschiedlich sein");
        if (s.Port is < 1024 or > 65535) p.Add("Port muss zwischen 1024 und 65535 liegen");
        if (string.IsNullOrWhiteSpace(s.StoragePath)) p.Add("Kein Speicherordner gewählt");
        return p;
    }

    public static void StartIfEnabled() { if (Settings.Enabled) Start(); }

    public static bool Start()
    {
        if (Running) return true;
        var st = Settings;
        var problems = Problems(st);
        if (problems.Count > 0) { LastError = string.Join(" · ", problems); Notify(); return false; }
        if (string.IsNullOrEmpty(st.SessionSecret)) { st.SessionSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); Save(); }
        _pwVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(st.Password + "|" + st.AdminPassword)))[..12].ToLowerInvariant();
        try
        {
            RemoveOldApp();
            Directory.CreateDirectory(FilesDir);
            Directory.CreateDirectory(TmpDir);
            LoadData();
            _loadedFrom = st.StoragePath;
            CleanupUploads();

            var l = new HttpListener();
            l.Prefixes.Add($"http://+:{st.Port}/");
            // große Teile über langsame Leitungen nicht abbrechen
            if (OperatingSystem.IsWindows())
            {
                l.TimeoutManager.EntityBody = TimeSpan.FromMinutes(30);
                l.TimeoutManager.DrainEntityBody = TimeSpan.FromMinutes(2);
                l.TimeoutManager.IdleConnection = TimeSpan.FromMinutes(5);
                l.TimeoutManager.HeaderWait = TimeSpan.FromMinutes(1);
            }
            l.Start();
            _listener = l;
            LastError = null;
            if (OperatingSystem.IsWindows())
            {
                Env.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{RuleName}\"");
                Env.Run("netsh.exe", $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={st.Port} remoteip=LocalSubnet,100.64.0.0/10 profile=any");
            }
            _ = Task.Run(() => Loop(l));
            _cleanupTimer ??= new System.Threading.Timer(_ =>
            {
                try { CleanupUploads(); if (_indexDirty) { _indexDirty = false; SaveIndex(); } } catch { }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            Act($"Dateifreigabe gestartet (Port {st.Port}, Speicher: {st.StoragePath})");
            if (st.Internet) _ = StartTunnelAsync();
            Notify();
            return true;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode is 183 or 32)
        {
            LastError = $"Port {st.Port} ist schon von einem anderen Programm belegt. Nimm in den Einstellungen einen anderen Port (z. B. 8090).";
        }
        catch (Exception ex) { LastError = ex.Message; }
        Log.Warn(LogModule.Share, "Dateifreigabe konnte nicht starten: " + LastError);
        Notify();
        return false;
    }

    /// <summary>
    /// Die alte eigenständige FileShareApp („Download Center“, Electron) beenden, aus dem Autostart nehmen und ihre
    /// portable exe löschen – sie würde sonst denselben Port belegen. Andere Programme werden nicht angefasst:
    /// „Download Center.exe“ zählt nur, wenn daneben resources\app.asar liegt (also genau diese Electron-App).
    /// </summary>
    public static void RemoveOldApp()
    {
        if (!OperatingSystem.IsWindows()) return;
        var notes = new List<string>();
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in new[] { "DownloadCenter-Portable", "Download Center" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var file = p.MainModule?.FileName ?? "";
                        bool ours = name == "DownloadCenter-Portable" ||
                                    File.Exists(Path.Combine(Path.GetDirectoryName(file) ?? "", "resources", "app.asar"));
                        if (!ours) continue;
                        if (name == "DownloadCenter-Portable" && file.Length > 0) exes.Add(file);
                        p.Kill(true);
                        p.WaitForExit(5000);
                        notes.Add("beendet");
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (run != null)
                    foreach (var v in run.GetValueNames())
                    {
                        var data = run.GetValue(v) as string ?? "";
                        if (!data.Contains("DownloadCenter-Portable.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var m = Regex.Match(data, "\"([^\"]+\\.exe)\"");
                        if (m.Success) exes.Add(m.Groups[1].Value);
                        run.DeleteValue(v, false);
                        notes.Add("aus dem Autostart genommen");
                    }
            }

            if (exes.Count > 0) Thread.Sleep(1500); // Windows gibt die Datei kurz nach dem Beenden frei
            foreach (var exe in exes)
            {
                if (!Path.GetFileName(exe).Equals("DownloadCenter-Portable.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) continue;
                for (int i = 0; i < 5; i++)
                {
                    try { File.Delete(exe); notes.Add("gelöscht: " + exe); break; }
                    catch { Thread.Sleep(1000); }
                }
            }
        }
        catch (Exception ex) { Log.Warn(LogModule.Share, "Alte Download-Center-App: " + ex.Message); }
        if (notes.Count > 0) Act("Alte Download-Center-App " + string.Join(", ", notes.Distinct()));
    }

    public static void Stop()
    {
        var wasRunning = Running;
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        StopTunnel();
        _cleanupTimer?.Dispose(); _cleanupTimer = null;
        if (_indexDirty) { _indexDirty = false; SaveIndex(); }
        SaveUploads();
        if (OperatingSystem.IsWindows()) Env.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{RuleName}\"");
        if (wasRunning) Act("Dateifreigabe beendet");
        Notify();
    }

    public static void Restart() { Stop(); Start(); }

    // ---------- Daten ----------
    private static void LoadData()
    {
        lock (Gate)
        {
            _index = ReadJson<Dictionary<string, ShareFile>>(IndexPath) ?? new();
            _uploads = ReadJson<Dictionary<string, ShareUpload>>(UploadsPath) ?? new();
            foreach (var id in _index.Keys.ToList())
                if (!File.Exists(Path.Combine(FilesDir, id))) _index.Remove(id);
        }
    }

    private static T? ReadJson<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (Exception ex)
        {
            Log.Warn(LogModule.Share, $"{Path.GetFileName(path)} beschädigt: {ex.Message}");
            try { File.Copy(path, path + ".defekt", true); } catch { }
            return default;
        }
    }

    private static void WriteJsonFile(string path, string json)
    {
        lock (FileGate)
        {
            try
            {
                File.WriteAllText(path + ".tmp", json);
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) { Log.Warn(LogModule.Share, $"Speichern von {Path.GetFileName(path)} fehlgeschlagen: {ex.Message}"); }
        }
    }

    private static void SaveIndex()
    {
        string json; lock (Gate) json = JsonSerializer.Serialize(_index, Json);
        WriteJsonFile(IndexPath, json);
    }

    private static void SaveUploads()
    {
        if (!Directory.Exists(TmpDir)) return;
        string json; lock (Gate) json = JsonSerializer.Serialize(_uploads, Json);
        WriteJsonFile(UploadsPath, json);
    }

    /// <summary>Unfertige Uploads nach 3 Tagen wegräumen, verwaiste Teildateien löschen.</summary>
    private static void CleanupUploads()
    {
        bool changed = false;
        var now = NowMs();
        lock (Gate)
        {
            foreach (var (id, u) in _uploads.ToList())
            {
                if (u.Busy.Count > 0) continue;
                if (now - Math.Max(u.Updated, u.Started) > 3 * 86_400_000L || !File.Exists(PartPath(id)))
                {
                    _uploads.Remove(id);
                    TryDelete(PartPath(id));
                    changed = true;
                }
            }
            if (Directory.Exists(TmpDir))
                foreach (var f in Directory.GetFiles(TmpDir, "*.part"))
                    if (!_uploads.ContainsKey(Path.GetFileNameWithoutExtension(f))) { TryDelete(f); changed = true; }
        }
        if (changed) SaveUploads();
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public sealed record Stats(int Files, long Used, int Uploads, long Free, long Total);

    public static Stats GetStats()
    {
        EnsureLoaded();
        int files, ups; long used;
        lock (Gate) { files = _index.Count; used = _index.Values.Sum(f => f.Size); ups = _uploads.Count; }
        long free = 0, total = 0;
        try { var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Settings.StoragePath))!); free = d.AvailableFreeSpace; total = d.TotalSize; } catch { }
        return new Stats(files, used, ups, free, total);
    }

    // ---------- HTTP ----------
    private static async Task Loop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private sealed record User(string Name, bool Admin);

    private static async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request; var res = ctx.Response;
        try
        {
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["X-Frame-Options"] = "DENY";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";

            var path = req.Url?.AbsolutePath ?? "/";
            var m = req.HttpMethod;
            var parts = path.Trim('/').Split('/');

            if (!path.StartsWith("/api/")) { await ServeStatic(res, path); return; }

            if (m == "POST" && path == "/api/login") { await Login(req, res); return; }
            if (m == "POST" && path == "/api/join") { await JoinCreate(req, res); return; }
            if (m == "GET" && parts.Length == 3 && parts[1] == "join") { await JoinStatus(req, res, parts[2]); return; }
            if (m == "POST" && path == "/api/logout") { SetCookie(req, res, "", 0); await Reply(res, 200, new { ok = true }); return; }
            var user = GetUser(req);
            if (m == "GET" && path == "/api/me") { await Reply(res, 200, new { title = Settings.Title, owner = OwnerName, user = user == null ? null : new { name = user.Name, admin = user.Admin } }); return; }
            if (user == null) { await Reply(res, 401, new { error = "Nicht angemeldet" }); return; }

            if (m == "GET" && path == "/api/files") { await ListFiles(res); return; }
            if ((m == "GET" || m == "HEAD") && parts.Length == 3 && parts[1] == "download") { await Download(req, res, user, parts[2]); return; }
            if (m == "DELETE" && parts.Length == 3 && parts[1] == "files") { await DeleteFile(res, user, parts[2]); return; }
            if (m == "POST" && path == "/api/files/delete-mine") { await DeleteMine(res, user); return; }
            if (m == "POST" && path == "/api/upload/init") { await UploadInit(req, res, user); return; }
            if (parts.Length >= 3 && parts[1] == "upload")
            {
                var id = parts[2];
                if (m == "GET" && parts.Length == 3) { await UploadStatus(res, user, id); return; }
                if (m == "POST" && parts.Length == 4 && parts[3] == "urls") { await UploadUrls(req, res, user, id); return; }
                if (m == "PUT" && parts.Length == 5 && parts[3] == "part") { await UploadPart(req, res, user, id, parts[4]); return; }
                if (m == "POST" && parts.Length == 4 && parts[3] == "complete") { await UploadComplete(res, user, id); return; }
                if (m == "DELETE" && parts.Length == 3) { await UploadCancel(res, user, id); return; }
            }
            await Reply(res, 404, new { error = "Nicht gefunden" });
        }
        catch (HttpListenerException) { try { res.Abort(); } catch { } }
        catch (IOException) { try { res.Abort(); } catch { } }
        catch (Exception ex)
        {
            Log.Warn(LogModule.Share, "Fehler: " + ex.Message);
            try { await Reply(res, 500, new { error = "Serverfehler, bitte nochmal versuchen" }); } catch { try { res.Abort(); } catch { } }
        }
    }

    private static async Task Reply(HttpListenerResponse res, int code, object body)
    {
        var b = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        res.StatusCode = code;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
        res.Close();
    }

    private static async Task<JsonElement> ReadBody(HttpListenerRequest req)
    {
        if (req.ContentLength64 > 50 * 1024) throw new InvalidDataException("Anfrage zu groß");
        using var ms = new MemoryStream();
        await req.InputStream.CopyToAsync(ms);
        if (ms.Length == 0) return default;
        try { return JsonDocument.Parse(ms.ToArray()).RootElement; } catch { return default; }
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;

    private static async Task ServeStatic(HttpListenerResponse res, string path)
    {
        var name = path is "/" or "/index.html" ? "index.html" : path.TrimStart('/');
        var type = Path.GetExtension(name) switch
        {
            ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8", ".js" => "text/javascript; charset=utf-8", _ => null
        };
        using var s = type == null || name.Contains('/') ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream("ShareWeb." + name);
        if (s == null) { res.StatusCode = 404; res.Close(); return; }
        res.ContentType = type;
        res.ContentLength64 = s.Length;
        res.Headers["Cache-Control"] = "no-cache";
        await s.CopyToAsync(res.OutputStream);
        res.Close();
    }

    // ---------- Login ----------
    private static string ClientIp(HttpListenerRequest req)
    {
        var ip = req.RemoteEndPoint?.Address;
        // über den Cloudflare-Tunnel kommt alles von 127.0.0.1 – dann zählt die echte Adresse aus dem Header
        if (ip != null && IPAddress.IsLoopback(ip) && req.Headers["CF-Connecting-IP"] is { Length: > 0 } cf) return cf;
        return ip?.ToString() ?? "?";
    }

    private static bool IsHttps(HttpListenerRequest req) =>
        string.Equals(req.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase) ||
        (req.Headers["Cf-Visitor"] ?? "").Contains("https");

    private static void SetCookie(HttpListenerRequest req, HttpListenerResponse res, string value, int maxAge) =>
        res.Headers.Add("Set-Cookie", $"dc_session={value}; HttpOnly; SameSite=Strict; Path=/; Max-Age={maxAge}{(IsHttps(req) ? "; Secure" : "")}");

    private static bool SafeEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
    private static string Sign(string data) => B64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Settings.SessionSecret!), Encoding.UTF8.GetBytes(data)));

    private static string MakeToken(string name, bool admin, string member = "")
    {
        var payload = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { n = name, a = admin, e = NowMs() + SessionDays * 86_400_000L, v = _pwVersion, m = member }));
        return payload + "." + Sign(payload);
    }

    private static User? GetUser(HttpListenerRequest req)
    {
        var cookie = req.Headers["Cookie"] ?? "";
        string? token = null;
        foreach (var part in cookie.Split(';'))
        {
            var i = part.IndexOf('=');
            if (i > 0 && part[..i].Trim() == "dc_session") token = Uri.UnescapeDataString(part[(i + 1)..].Trim());
        }
        if (string.IsNullOrEmpty(token) || !token.Contains('.')) return null;
        var sp = token.Split('.');
        if (sp.Length != 2) return null;
        var expected = Sign(sp[0]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sp[1]), Encoding.ASCII.GetBytes(expected))) return null;
        try
        {
            var p = JsonDocument.Parse(FromB64Url(sp[0])).RootElement;
            if (p.GetProperty("e").GetInt64() < NowMs() || p.GetProperty("v").GetString() != _pwVersion) return null;
            // per Anfrage reingelassen: nur solange der Freund noch in der Liste steht
            if (p.TryGetProperty("m", out var mem) && mem.GetString() is { Length: > 0 } mid)
                lock (Gate) if (!Settings.Members.Any(x => x.Id == mid)) return null;
            return new User(p.GetProperty("n").GetString() ?? "?", p.GetProperty("a").GetBoolean());
        }
        catch { return null; }
    }

    private static string CleanText(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
        var t = sb.ToString().Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static string CleanFilename(string raw)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/').Split('/').Last());
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length == 0 || name == "..") name = "datei";
        if (name.Length > 200) { var ext = Path.GetExtension(name); ext = ext.Length > 20 ? "" : ext; name = name[..(200 - ext.Length)] + ext; }
        return name;
    }

    private static async Task Login(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ip = ClientIp(req);
        if (Failed.TryGetValue(ip, out var f) && f.LockedUntil > DateTime.Now)
        {
            await Reply(res, 429, new { error = $"Zu viele Fehlversuche. Warte {Math.Ceiling((f.LockedUntil - DateTime.Now).TotalMinutes)} Min." });
            return;
        }
        var body = await ReadBody(req);
        var pw = Str(body, "password");
        var name = CleanText(Str(body, "name"), 30);
        var key = Str(body, "key");
        if (key.Length > 0)
        {
            ShareMember? member;
            var h = Hash(key);
            lock (Gate) member = Settings.Members.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.KeyHash), Encoding.ASCII.GetBytes(h)));
            if (member == null)
            {
                Failed[ip] = (f.Count + 1, f.Count + 1 >= 5 ? DateTime.Now.AddMinutes(10) : DateTime.MinValue);
                await Reply(res, 401, new { error = "Kein Zugang mehr – bitte neu anfragen", revoked = true });
                return;
            }
            Failed.TryRemove(ip, out _);
            bool first = DateTime.Now - member.LastSeen > TimeSpan.FromHours(1);
            member.LastSeen = DateTime.Now; Save();
            SetCookie(req, res, MakeToken(member.Name, false, member.Id), SessionDays * 86400);
            if (first) Act($"{member.Name} ist da");
            await Reply(res, 200, new { name = member.Name, admin = false });
            return;
        }
        if (name.Length == 0) { await Reply(res, 400, new { error = "Bitte gib deinen Namen ein" }); return; }
        bool admin = false;
        if (SafeEqual(pw, Settings.AdminPassword ?? "")) admin = true;
        else if (!SafeEqual(pw, Settings.Password ?? ""))
        {
            var count = f.Count + 1;
            if (count >= 5) { Failed[ip] = (0, DateTime.Now.AddMinutes(10)); Act($"Login für {ip} 10 Minuten gesperrt (5 falsche Passwörter)"); }
            else Failed[ip] = (count, DateTime.MinValue);
            await Reply(res, 401, new { error = "Falsches Passwort" });
            return;
        }
        Failed.TryRemove(ip, out _);
        SetCookie(req, res, MakeToken(name, admin), SessionDays * 86400);
        Act($"{name} hat sich angemeldet{(admin ? " (Admin)" : "")}");
        await Reply(res, 200, new { name, admin });
    }

    private static async Task JoinCreate(HttpListenerRequest req, HttpListenerResponse res)
    {
        var ip = ClientIp(req);
        var body = await ReadBody(req);
        var name = CleanText(Str(body, "name"), 30);
        if (name.Length == 0) { await Reply(res, 400, new { error = "Name fehlt" }); return; }
        var pending = PendingRequests();
        if (pending.Count >= 20 || pending.Count(r => r.Ip == ip) >= 3) { await Reply(res, 429, new { error = "Zu viele offene Anfragen – kurz warten." }); return; }
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var r = new JoinRequest { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), Name = name, Ip = ip, SecretHash = Hash(secret) };
        Requests[r.Id] = r;
        Act($"{name} möchte sich verbinden – Anfrage wartet auf dich");
        JoinRequested?.Invoke(r);
        Notify();
        await Reply(res, 200, new { id = r.Id, secret });
    }

    private static async Task JoinStatus(HttpListenerRequest req, HttpListenerResponse res, string id)
    {
        var secret = req.Headers["X-Join-Secret"] ?? "";
        if (!Requests.TryGetValue(id, out var r) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(r.SecretHash), Encoding.ASCII.GetBytes(Hash(secret))))
        { await Reply(res, 404, new { error = "Anfrage abgelaufen" }); return; }
        if (r.Status == "approved")
        {
            Requests.TryRemove(id, out _); // Schlüssel nur einmal herausgeben
            await Reply(res, 200, new { status = "approved", key = r.IssuedKey, owner = OwnerName });
            return;
        }
        if (r.Status == "denied") Requests.TryRemove(id, out _);
        await Reply(res, 200, new { status = r.Status });
    }

    // ---------- Dateien ----------
    private static async Task ListFiles(HttpListenerResponse res)
    {
        List<object> files;
        long used;
        lock (Gate)
        {
            files = _index.OrderByDescending(kv => kv.Value.UploadedAt)
                .Select(kv => (object)new { id = kv.Key, name = kv.Value.Name, size = kv.Value.Size, category = kv.Value.Category, note = kv.Value.Note, uploader = kv.Value.Uploader, uploadedAt = kv.Value.UploadedAt, downloads = kv.Value.Downloads })
                .ToList();
            used = _index.Values.Sum(x => x.Size);
        }
        var st = GetStats();
        await Reply(res, 200, new { files, used, limit = used + st.Free, maxUploadBytes = MaxBytes });
    }

    private static async Task Download(HttpListenerRequest req, HttpListenerResponse res, User user, string id)
    {
        ShareFile? meta;
        lock (Gate) _index.TryGetValue(id, out meta);
        var file = Path.Combine(FilesDir, id);
        if (!IdRe.IsMatch(id) || meta == null || !File.Exists(file)) { res.StatusCode = 404; res.Close(); return; }

        long len = new FileInfo(file).Length, start = 0, end = len - 1;
        bool partial = false;
        var range = req.Headers["Range"];
        if (!string.IsNullOrEmpty(range))
        {
            var rm = Regex.Match(range, @"^bytes=(\d*)-(\d*)$");
            if (rm.Success && (rm.Groups[1].Length > 0 || rm.Groups[2].Length > 0))
            {
                if (rm.Groups[1].Length == 0) start = Math.Max(0, len - long.Parse(rm.Groups[2].Value));
                else
                {
                    start = long.Parse(rm.Groups[1].Value);
                    if (rm.Groups[2].Length > 0) end = Math.Min(long.Parse(rm.Groups[2].Value), len - 1);
                }
                if (start >= len || start > end)
                {
                    res.StatusCode = 416; res.Headers["Content-Range"] = $"bytes */{len}"; res.Close(); return;
                }
                partial = true;
            }
        }

        var ascii = Regex.Replace(meta.Name, @"[^\x20-\x7e]|[""\\]", "_");
        res.StatusCode = partial ? 206 : 200;
        res.ContentType = "application/octet-stream";
        res.Headers["Accept-Ranges"] = "bytes";
        res.Headers["Content-Disposition"] = $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(meta.Name)}";
        if (partial) res.Headers["Content-Range"] = $"bytes {start}-{end}/{len}";
        res.SendChunked = false;
        res.ContentLength64 = end - start + 1;
        if (req.HttpMethod == "HEAD") { res.Close(); return; }

        bool fresh = start == 0;
        if (fresh) Act($"{user.Name} lädt herunter: {meta.Name} ({Fmt(meta.Size)})");
        Interlocked.Increment(ref _activeDownloads);
        Notify();
        try
        {
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[1 << 20];
            long left = end - start + 1;
            while (left > 0)
            {
                int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)));
                if (n <= 0) break;
                await res.OutputStream.WriteAsync(buf.AsMemory(0, n));
                Interlocked.Add(ref _bytesSent, n);
                left -= n;
            }
            res.Close();
            if (fresh && left == 0)
            {
                lock (Gate) if (_index.TryGetValue(id, out var m2)) m2.Downloads++;
                _indexDirty = true;
            }
        }
        finally { Interlocked.Decrement(ref _activeDownloads); Notify(); }
    }

    private static async Task DeleteFile(HttpListenerResponse res, User user, string id)
    {
        ShareFile? meta;
        bool allowed = true;
        lock (Gate)
        {
            if (_index.TryGetValue(id, out meta))
            {
                // Admin darf alles, Freunde nur ihre eigenen Uploads
                allowed = user.Admin || meta.Uploader.Equals(user.Name, StringComparison.OrdinalIgnoreCase);
                if (allowed) _index.Remove(id);
            }
        }
        if (meta == null) { await Reply(res, 404, new { error = "Nicht gefunden" }); return; }
        if (!allowed) { await Reply(res, 403, new { error = "Du kannst nur deine eigenen Dateien löschen" }); return; }
        SaveIndex();
        TryDelete(Path.Combine(FilesDir, id));
        Act($"{user.Name} hat gelöscht: {meta.Name}");
        await Reply(res, 200, new { ok = true });
        Notify();
    }

    /// <summary>Freund: alles löschen, was er selbst hochgeladen hat (Admin: alles).</summary>
    private static async Task DeleteMine(HttpListenerResponse res, User user)
    {
        List<string> ids;
        lock (Gate)
        {
            ids = _index.Where(kv => user.Admin || kv.Value.Uploader.Equals(user.Name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var id in ids) _index.Remove(id);
        }
        if (ids.Count > 0)
        {
            SaveIndex();
            foreach (var id in ids) TryDelete(Path.Combine(FilesDir, id));
            Act($"{user.Name} hat {(user.Admin ? "alle" : "seine")} {ids.Count} Datei(en) gelöscht");
            Notify();
        }
        await Reply(res, 200, new { deleted = ids.Count });
    }

    /// <summary>Alle geteilten Dateien löschen (Knopf „Alles löschen“ am PC).</summary>
    public static int DeleteAllLocal()
    {
        EnsureLoaded();
        List<string> ids;
        lock (Gate) { ids = _index.Keys.ToList(); _index.Clear(); }
        SaveIndex();
        foreach (var id in ids) TryDelete(Path.Combine(FilesDir, id));
        if (ids.Count > 0) Act($"Alle {ids.Count} Datei(en) gelöscht (am PC)");
        Notify();
        return ids.Count;
    }

    /// <summary>Datei vom PC aus löschen (Seite im Control Center).</summary>
    public static void DeleteLocal(string id)
    {
        ShareFile? meta;
        lock (Gate) { if (_index.TryGetValue(id, out meta)) _index.Remove(id); }
        if (meta == null) return;
        SaveIndex();
        TryDelete(Path.Combine(FilesDir, id));
        Act($"Gelöscht (am PC): {meta.Name}");
        Notify();
    }

    /// <summary>Datei vom PC aus in den File Share kopieren (mit Fortschritt, abbrechbar).</summary>
    public static async Task AddLocalAsync(string path, string category, Action<long, long> progress, CancellationToken ct)
    {
        EnsureLoaded();
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(TmpDir);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var tmp = Path.Combine(TmpDir, id + ".local");
        bool isDir = Directory.Exists(path);
        var name = isDir ? new DirectoryInfo(path).Name + ".zip" : Path.GetFileName(path);
        try
        {
            if (isDir) await ShareClient.ZipFolderAsync(path, tmp, progress, ct);
            else await CopyAsync(path, tmp, new FileInfo(path).Length, progress, ct);
            File.Move(tmp, Path.Combine(FilesDir, id));
        }
        catch { TryDelete(tmp); throw; }
        var size = new FileInfo(Path.Combine(FilesDir, id)).Length;
        lock (Gate)
            _index[id] = new ShareFile { Name = CleanFilename(name), Size = size, Category = string.IsNullOrWhiteSpace(category) ? "Sonstiges" : CleanText(category, 40), Uploader = OwnerName, UploadedAt = NowMs() };
        SaveIndex();
        Act($"Hinzugefügt (am PC): {name} ({Fmt(size)})");
        Notify();
    }

    /// <summary>Datei aus dem File Share in einen Ordner speichern.</summary>
    public static async Task<string> ExportAsync(string id, string folder, Action<long, long> progress, CancellationToken ct)
    {
        ShareFile? meta;
        lock (Gate) _index.TryGetValue(id, out meta);
        var src = Path.Combine(FilesDir, id);
        if (meta == null || !File.Exists(src)) throw new FileNotFoundException("Die Datei gibt es nicht mehr.");
        Directory.CreateDirectory(folder);
        var dest = ShareClient.UniquePath(folder, meta.Name);
        var tmp = dest + ".teil";
        try { await CopyAsync(src, tmp, meta.Size, progress, ct); File.Move(tmp, dest); }
        catch { TryDelete(tmp); throw; }
        return dest;
    }

    private static async Task CopyAsync(string from, string to, long total, Action<long, long> progress, CancellationToken ct)
    {
        await using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 22, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22, FileOptions.Asynchronous);
        var buf = new byte[4 << 20];
        long done = 0; int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            progress(done, total);
        }
    }

    public sealed record FileRow(string Id, string Name, long Size, string Category, string Uploader, DateTime Uploaded, int Downloads)
    {
        public string SizeText => Fmt(Size);
        public string Info => $"{Fmt(Size)} · {Category} · von {Uploader} · {Uploaded:dd.MM.yyyy HH:mm} · {Downloads}× geladen";
    }

    private static string? _loadedFrom;

    /// <summary>Liste auch anzeigen, wenn die Freigabe gerade aus ist.</summary>
    private static void EnsureLoaded()
    {
        if (Running || _loadedFrom == Settings.StoragePath) return;
        _loadedFrom = Settings.StoragePath;
        try { if (File.Exists(IndexPath) || Directory.Exists(TmpDir)) LoadData(); else lock (Gate) { _index = new(); _uploads = new(); } } catch { }
    }

    public static List<FileRow> Files()
    {
        EnsureLoaded();
        lock (Gate)
            return _index.OrderByDescending(kv => kv.Value.UploadedAt)
                .Select(kv => new FileRow(kv.Key, kv.Value.Name, kv.Value.Size, kv.Value.Category, kv.Value.Uploader,
                    DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.UploadedAt).LocalDateTime, kv.Value.Downloads)).ToList();
    }

    // ---------- Upload in Teilen ----------
    private static (ShareUpload? U, int Code, string? Err) Own(User user, string id)
    {
        lock (Gate)
        {
            if (!IdRe.IsMatch(id) || !_uploads.TryGetValue(id, out var u)) return (null, 404, "Upload nicht gefunden");
            if (u.Uploader != user.Name && !user.Admin) return (null, 403, "Nicht dein Upload");
            return (u, 200, null);
        }
    }

    private static async Task UploadInit(HttpListenerRequest req, HttpListenerResponse res, User user)
    {
        var b = await ReadBody(req);
        long size = Num(b, "size");
        if (size <= 0) { await Reply(res, 400, new { error = "Leere oder ungültige Datei" }); return; }
        if (size > MaxBytes) { await Reply(res, 400, new { error = $"Datei zu groß (max. {Settings.MaxUploadGB} GB)" }); return; }
        var st = GetStats();
        long reserved;
        lock (Gate) reserved = _uploads.Values.Sum(u => (long)(u.TotalParts - u.Done.Count) * u.PartSize);
        if (st.Total > 0 && size + reserved + (1L << 30) > st.Free) { await Reply(res, 507, new { error = "Nicht genug Speicherplatz auf dem Server" }); return; }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var partSize = Math.Max(PartSize, (long)Math.Ceiling(size / 9000.0 / (1024 * 1024)) * 1024 * 1024);
        var up = new ShareUpload
        {
            Name = CleanFilename(Str(b, "name")), Size = size, PartSize = partSize,
            Category = CleanText(Str(b, "category"), 40) is { Length: > 0 } c ? c : "Sonstiges",
            Note = CleanText(Str(b, "note"), 300), Uploader = user.Name, Started = NowMs(), Updated = NowMs()
        };
        await File.WriteAllBytesAsync(PartPath(id), Array.Empty<byte>());
        lock (Gate) _uploads[id] = up;
        SaveUploads();
        Act($"{user.Name} lädt hoch: {up.Name} ({Fmt(size)})");
        Notify();
        await Reply(res, 200, new { id, partSize, parts = Array.Empty<int>() });
    }

    private static async Task UploadStatus(HttpListenerResponse res, User user, string id)
    {
        bool done; lock (Gate) done = _index.ContainsKey(id);
        if (IdRe.IsMatch(id) && done) { await Reply(res, 200, new { done = true }); return; }
        var (u, code, err) = Own(user, id);
        if (u == null) { await Reply(res, code, new { error = err }); return; }
        int[] parts; lock (Gate) parts = u.Done.ToArray();
        await Reply(res, 200, new { size = u.Size, partSize = u.PartSize, parts });
    }

    private static async Task UploadUrls(HttpListenerRequest req, HttpListenerResponse res, User user, string id)
    {
        var (u, code, err) = Own(user, id);
        if (u == null) { await Reply(res, code, new { error = err }); return; }
        var b = await ReadBody(req);
        var urls = new Dictionary<string, string>();
        if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("parts", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray().Take(50))
                if (e.TryGetInt32(out var n) && n >= 1 && n <= u.TotalParts) urls[n.ToString()] = $"/api/upload/{id}/part/{n}";
        await Reply(res, 200, new { urls });
    }

    private static async Task UploadPart(HttpListenerRequest req, HttpListenerResponse res, User user, string id, string nText)
    {
        var (u, code, err) = Own(user, id);
        if (u == null) { await Reply(res, code, new { error = err }); return; }
        if (!int.TryParse(nText, out var n) || n < 1 || n > u.TotalParts) { await Reply(res, 400, new { error = "Ungültiger Teil" }); return; }
        long expected = u.PartLength(n);
        if (req.ContentLength64 != expected) { await Reply(res, 400, new { error = "Falsche Teilgröße" }); return; }
        bool added;
        lock (Gate) added = u.Busy.Add(n);
        if (!added) { await Reply(res, 409, new { error = "Teil wird schon hochgeladen" }); return; }

        Interlocked.Increment(ref _activeUploads);
        long got = 0;
        try
        {
            // direkt an die richtige Stelle der Datei schreiben – kein Zwischenspeicher im RAM
            await using (var fs = new FileStream(PartPath(id), FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 20, FileOptions.Asynchronous))
            {
                fs.Seek((n - 1) * u.PartSize, SeekOrigin.Begin);
                var buf = new byte[1 << 20];
                while (got < expected)
                {
                    int r = await req.InputStream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, expected - got)));
                    if (r <= 0) break;
                    await fs.WriteAsync(buf.AsMemory(0, r));
                    got += r;
                    Interlocked.Add(ref _bytesReceived, r);
                }
            }
        }
        catch (Exception) { got = -1; }
        finally
        {
            lock (Gate) u.Busy.Remove(n);
            Interlocked.Decrement(ref _activeUploads);
        }
        if (got != expected) { try { await Reply(res, 409, new { error = "Teil unvollständig, wird wiederholt" }); } catch { } return; }
        lock (Gate) { if (!u.Done.Contains(n)) u.Done.Add(n); u.Updated = NowMs(); }
        SaveUploads();
        await Reply(res, 200, new { ok = true });
    }

    private static async Task UploadComplete(HttpListenerResponse res, User user, string id)
    {
        var (u, code, err) = Own(user, id);
        if (u == null) { await Reply(res, code, new { error = err }); return; }
        int[] doneParts;
        bool ready;
        lock (Gate) { ready = u.Done.Count == u.TotalParts && u.Busy.Count == 0; doneParts = u.Done.ToArray(); }
        if (!ready || new FileInfo(PartPath(id)).Length != u.Size) { await Reply(res, 409, new { error = "Upload unvollständig", parts = doneParts }); return; }
        File.Move(PartPath(id), Path.Combine(FilesDir, id), true);
        lock (Gate)
        {
            _index[id] = new ShareFile { Name = u.Name, Size = u.Size, Category = u.Category, Note = u.Note, Uploader = u.Uploader, UploadedAt = NowMs() };
            _uploads.Remove(id);
        }
        SaveIndex(); SaveUploads();
        Act($"Upload fertig: {u.Name} (von {u.Uploader})");
        Notify();
        await Reply(res, 200, new { done = true });
    }

    private static async Task UploadCancel(HttpListenerResponse res, User user, string id)
    {
        var (u, code, err) = Own(user, id);
        if (u == null) { await Reply(res, code == 404 ? 200 : code, code == 404 ? new { ok = true } : (object)new { error = err }); return; }
        lock (Gate) _uploads.Remove(id);
        TryDelete(PartPath(id));
        SaveUploads();
        await Reply(res, 200, new { ok = true });
    }

    public static string Fmt(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + u[i];
    }

    // ---------- Adressen ----------
    public static List<(string Kind, string Url)> Links()
    {
        var list = new List<(string, string)>();
        if (PublicUrl != null) list.Add(("Internet (Cloudflare)", PublicUrl));
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var a in ni.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var b = a.Address.GetAddressBytes();
                var url = $"http://{a.Address}:{Settings.Port}";
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) list.Add(("Tailscale", url));
                else if (b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31) list.Add(("Heimnetz", url));
                else if (b[0] != 169 && b[0] != 127) list.Add(("Netzwerk", url));
            }
        list.Add(("Auf diesem PC", $"http://localhost:{Settings.Port}"));
        return list.Distinct().ToList();
    }

    // ---------- Cloudflare-Tunnel (wie bei der KI-Freigabe) ----------
    private static Process? _tunnel;
    private static string CloudflaredExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NickControlCenter", "cloudflared.exe");
    private static void SetTunnel(string state) { TunnelState = state; Notify(); }

    private static async Task StartTunnelAsync()
    {
        if (_tunnel is { HasExited: false } || !OperatingSystem.IsWindows()) return;
        try
        {
            if (!File.Exists(CloudflaredExe))
            {
                SetTunnel("Lade Cloudflare-Tunnel (einmalig, ca. 60 MB) …");
                Directory.CreateDirectory(Path.GetDirectoryName(CloudflaredExe)!);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var bytes = await http.GetByteArrayAsync("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe");
                await File.WriteAllBytesAsync(CloudflaredExe + ".part", bytes);
                File.Move(CloudflaredExe + ".part", CloudflaredExe, true);
            }
            if (!Running || !Settings.Internet) return;
            SetTunnel("Starte Tunnel …");
            var psi = new ProcessStartInfo(CloudflaredExe, $"tunnel --no-autoupdate --url http://127.0.0.1:{Settings.Port}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void OnLine(string? line)
            {
                if (line == null) return;
                var m = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com");
                if (m.Success && m.Value != PublicUrl)
                {
                    PublicUrl = m.Value; Act("Übers Internet erreichbar: " + m.Value); SetTunnel("Übers Internet erreichbar");
                    _ = Task.Run(() => PublishAddress(m.Value));
                }
            }
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.Exited += (_, _) =>
            {
                PublicUrl = null;
                if (Running && Settings.Internet && _tunnel == p)
                {
                    SetTunnel("Tunnel unterbrochen – starte neu …");
                    _tunnel = null;
                    Task.Delay(15000).ContinueWith(_ => StartTunnelAsync());
                }
            };
            p.Start(); p.BeginErrorReadLine(); p.BeginOutputReadLine();
            _tunnel = p;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopTunnel();
        }
        catch (Exception ex)
        {
            SetTunnel("Tunnel-Fehler: " + ex.Message);
            Log.Warn(LogModule.Share, "Cloudflare-Tunnel: " + ex.Message);
        }
    }

    // ---------- Einladungs-Code & veröffentlichte Adresse ----------
    public const string AddressFile = "datei-adresse.txt";
    private const string Repo = "NickderMeister69/ControlCenter";
    private static readonly SemaphoreSlim GitGate = new(1);

    /// <summary>Liegt die App im git-Ordner des Repos (beim Besitzer)? Dann wird die Tunnel-Adresse veröffentlicht.</summary>
    public static bool CanPublish => Directory.Exists(Path.Combine(AppContext.BaseDirectory.TrimEnd('\\', '/'), ".git"));

    /// <summary>Code für Freunde mit Control Center: Name, Adresse und Passwort – einmal eingeben, dann dauerhaft verbunden.</summary>
    public static string InviteCode()
    {
        var links = Links();
        var url = PublicUrl ?? links.FirstOrDefault(l => l.Kind == "Tailscale").Url ?? links.FirstOrDefault(l => l.Kind == "Heimnetz").Url
                  ?? links.FirstOrDefault(l => l.Kind == "Netzwerk").Url ?? $"http://localhost:{Settings.Port}";
        var name = string.IsNullOrWhiteSpace(Settings.Title) || Settings.Title == "Download Center" ? Environment.UserName : Settings.Title;
        var json = JsonSerializer.Serialize(new ShareClient.Invite(name, url, Settings.Password ?? "", PublicUrl != null && CanPublish));
        return "CCDC-" + B64Url(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Neue Tunnel-Adresse ins Repo schreiben und pushen, damit die Apps der Freunde sie finden.</summary>
    private static async Task PublishAddress(string url)
    {
        if (!CanPublish) return;
        var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        await GitGate.WaitAsync();
        try
        {
            File.WriteAllText(Path.Combine(dir, AddressFile), url + "\n");
            for (int i = 0; i < 4; i++)
            {
                string G(string args) => Env.Run("git.exe", $"-c safe.directory=* -C \"{dir}\" {args}", 60_000).Output;
                G($"add {AddressFile}");
                G($"commit -m \"Datei-Adresse aktualisiert\" -- {AddressFile}");
                var (code, outp) = Env.Run("git.exe", $"-c safe.directory=* -C \"{dir}\" push", 90_000);
                if (code == 0) { Act("Adresse für die Apps deiner Freunde veröffentlicht"); return; }
                if (i == 3) { Act("Adresse konnte nicht gepusht werden – einmal „git push“ im Repo machen"); Log.Warn(LogModule.Share, "push: " + outp); }
                await Task.Delay(8000); // evtl. pusht gerade die KI-Freigabe
            }
        }
        catch (Exception ex) { Log.Warn(LogModule.Share, "Adresse veröffentlichen: " + ex.Message); }
        finally { GitGate.Release(); }
    }

    /// <summary>Aktuelle Tunnel-Adresse aus dem Repo (für die Apps der Freunde).</summary>
    public static async Task<string?> PublishedAddressAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ControlCenter");
        foreach (var (url, accept) in new[] { ($"https://api.github.com/repos/{Repo}/contents/{AddressFile}", "application/vnd.github.raw"), ($"https://raw.githubusercontent.com/{Repo}/main/{AddressFile}", "text/plain") })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd(accept);
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;
                var t = (await resp.Content.ReadAsStringAsync()).Trim();
                if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == "https") return t;
            }
            catch { }
        }
        return null;
    }

    private static void StopTunnel()
    {
        var p = _tunnel; _tunnel = null; PublicUrl = null;
        try { if (p is { HasExited: false }) p.Kill(); } catch { }
        TunnelState = "";
    }
}
