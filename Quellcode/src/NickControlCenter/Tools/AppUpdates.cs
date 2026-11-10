using System.Diagnostics;
using System.Text;
using System.IO;

namespace NickControlCenter;

/// <summary>Ein Programm mit verfügbarem Update (aus winget).</summary>
public sealed class AppUpdate : ObservableObject
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Version { get; init; } = "";
    public string Available { get; init; } = "";
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    private bool _busy;
    public bool Busy { get => _busy; set { Set(ref _busy, value); RaiseAll(nameof(NotBusy), nameof(CanUpdate), nameof(ButtonText)); } }
    public bool NotBusy => !_busy;
    private bool _done;
    /// <summary>Erfolgreich aktualisiert → ausgegraut, Knopf aus.</summary>
    public bool Done { get => _done; set { Set(ref _done, value); RaiseAll(nameof(CanUpdate), nameof(ButtonText), nameof(RowOpacity)); } }
    public bool CanUpdate => !_busy && !_done;
    public string ButtonText => _done ? "✔ Aktuell" : _busy ? "Läuft …" : "Aktualisieren";
    public double RowOpacity => _done ? 0.45 : 1;
    private long _bytes = -1;
    /// <summary>Größe des Installers (-1 = wird ermittelt, 0 = unbekannt).</summary>
    public long Bytes { get => _bytes; set { Set(ref _bytes, value); Raise(nameof(SizeText)); } }
    public string SizeText => _bytes < 0 ? "Größe wird ermittelt …" : InstallEstimate.Text(_bytes, InstallEstimate.AppInstallSeconds(_bytes));
    public void RefreshEstimate() => Raise(nameof(SizeText));
}

/// <summary>
/// Programme aktualisieren über die Windows-Paketverwaltung „winget“ (in Windows 10/11 eingebaut).
/// </summary>
public static class AppUpdates
{
    public static bool Available => Find() != null;

