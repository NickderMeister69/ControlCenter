using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace NickControlCenter;

public enum SensorKind { Temperature, Load, Clock, Power, Fan, Control, Voltage, Data, Throughput, Other }
public enum SensorSource { Lhm, Hwinfo }
public enum SourcePreference { Auto, HwinfoFirst, LhmOnly }

/// <summary>Ein Messwert – egal ob von LibreHardwareMonitor oder HWiNFO.</summary>
public sealed class SensorReading
{
    public required string Id { get; init; }
    public required string Group { get; init; }   // z. B. "CPU [#0]: Intel Core i5-13400F" / "Intel Core i5-13400F"
    public required string Name { get; init; }
    public SensorKind Kind { get; init; }
    public string Unit { get; init; } = "";
    public double Value { get; set; }
    public double Min { get; set; } = double.NaN;
    public double Max { get; set; } = double.NaN;
    public SensorSource Source { get; init; }
    public string GroupKind { get; init; } = ""; // Cpu, Gpu, Board, Storage, Memory, Network, Other
    /// <summary>Originalname (HWiNFO: nicht vom Nutzer umbenannt) – zum zuverlässigen Erkennen.</summary>
    public string OriginalName { get; init; } = "";
    public string OriginalGroup { get; init; } = "";
}

/// <summary>Die wichtigsten, bereits ausgewerteten Werte – für Dashboard, Lüfter, Stresstest, Kacheln.</summary>
public sealed class SensorSnapshot
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string CpuName { get; set; } = "";
    public string GpuName { get; set; } = "";
    public double CpuTemp { get; set; } = double.NaN;
    public string CpuTempLabel { get; set; } = "";
    public double CpuPackage { get; set; } = double.NaN;
    public double CpuLoad { get; set; } = double.NaN;
    public double CpuClock { get; set; } = double.NaN;
    public double CpuPower { get; set; } = double.NaN;
    public double GpuTemp { get; set; } = double.NaN;
    public double GpuHotSpot { get; set; } = double.NaN;
    public double GpuLoad { get; set; } = double.NaN;
    public double GpuClock { get; set; } = double.NaN;
    public double GpuMemClock { get; set; } = double.NaN;
    public double GpuPower { get; set; } = double.NaN;
    /// <summary>GPU-Leistung nur geschätzt (Karte meldet keine Watt).</summary>
    public bool GpuPowerEstimated { get; set; }
    public double GpuFanRpm { get; set; } = double.NaN;
    public double GpuVramUsedGb { get; set; } = double.NaN;
    public double GpuVramTotalGb { get; set; } = double.NaN;
    public double BoardTemp { get; set; } = double.NaN;
    public double RamLoad { get; set; } = double.NaN;
    public double RamUsedGb { get; set; } = double.NaN;
    public double RamTotalGb { get; set; } = double.NaN;
    public double NetDownBps { get; set; }
    public double NetUpBps { get; set; }
    public List<(string Name, double Rpm)> Fans { get; } = new();
    public List<(string Name, double Temp)> DriveTemps { get; } = new();
    public List<SensorReading> All { get; } = new();
    /// <summary>Woher die Temperaturen stammen ("HWiNFO" / "LibreHardwareMonitor").</summary>
    public string TempSourceText { get; set; } = "";
    public bool FromHwinfo { get; set; }

    public double Hottest => double.IsNaN(CpuTemp) ? GpuTemp : double.IsNaN(GpuTemp) ? CpuTemp : Math.Max(CpuTemp, GpuTemp);

    public static bool Plausible(double t) => !double.IsNaN(t) && t > 5 && t < 125;

    /// <summary>
    /// CPU-Temperatur aus einer Liste von Kerntemperaturen: Ø P-Kerne (ruhig, wie gewünscht),
    /// sonst Ø Kerne, sonst Package/Tctl. Gleiche Kernnummer aus mehreren Sensoren zählt nur einmal.
    /// </summary>
    public static (double Value, string Label) PickCpuTemp(IEnumerable<(string Name, double Value)> temps)
    {
        var list = temps.Where(t => Plausible(t.Value) && !t.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                                   && !t.Name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)).ToList();
        double Avg(string pattern)
        {
            var v = list.Where(t => Regex.IsMatch(t.Name, pattern, RegexOptions.IgnoreCase))
                        .GroupBy(t => Regex.Match(t.Name, @"\d+").Value)
                        .Select(g => g.First().Value).ToList();
            return v.Count > 0 ? v.Average() : double.NaN;
        }
        double p = Avg(@"^P-?Core\s*#?\s*\d+(\s|$|:)");
        if (!double.IsNaN(p)) return (p, "Ø P-Kerne");
        double c = Avg(@"^(CPU\s+)?Core\s*#?\s*\d+(\s|$|:)");
        if (!double.IsNaN(c)) return (c, "Ø Kerne");
        foreach (var key in new[] { "Core Average", "CPU (Tctl/Tdie)", "Core (Tctl/Tdie)", "CPU Package", "Package", "Tctl", "CPU Die (average)", "Tdie", "Core Max", "CPU" })
        {
            var m = list.FirstOrDefault(t => t.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (m.Name != null) return (m.Value, m.Name);
        }
        return (double.NaN, "");
    }
}

