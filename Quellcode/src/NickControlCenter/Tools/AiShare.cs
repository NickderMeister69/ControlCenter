using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NickControlCenter;

/// <summary>
/// Eigene KI (Ollama) für Freunde freigeben. Die App startet dafür einen kleinen Durchgang (Port 11435), der
///  • nur Anfragen mit dem geheimen Schlüssel aus dem Einladungs-Code annimmt,
///  • nur „Modelle anzeigen“ und „Frage stellen“ durchlässt (kein Löschen/Herunterladen von Modellen),
///  • per Firewall nur aus dem Heimnetz und aus Tailscale (100.64.0.0/10) erreichbar ist.
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

    public static string InviteCode(string ip)
    {
        var st = ArkWeb.Settings;
        var json = JsonSerializer.Serialize(new Invite(ShareName(), $"http://{ip}:{st.SharePort}", st.ShareToken ?? ""));
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
            return inv is { u.Length: > 0, t.Length: > 0 } && Uri.TryCreate(inv.u, UriKind.Absolute, out var uri) && uri.Scheme == "http" ? inv : null;
        }
        catch { return null; }
    }
}
