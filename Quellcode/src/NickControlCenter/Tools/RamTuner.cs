using System.Management;
using System.Text.RegularExpressions;

namespace NickControlCenter;

/// <summary>
/// RAM-Übertaktung: Takt und Timings des Arbeitsspeichers lassen sich auf normalen Mainboards nur im BIOS ändern
/// (Windows hat darauf keinen sicheren Zugriff). Diese Klasse liest aus, was die Riegel können und womit sie gerade laufen,
/// erkennt ein ausgeschaltetes XMP/EXPO-Profil und liefert die passende BIOS-Anleitung für das Mainboard.
/// </summary>
public static class RamTuner
{
    public sealed record Module(string Slot, string Maker, string Part, double SizeGb, int RatedMts, int RunningMts, string Type);

    public sealed record Info(List<Module> Modules, int RunningMts, int RatedMts, int KitMts, string Type, string Board, string BoardMaker, bool IsAmd)
    {
        /// <summary>Höchster Takt, den die Riegel laut Profil schaffen (Herstellerangabe aus der Typnummer, sonst SPD).</summary>
        public int TargetMts => Math.Max(KitMts, RatedMts);
        public bool XmpOff => TargetMts > 0 && RunningMts > 0 && RunningMts < TargetMts - 100;
        public string Profile => IsAmd ? "EXPO / DOCP" : "XMP";
    }

    public static Info Read()
    {
        var mods = new List<Module>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    var type = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => "" };
                    mods.Add(new Module(
                        (o["DeviceLocator"] as string ?? "").Trim(),
                        (o["Manufacturer"] as string ?? "").Trim(),
                        (o["PartNumber"] as string ?? "").Trim(),
                        Convert.ToDouble(o["Capacity"] ?? 0) / 1024 / 1024 / 1024,
                        Convert.ToInt32(o["Speed"] ?? 0),
                        Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0),
                        type));
                }
        }
        catch { }

        string board = "", maker = "";
        try
        {
            using var b = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject o in b.Get()) using (o) { maker = (o["Manufacturer"] as string ?? "").Trim(); board = (o["Product"] as string ?? "").Trim(); }
        }
        catch { }

        var cpu = HardwareHub.Instance.Latest.CpuName ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        return new Info(mods,
            mods.Count > 0 ? mods.Max(m => m.RunningMts) : 0,
            mods.Count > 0 ? mods.Max(m => m.RatedMts) : 0,
            mods.Count > 0 ? mods.Max(m => KitSpeed(m.Part)) : 0,
            mods.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "",
            board, maker,
            cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Takt aus der Typnummer der Riegel (G.Skill, Corsair, Kingston Fury, Crucial Ballistix/Pro, TeamGroup, Patriot …).</summary>
    public static int KitSpeed(string part)
    {
        var p = part.ToUpperInvariant().Replace(" ", "");
        Match m;
        if ((m = Regex.Match(p, @"^F[45]-(\d{4})")).Success) return int.Parse(m.Groups[1].Value);                      // G.Skill F4-3600C16…
        if ((m = Regex.Match(p, @"^CM[A-Z]*\d+GX[45]M\d[A-Z]?(\d{4})")).Success) return int.Parse(m.Groups[1].Value);  // Corsair CMK16GX4M2B3200C16
        if ((m = Regex.Match(p, @"^KF[45]?(\d{2})C")).Success) return int.Parse(m.Groups[1].Value) * 100;              // Kingston Fury KF432C16… / KF560C40
        if ((m = Regex.Match(p, @"^KF[45]?(\d{3})C")).Success) return int.Parse(m.Groups[1].Value) * 10;                // KF5600C… (selten)
        if ((m = Regex.Match(p, @"^BL\d+G(\d{2})C")).Success) return int.Parse(m.Groups[1].Value) * 100;               // Crucial Ballistix BL8G32C16
        if ((m = Regex.Match(p, @"^CP\d+G\d+D[45]\w*?(\d{4})")).Success) return int.Parse(m.Groups[1].Value);          // Crucial Pro
        if ((m = Regex.Match(p, @"^PV[SE]?\d+G(\d{3})C")).Success) return int.Parse(m.Groups[1].Value) * 10;           // Patriot Viper PVS416G360C
        if ((m = Regex.Match(p, @"(\d{4})H?C\d{2}")).Success && int.Parse(m.Groups[1].Value) is >= 2133 and <= 9000 and var v) return v;
        return 0;
    }

    /// <summary>Wo im BIOS das Profil eingeschaltet wird – je nach Mainboard-Hersteller.</summary>
    public static string[] BiosSteps(Info i)
    {
        var m = i.BoardMaker.ToUpperInvariant();
        var steps = new List<string> { "PC neu starten und beim Start Entf (Del) drücken – oder unten „Ins BIOS neu starten“." };
        if (m.Contains("GIGABYTE"))
            steps.AddRange(new[] { "Mit F2 in den „Advanced Mode“ wechseln (falls nötig).", "Reiter „Tweaker“ → „Extreme Memory Profile (X.M.P.)“ bzw. „EXPO“ → „Profile1“.", "F10 → Speichern und neu starten." });
        else if (m.Contains("ASUS"))
            steps.AddRange(new[] { "Mit F7 in den „Advanced Mode“.", "„Ai Tweaker“ → „Ai Overclock Tuner“ → „XMP I“ (AMD: „EXPO I“ bzw. „D.O.C.P.“).", "F10 → Speichern und neu starten." });
        else if (m.Contains("MICRO-STAR") || m.Contains("MSI"))
            steps.AddRange(new[] { "Oben links den Schalter „XMP“ / „A-XMP“ / „EXPO“ auf „Profil 1“ stellen (oder „OC“ → „Extreme Memory Profile“).", "F10 → Speichern und neu starten." });
        else if (m.Contains("ASROCK"))
            steps.AddRange(new[] { "Mit F6 in den „Advanced Mode“.", "„OC Tweaker“ → „DRAM Configuration“ → „Load XMP Setting“ (AMD: „EXPO“) → „Profile 1“.", "F10 → Speichern und neu starten." });
        else
            steps.AddRange(new[] { $"Im Bereich für Übertaktung (oft „OC“, „Tweaker“ oder „Advanced“) die Einstellung „{i.Profile}“ suchen und „Profil 1“ wählen.", "F10 → Speichern und neu starten." });
        steps.Add("Zurück in Windows: hier schauen, ob der Takt stimmt – dann den RAM-Stresstest laufen lassen (15–30 Minuten).");
        return steps.ToArray();
    }

    /// <summary>Neustart direkt ins BIOS/UEFI (Windows 10/11 mit UEFI).</summary>
    public static (bool Ok, string Msg) RebootToBios()
    {
        var r = Env.Run("shutdown.exe", "/r /fw /t 5");
        return r.Code == 0 ? (true, "Neustart ins BIOS in 5 Sekunden …") : (false, "Ging nicht (kein UEFI?) – dann beim Start Entf drücken. " + r.Output.Trim());
    }
}
