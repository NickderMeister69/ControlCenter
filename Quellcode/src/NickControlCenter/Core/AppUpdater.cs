using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace NickControlCenter;

/// <summary>
/// Updates über GitHub-Releases: Nick veröffentlicht im Repo ein Release (Tag z. B. „v2.12“) und hängt die
/// ControlCenter.exe an. Die App sieht das, zeigt „Neue Version v2.12 verfügbar“ und lädt sie auf Klick herunter –
/// als ControlCenter.update.exe neben die exe, die sich dann wie gewohnt selbst einspielt.
/// </summary>
public sealed class AppUpdater : ObservableObject
{
    public const string Repo = "NickderMeister69/ControlCenter";
    public static AppUpdater Instance { get; } = new();

    private static readonly HttpClient Http = Create();
    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ControlCenter-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current
    {
        get
        {
            var v = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(1, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
    public static string CurrentText => Text(Current);
    public static string Text(Version v) => v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";

    /// <summary>„v2.12“, „2.12.1“, „Version 2.3“ → Version.</summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    private Version? _latest;
    public string LatestText => _latest == null ? "" : Text(_latest);
    public string Notes { get; private set; } = "";
    public long Size { get; private set; }
    private string? _url;
    private bool _available;
    public bool Available { get => _available; private set { Set(ref _available, value); RaiseAll(nameof(ButtonText), nameof(LatestText)); } }
    private bool _busy;
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(ButtonText)); } }
    private double _progress;
    public double Progress { get => _progress; private set { Set(ref _progress, value); Raise(nameof(ButtonText)); } }
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public DateTime? LastCheck { get; private set; }

    public string ButtonText => _busy ? $"Lädt {LatestText} … {_progress:0} %" : $"Neue Version {LatestText} verfügbar";

    private const string RawBase = "https://raw.githubusercontent.com/" + Repo + "/main/";

