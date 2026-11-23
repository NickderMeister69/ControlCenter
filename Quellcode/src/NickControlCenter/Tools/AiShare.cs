using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace NickControlCenter;

/// <summary>
/// Eigene KI (Ollama) für Freunde freigeben. Die App startet dafür einen kleinen Durchgang (Port 11435), der
///  • nur Anfragen mit dem geheimen Schlüssel aus dem Einladungs-Code annimmt,
///  • nur „Modelle anzeigen“ und „Frage stellen“ durchlässt (kein Löschen/Herunterladen von Modellen),
///  • per Firewall nur aus dem Heimnetz und aus Tailscale (100.64.0.0/10) erreichbar ist,
///  • übers Internet nur über einen Cloudflare-Tunnel (https, keine Router-Einstellung, eigene IP bleibt verborgen).
/// Die Tunnel-Adresse ändert sich bei jedem Start – sie wird als „ki-adresse.txt“ ins GitHub-Repo gepusht (nur wenn die
/// App in einem git-Ordner liegt, also beim Besitzer). Die Apps der Freunde lesen sie dort und verbinden sich automatisch.
/// Ollama selbst bleibt nur auf diesem PC erreichbar.
/// </summary>
public static class AiShare
{
    public const string TokenHeader = "X-CC-Token";
    private const string RuleName = "ControlCenter KI-Freigabe";
    private static HttpListener? _listener;
    private static readonly HttpClient Ollama = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim Gate = new(2);
    public static int Requests { get; private set; }
    public static string? LastError { get; private set; }
    public static bool Running => _listener?.IsListening == true;

    public const string AddressFile = "ki-adresse.txt";
    private const string Repo = "NickderMeister69/ControlCenter";
    private static Process? _tunnel;
    public static string? PublicUrl { get; private set; }
    public static string TunnelState { get; private set; } = "";
    public static event Action? StateChanged;
    private static void Notify(string state) { TunnelState = state; StateChanged?.Invoke(); }

    public static void StartIfEnabled() { if (ArkWeb.Settings.ShareEnabled) Start(); }

