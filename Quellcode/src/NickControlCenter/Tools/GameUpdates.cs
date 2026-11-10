using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace NickControlCenter;

/// <summary>Ein Steam-Spiel in „Spiele aktualisieren“ – mit Live-Fortschritt wie in Steam.</summary>
public sealed class SteamGameVM : ObservableObject
{
    public SteamGameState S { get; private set; }
    private long _lastBytes = -1;
    private DateTime _lastTime;
    private double _rate; // Bytes/s, geglättet
    /// <summary>Wann der Nutzer „Aktualisieren“ gedrückt hat (für „Steam hat nicht angefangen“).</summary>
    public DateTime? RequestedAt { get; set; }

    public SteamGameVM(SteamGameState s) { S = s; }

    public void Update(SteamGameState s)
    {
        var now = DateTime.Now;
        if (s.Updating && s.BytesDownloaded > 0)
        {
            if (_lastBytes >= 0 && s.BytesDownloaded > _lastBytes && (now - _lastTime).TotalSeconds > 0.5)
            {
                double r = (s.BytesDownloaded - _lastBytes) / (now - _lastTime).TotalSeconds;
                _rate = _rate <= 0 ? r : _rate * 0.6 + r * 0.4;
            }
            if (s.BytesDownloaded != _lastBytes) { _lastBytes = s.BytesDownloaded; _lastTime = now; }
            // Steam schreibt den Stand nur alle paar Sekunden – solange nichts Neues kommt, die Netzwerk-Rate nehmen
            if ((now - _lastTime).TotalSeconds > 6 || _rate <= 0)
            {
                var net = Services.Hub.Latest.NetDownBps;
                if (net > 50_000) _rate = _rate <= 0 ? net : _rate * 0.7 + net * 0.3;
            }
            RequestedAt = null;
        }
        else { _lastBytes = -1; _rate = 0; }
        S = s;
        RaiseAll(nameof(Name), nameof(StateText), nameof(StateBrush), nameof(Progress), nameof(ShowProgress), nameof(Info), nameof(NeedsUpdate),
                 nameof(ButtonText), nameof(ShowButton), nameof(ShowSteamButton), nameof(SpeedText));
    }

    public string Name => S.Name;
    public bool NeedsUpdate => S.NeedsUpdate;
    public bool ShowProgress => S.Updating || S.Paused;
    public double Progress => S.Progress;
    public string SpeedText => _rate > 0 ? $"{_rate * 8 / 1e6:0} Mbit/s" : "";
    /// <summary>Hat Steam nach dem Start nicht von selbst angefangen? Dann hilft nur das Spiel in Steam zu öffnen.</summary>
    public bool NotStarted => RequestedAt is { } t && (DateTime.Now - t).TotalSeconds > 90 && !S.Updating;

    public string StateText
    {
        get
        {
            if (S.Updating)
            {
                var parts = new List<string> { $"Wird aktualisiert – {S.Progress:0} %" };
                if (S.BytesToDownload > 0) parts.Add($"{Fmt.Bytes(S.BytesDownloaded)} von {Fmt.Bytes(S.BytesToDownload)}");
                if (_rate > 0) parts.Add($"{_rate * 8 / 1e6:0} Mbit/s");
                if (_rate > 0 && S.Remaining > 0) parts.Add("noch " + InstallEstimate.DurationText(TimeSpan.FromSeconds(S.Remaining / _rate)));
                else if (S.BytesToDownload == 0) parts.Add("wird vorbereitet …");
                return string.Join(" · ", parts);
            }
            if (S.Paused) return $"Update pausiert – {S.Progress:0} % · noch {Fmt.Bytes(S.Remaining)}";
            if (RequestedAt != null) return NotStarted
                ? "Steam hat das Update nicht von selbst gestartet (Spiel steht wohl auf „nur beim Start aktualisieren“) – „In Steam öffnen“ drücken"
                : "Steam startet im Hintergrund und prüft das Update …";
            if (S.OnlineOnly) return "Update verfügbar (neue Version – die Größe kennt Steam erst beim Laden)";
            if (S.NeedsUpdate) return S.Remaining > 0 ? $"Update verfügbar · {InstallEstimate.Text(S.Remaining, 10 + S.Remaining / 1e6 * 0.05)}" : "Update verfügbar";
            return "✔ Aktuell";
        }
    }
    public System.Windows.Media.Brush StateBrush => S.Updating ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentBrush") : S.NeedsUpdate ? Palette.Warn : Palette.Good;
    public string ButtonText => "Aktualisieren";
    public bool ShowButton => S.NeedsUpdate && !S.Updating && RequestedAt == null;
    public bool ShowSteamButton => S.Paused || NotStarted;
    public string Info => $"{(S.SizeOnDisk > 0 ? Fmt.Bytes(S.SizeOnDisk) + " auf der Platte" : "")}{(S.LastUpdated is { } d ? $" · zuletzt aktualisiert {d:dd.MM.yyyy}" : "")}";
    public void Tick() => RaiseAll(nameof(StateText), nameof(ShowButton), nameof(ShowSteamButton));
}

