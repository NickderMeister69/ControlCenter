using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using SD = System.Drawing;

namespace NickControlCenter;

public enum OptKind { Toggle, Choice }

/// <summary>Eine Windows-Einstellung (Taskleiste, Startmenü, Explorer …).</summary>
public sealed class WinOption : ObservableObject
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public string Desc { get; init; } = "";
    public string Icon { get; init; } = "\uE713";
    public OptKind Kind { get; init; } = OptKind.Toggle;
    public string Key { get; init; } = "";          // relativ zu HKCU
    public string Name { get; init; } = "";
    public object On { get; init; } = 1;
    public object Off { get; init; } = 0;
    public object Default { get; init; } = 1;
    public RegistryValueKind ValueKind { get; init; } = RegistryValueKind.DWord;
    public (string Label, object Value)[] Choices { get; init; } = Array.Empty<(string, object)>();
    public bool NeedsExplorer { get; init; }
    public Func<bool>? CustomGet { get; init; }
    public Action<bool>? CustomSet { get; init; }
    public Func<object?>? CustomGetValue { get; init; }
    public Action<object>? CustomSetValue { get; init; }
    public CustomizeService Svc { get; set; } = null!;

    public string[] ChoiceLabels => Choices.Select(c => c.Label).ToArray();

    public bool IsOn
    {
        get
        {
            if (CustomGet != null) try { return CustomGet(); } catch { return false; }
            var v = Svc.ReadUser(Key, Name) ?? Default;
            return v.ToString() == On.ToString();
        }
        set
        {
            try
            {
                if (CustomSet != null) CustomSet(value);
                else Svc.WriteUser(Key, Name, value ? On : Off, ValueKind);
                Svc.AfterChange(this);
                Log.Info(LogModule.App, $"Design: {Title} → {(value ? "an" : "aus")}");
            }
            catch (Exception ex) { Ui.Toast($"{Title}: {Svc.Explain(ex)}", true); Log.Warn(LogModule.App, $"Design {Title}: {ex.Message}"); }
            Raise();
        }
    }

    public int ChoiceIndex
    {
        get
        {
            object? v;
            try { v = CustomGetValue != null ? CustomGetValue() : Svc.ReadUser(Key, Name); } catch { v = null; }
            v ??= Default;
            for (int i = 0; i < Choices.Length; i++) if (Choices[i].Value.ToString() == v.ToString()) return i;
            return -1;
        }
        set
        {
            if (value < 0 || value >= Choices.Length) return;
            try
            {
                var val = Choices[value].Value;
                if (CustomSetValue != null) CustomSetValue(val);
                else Svc.WriteUser(Key, Name, val, ValueKind);
                Svc.AfterChange(this);
                Log.Info(LogModule.App, $"Design: {Title} → {Choices[value].Label}");
            }
            catch (Exception ex) { Ui.Toast($"{Title}: {Svc.Explain(ex)}", true); }
            Raise();
        }
    }

    public void Refresh() => RaiseAll(nameof(IsOn), nameof(ChoiceIndex));
}

public sealed record ThemePreset(string Name, string Accent, bool Light, string[] Colors, string Glow, bool TaskbarAccent, bool TitleAccent);

public sealed class CustomizeSettings
{
    public List<string> RecentAccents { get; set; } = new();
    public string? SlideshowFolder { get; set; }
    public int SlideshowMinutes { get; set; }
    public int SlideshowIndex { get; set; }
    public bool SyncAppAccent { get; set; }
}

[ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
    uint GetMonitorDevicePathCount();
    WpRect GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
    void SetBackgroundColor(uint color);
    uint GetBackgroundColor();
    void SetPosition(int position);
    int GetPosition();
    void SetSlideshow(IntPtr items);
    IntPtr GetSlideshow();
    void SetSlideshowOptions(uint options, uint tick);
    void GetSlideshowOptions(out uint options, out uint tick);
    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
    uint GetStatus();
    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[StructLayout(LayoutKind.Sequential)] internal struct WpRect { public int Left, Top, Right, Bottom; }
[ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] internal class DesktopWallpaperCo { }

/// <summary>
/// Windows-Design (früher „Win11 Customizer“ in PowerShell). Wichtigste Korrekturen:
///  • Akzentfarbe wird jetzt wie über die Windows-Einstellungen angewendet (.theme-Datei) – dadurch übernehmen
///    Startmenü, Taskleiste, Explorer, Einstellungen und Titelleisten die Farbe wirklich (vorher nur Registry → wurde ignoriert)
///  • Hintergrundbild über die offizielle Windows-Schnittstelle (IDesktopWallpaper), Blickpunkt/Diashow wird vorher ausgeschaltet
///  • jede Änderung wird vorher gesichert („Alles auf Original“)
/// </summary>
public sealed class CustomizeService : ObservableObject
{
    private const string ADV = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string PERS = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DWM = @"Software\Microsoft\Windows\DWM";
    private const string ACC = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string SRCH = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string DESK = @"Control Panel\Desktop";
    private const string ICONS = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string CLASSIC_ROOT = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public CustomizeSettings Settings { get; } = JsonStore.Load<CustomizeSettings>("design.json");
    private readonly List<RegBackupEntry> _backup = JsonStore.Load<List<RegBackupEntry>>("design-sicherung.json");
    public ObservableCollection<WinOption> Options { get; } = new();
    private readonly DispatcherTimer _slide = new();
    private bool _needsExplorer;
    public bool NeedsExplorerRestart { get => _needsExplorer; private set => Set(ref _needsExplorer, value); }

    public static readonly ThemePreset[] Presets =
    {
        new("Midnight Purple", "#8B5CF6", false, new[] { "#0B0A1F", "#2A1B5C", "#5B2A86" }, "#A78BFA", false, true),
        new("Ocean Deep", "#0EA5E9", false, new[] { "#04111F", "#0A3D62", "#0E7490" }, "#38BDF8", false, true),
        new("Neon Gaming", "#00E676", false, new[] { "#030806", "#062A17", "#0B3D2A" }, "#00FF95", true, true),
        new("Sunset Drive", "#F97316", false, new[] { "#1A0B2E", "#7A1F5C", "#F2643A" }, "#FDBA74", false, true),
        new("Blood Moon", "#E11D48", false, new[] { "#050000", "#2B0509", "#6B0F1A" }, "#FB7185", true, true),
        new("Cyber Teal", "#14B8A6", false, new[] { "#020617", "#0F172A", "#134E4A" }, "#2DD4BF", false, true),
        new("Gold Rush", "#EAB308", false, new[] { "#0C0A05", "#2A2109", "#4A3A0B" }, "#FDE047", false, false),
        new("Mono Black", "#A1A1AA", false, new[] { "#000000", "#0A0A0A", "#18181B" }, "#52525B", false, false),
        new("Arctic Light", "#2563EB", true, new[] { "#F8FAFC", "#DBEAFE", "#BFDBFE" }, "#FFFFFF", false, false),
        new("Rose Quartz", "#DB2777", true, new[] { "#FFF1F2", "#FBCFE8", "#E9D5FF" }, "#FFFFFF", false, false),
        new("Aurora", "#22D3EE", false, new[] { "#020B14", "#053B3F", "#1E1B4B" }, "#5EEAD4", false, true),
        new("Lava", "#FB923C", false, new[] { "#0A0503", "#3B0D02", "#7C2D12" }, "#FDBA74", true, true),
    };

    public static readonly string[] AccentPalette =
    {
        "#E81123", "#F43F5E", "#EC4899", "#DB2777", "#C026D3", "#A855F7", "#8B5CF6", "#6366F1", "#3B82F6", "#0078D4",
        "#0EA5E9", "#06B6D4", "#14B8A6", "#10B981", "#22C55E", "#84CC16", "#EAB308", "#F59E0B", "#F97316", "#EF4444",
        "#78716C", "#64748B", "#A1A1AA", "#FFFFFF"
    };

    public CustomizeService()
    {
        BuildOptions();
        foreach (var o in Options) o.Svc = this;
        _slide.Tick += (_, _) => NextSlide();
        if (Settings.SlideshowMinutes > 0 && Directory.Exists(Settings.SlideshowFolder)) StartSlideshow(Settings.SlideshowFolder!, Settings.SlideshowMinutes);
    }

    public void Save() => JsonStore.Save("design.json", Settings);

    // ================================================================ Registry mit Sicherung (nur HKCU)
    public object? ReadUser(string key, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetValue(name); } catch { return null; }
    }

    private void Remember(string key, string name)
    {
        if (_backup.Any(e => e.Key == key && e.Name == name)) return;
        using var k = Registry.CurrentUser.OpenSubKey(key);
        var v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        _backup.Add(new RegBackupEntry
        {
            Key = key, Name = name, Existed = v != null, Kind = v == null ? "" : k!.GetValueKind(name).ToString(),
            Value = v switch { null => null, byte[] b => Convert.ToBase64String(b), int i => i.ToString(), _ => v.ToString() }
        });
        JsonStore.Save("design-sicherung.json", _backup);
    }

    public void WriteUser(string key, string name, object value, RegistryValueKind kind)
    {
        Remember(key, name);
        using var k = Registry.CurrentUser.CreateSubKey(key, true);
        k.SetValue(name, kind == RegistryValueKind.DWord ? Convert.ToInt32(value) : value, kind);
    }

    public string Explain(Exception ex) => ex is UnauthorizedAccessException
        ? "Windows sperrt diese Einstellung für Apps (z. B. Widgets ab 24H2) – bitte direkt in den Windows-Einstellungen ändern."
        : ex.Message;

    public void AfterChange(WinOption o)
    {
        Native.BroadcastSettingChange("ImmersiveColorSet");
        Native.BroadcastSettingChange("TraySettings");
        Native.BroadcastSettingChange("WindowsThemeElement");
        if (o.NeedsExplorer) NeedsExplorerRestart = true;
    }

    public void RestoreAll()
    {
        int ok = 0;
        foreach (var e in _backup.ToList())
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(e.Key, true);
                if (!e.Existed) k.DeleteValue(e.Name, false);
                else if (Enum.TryParse<RegistryValueKind>(e.Kind, out var kind))
                {
                    object val = kind switch
                    {
                        RegistryValueKind.DWord => int.Parse(e.Value ?? "0"),
                        RegistryValueKind.Binary => Convert.FromBase64String(e.Value ?? ""),
                        _ => e.Value ?? ""
                    };
                    k.SetValue(e.Name, val, kind);
                }
                ok++;
            }
            catch { }
        }
        try { if (!_backup.Any(b => b.Key.StartsWith(CLASSIC_ROOT))) { } } catch { }
        _backup.Clear();
        JsonStore.Save("design-sicherung.json", _backup);
        Native.BroadcastSettingChange("ImmersiveColorSet");
        foreach (var o in Options) o.Refresh();
        NeedsExplorerRestart = true;
        Log.Ok(LogModule.App, $"Design: {ok} Einstellungen auf Original zurückgesetzt");
        Ui.Toast($"{ok} Einstellungen wiederhergestellt – Explorer neu starten, damit alles übernommen wird.");
        RaiseAll(nameof(AccentHex), nameof(DarkApps), nameof(DarkSystem), nameof(Transparency), nameof(AccentOnTaskbar), nameof(AccentOnTitlebars));
    }

    // ================================================================ Farben
    public string AccentHex
    {
        get
        {
            var v = ReadUser(DWM, "AccentColor") ?? ReadUser(ACC, "AccentColorMenu");
            if (v is not int i) return "#0078D4";
            var b = BitConverter.GetBytes(i);
            return $"#{b[0]:X2}{b[1]:X2}{b[2]:X2}";
        }
    }

    public bool DarkApps { get => ReadUser(PERS, "AppsUseLightTheme") is int v && v == 0; set { SetMode(value, DarkSystem); } }
    public bool DarkSystem { get => !(ReadUser(PERS, "SystemUsesLightTheme") is int v && v == 1); set { SetMode(DarkApps, value); } }
    public bool Transparency
    {
        get => !(ReadUser(PERS, "EnableTransparency") is int v && v == 0);
        set { WriteUser(PERS, "EnableTransparency", value ? 1 : 0, RegistryValueKind.DWord); Native.BroadcastSettingChange("ImmersiveColorSet"); Raise(); }
    }
    public bool AccentOnTaskbar
    {
        get => ReadUser(PERS, "ColorPrevalence") is int v && v == 1;
        set
        {
            WriteUser(PERS, "ColorPrevalence", value ? 1 : 0, RegistryValueKind.DWord);
            if (value && !DarkSystem) { SetMode(DarkApps, true); Ui.Toast("Akzent auf Start & Taskleiste geht nur im dunklen Windows-Modus – Windows-Modus auf Dunkel gestellt."); }
            Native.BroadcastSettingChange("ImmersiveColorSet");
            Raise();
        }
    }
    public bool AccentOnTitlebars
    {
        get => ReadUser(DWM, "ColorPrevalence") is int v && v == 1;
        set { WriteUser(DWM, "ColorPrevalence", value ? 1 : 0, RegistryValueKind.DWord); Native.BroadcastSettingChange("ImmersiveColorSet"); Raise(); }
    }

    private void SetMode(bool darkApps, bool darkSystem)
    {
        WriteUser(PERS, "AppsUseLightTheme", darkApps ? 0 : 1, RegistryValueKind.DWord);
        WriteUser(PERS, "SystemUsesLightTheme", darkSystem ? 0 : 1, RegistryValueKind.DWord);
        Native.BroadcastSettingChange("ImmersiveColorSet");
        RaiseAll(nameof(DarkApps), nameof(DarkSystem));
        Log.Info(LogModule.App, $"Design: Apps {(darkApps ? "dunkel" : "hell")}, Windows {(darkSystem ? "dunkel" : "hell")}");
    }

    private static (byte r, byte g, byte b) Mix((byte r, byte g, byte b) c, int r, int g, int b, double f) =>
        ((byte)Math.Round(c.r + (r - c.r) * f), (byte)Math.Round(c.g + (g - c.g) * f), (byte)Math.Round(c.b + (b - c.b) * f));

    /// <summary>
    /// Akzentfarbe systemweit setzen. 1) Registry-Werte (Palette mit 8 Abstufungen, Start, DWM)
    /// 2) über eine .theme-Datei offiziell anwenden – erst dadurch färben Startmenü, Taskleiste, Explorer und Einstellungen um.
    /// </summary>
    public async Task SetAccentAsync(string hex, bool viaTheme = true, string? wallpaper = null, bool? dark = null)
    {
        if (!RgbF.TryParseHex(hex, out var col)) return;
        (byte r, byte g, byte b) c = (col.R8, col.G8, col.B8);
        var shades = new[] { Mix(c, 255, 255, 255, .55), Mix(c, 255, 255, 255, .35), Mix(c, 255, 255, 255, .18), c, Mix(c, 0, 0, 0, .18), Mix(c, 0, 0, 0, .36), Mix(c, 0, 0, 0, .55), Mix(c, 0, 0, 0, .70) };
        var pal = new List<byte>();
        foreach (var s in shades) { pal.Add(s.r); pal.Add(s.g); pal.Add(s.b); pal.Add(0); }
        int Abgr((byte r, byte g, byte b) x) => unchecked((int)(0xFF000000u | ((uint)x.b << 16) | ((uint)x.g << 8) | x.r));
        int argb = unchecked((int)(0xC4000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | c.b));

        WriteUser(DESK, "AutoColorization", 0, RegistryValueKind.DWord);
        WriteUser(ACC, "AccentPalette", pal.ToArray(), RegistryValueKind.Binary);
        WriteUser(ACC, "AccentColorMenu", Abgr(c), RegistryValueKind.DWord);
        WriteUser(ACC, "StartColorMenu", Abgr(shades[5]), RegistryValueKind.DWord);
        WriteUser(DWM, "AccentColor", Abgr(c), RegistryValueKind.DWord);
        WriteUser(DWM, "ColorizationColor", argb, RegistryValueKind.DWord);
        WriteUser(DWM, "ColorizationAfterglow", argb, RegistryValueKind.DWord);
        WriteUser(DWM, "EnableWindowColorization", 1, RegistryValueKind.DWord);
        if (dark != null) { WriteUser(PERS, "AppsUseLightTheme", dark.Value ? 0 : 1, RegistryValueKind.DWord); WriteUser(PERS, "SystemUsesLightTheme", dark.Value ? 0 : 1, RegistryValueKind.DWord); }

        if (viaTheme) await ApplyThemeFileAsync(hex, wallpaper);
        Native.BroadcastSettingChange("ImmersiveColorSet");
        Native.BroadcastSettingChange("WindowsThemeElement");

        Settings.RecentAccents.RemoveAll(x => x.Equals(hex, StringComparison.OrdinalIgnoreCase));
        Settings.RecentAccents.Insert(0, hex.ToUpperInvariant());
        if (Settings.RecentAccents.Count > 10) Settings.RecentAccents.RemoveRange(10, Settings.RecentAccents.Count - 10);
        Save();
        if (Settings.SyncAppAccent) { AppSettings.Current.Accent = hex; AppSettings.Current.Save(); AppSettings.ApplyAccent(hex); }
        RaiseAll(nameof(AccentHex), nameof(DarkApps), nameof(DarkSystem));
        Log.Ok(LogModule.App, $"Design: Akzentfarbe {hex} gesetzt{(viaTheme ? " (über Theme-Datei)" : "")}");
    }

    private async Task ApplyThemeFileAsync(string hex, string? wallpaper)
    {
        try
        {
            RgbF.TryParseHex(hex, out var col);
            var dir = Path.Combine(JsonStore.Folder, "Themes");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "NickControlCenter.theme");
            wallpaper ??= CurrentWallpaper();
            var sb = new StringBuilder();
            sb.AppendLine("[Theme]");
            sb.AppendLine("DisplayName=Nick Control Center");
            sb.AppendLine();
            sb.AppendLine(@"[Control Panel\Desktop]");
            if (!string.IsNullOrEmpty(wallpaper) && File.Exists(wallpaper)) sb.AppendLine("Wallpaper=" + wallpaper);
            sb.AppendLine("TileWallpaper=0");
            sb.AppendLine("WallpaperStyle=" + (ReadUser(DESK, "WallpaperStyle") as string ?? "10"));
            sb.AppendLine("Pattern=");
            sb.AppendLine();
            sb.AppendLine("[VisualStyles]");
            sb.AppendLine(@"Path=%SystemRoot%\resources\themes\Aero\Aero.msstyles");
            sb.AppendLine("ColorStyle=NormalColor");
            sb.AppendLine("Size=NormalSize");
            sb.AppendLine("AutoColorization=0");
            sb.AppendLine($"ColorizationColor=0XC4{col.R8:X2}{col.G8:X2}{col.B8:X2}");
            sb.AppendLine("SystemMode=" + (DarkSystem ? "Dark" : "Light"));
            sb.AppendLine("AppMode=" + (DarkApps ? "Dark" : "Light"));
            sb.AppendLine("VisualStyleVersion=10");
            sb.AppendLine();
            sb.AppendLine("[MasterThemeSelector]");
            sb.AppendLine("MTSM=RJSPBS");
            sb.AppendLine();
            sb.AppendLine("[Sounds]");
            sb.AppendLine(@"SchemeName=@%SystemRoot%\System32\mmres.dll,-800");
            File.WriteAllText(file, sb.ToString(), Encoding.Unicode);

            bool settingsOpen = Process.GetProcessesByName("SystemSettings").Length > 0;
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            // Windows öffnet dabei kurz die Einstellungen – danach wieder schließen
            await Task.Delay(3500);
            if (!settingsOpen) foreach (var p in Process.GetProcessesByName("SystemSettings")) try { p.Kill(); } catch { }
        }
        catch (Exception ex) { Log.Warn(LogModule.App, "Theme-Datei anwenden: " + ex.Message); }
    }

    public async Task ApplyPresetAsync(ThemePreset p)
    {
        var wp = MakeGradientWallpaper(p.Name, p.Colors, p.Glow);
        WriteUser(PERS, "ColorPrevalence", p.TaskbarAccent ? 1 : 0, RegistryValueKind.DWord);
        WriteUser(DWM, "ColorPrevalence", p.TitleAccent ? 1 : 0, RegistryValueKind.DWord);
        SetWallpaper(wp, 4);
        await SetAccentAsync(p.Accent, true, wp, !p.Light);
        RaiseAll(nameof(AccentOnTaskbar), nameof(AccentOnTitlebars));
        Ui.Toast($"Theme „{p.Name}“ angewendet");
    }

    // ================================================================ Hintergrund
    public static string[] PositionNames { get; } = { "Zentriert", "Kacheln", "Strecken", "Anpassen", "Füllen", "Spannen" };

    public string CurrentWallpaper()
    {
        try
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaperCo();
            try { var w = dw.GetWallpaper(dw.GetMonitorDevicePathAt(0)); if (!string.IsNullOrEmpty(w)) return w; }
            finally { Marshal.ReleaseComObject(dw); }
        }
        catch { }
        return ReadUser(DESK, "Wallpaper") as string ?? "";
    }

    /// <summary>Hintergrundbild setzen (alle Bildschirme oder einen). position: 0 Zentriert … 4 Füllen, 5 Spannen.</summary>
    public void SetWallpaper(string file, int position, int monitor = -1)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Bild nicht gefunden", file);
        // Blickpunkt / Diashow / Volltonfarbe abschalten, sonst überschreibt Windows das Bild sofort wieder
        WriteUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers", "BackgroundType", 0, RegistryValueKind.DWord);
        WriteUser(@"Software\Microsoft\Windows\CurrentVersion\DesktopSpotlight\Settings", "EnabledState", 0, RegistryValueKind.DWord);
        bool ok = false;
        try
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaperCo();
            try
            {
                dw.Enable(true);
                dw.SetPosition(Math.Clamp(position, 0, 5));
                if (monitor < 0) dw.SetWallpaper(null, file);
                else dw.SetWallpaper(dw.GetMonitorDevicePathAt((uint)monitor), file);
                ok = true;
            }
            finally { Marshal.ReleaseComObject(dw); }
        }
        catch (Exception ex) { Log.Warn(LogModule.App, "IDesktopWallpaper: " + ex.Message + " – nutze Ersatzweg"); }
        if (!ok)
        {
            string style = position switch { 0 => "0", 1 => "0", 2 => "2", 3 => "6", 5 => "22", _ => "10" };
            WriteUser(DESK, "WallpaperStyle", style, RegistryValueKind.String);
            WriteUser(DESK, "TileWallpaper", position == 1 ? "1" : "0", RegistryValueKind.String);
            if (!Native.SystemParametersInfo(0x0014, 0, file, 3)) throw new InvalidOperationException("Windows hat das Hintergrundbild abgelehnt.");
        }
        Log.Ok(LogModule.App, $"Hintergrundbild gesetzt: {file}" + (monitor >= 0 ? $" (Bildschirm {monitor + 1})" : ""));
        Raise(nameof(CurrentWallpaperPath));
    }

    public string CurrentWallpaperPath => CurrentWallpaper();

    public int MonitorCount
    {
        get
        {
            try { var dw = (IDesktopWallpaper)new DesktopWallpaperCo(); try { return (int)dw.GetMonitorDevicePathCount(); } finally { Marshal.ReleaseComObject(dw); } }
            catch { return 1; }
        }
    }

    public void SetSolidColor(string hex)
    {
        if (!RgbF.TryParseHex(hex, out var c)) return;
        WriteUser(@"Control Panel\Colors", "Background", $"{c.R8} {c.G8} {c.B8}", RegistryValueKind.String);
        try
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaperCo();
            try { dw.SetBackgroundColor((uint)(c.R8 | (c.G8 << 8) | (c.B8 << 16))); dw.Enable(false); }
            finally { Marshal.ReleaseComObject(dw); }
        }
        catch
        {
            Native.SystemParametersInfo(0x0014, 0, "", 3);
            Native.SetSysColors(1, new[] { 1 }, new[] { c.R8 | (c.G8 << 8) | (c.B8 << 16) });
        }
        WriteUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers", "BackgroundType", 1, RegistryValueKind.DWord);
        Log.Ok(LogModule.App, "Hintergrund: Volltonfarbe " + hex);
    }

    /// <summary>Erzeugt ein 4K-Verlaufs-Hintergrundbild (für Themes und den Generator).</summary>
    public static string MakeGradientWallpaper(string name, string[] colors, string glow, int w = 3840, int h = 2160)
    {
        var dir = Path.Combine(JsonStore.Folder, "Hintergruende");
        Directory.CreateDirectory(dir);
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        var file = Path.Combine(dir, $"{safe}_{string.Join("", colors).Replace("#", "")}.png");
        if (File.Exists(file)) return file;
        SD.Color C(string hex, int a = 255) { RgbF.TryParseHex(hex, out var c); return SD.Color.FromArgb(a, c.R8, c.G8, c.B8); }
        using var bmp = new SD.Bitmap(w, h);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new SD.Rectangle(0, 0, w, h);
        using (var lg = new SD.Drawing2D.LinearGradientBrush(rect, C(colors[0]), C(colors[^1]), 30f))
        {
            var blend = new SD.Drawing2D.ColorBlend(colors.Length) { Colors = colors.Select(x => C(x)).ToArray(), Positions = colors.Select((_, i) => colors.Length == 1 ? 0f : i / (float)(colors.Length - 1)).ToArray() };
            if (colors.Length > 1) lg.InterpolationColors = blend;
            g.FillRectangle(lg, rect);
        }
        void Glow(int x, int y, int gw, int gh, int a)
        {
            using var path = new SD.Drawing2D.GraphicsPath();
            path.AddEllipse(x, y, gw, gh);
            using var pg = new SD.Drawing2D.PathGradientBrush(path) { CenterColor = C(glow, a), SurroundColors = new[] { C(glow, 0) } };
            g.FillPath(pg, path);
        }
        Glow(w * 55 / 100, h * 42 / 100, w * 68 / 100, h * 92 / 100, 95);
        Glow(-w * 13 / 100, -h * 23 / 100, w * 57 / 100, h * 79 / 100, 55);
        Glow(w * 31 / 100, h * 69 / 100, w * 42 / 100, h * 56 / 100, 35);
        bmp.Save(file, SD.Imaging.ImageFormat.Png);
        return file;
    }

    // ---- eigene Diashow (zuverlässiger als die Windows-Diashow)
    public bool SlideshowOn => _slide.IsEnabled;
    public void StartSlideshow(string folder, int minutes)
    {
        Settings.SlideshowFolder = folder; Settings.SlideshowMinutes = minutes; Save();
        _slide.Interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _slide.Start();
        NextSlide();
        Log.Info(LogModule.App, $"Hintergrund-Diashow: {folder}, alle {minutes} min");
        Raise(nameof(SlideshowOn));
    }

    public void StopSlideshow()
    {
        _slide.Stop();
        Settings.SlideshowMinutes = 0; Save();
        Raise(nameof(SlideshowOn));
    }

    public void NextSlide()
    {
        try
        {
            if (!Directory.Exists(Settings.SlideshowFolder)) { StopSlideshow(); return; }
            var files = Directory.GetFiles(Settings.SlideshowFolder!).Where(f => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).ToList();
            if (files.Count == 0) return;
            Settings.SlideshowIndex = (Settings.SlideshowIndex + 1) % files.Count;
            Save();
            SetWallpaper(files[Settings.SlideshowIndex], 4);
        }
        catch (Exception ex) { Log.Warn(LogModule.App, "Diashow: " + ex.Message); }
    }

    // ================================================================ Explorer
    public void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) try { p.Kill(); } catch { }
            // Windows startet den Explorer selbst neu (als normaler Nutzer). Nur falls nicht: manuell nicht erhöht starten.
            for (int i = 0; i < 20 && Process.GetProcessesByName("explorer").Length == 0; i++) Thread.Sleep(250);
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo("runas.exe", "/trustlevel:0x20000 explorer.exe") { UseShellExecute = false, CreateNoWindow = true });
            NeedsExplorerRestart = false;
            Log.Info(LogModule.App, "Explorer neu gestartet");
            Ui.Toast("Explorer neu gestartet – alles übernommen.");
        }
        catch (Exception ex) { Ui.Toast("Explorer-Neustart fehlgeschlagen: " + ex.Message, true); }
    }

    // ================================================================ Optionen
    private void BuildOptions()
    {
        WinOption T(string id, string group, string title, string desc, string icon, string key, string name, object on, object off, object def, bool explorer = false,
                    RegistryValueKind kind = RegistryValueKind.DWord) =>
            new() { Id = id, Group = group, Title = title, Desc = desc, Icon = icon, Key = key, Name = name, On = on, Off = off, Default = def, NeedsExplorer = explorer, ValueKind = kind };
        WinOption C(string id, string group, string title, string desc, string icon, string key, string name, object def, (string, object)[] choices, bool explorer = false,
                    RegistryValueKind kind = RegistryValueKind.DWord) =>
            new() { Id = id, Group = group, Title = title, Desc = desc, Icon = icon, Kind = OptKind.Choice, Key = key, Name = name, Default = def, Choices = choices, NeedsExplorer = explorer, ValueKind = kind };

        // Taskleiste
        Options.Add(C("tbAlign", "Taskleiste", "Ausrichtung", "Symbole in der Mitte (Windows 11) oder links wie bei Windows 10.", "\uE7F4", ADV, "TaskbarAl", 1, new (string, object)[] { ("Links", 0), ("Mitte", 1) }));
        Options.Add(C("tbSearch", "Taskleiste", "Suche", "Ausblenden, nur Lupe, Suchfeld oder Lupe mit Text.", "\uE721", SRCH, "SearchboxTaskbarMode", 2, new (string, object)[] { ("Aus", 0), ("Symbol", 1), ("Feld", 2), ("Symbol + Text", 3) }));
        Options.Add(C("tbGlom", "Taskleiste", "Fenster gruppieren", "Wie Fenster derselben App zusammengefasst werden.", "\uE8A9", ADV, "TaskbarGlomLevel", 0, new (string, object)[] { ("Immer", 0), ("Wenn voll", 1), ("Nie", 2) }, true));
        Options.Add(T("tbTaskView", "Taskleiste", "Taskansicht-Button", "Button für virtuelle Desktops.", "\uE7C4", ADV, "ShowTaskViewButton", 1, 0, 1));
        Options.Add(T("tbWidgets", "Taskleiste", "Widgets-Button", "Wetter/News links in der Taskleiste.", "\uE71D", ADV, "TaskbarDa", 1, 0, 1));
        Options.Add(T("tbSeconds", "Taskleiste", "Sekunden in der Uhr", "Uhrzeit unten rechts mit Sekunden.", "\uE823", ADV, "ShowSecondsInSystemClock", 1, 0, 0, true));
        Options.Add(T("tbEndTask", "Taskleiste", "„Task beenden“ im Rechtsklick", "Hängende Apps direkt in der Taskleiste beenden.", "\uE711", ADV + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0, 0));
        Options.Add(T("tbMulti", "Taskleiste", "Taskleiste auf allen Bildschirmen", "Bei mehreren Monitoren auf jedem eine Taskleiste.", "\uE7F4", ADV, "MMTaskbarEnabled", 1, 0, 1));
        Options.Add(T("tbBadges", "Taskleiste", "Zähler auf App-Symbolen", "Zahlen für ungelesene Nachrichten.", "\uE8BD", ADV, "TaskbarBadges", 1, 0, 1));
        Options.Add(new WinOption
        {
            Id = "tbAutoHide", Group = "Taskleiste", Title = "Automatisch ausblenden", Desc = "Erscheint erst, wenn die Maus an den unteren Rand geht.", Icon = "\uE740", NeedsExplorer = true,
            CustomGet = () => ReadUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", "Settings") is byte[] b && b.Length > 8 && (b[8] & 1) == 1,
            CustomSet = on =>
            {
                const string p = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
                if (ReadUser(p, "Settings") is not byte[] b || b.Length <= 8) throw new InvalidOperationException("Taskleisten-Einstellung nicht gefunden.");
                var nb = (byte[])b.Clone();
                nb[8] = on ? (byte)(nb[8] | 1) : (byte)(nb[8] & 0xFE);
                WriteUser(p, "Settings", nb, RegistryValueKind.Binary);
            }
        });
        // Startmenü
        Options.Add(C("stLayout", "Startmenü", "Layout", "Mehr Platz für Pins oder Empfehlungen.", "\uE71D", ADV, "Start_Layout", 0, new (string, object)[] { ("Standard", 0), ("Mehr Pins", 1), ("Mehr Empfehlungen", 2) }));
        Options.Add(T("stDocs", "Startmenü", "Zuletzt geöffnete Dateien", "Im Start, in Sprunglisten und im Explorer.", "\uE8A5", ADV, "Start_TrackDocs", 1, 0, 1));
        Options.Add(T("stProgs", "Startmenü", "Meistverwendete Apps", "Deine meistgenutzten Apps im Start.", "\uE8FD", ADV, "Start_TrackProgs", 1, 0, 1));
        Options.Add(T("stIris", "Startmenü", "Tipps & App-Empfehlungen", "Werbung für Apps und Tipps im Start.", "\uE789", ADV, "Start_IrisRecommendations", 1, 0, 1));
        Options.Add(T("stAcct", "Startmenü", "Konto-Hinweise", "Hinweise zum Microsoft-Konto am Profilbild.", "\uE77B", ADV, "Start_AccountNotifications", 1, 0, 1));
        Options.Add(new WinOption
        {
            Id = "stBing", Group = "Startmenü", Title = "Websuche (Bing)", Desc = "Aus = Suche zeigt nur deine Apps, Dateien und Einstellungen.", Icon = "\uE774", NeedsExplorer = true,
            CustomGet = () => !(ReadUser(SRCH, "BingSearchEnabled") is int v && v == 0),
            CustomSet = on =>
            {
                WriteUser(SRCH, "BingSearchEnabled", on ? 1 : 0, RegistryValueKind.DWord);
                WriteUser(SRCH, "CortanaConsent", on ? 1 : 0, RegistryValueKind.DWord);
                WriteUser(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", on ? 0 : 1, RegistryValueKind.DWord);
            }
        });
        Options.Add(T("stHighlights", "Startmenü", "Suchhighlights", "Bunte Tagesthemen im Suchfeld.", "\uE721", @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 1, 0, 1));
        // Explorer
        Options.Add(new WinOption
        {
            Id = "exClassic", Group = "Explorer", Title = "Klassisches Rechtsklick-Menü", Desc = "Volles Windows-10-Kontextmenü ohne „Weitere Optionen anzeigen“.", Icon = "\uE8C4", NeedsExplorer = true,
            CustomGet = () => { using var k = Registry.CurrentUser.OpenSubKey(CLASSIC_ROOT + @"\InprocServer32"); return k != null; },
            CustomSet = on =>
            {
                if (on) { using var k = Registry.CurrentUser.CreateSubKey(CLASSIC_ROOT + @"\InprocServer32", true); k.SetValue("", ""); }
                else Registry.CurrentUser.DeleteSubKeyTree(CLASSIC_ROOT, false);
            }
        });
        Options.Add(T("exExt", "Explorer", "Dateiendungen anzeigen", "Zeigt .exe, .jpg, .pdf usw.", "\uE8A5", ADV, "HideFileExt", 0, 1, 1));
        Options.Add(T("exHidden", "Explorer", "Versteckte Dateien anzeigen", "Zeigt z. B. den Ordner AppData.", "\uE890", ADV, "Hidden", 1, 2, 2));
        Options.Add(C("exLaunch", "Explorer", "Explorer öffnet mit", "Welche Seite beim Öffnen zuerst kommt.", "\uE8B7", ADV, "LaunchTo", 2, new (string, object)[] { ("Start", 2), ("Dieser PC", 1), ("Downloads", 3) }));
        Options.Add(T("exCompact", "Explorer", "Kompakte Ansicht", "Weniger Abstand – mehr Dateien auf einen Blick.", "\uE8FD", ADV, "UseCompactMode", 1, 0, 0));
        Options.Add(T("exCheck", "Explorer", "Kontrollkästchen zum Auswählen", "Häkchen an Dateien für Mehrfachauswahl.", "\uE73E", ADV, "AutoCheckSelect", 1, 0, 0));
        Options.Add(T("exPath", "Explorer", "Vollständiger Pfad in der Titelleiste", "Zeigt C:\\Users\\… statt nur „Bilder“.", "\uE8B7", @"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath", 1, 0, 0));
        Options.Add(T("exRecent", "Explorer", "Zuletzt verwendete Dateien in „Start“", "Liste auf der Explorer-Startseite.", "\uE81C", @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowRecent", 1, 0, 1));
        Options.Add(T("exFreq", "Explorer", "Häufig verwendete Ordner", "Im Schnellzugriff.", "\uE8B7", @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowFrequent", 1, 0, 1));
        // Desktop
        Options.Add(T("dkIcons", "Desktop", "Desktop-Symbole anzeigen", "Alle Symbole ein- oder ausblenden (cleaner Look).", "\uE7F8", ADV, "HideIcons", 0, 1, 0, true));
        Options.Add(T("dkPC", "Desktop", "„Dieser PC“", "Symbol auf dem Desktop.", "\uE7F8", ICONS, "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 0, 1, 1, true));
        Options.Add(T("dkBin", "Desktop", "Papierkorb", "Symbol auf dem Desktop.", "\uE74D", ICONS, "{645FF040-5081-101B-9F08-00AA002F954E}", 0, 1, 0, true));
        Options.Add(T("dkUser", "Desktop", "Benutzerordner", "Dein persönlicher Ordner.", "\uE77B", ICONS, "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", 0, 1, 1, true));
        Options.Add(T("dkNet", "Desktop", "Netzwerk", "Symbol „Netzwerk“.", "\uE774", ICONS, "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", 0, 1, 1, true));
        Options.Add(T("dkCP", "Desktop", "Systemsteuerung", "Die klassische Systemsteuerung.", "\uE713", ICONS, "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", 0, 1, 1, true));
        // Effekte & Maus
        Options.Add(new WinOption
        {
            Id = "fxAnim", Group = "Effekte & Maus", Title = "Animationen", Desc = "Fenster- und Menü-Animationen. Aus = alles fühlt sich schneller an.", Icon = "\uE945",
            CustomGet = () => !(ReadUser(DESK + @"\WindowMetrics", "MinAnimate") as string == "0"),
            CustomSet = on =>
            {
                WriteUser(DESK + @"\WindowMetrics", "MinAnimate", on ? "1" : "0", RegistryValueKind.String);
                Native.SystemParametersInfo(0x1043, 0, on ? new IntPtr(1) : IntPtr.Zero, 3);
            }
        });
        Options.Add(new WinOption
        {
            Id = "fxMenu", Group = "Effekte & Maus", Title = "Menü-Geschwindigkeit", Desc = "Wie schnell Untermenüs aufklappen.", Icon = "\uE916", Kind = OptKind.Choice,
            Key = DESK, Name = "MenuShowDelay", Default = "400", ValueKind = RegistryValueKind.String,
            Choices = new (string, object)[] { ("Normal", "400"), ("Schnell", "150"), ("Sofort", "0") },
            CustomSetValue = v => { WriteUser(DESK, "MenuShowDelay", v.ToString()!, RegistryValueKind.String); Native.SystemParametersInfo(0x6B, uint.Parse(v.ToString()!), IntPtr.Zero, 3); }
        });
        Options.Add(T("fxSnap", "Effekte & Maus", "Snap-Layouts am Maximieren-Button", "Layout-Vorschläge beim Überfahren.", "\uE8A9", ADV, "EnableSnapAssistFlyout", 1, 0, 1));
        Options.Add(new WinOption
        {
            Id = "fxShake", Group = "Effekte & Maus", Title = "Fenster schütteln = andere minimieren", Desc = "Fenster an der Titelleiste schütteln.", Icon = "\uE7C4",
            CustomGet = () => ReadUser(ADV, "DisallowShaking") is int v && v == 0,
            CustomSet = on => WriteUser(ADV, "DisallowShaking", on ? 0 : 1, RegistryValueKind.DWord)
        });
        Options.Add(new WinOption
        {
            Id = "fxAccel", Group = "Effekte & Maus", Title = "Mausbeschleunigung", Desc = "Aus = 1:1-Bewegung, beliebt beim Zocken.", Icon = "\uE962",
            CustomGet = () => ReadUser(@"Control Panel\Mouse", "MouseSpeed") as string != "0",
            CustomSet = on =>
            {
                var (s, t1, t2) = on ? ("1", "6", "10") : ("0", "0", "0");
                WriteUser(@"Control Panel\Mouse", "MouseSpeed", s, RegistryValueKind.String);
                WriteUser(@"Control Panel\Mouse", "MouseThreshold1", t1, RegistryValueKind.String);
                WriteUser(@"Control Panel\Mouse", "MouseThreshold2", t2, RegistryValueKind.String);
                Native.SystemParametersInfo(0x0004, 0, new[] { int.Parse(t1), int.Parse(t2), int.Parse(s) }, 3);
            }
        });
        Options.Add(new WinOption
        {
            Id = "fxCursor", Group = "Effekte & Maus", Title = "Mauszeiger-Größe", Desc = "Windows-Zeiger in normal, groß oder extra groß.", Icon = "\uE962", Kind = OptKind.Choice,
            Choices = new (string, object)[] { ("Normal", 0), ("Groß", 1), ("Extra groß", 2) },
            CustomGetValue = () => { var a = ReadUser(@"Control Panel\Cursors", "Arrow") as string ?? ""; return a.Contains("_xl.") ? 2 : a.Contains("_l.") ? 1 : 0; },
            CustomSetValue = v => SetCursorSize(Convert.ToInt32(v))
        });
        // Werbung
        void Ad(string id, string title, string desc, params string[] names)
        {
            Options.Add(new WinOption
            {
                Id = id, Group = "Werbung & Tipps", Title = title, Desc = desc, Icon = "\uEA8F",
                CustomGet = () => names.Any(n => !(ReadUser(CDM, n) is int v && v == 0)),
                CustomSet = on => { foreach (var n in names) WriteUser(CDM, n, on ? 1 : 0, RegistryValueKind.DWord); }
            });
        }
        Ad("adTips", "Tipps & Vorschläge als Benachrichtigung", "„Hol das Beste aus Windows raus“-Popups.", "SubscribedContent-338389Enabled", "SoftLandingEnabled");
        Ad("adSettings", "Vorschläge in den Einstellungen", "Empfohlene Inhalte in der Einstellungen-App.", "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled");
        Ad("adLock", "Fun Facts auf dem Sperrbildschirm", "Tipps und Werbung auf dem Sperrbildschirm.", "RotatingLockScreenOverlayEnabled", "SubscribedContent-338387Enabled");
        Ad("adApps", "Automatisch installierte Vorschlags-Apps", "Windows installiert Werbe-Apps (Spiele, Social Media) nach.", "SilentInstalledAppsEnabled", "OemPreInstalledAppsEnabled", "PreInstalledAppsEnabled");
        Ad("adStart", "Vorschläge im Startmenü", "App-Vorschläge im Start.", "SystemPaneSuggestionsEnabled");
        Options.Add(T("adWelcome", "Werbung & Tipps", "„Einrichtung abschließen“-Seite nach Updates", "Der Bildschirm „Lass uns die Einrichtung abschließen“.", "\uEA8F",
                      @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 1, 0, 1));
    }

    private static readonly (string Key, string File)[] CursorMap =
    {
        ("Arrow", "aero_arrow{0}.cur"), ("Help", "aero_helpsel{0}.cur"), ("AppStarting", "aero_working{0}.ani"), ("Wait", "aero_busy{0}.ani"),
        ("NWPen", "aero_pen{0}.cur"), ("No", "aero_unavail{0}.cur"), ("SizeNS", "aero_ns{0}.cur"), ("SizeWE", "aero_ew{0}.cur"),
        ("SizeNWSE", "aero_nwse{0}.cur"), ("SizeNESW", "aero_nesw{0}.cur"), ("SizeAll", "aero_move{0}.cur"), ("UpArrow", "aero_up{0}.cur"),
        ("Hand", "aero_link{0}.cur"), ("Pin", "aero_pin{0}.cur"), ("Person", "aero_person{0}.cur")
    };

    private void SetCursorSize(int size)
    {
        var suffix = new[] { "", "_l", "_xl" }[Math.Clamp(size, 0, 2)];
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var (key, f) in CursorMap)
        {
            var file = string.Format(f, suffix);
            if (!File.Exists(Path.Combine(win, "Cursors", file))) file = string.Format(f, "");
            if (File.Exists(Path.Combine(win, "Cursors", file))) WriteUser(@"Control Panel\Cursors", key, $@"%SystemRoot%\cursors\{file}", RegistryValueKind.ExpandString);
        }
        Native.SystemParametersInfo(0x57, 0, IntPtr.Zero, 3);
    }
}
