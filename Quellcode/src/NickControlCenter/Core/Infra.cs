using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace NickControlCenter;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll(params string[] names) { foreach (var n in names) Raise(n); }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run) : this(_ => run()) { }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _run(p);
}

/// <summary>Feste Status-Farben (für Code, der Brushes setzt).</summary>
public static class Palette
{
    public static readonly SolidColorBrush Good = F(52, 211, 153);
    public static readonly SolidColorBrush Warn = F(251, 191, 36);
    public static readonly SolidColorBrush Bad = F(244, 63, 94);
    public static readonly SolidColorBrush Info = F(96, 165, 250);
    public static readonly SolidColorBrush Muted = F(107, 118, 133);
    public static readonly SolidColorBrush Text = F(230, 234, 240);
    public static readonly SolidColorBrush Pump = F(167, 139, 250);
    public static readonly SolidColorBrush Idle = F(80, 90, 104);

    /// <summary>Aktuelle Akzentfarbe (aus den App-Ressourcen).</summary>
    public static Brush Accent => Application.Current?.TryFindResource("AccentBrush") as Brush ?? Info;

    public static SolidColorBrush F(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }

    public static Brush ForSeverity(Severity s) => s switch
    {
        Severity.Problem => Bad, Severity.Warning => Warn, Severity.Hint => Info, _ => Muted
    };

    public static Brush ForTemp(double t, double warn = 75, double crit = 88) =>
        double.IsNaN(t) ? Muted : t >= crit ? Bad : t >= warn ? Warn : Good;
}

/// <summary>JSON-Dateien im Einstellungsordner (%AppData%\NickControlCenter).</summary>
public static class JsonStore
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NickControlCenter");

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static T Load<T>(string name) where T : new()
    {
        var file = Path.Combine(Folder, name);
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Warn(LogModule.App, $"Einstellungen {name} beschädigt – Standardwerte ({ex.Message}). Sicherung: {name}.defekt");
            try { File.Copy(file, file + ".defekt", true); } catch { }
        }
        return new T();
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, name);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, file, true);
        }
        catch (Exception ex) { Log.Error(LogModule.App, $"Speichern {name} fehlgeschlagen", ex); }
    }
}

public static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    /// <summary>Füllt das Fenster den ganzen Bildschirm (Vollbild oder randloses Vollbild)?</summary>
    public static bool IsFullscreen(IntPtr h)
    {
        if (h == IntPtr.Zero || h == GetShellWindow() || h == GetDesktopWindow()) return false;
        if (!GetWindowRect(h, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool DeleteFileW(string path);
    [DllImport("ntdll.dll")] public static extern int NtSetTimerResolution(uint desired, bool set, out uint current);
    [DllImport("ntdll.dll")] public static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);
    [DllImport("ntdll.dll")] public static extern int NtSetSystemInformation(int cls, ref int info, int len);
    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, int[] pv, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, string pv, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] public static extern bool SetSysColors(int n, int[] elements, int[] colors);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    public sealed class MemStatus { public uint Length = 64, Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx([In, Out] MemStatus m);

    public static (double LoadPct, double TotalGb, double UsedGb) Memory()
    {
        var m = new MemStatus();
        if (!GlobalMemoryStatusEx(m)) return (double.NaN, double.NaN, double.NaN);
        return (m.Load, m.Total / 1073741824.0, (m.Total - m.Avail) / 1073741824.0);
    }

    /// <summary>WM_SETTINGCHANGE an alle Fenster (Explorer übernimmt Farben/Theme).</summary>
    public static void BroadcastSettingChange(string area)
    {
        try { SendMessageTimeout(new IntPtr(0xFFFF), 0x1A, UIntPtr.Zero, area, 2, 1500, out _); } catch { }
    }

    /// <summary>Privileg im eigenen Prozess aktivieren (z. B. SeProfileSingleProcessPrivilege für Standby-Liste).</summary>
    public static bool EnablePrivilege(string name)
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out var token)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, name, out long luid)) return false;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attr = 2 };
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TokenPrivileges { public int Count; public long Luid; public int Attr; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

public static class Env
{
    public static bool IsAdmin
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ControlCenter.exe");
    /// <summary>
    /// Entfernt die Windows-"Aus dem Internet"-Markierung (Zone.Identifier) von der exe,
    /// damit SmartScreen nicht bei jedem Start "Trotzdem ausführen" verlangt.
    /// </summary>
    public static void UnblockSelf()
    {
        try
        {
            foreach (var f in new[] { ExePath }.Concat(Directory.EnumerateFiles(ExeDir, "*.exe")).Distinct(StringComparer.OrdinalIgnoreCase))
                if (Native.DeleteFileW(f + ":Zone.Identifier")) Log.Info(LogModule.App, $"Download-Sperre entfernt: {Path.GetFileName(f)}");
        }
        catch { }
    }

    public static string ExeDir => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    public static int WindowsBuild
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                return int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
            }
            catch { return Environment.OSVersion.Version.Build; }
        }
    }

    public static bool IsWindows11 => WindowsBuild >= 22000;

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn(LogModule.App, $"Öffnen von {target} fehlgeschlagen: {ex.Message}"); }
    }

    /// <summary>Öffnet etwas als normaler Nutzer (nicht mit den Admin-Rechten der App) – über den Explorer.</summary>
    /// <summary>Programm mit Argumenten als normaler Benutzer (ohne Admin) starten – über eine kleine .cmd, die der Explorer ausführt.</summary>
    public static void OpenAsUser(string file, string args)
    {
        if (string.IsNullOrWhiteSpace(args)) { OpenAsUser(file); return; }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NickControlCenter");
            Directory.CreateDirectory(dir);
            var cmd = Path.Combine(dir, "starte-spiel.cmd");
            File.WriteAllText(cmd, $"@echo off\r\nstart \"\" \"{file}\" {args}\r\n");
            OpenAsUser(cmd);
        }
        catch { try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); } catch { } }
    }

    public static void OpenAsUser(string target)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false }); }
        catch { Open(target); }
    }

    public static void ShowInExplorer(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }

    /// <summary>Startet ein Kommandozeilenprogramm unsichtbar und liefert (ExitCode, Ausgabe).</summary>
    public static (int Code, string Output) Run(string exe, string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "Zeitüberschreitung"); }
            return (p.ExitCode, outTask.Result + errTask.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}