/// <summary>
/// Steam-Spiele: Update-Zustand aus den Steam-Dateien lesen. Steam lässt Updates nur von sich selbst starten –
/// deshalb öffnet „Aktualisieren“ das Spiel bzw. die Downloads in Steam, wo das Update sofort loslegt.
/// </summary>
public static class GameUpdates
{
    public static List<string> Libraries()
    {
        var roots = new List<string>();
        try { if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string sp) roots.Add(sp.Replace('/', '\\')); } catch { }
        try { if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") is string ip) roots.Add(ip); } catch { }
        return SteamLibrary.FindLibraries(roots);
    }

    public static List<SteamGameState> Scan() => SteamUpdates.Scan(Libraries());

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<int, (long Build, DateTime At)> _latest = new();

    /// <summary>
    /// Neueste Version jedes Spiels im Internet nachschauen (öffentliche Steam-App-Infos über api.steamcmd.net) –
    /// so sieht die App Updates auch, wenn Steam gerade nicht läuft. Ergebnis 30 min zwischengespeichert.
    /// </summary>
    public static async Task<List<SteamGameState>> CheckOnlineAsync(List<SteamGameState> games)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(games.Where(g => g.BuildId > 0).Select(async g =>
        {
            lock (_latest) if (_latest.TryGetValue(g.AppId, out var c) && (DateTime.Now - c.At).TotalMinutes < 30) return;
            await gate.WaitAsync();
            try
            {
                var json = await Http.GetStringAsync($"https://api.steamcmd.net/v1/info/{g.AppId}");
                var b = SteamUpdates.ParsePublicBuild(json);
                if (b > 0) lock (_latest) _latest[g.AppId] = (b, DateTime.Now);
            }
            catch { }
            finally { gate.Release(); }
        }));
        var res = new List<SteamGameState>();
        foreach (var g in games)
        {
            long latest; lock (_latest) latest = _latest.TryGetValue(g.AppId, out var c) ? c.Build : 0;
            bool newer = latest > 0 && g.BuildId > 0 && latest != g.BuildId && !g.NeedsUpdate;
            res.Add(newer ? g with { NeedsUpdate = true, LatestBuild = latest, OnlineOnly = true } : g with { LatestBuild = latest });
        }
        int found = res.Count(r => r.OnlineOnly);
        if (found > 0) Log.Info(LogModule.Boost, $"Spiele-Updates (online geprüft): {string.Join(", ", res.Where(r => r.OnlineOnly).Select(r => r.Name))}");
        return res;
    }

    public static bool SteamRunning => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Spielseite in Steam öffnen – dort steht bei ausstehenden Updates der „Aktualisieren“-Knopf.</summary>
    public static void OpenGame(int appId) => Env.Open($"steam://nav/games/details/{appId}");

    /// <summary>Steam-Downloads öffnen (startet Steam, falls es nicht läuft) – ausstehende Updates starten dort.</summary>
    public static void OpenDownloads() => Env.Open("steam://open/downloads");

    /// <summary>
    /// Steam unsichtbar starten (nur Tray, kein Fenster). Steam lädt ausstehende Updates dann von selbst –
    /// den Fortschritt liest die App aus den Steam-Dateien. Läuft Steam schon, passiert nichts.
    /// </summary>
    public static bool StartSteamSilent()
    {
        if (SteamRunning) return true;
        string? exe = null;
        try { if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamExe") is string se) exe = se.Replace('/', '\\'); } catch { }
        if (exe == null || !File.Exists(exe))
            foreach (var lib in Libraries()) { var f = Path.Combine(lib, "steam.exe"); if (File.Exists(f)) { exe = f; break; } }
        if (exe == null) return false;
        Env.OpenAsUser(exe, "-silent"); // als normaler Benutzer, nicht mit den Admin-Rechten der App
        Log.Info(LogModule.Boost, "Steam im Hintergrund gestartet (für Spiele-Updates)");
        return true;
    }
}