    private static string? Find()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { var f = Path.Combine(dir.Trim(), "winget.exe"); if (File.Exists(f)) return f; } catch { }
        return null;
    }

    private static async Task<(int Code, string Output)> Run(string args, int timeoutMs, Action<string>? progress = null, bool hideInstallers = false)
    {
        var exe = Find() ?? "winget.exe";
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        Process? started;
        try { started = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception)
        {
            // der Store-Verweis auf winget.exe lässt sich aus Admin-Prozessen manchmal nicht direkt starten → über cmd
            psi.FileName = "cmd.exe"; psi.Arguments = "/d /c winget " + args;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            started = Process.Start(psi);
        }
        using var p = started ?? throw new InvalidOperationException("winget konnte nicht gestartet werden");
        // Installer-Fenster (Fortschrittsanzeigen trotz --silent) unsichtbar halten
        using var hider = hideInstallers ? new WindowHider(p.Id, TimeSpan.FromMinutes(8)) : null;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); progress?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, sb + "\n(Zeitüberschreitung)"); }
        return (p.ExitCode, sb.ToString());
    }

    /// <summary>Liste der Programme mit Update.</summary>
    public static async Task<List<AppUpdate>> ListAsync()
    {
        var (_, output) = await Run("upgrade --accept-source-agreements --disable-interactivity", 120_000);
        return Parse(output);
    }

    /// <summary>Tabelle von „winget upgrade“ auswerten (Spalten anhand der Kopfzeile).</summary>
    public static List<AppUpdate> Parse(string output)
    {
        var list = new List<AppUpdate>();
        // Fortschrittsanzeige von winget überschreibt die Zeile mit \r – nur den letzten Teil jeder Zeile nehmen
        var lines = output.Split('\n').Select(l => { l = l.TrimEnd('\r'); int r = l.LastIndexOf('\r'); return (r >= 0 ? l[(r + 1)..] : l).TrimEnd(); }).ToList();
        int dash = lines.FindIndex(l => l.Length > 20 && l.Trim().All(c => c == '-'));
        if (dash < 1) return list;
        var head = lines[dash - 1];
        // Kopfzeile: Name | Id | Version | Verfügbar/Available | Quelle/Source
        int iId = IndexOfWord(head, "Id", "ID"), iVer = IndexOfWord(head, "Version"), iAv = IndexOfWord(head, "Verfügbar", "Available"), iSrc = IndexOfWord(head, "Quelle", "Source");
        if (iId < 0 || iVer < 0 || iAv < 0) return list;
        foreach (var l in lines.Skip(dash + 1))
        {
            if (l.Length < iAv || string.IsNullOrWhiteSpace(l)) continue;
            if (char.IsDigit(l.TrimStart().FirstOrDefault()) && l.Contains("upgrade", StringComparison.OrdinalIgnoreCase)) break; // Fußzeile „x Upgrades verfügbar“
            string Cut(int a, int b) => a < l.Length ? l.Substring(a, Math.Max(0, Math.Min(b, l.Length) - a)).Trim() : "";
            var id = Cut(iId, iVer);
            if (id.Length == 0 || id.Contains(' ')) continue;
            list.Add(new AppUpdate { Name = Cut(0, iId), Id = id, Version = Cut(iVer, iAv), Available = Cut(iAv, iSrc > 0 ? iSrc : l.Length) });
        }
        return list;
    }

    private static int IndexOfWord(string head, params string[] words)
    {
        foreach (var w in words)
        {
            int i = head.IndexOf(" " + w + " ", StringComparison.Ordinal);
            if (i >= 0) return i + 1;
            if (head.EndsWith(" " + w)) return head.Length - w.Length;
        }
        return -1;
    }

    /// <summary>Installer-Größen nachladen (winget show → Installer-URL → Größe beim Server erfragen).</summary>
    public static async Task FillSizesAsync(IEnumerable<AppUpdate> list)
    {
        using var gate = new SemaphoreSlim(3);
        string? biggestUrl = null; long biggest = 0;
        await Task.WhenAll(list.Select(async a =>
        {
            await gate.WaitAsync();
            try
            {
                var (_, output) = await Run($"show --id \"{a.Id}\" --exact --accept-source-agreements --disable-interactivity", 60_000);
                var m = System.Text.RegularExpressions.Regex.Match(output, @"Installer[- ]?Url:\s*(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                long size = 0;
                if (m.Success)
                {
                    size = await InstallEstimate.SizeOfAsync(m.Groups[1].Value);
                    lock (gate) if (size > biggest) { biggest = size; biggestUrl = m.Groups[1].Value; }
                }
                System.Windows.Application.Current.Dispatcher.Invoke(() => a.Bytes = size);
            }
            catch { System.Windows.Application.Current.Dispatcher.Invoke(() => a.Bytes = 0); }
            finally { gate.Release(); }
        }));
        // Geschwindigkeit an einem echten Installer messen (sonst Standard-Messung)
        await InstallEstimate.MeasureAsync(biggest > (20L << 20) ? biggestUrl : null);
    }

    public static async Task<bool> UpgradeAsync(AppUpdate a)
    {
        a.Busy = true; a.Status = "Wird aktualisiert …";
        // HWiNFO startet das Control Center selbst im Hintergrund – fürs Update kurz beenden, danach wieder starten
        bool hwinfo = a.Id.Contains("HWiNFO", StringComparison.OrdinalIgnoreCase) && HwinfoLauncher.IsRunning;
        if (hwinfo)
        {
            foreach (var p in Process.GetProcessesByName("HWiNFO64").Concat(Process.GetProcessesByName("HWiNFO32")))
                try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
        var (code, output) = await Run($"upgrade --id \"{a.Id}\" --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity", 30 * 60_000, hideInstallers: true);
        a.Busy = false;
        bool ok = code == 0;
        if (ok) a.Done = true;
        if (hwinfo) HwinfoLauncher.StartIfWanted();
        a.Status = ok ? "✔ Aktualisiert" : "Fehlgeschlagen" + (output.Contains("läuft", StringComparison.OrdinalIgnoreCase) || output.Contains("running", StringComparison.OrdinalIgnoreCase) ? " – Programm erst schließen" : $" (Code {code})");
        Log.Write(ok ? LogLevel.Ok : LogLevel.Warn, LogModule.App, $"winget: {a.Name} {a.Version} → {a.Available}: {a.Status}");
        return ok;
    }
}