/// <summary>Autostart über die Aufgabenplanung: beim Anmelden mit Admin-Rechten, ohne UAC-Abfrage, nur im Tray.</summary>
public static class Autostart
{
    public const string TaskName = "NickControlCenter";

    public static bool IsEnabled() => Env.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"").Code == 0;

    public static bool Enable(out string error)
    {
        error = "";
        string xmlPath = Path.Combine(Path.GetTempPath(), "ncc-task.xml");
        try
        {
            var user = WindowsIdentity.GetCurrent().Name;
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Startet Control Center beim Anmelden im Infobereich.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT8S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(Env.ExePath)}</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>";
            File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);
            var (code, output) = Env.Run("schtasks.exe", $"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"");
            if (code != 0) { error = output.Trim(); Log.Warn(LogModule.App, "Autostart einrichten fehlgeschlagen: " + error); return false; }
            Log.Ok(LogModule.App, "Autostart eingerichtet (Aufgabenplanung, startet im Tray).");
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    public static void Disable()
    {
        var r = Env.Run("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"");
        Log.Info(LogModule.App, r.Code == 0 ? "Autostart entfernt." : "Autostart war nicht eingerichtet.");
    }
}

/// <summary>Verknüpfungen auf dem Desktop und im Startmenü.</summary>
public static class Shortcuts
{
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Control Center.lnk");
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Control Center.lnk");

    public static bool Exists => File.Exists(DesktopLink) && File.Exists(StartMenuLink);

    /// <summary>Legt (oder aktualisiert) beide Verknüpfungen an. Zeigt die alte Verknüpfung auf eine andere exe, wird sie korrigiert.</summary>
    public static bool CreateAll(out string error)
    {
        // alte Verknüpfungen (früherer Name „Nick Control Center“) entfernen
        foreach (var old in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Nick Control Center.lnk"),
                                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nick Control Center.lnk") })
            try { if (File.Exists(old)) File.Delete(old); } catch { }
        error = "";
        bool ok = true;
        foreach (var link in new[] { DesktopLink, StartMenuLink })
        {
            try { Create(link, Env.ExePath, "Lüfter, RGB, FPS-Boost, Monitor, Stresstest, Tweaker und Windows-Design in einer App"); }
            catch (Exception ex) { ok = false; error = ex.Message; Log.Warn(LogModule.App, $"Verknüpfung {link}: {ex.Message}"); }
        }
        if (ok) Log.Ok(LogModule.App, "Verknüpfungen auf Desktop und im Startmenü angelegt.");
        return ok;
    }

    public static void Create(string linkPath, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell nicht verfügbar");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(linkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.Description = description;
            lnk.IconLocation = target + ",0";
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
