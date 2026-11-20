using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace NickControlCenter;

/// <summary>
/// Startet HWiNFO unsichtbar im Hintergrund (nur Sensoren, minimiert, ohne Startfenster, Shared Memory an),
/// damit die App überall genaue Temperaturen bekommt. Die kostenlose HWiNFO-Version beendet das Shared Memory
/// nach 12 Stunden – dann wird HWiNFO automatisch neu gestartet.
/// </summary>
public static class HwinfoLauncher
{
    private static DateTime _lastRestart = DateTime.MinValue;
    public static bool StartedByUs { get; private set; }

    public static bool IsRunning => Process.GetProcessesByName("HWiNFO64").Concat(Process.GetProcessesByName("HWiNFO32")).Any(p => { p.Dispose(); return true; });

    /// <summary>HWiNFO64.exe finden (eigener Pfad aus den Einstellungen, Installation, Standardordner).</summary>
    public static string? FindExe()
    {
        var custom = AppSettings.Current.HwinfoPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        var list = new List<string>();
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HWiNFO64_is1", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\HWiNFO64_is1" })
                try { if (root.OpenSubKey(key)?.GetValue("InstallLocation") is string loc) list.Add(Path.Combine(loc, "HWiNFO64.EXE")); } catch { }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        list.Add(Path.Combine(pf, "HWiNFO64", "HWiNFO64.EXE"));
        list.Add(Path.Combine(pf, "HWiNFO", "HWiNFO64.EXE"));
        list.Add(@"C:\HWiNFO64\HWiNFO64.EXE");
        // läuft gerade? → Pfad vom Prozess
        foreach (var p in Process.GetProcessesByName("HWiNFO64"))
            using (p) try { if (p.MainModule?.FileName is { } f) list.Insert(0, f); } catch { }
        return list.FirstOrDefault(File.Exists);
    }

    /// <summary>HWiNFO-Einstellungen für den stillen Start setzen (HWiNFO64.INI neben der exe).</summary>
    public static void PrepareIni(string exe)
    {
        try
        {
            var ini = Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + ".INI");
            var f = IniFile.Load(ini);
            f.Set("Settings", "SensorsOnly", "1");            // nur das Sensorfenster, keine Übersicht
            f.Set("Settings", "SensorsSM", "1");              // Shared Memory an (dafür ist es da)
            f.Set("Settings", "MinimalizeSensors", "1");      // Sensorfenster minimiert (Symbol neben der Uhr)
            f.Set("Settings", "MinimalizeMainWnd", "1");
            f.Set("Settings", "ShowWelcomeAndProgress", "0"); // kein Start-/Hinweisfenster
            f.Save(ini);
        }
        catch (Exception ex) { Log.Warn(LogModule.Monitor, "HWiNFO-Einstellungen konnten nicht geschrieben werden: " + ex.Message); }
    }

    /// <summary>Beim App-Start aufrufen.</summary>
    public static void StartIfWanted()
    {
        if (!AppSettings.Current.HwinfoAutostart || IsRunning) return;
        var exe = FindExe();
        if (exe == null) { Log.Info(LogModule.Monitor, "HWiNFO nicht installiert – Autostart übersprungen"); return; }
        Start(exe);
    }

    public static bool Start(string exe)
    {
        try
        {
            PrepareIni(exe);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            StartedByUs = true;
            _ = Task.Run(HideWindows);
            Log.Ok(LogModule.Monitor, "HWiNFO im Hintergrund gestartet: " + exe);
            return true;
        }
        catch (Exception ex) { Log.Warn(LogModule.Monitor, "HWiNFO-Start fehlgeschlagen: " + ex.Message); return false; }
    }

    /// <summary>
    /// HWiNFO zeigt beim Start trotz Einstellungen manchmal ein kleines Fenster (z. B. Hinweis zur 12-Stunden-Grenze
    /// des Shared Memory). Die ersten 40 s nach dem Start: Hinweisfenster schließen, andere Fenster in den Hintergrund.
    /// </summary>
    private static void HideWindows()
    {
        var end = DateTime.Now.AddSeconds(40);
        int closed = 0;
        while (DateTime.Now < end)
        {
            Thread.Sleep(400);
            var pids = Process.GetProcessesByName("HWiNFO64").Concat(Process.GetProcessesByName("HWiNFO32")).Select(p => { int id = p.Id; p.Dispose(); return id; }).ToHashSet();
            if (pids.Count == 0) continue;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains((int)pid)) return true;
                var cls = new System.Text.StringBuilder(64); GetClassName(h, cls, 64);
                var title = new System.Text.StringBuilder(256); GetWindowText(h, title, 256);
                bool sensors = title.ToString().Contains("Sensor", StringComparison.OrdinalIgnoreCase);
                if (cls.ToString() == "#32770" && !sensors) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); closed++; } // WM_CLOSE – Hinweisfenster
                else ShowWindow(h, 6); // SW_MINIMIZE
                return true;
            }, IntPtr.Zero);
        }
        if (closed > 0) Log.Info(LogModule.Monitor, $"HWiNFO-Startfenster ausgeblendet ({closed})");
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);

    /// <summary>Shared Memory abgelaufen (12-Stunden-Grenze der Gratis-Version)? → HWiNFO neu starten.</summary>
    public static void OnStale()
    {
        if (!AppSettings.Current.HwinfoAutostart || (DateTime.Now - _lastRestart).TotalMinutes < 30) return;
        _lastRestart = DateTime.Now;
        var exe = FindExe();
        if (exe == null) return;
        Log.Info(LogModule.Monitor, "HWiNFO liefert keine Werte mehr (12-Stunden-Grenze?) – starte HWiNFO neu");
        foreach (var p in Process.GetProcessesByName("HWiNFO64"))
            using (p) try { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(); } catch { }
        Thread.Sleep(1500);
        Start(exe);
    }
}