    /// <summary>
    /// Neueste Version suchen – an zwei Stellen, die höhere gewinnt:
    ///  1) GitHub-Release mit angehängter exe
    ///  2) direkt im Repo: version.txt + ControlCenter.exe (die App schreibt version.txt selbst neben die exe →
    ///     ein normales „git add / commit / push“ des App-Ordners veröffentlicht die neue Version)
    /// </summary>
    public async Task<bool> CheckAsync(bool silent = true)
    {
        Status = "Suche nach Updates …";
        Version? relV = null; string? relUrl = null; long relSize = 0; string relNotes = "";
        Version? repoV = null;
        string? error = null;
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            relV = Parse(r.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? Parse(r.TryGetProperty("name", out var n) ? n.GetString() : null);
            if (r.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    relUrl = a.GetProperty("browser_download_url").GetString();
                    relSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    if (name.Contains("ControlCenter", StringComparison.OrdinalIgnoreCase)) break;
                }
            relNotes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            if (relUrl == null) relV = null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        catch (Exception ex) { error = ex.Message; }
        try { repoV = Parse((await Http.GetStringAsync(RawBase + "version.txt")).Trim()); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        catch (Exception ex) { error ??= ex.Message; }

        LastCheck = DateTime.Now;
        Version? v; string? url; long size; string notes;
        if (repoV != null && (relV == null || repoV > relV)) { v = repoV; url = RawBase + "ControlCenter.exe"; size = 0; notes = ""; }
        else { v = relV; url = relUrl; size = relSize; notes = relNotes; }
        if (v == null || url == null)
        {
            Available = false;
            Status = error != null ? "Update-Suche fehlgeschlagen: " + error : "Keine Version auf GitHub gefunden (Repo öffentlich? version.txt bzw. Release vorhanden?)";
            if (!silent && error != null) Log.Warn(LogModule.App, Status);
            return false;
        }
        if (size <= 0) size = await InstallEstimate.SizeOfAsync(url);
        _latest = v; _url = url; Size = size; Notes = notes;
        bool newer = v > Current;
        var st = AppSettings.Current;
        // schon einmal geladen, App ist danach aber immer noch alt → die exe auf GitHub passt nicht zur Versionsnummer
        if (newer && st.UpdateAttemptTag == Text(v) && st.UpdateAttemptFrom == CurrentText)
        {
            Available = false;
            Status = $"Auf GitHub steht {Text(v)}, die exe dort ist aber noch {CurrentText} – richtige ControlCenter.exe hochladen";
            Log.Warn(LogModule.App, Status);
            return false;
        }
        Available = newer;
        Status = newer ? $"Neue Version {Text(v)} verfügbar ({Fmt.Bytes(size)})" : $"Du hast die neueste Version ({CurrentText})";
        if (newer) Log.Info(LogModule.App, $"Update verfügbar: {Text(v)} (installiert {CurrentText})");
        return newer;
    }

    /// <summary>version.txt neben die exe schreiben – liegt der App-Ordner in Git, wird sie mit committet.</summary>
    public static void WriteVersionFile()
    {
        try
        {
            var f = Path.Combine(Env.ExeDir, "version.txt");
            var txt = $"{Current.Major}.{Current.Minor}.{Current.Build}";
            if (!File.Exists(f) || File.ReadAllText(f).Trim() != txt) File.WriteAllText(f, txt);
        }
        catch { }
    }

    /// <summary>Neue Version laden und als ControlCenter.update.exe ablegen – die App spielt sie dann von selbst ein.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (_url == null || Busy) return false;
        Busy = true; Progress = 0;
        AppSettings.Current.UpdateAttemptTag = LatestText; AppSettings.Current.UpdateAttemptFrom = CurrentText; AppSettings.Current.Save();
        var target = SelfUpdate.UpdateFile;
        var part = target + ".part";
        try
        {
            var start = DateTime.Now;
            using (var resp = await Http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(part);
                var buf = new byte[1 << 16];
                long done = 0; int read; var last = DateTime.MinValue;
                while ((read = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read));
                    done += read;
                    if ((DateTime.Now - last).TotalMilliseconds > 200)
                    {
                        last = DateTime.Now;
                        if (total > 0) Progress = done * 100.0 / total;
                        double secs = (DateTime.Now - start).TotalSeconds;
                        Status = total > 0 && secs > 1 ? $"{Fmt.Bytes(done)} von {Fmt.Bytes(total)} · noch {InstallEstimate.DurationText(TimeSpan.FromSeconds((total - done) / Math.Max(1, done / secs)))}" : Fmt.Bytes(done);
                    }
                }
                if (total > 0 && done < total) throw new IOException("Download unvollständig");
                InstallEstimate.Report(done, DateTime.Now - start);
            }
            if (new FileInfo(part).Length < 1_000_000) throw new IOException("Datei zu klein – kein gültiges Programm");
            File.Move(part, target, true); // → SelfUpdate erkennt die Datei und startet neu
            Progress = 100;
            Status = $"{LatestText} geladen – wird eingespielt, die App startet gleich neu";
            Log.Ok(LogModule.App, $"Update {LatestText} heruntergeladen – wird eingespielt");
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            Status = "Download fehlgeschlagen: " + ex.Message;
            Log.Warn(LogModule.App, Status);
            Busy = false;
            return false;
        }
    }

    /// <summary>Beim Start nach kurzer Zeit und dann alle 3 Stunden prüfen.</summary>
    public void StartAutoCheck()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        t.Tick += async (_, _) =>
        {
            t.Interval = TimeSpan.FromHours(3);
            if (!AppSettings.Current.AutoUpdateCheck || Busy) return;
            if (await CheckAsync() && !Notified)
            {
                Notified = true;
                Ui.Toast($"Neue Version {LatestText} verfügbar – unten links auf den Knopf klicken");
            }
        };
        t.Start();
    }
    private bool Notified;
}