/// <summary>
/// Liest HWiNFO über dessen „Shared Memory Support“ (Global\HWiNFO_SENS_SM2, Format laut HWiNFO-SDK).
/// HWiNFO misst sehr genau und kennt praktisch jede Hardware – wenn es läuft, nutzt die App seine Werte.
/// Hinweis: In der kostenlosen Version schaltet sich Shared Memory nach 12 Stunden ab – dann fällt die App
/// automatisch auf LibreHardwareMonitor zurück und meldet das im Problemlöser.
/// </summary>
public static class HwinfoSource
{
    public const string MapName = "Global\\HWiNFO_SENS_SM2";
    public const string MutexName = "Global\\HWiNFO_SM2_MUTEX";
    public const uint SigActive = 0x53695748; // "HWiS"
    public const uint SigDead = 0x44414544;   // "DEAD"

    public enum Status { NotRunning, Inactive, Stale, Ok }

    public static Status LastStatus { get; private set; } = Status.NotRunning;
    public static DateTime LastPoll { get; private set; }

    private static readonly Encoding Enc = Encoding.Latin1;

    private static string Str(byte[] buf, int off, int len)
    {
        if (off < 0 || off + len > buf.Length) return "";
        int end = Array.IndexOf(buf, (byte)0, off, len);
        if (end < 0) end = off + len;
        return Enc.GetString(buf, off, end - off).Trim();
    }

    private static SensorKind Map(uint t) => t switch
    {
        1 => SensorKind.Temperature, 2 => SensorKind.Voltage, 3 => SensorKind.Fan, 4 => SensorKind.Other,
        5 => SensorKind.Power, 6 => SensorKind.Clock, 7 => SensorKind.Load, _ => SensorKind.Other
    };

    /// <summary>Liest alle Werte. null = HWiNFO läuft nicht / Shared Memory aus / veraltet.</summary>
    public static List<SensorReading>? Read()
    {
        if (!OperatingSystem.IsWindows()) { LastStatus = Status.NotRunning; return null; }
        MemoryMappedFile mmf;
        try { mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read); }
        catch { LastStatus = Status.NotRunning; return null; }