    public static bool Start()
    {
        var st = ArkWeb.Settings;
        if (string.IsNullOrEmpty(st.ShareToken)) { st.ShareToken = NewToken(); ArkWeb.Save(); }
        if (Running) return true;
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://+:{st.SharePort}/");
            l.Start();
            _listener = l;
            LastError = null;
            Env.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{RuleName}\"");
            Env.Run("netsh.exe", $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={st.SharePort} remoteip=LocalSubnet,100.64.0.0/10 profile=any");
            _ = Task.Run(() => Loop(l));
            Log.Info(LogModule.App, $"KI-Freigabe gestartet (Port {st.SharePort})");
            _ = StartTunnelAsync();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn(LogModule.App, "KI-Freigabe konnte nicht starten: " + ex.Message);
            return false;
        }
    }

    public static void Stop()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        StopTunnel();
        Env.Run("netsh.exe", $"advfirewall firewall delete rule name=\"{RuleName}\"");
        Log.Info(LogModule.App, "KI-Freigabe beendet");
    }

    /// <summary>Neuen Schlüssel erzeugen – alte Einladungs-Codes gelten dann nicht mehr.</summary>
    public static void NewKey() { ArkWeb.Settings.ShareToken = NewToken(); ArkWeb.Save(); }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static async Task Loop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private static async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request; var res = ctx.Response;
        try
        {
            var token = req.Headers[TokenHeader] ?? "";
            var ok = !string.IsNullOrEmpty(ArkWeb.Settings.ShareToken) &&
                     CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(ArkWeb.Settings.ShareToken!));
            if (!ok) { res.StatusCode = 401; res.Close(); return; }
            var path = req.Url?.AbsolutePath ?? "";
            var allowed = (req.HttpMethod == "GET" && path == "/api/tags") || (req.HttpMethod == "POST" && path == "/api/chat");
            if (!allowed) { res.StatusCode = 403; res.Close(); return; }
            if (req.ContentLength64 > 512 * 1024) { res.StatusCode = 413; res.Close(); return; }

            if (!await Gate.WaitAsync(TimeSpan.FromSeconds(60))) { res.StatusCode = 503; res.Close(); return; }
            try
            {
                using var fwd = new HttpRequestMessage(new HttpMethod(req.HttpMethod), ArkAi.LocalOllama + path);
                if (req.HttpMethod == "POST")
                {
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    fwd.Content = new ByteArrayContent(ms.ToArray());
                    fwd.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    Requests++;
                }
                using var resp = await Ollama.SendAsync(fwd, HttpCompletionOption.ResponseHeadersRead);
                res.StatusCode = (int)resp.StatusCode;
                res.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
                res.SendChunked = true;
                await using var src = await resp.Content.ReadAsStreamAsync();
                var buf = new byte[8192]; int n;
                while ((n = await src.ReadAsync(buf)) > 0) { await res.OutputStream.WriteAsync(buf.AsMemory(0, n)); await res.OutputStream.FlushAsync(); }
            }
            finally { Gate.Release(); }
            res.Close();
        }
        catch (Exception ex)
        {
            try { res.StatusCode = 502; res.Close(); } catch { }
            LastError = ex.Message;
        }
    }

    // ---------- Cloudflare-Tunnel ----------
    private static string CloudflaredExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NickControlCenter", "cloudflared.exe");

    private static async Task StartTunnelAsync()
    {
        if (_tunnel is { HasExited: false }) return;
        try
        {
            if (!File.Exists(CloudflaredExe))
            {
                Notify("Lade Cloudflare-Tunnel (einmalig, ca. 60 MB) …");
                Directory.CreateDirectory(Path.GetDirectoryName(CloudflaredExe)!);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var bytes = await http.GetByteArrayAsync("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe");
                await File.WriteAllBytesAsync(CloudflaredExe + ".part", bytes);
                File.Move(CloudflaredExe + ".part", CloudflaredExe, true);
            }
            Notify("Starte Tunnel …");
            var psi = new ProcessStartInfo(CloudflaredExe, $"tunnel --no-autoupdate --url http://127.0.0.1:{ArkWeb.Settings.SharePort}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void OnLine(string? line)
            {
                if (line == null) return;
                var m = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com");
                if (m.Success && m.Value != PublicUrl) { PublicUrl = m.Value; Notify("Übers Internet erreichbar"); _ = Task.Run(() => PublishAddress(m.Value)); }
            }
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.Exited += (_, _) =>
            {
                PublicUrl = null;
                if (Running && ArkWeb.Settings.ShareEnabled) { Notify("Tunnel unterbrochen – starte neu …"); Task.Delay(15000).ContinueWith(_ => StartTunnelAsync()); }
            };
            p.Start(); p.BeginErrorReadLine(); p.BeginOutputReadLine();
            _tunnel = p;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopTunnel();
        }
        catch (Exception ex)
        {
            Notify("Tunnel-Fehler: " + ex.Message);
            Log.Warn(LogModule.App, "Cloudflare-Tunnel: " + ex.Message);
        }
    }

    private static void StopTunnel()
    {
        var p = _tunnel; _tunnel = null; PublicUrl = null;
        try { if (p is { HasExited: false }) p.Kill(); } catch { }
        Notify("");
    }

    /// <summary>Neue Adresse ins Repo schreiben und pushen – nur beim Besitzer (App liegt im git-Ordner).</summary>
    private static void PublishAddress(string url)
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        if (!Directory.Exists(Path.Combine(dir, ".git"))) { Notify("Übers Internet erreichbar (Adresse nicht veröffentlicht – App liegt nicht im Repo-Ordner)"); return; }
        try
        {
            File.WriteAllText(Path.Combine(dir, AddressFile), url + "\n");
            string G(string args) => Env.Run("git.exe", $"-c safe.directory=* -C \"{dir}\" {args}", 60_000).Output;
            G($"add {AddressFile}");
            G($"commit -m \"KI-Adresse aktualisiert\" -- {AddressFile}");
            var (code, outp) = Env.Run("git.exe", $"-c safe.directory=* -C \"{dir}\" push", 90_000);
            if (code == 0) { Notify("Übers Internet erreichbar – Adresse veröffentlicht"); Log.Info(LogModule.App, "KI-Adresse veröffentlicht"); }
            else { Notify("Adresse konnte nicht gepusht werden – in GitHub Desktop/Terminal einmal „git push“"); Log.Warn(LogModule.App, "KI-Adresse push: " + outp); }
        }
        catch (Exception ex) { Notify("Adresse veröffentlichen fehlgeschlagen: " + ex.Message); }
    }

    // ---------- Freunde: aktuelle Adresse holen ----------
    private static string? _published; private static DateTime _publishedAt;

    /// <summary>Aktuelle Tunnel-Adresse aus dem Repo (für die Apps der Freunde).</summary>
    public static async Task<string?> PublishedAddressAsync(bool force = false)
    {
        if (!force && _published != null && DateTime.Now - _publishedAt < TimeSpan.FromMinutes(3)) return _published;
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
                if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == "https") { _published = t; _publishedAt = DateTime.Now; return t; }
            }
            catch { }
        }
        return _published;
    }

    // ---------- Adressen & Einladungs-Code ----------
    /// <summary>Mögliche Adressen dieses PCs: Tailscale (übers Internet, verschlüsselt) und Heimnetz.</summary>
    public static List<(string Kind, string Ip)> Addresses()
    {
        var list = new List<(string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var a in ni.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var b = a.Address.GetAddressBytes();
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) list.Add(("Tailscale", a.Address.ToString()));
                else if (b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31) list.Add(("Heimnetz", a.Address.ToString()));
            }
        return list.OrderBy(x => x.Item1 == "Tailscale" ? 0 : 1).Distinct().ToList();
    }

    public static string InviteCode()
    {
        var st = ArkWeb.Settings;
        var lan = Addresses().FirstOrDefault().Ip;
        var url = PublicUrl ?? (lan != null ? $"http://{lan}:{st.SharePort}" : "");
        var json = JsonSerializer.Serialize(new Invite(ShareName(), url, st.ShareToken ?? ""));
        return "CCKI-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string ShareName() => string.IsNullOrWhiteSpace(ArkWeb.Settings.ShareName) ? Environment.UserName : ArkWeb.Settings.ShareName!.Trim();

    public sealed record Invite(string n, string u, string t);

    public static Invite? ParseInvite(string code)
    {
        try
        {
            code = code.Trim();
            if (!code.StartsWith("CCKI-")) return null;
            var b = code[5..].Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var inv = JsonSerializer.Deserialize<Invite>(Encoding.UTF8.GetString(Convert.FromBase64String(b)));
            return inv is { t.Length: > 0 } ? inv : null;
        }
        catch { return null; }
    }
}