        Mutex? mutex = null;
        bool locked = false;
        try
        {
            try { mutex = Mutex.OpenExisting(MutexName); locked = mutex.WaitOne(50); } catch { mutex = null; }
            using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (acc.Capacity < 44) { LastStatus = Status.Inactive; return null; }
            var head = new byte[48];
            acc.ReadArray(0, head, 0, (int)Math.Min(48, acc.Capacity));
            uint need = ParseHeaderNeed(head, out var st);
            if (st != Status.Ok || need > acc.Capacity) { LastStatus = st == Status.Ok ? Status.Inactive : st; return null; }
            var buf = new byte[need];
            acc.ReadArray(0, buf, 0, buf.Length);
            var res = Parse(buf, DateTime.UtcNow);
            if (res == null) LastStatus = Status.Stale;
            return res;
        }
        catch { LastStatus = Status.Inactive; return null; }
        finally
        {
            if (locked) try { mutex!.ReleaseMutex(); } catch { }
            mutex?.Dispose();
            mmf.Dispose();
        }
    }

    private static uint ParseHeaderNeed(byte[] h, out Status st)
    {
        st = Status.Inactive;
        uint sig = BitConverter.ToUInt32(h, 0);
        if (sig == SigDead) { st = Status.Inactive; return 0; }
        if (sig != SigActive) return 0;
        uint offS = BitConverter.ToUInt32(h, 20), sizeS = BitConverter.ToUInt32(h, 24), numS = BitConverter.ToUInt32(h, 28);
        uint offR = BitConverter.ToUInt32(h, 32), sizeR = BitConverter.ToUInt32(h, 36), numR = BitConverter.ToUInt32(h, 40);
        if (sizeR < 316 || sizeS < 264 || numR > 100000 || numS > 10000) return 0;
        st = Status.Ok;
        return (uint)Math.Max(offS + (long)sizeS * numS, offR + (long)sizeR * numR);
    }

    /// <summary>Wertet einen kompletten Shared-Memory-Block aus (öffentlich für Tests).</summary>
    public static List<SensorReading>? Parse(byte[] buf, DateTime nowUtc)
    {
        if (buf.Length < 44) return null;
        uint need = ParseHeaderNeed(buf, out var st);
        if (st != Status.Ok || need > buf.Length) { LastStatus = st; return null; }
        long poll = BitConverter.ToInt64(buf, 12);
        LastPoll = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(poll, 0, 253402300799)).UtcDateTime;
        if (poll > 0 && (nowUtc - LastPoll).TotalSeconds > 15) { LastStatus = Status.Stale; return null; }

        uint offS = BitConverter.ToUInt32(buf, 20), sizeS = BitConverter.ToUInt32(buf, 24), numS = BitConverter.ToUInt32(buf, 28);
        uint offR = BitConverter.ToUInt32(buf, 32), sizeR = BitConverter.ToUInt32(buf, 36), numR = BitConverter.ToUInt32(buf, 40);

        var sensors = new List<(string orig, string user)>();
        for (int i = 0; i < numS; i++)
        {
            int b = (int)(offS + i * sizeS);
            var orig = Str(buf, b + 8, 128);
            var user = Str(buf, b + 136, 128);
            sensors.Add((orig, string.IsNullOrEmpty(user) ? orig : user));
        }

        var list = new List<SensorReading>((int)numR);
        for (int i = 0; i < numR; i++)
        {
            int b = (int)(offR + i * sizeR);
            uint type = BitConverter.ToUInt32(buf, b);
            uint sIdx = BitConverter.ToUInt32(buf, b + 4);
            uint rid = BitConverter.ToUInt32(buf, b + 8);
            var lo = Str(buf, b + 12, 128);
            var lu = Str(buf, b + 140, 128);
            var sn = sIdx < sensors.Count ? sensors[(int)sIdx] : ("", "");
            string groupKind = sn.Item1.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) ? "Cpu"
                             : sn.Item1.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) ? "Gpu"
                             : sn.Item1.StartsWith("S.M.A.R.T", StringComparison.OrdinalIgnoreCase) || sn.Item1.StartsWith("Drive", StringComparison.OrdinalIgnoreCase) ? "Storage"
                             : sn.Item1.StartsWith("Network", StringComparison.OrdinalIgnoreCase) ? "Network"
                             : sn.Item1.StartsWith("Memory", StringComparison.OrdinalIgnoreCase) || sn.Item1.StartsWith("System", StringComparison.OrdinalIgnoreCase) ? "Memory"
                             : "Board";
            list.Add(new SensorReading
            {
                Id = $"hwinfo/{sIdx}/{rid}/{lo}",
                Group = sn.Item2,
                Name = string.IsNullOrEmpty(lu) ? lo : lu,
                Kind = Map(type),
                Unit = Str(buf, b + 268, 16),
                Value = BitConverter.ToDouble(buf, b + 284),
                Min = BitConverter.ToDouble(buf, b + 292),
                Max = BitConverter.ToDouble(buf, b + 300),
                Source = SensorSource.Hwinfo,
                GroupKind = groupKind,
                OriginalName = lo,
                OriginalGroup = sn.Item1
            });
        }
        LastStatus = Status.Ok;
        return list;
    }

    /// <summary>Welche HWiNFO-GPU-Gruppe ("GPU [#0]", "GPU [#1]" …) ist die eigentliche Grafikkarte?</summary>
    public static string PickGpuPrefix(IEnumerable<SensorReading> all)
    {
        var groups = all.Where(r => r.OriginalGroup.StartsWith("GPU [#", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.OriginalGroup[..(r.OriginalGroup.IndexOf(']') + 1)])
                        .Select(g => new
                        {
                            Prefix = g.Key,
                            Dedicated = g.Any(r => DriverCheck.VendorFromName(r.OriginalGroup) is GpuVendor.Nvidia or GpuVendor.Amd),
                            HasTemp = g.Any(r => r.Kind == SensorKind.Temperature && r.OriginalName.StartsWith("GPU Temperature", StringComparison.OrdinalIgnoreCase))
                        })
                        .OrderByDescending(x => x.Dedicated).ThenByDescending(x => x.HasTemp).ThenBy(x => x.Prefix)
                        .ToList();
        return groups.FirstOrDefault()?.Prefix ?? "GPU [#0]";
    }

    /// <summary>Füllt die wichtigsten Werte des Snapshots aus den HWiNFO-Werten.</summary>
    public static void Fill(SensorSnapshot s, List<SensorReading> all)
    {
        static bool IsCpu(SensorReading r) => r.OriginalGroup.StartsWith("CPU [#0]", StringComparison.OrdinalIgnoreCase);
        // Mit iGPU ist "GPU [#0]" oft die Intel-Grafik – deshalb die dedizierte Karte mit Temperatursensor wählen
        string gpuPrefix = PickGpuPrefix(all);
        bool IsGpu(SensorReading r) => r.OriginalGroup.StartsWith(gpuPrefix, StringComparison.OrdinalIgnoreCase);

        double Pick(Func<SensorReading, bool> grp, SensorKind kind, params string[] labels)
        {
            var c = all.Where(r => r.Kind == kind && grp(r)).ToList();
            foreach (var l in labels)
            {
                var m = c.FirstOrDefault(r => r.OriginalName.Equals(l, StringComparison.OrdinalIgnoreCase));
                if (m != null) return m.Value;
            }
            foreach (var l in labels)
            {
                var m = c.FirstOrDefault(r => r.OriginalName.Contains(l, StringComparison.OrdinalIgnoreCase));
                if (m != null) return m.Value;
            }
            return double.NaN;
        }

        var cpuS = all.Where(IsCpu).Select(r => r.OriginalGroup).OrderBy(n => n.Length).FirstOrDefault();
        if (cpuS != null) s.CpuName = CleanName(cpuS);
        var gpuS = all.Where(IsGpu).Select(r => r.OriginalGroup).OrderBy(n => n.Length).FirstOrDefault();
        if (gpuS != null) s.GpuName = CleanName(gpuS);

        var (ct, lbl) = SensorSnapshot.PickCpuTemp(all.Where(r => IsCpu(r) && r.Kind == SensorKind.Temperature).Select(r => (r.OriginalName, r.Value)));
        if (double.IsNaN(ct))
        {
            // AMD: "CPU (Tctl/Tdie)" steht beim Sensor "CPU [#0]: AMD Ryzen ... Enhanced"
            var amd = all.Where(r => r.Kind == SensorKind.Temperature && r.OriginalGroup.StartsWith("CPU", StringComparison.OrdinalIgnoreCase));
            (ct, lbl) = SensorSnapshot.PickCpuTemp(amd.Select(r => (r.OriginalName, r.Value)));
        }
        s.CpuTemp = ct; s.CpuTempLabel = lbl;
        s.CpuPackage = Pick(IsCpu, SensorKind.Temperature, "CPU Package", "CPU (Tctl/Tdie)", "CPU Die (average)");
        s.CpuLoad = Pick(IsCpu, SensorKind.Load, "Total CPU Usage", "Total CPU Utility");
        // CPU-Takt = höchster echter Kern-Takt (wie bei LibreHardwareMonitor). Früher: „Average Effective Clock“ – der Durchschnitt
        // aller Kerne inkl. schlafender liegt bei Last auf einem Kern bei ~1 GHz und hat den Turbo-Test verfälscht.
        var coreClocks = all.Where(r => r.Kind == SensorKind.Clock && IsCpu(r) && r.Value is > 300 and < 7000 &&
                                        Regex.IsMatch(r.OriginalName, @"^(P-|E-)?Core \d+ Clock", RegexOptions.IgnoreCase) &&
                                        !r.OriginalName.Contains("Effective", StringComparison.OrdinalIgnoreCase))
                            .Select(r => r.Value).ToList();
        s.CpuClock = coreClocks.Count > 0 ? coreClocks.Max() : Pick(IsCpu, SensorKind.Clock, "Core Clocks (avg)");
        s.CpuPower = Pick(IsCpu, SensorKind.Power, "CPU Package Power", "CPU PPT");

        s.GpuTemp = Pick(IsGpu, SensorKind.Temperature, "GPU Temperature", "GPU Edge Temperature");
        s.GpuHotSpot = Pick(IsGpu, SensorKind.Temperature, "GPU Hot Spot Temperature", "GPU Hotspot Temperature", "Hot Spot");
        s.GpuLoad = Pick(IsGpu, SensorKind.Load, "GPU Core Load", "GPU Utilization", "GPU D3D Usage");
        s.GpuClock = Pick(IsGpu, SensorKind.Clock, "GPU Clock", "GPU Core Clock", "GPU Shader Clock");
        s.GpuMemClock = Pick(IsGpu, SensorKind.Clock, "GPU Memory Clock");
        // Gesamtleistung der Karte; Werte von 0 W (nicht belegte Stecker/Schienen) überspringen
        var gpuW = all.Where(r => IsGpu(r) && r.Kind == SensorKind.Power && r.Unit.Trim() == "W" && r.Value > 0.5).ToList();
        var pw = gpuW.FirstOrDefault(r => r.OriginalName is "GPU Power" or "Total Board Power" or "GPU Power (Total)" or "Total Graphics Power" or "GPU ASIC Power")
              ?? gpuW.FirstOrDefault(r => r.OriginalName.Contains("Board Power", StringComparison.OrdinalIgnoreCase) || r.OriginalName.StartsWith("GPU Power", StringComparison.OrdinalIgnoreCase))
              ?? gpuW.OrderByDescending(r => r.Value).FirstOrDefault();
        if (pw != null) s.GpuPower = pw.Value;
        var alloc = all.FirstOrDefault(r => IsGpu(r) && (r.OriginalName.StartsWith("GPU Memory Allocated") || r.OriginalName.StartsWith("GPU D3D Memory Dedicated")));
        if (alloc != null) s.GpuVramUsedGb = alloc.Unit.Equals("MB", StringComparison.OrdinalIgnoreCase) ? alloc.Value / 1024 : alloc.Value;

        foreach (var r in all.Where(r => r.Kind == SensorKind.Fan && r.Unit.Contains("RPM", StringComparison.OrdinalIgnoreCase)))
        {
            var name = r.Name;
            if (IsGpu(r) && !name.StartsWith("GPU")) name = "GPU " + name;
            if (s.Fans.All(f => f.Name != name)) s.Fans.Add((name, r.Value));
            if (IsGpu(r) && double.IsNaN(s.GpuFanRpm)) s.GpuFanRpm = r.Value;
        }
        foreach (var grp in all.Where(r => r.Kind == SensorKind.Temperature && r.GroupKind == "Storage").GroupBy(r => r.Group))
        {
            var t = grp.FirstOrDefault(r => r.OriginalName.StartsWith("Drive Temperature")) ?? grp.First();
            if (t.Value > 0) s.DriveTemps.Add((Regex.Replace(grp.Key, @"^(S\.M\.A\.R\.T\.|Drive):\s*", ""), t.Value));
        }
        var mb = all.FirstOrDefault(r => r.Kind == SensorKind.Temperature && r.GroupKind == "Board" &&
                                         (r.OriginalName.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) || r.OriginalName.Equals("System", StringComparison.OrdinalIgnoreCase)));
        if (mb != null) s.BoardTemp = mb.Value;
        s.FromHwinfo = true;
        s.TempSourceText = "HWiNFO";
    }

    public static string CleanName(string n)
    {
        n = Regex.Replace(n, @"^(CPU|GPU) \[#\d+\]:\s*", "");
        int i = n.IndexOf(':');
        if (i > 0) n = n[..i];
        return n.Trim();
    }

    /// <summary>Baut einen HWiNFO-Speicherblock (für Tests).</summary>
    public static byte[] BuildTestBlock(DateTime pollUtc, IList<(string sensor, uint type, string label, string unit, double value)> readings)
    {
        var sensorNames = readings.Select(r => r.sensor).Distinct().ToList();
        const int sizeS = 264, sizeR = 316, offS = 48;
        int offR = offS + sizeS * sensorNames.Count;
        var buf = new byte[offR + sizeR * readings.Count];
        BitConverter.GetBytes(SigActive).CopyTo(buf, 0);
        BitConverter.GetBytes(new DateTimeOffset(pollUtc).ToUnixTimeSeconds()).CopyTo(buf, 12);
        BitConverter.GetBytes((uint)offS).CopyTo(buf, 20);
        BitConverter.GetBytes((uint)sizeS).CopyTo(buf, 24);
        BitConverter.GetBytes((uint)sensorNames.Count).CopyTo(buf, 28);
        BitConverter.GetBytes((uint)offR).CopyTo(buf, 32);
        BitConverter.GetBytes((uint)sizeR).CopyTo(buf, 36);
        BitConverter.GetBytes((uint)readings.Count).CopyTo(buf, 40);
        for (int i = 0; i < sensorNames.Count; i++) Enc.GetBytes(sensorNames[i]).CopyTo(buf, offS + i * sizeS + 8);
        for (int i = 0; i < readings.Count; i++)
        {
            int b = offR + i * sizeR;
            var r = readings[i];
            BitConverter.GetBytes(r.type).CopyTo(buf, b);
            BitConverter.GetBytes((uint)sensorNames.IndexOf(r.sensor)).CopyTo(buf, b + 4);
            BitConverter.GetBytes((uint)i).CopyTo(buf, b + 8);
            Enc.GetBytes(r.label).CopyTo(buf, b + 12);
            Enc.GetBytes(r.unit).CopyTo(buf, b + 268);
            BitConverter.GetBytes(r.value).CopyTo(buf, b + 284);
            BitConverter.GetBytes(r.value).CopyTo(buf, b + 292);
            BitConverter.GetBytes(r.value).CopyTo(buf, b + 300);
        }
        return buf;
    }
}
