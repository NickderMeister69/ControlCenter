using NickControlCenter;
using Xunit;

namespace NickControlCenter.Tests;

public class RgbTests
{
    [Theory]
    [InlineData("#FF0000", 255, 0, 0)]
    [InlineData("00ff80", 0, 255, 128)]
    [InlineData("#abc", 170, 187, 204)]
    public void Hex_Parses(string hex, int r, int g, int b)
    {
        Assert.True(RgbF.TryParseHex(hex, out var c));
        Assert.Equal((r, g, b), (c.R8, c.G8, c.B8));
    }

    [Fact]
    public void Hex_Invalid_IsRejected() => Assert.False(RgbF.TryParseHex("#12345Z", out _));

    [Fact]
    public void Hsv_RoundTrip_KeepsColor()
    {
        foreach (var hex in new[] { "#FF0000", "#123456", "#FFFFFF", "#000000", "#80FF20", "#FFB000" })
        {
            var c = RgbF.FromHex(hex);
            c.ToHsv(out var h, out var s, out var v);
            Assert.Equal(hex, RgbF.FromHsv(h, s, v).Hex);
        }
    }

    [Fact]
    public void Static_ReturnsFirstColor()
    {
        var snap = EffectSnap.From(new EffectConfig { Type = LightEffect.Static, Colors = { "#00FF00" } });
        Assert.Equal("#FF0000", EffectMath.Eval(snap, 3, 0.2, null).Hex); // Liste beginnt mit #FF0000 (Standard) + #00FF00
    }

    [Fact]
    public void Off_IsBlack() =>
        Assert.Equal("#000000", EffectMath.Eval(EffectSnap.From(new EffectConfig { Type = LightEffect.Off }), 1, 0, null).Hex);

    [Fact]
    public void Temperature_MapsColdToFirstAndHotToLast()
    {
        var cfg = new EffectConfig { Type = LightEffect.Temperature, Colors = new() { "#0000FF", "#FF0000" }, TempLow = 40, TempHigh = 80, TempSource = TempSource.Cpu };
        var snap = EffectSnap.From(cfg);
        Assert.Equal("#0000FF", EffectMath.Eval(snap, 0, 0, new EffectInputs { CpuTemp = 30 }).Hex);
        Assert.Equal("#FF0000", EffectMath.Eval(snap, 0, 0, new EffectInputs { CpuTemp = 95 }).Hex);
        Assert.Equal("#800080", EffectMath.Eval(snap, 0, 0, new EffectInputs { CpuTemp = 60 }).Hex);
    }

    [Fact]
    public void Comet_IsDarkOutsideTail()
    {
        var snap = EffectSnap.From(new EffectConfig { Type = LightEffect.Comet, Colors = new() { "#FFFFFF" }, Speed = 1, Tail = 0.2 });
        // t = 0 → Kopf bei 0; Position 0.5 liegt weit hinter dem Schweif
        Assert.Equal("#000000", EffectMath.Eval(snap, 0, 0.5, null).Hex);
        Assert.Equal("#FFFFFF", EffectMath.Eval(snap, 0, 0, null).Hex);
    }

    [Fact]
    public void Wave_StaysWithinUserColors()
    {
        var snap = EffectSnap.From(new EffectConfig { Type = LightEffect.Wave, Colors = new() { "#FF0000", "#0000FF" } });
        for (double p = 0; p < 1; p += 0.05)
        {
            var c = EffectMath.Eval(snap, 1.3, p, null);
            Assert.Equal(0, c.G8);
        }
    }

    [Fact]
    public void Slideshow_HoldsThenFades()
    {
        var snap = EffectSnap.From(new EffectConfig { Type = LightEffect.Slideshow, Colors = new() { "#FF0000", "#0000FF" }, Seconds = 10, Fade = true });
        Assert.Equal("#FF0000", EffectMath.Eval(snap, 2, 0, null).Hex);
        Assert.Equal("#0000FF", EffectMath.Eval(snap, 10.1, 0, null).Hex);
    }

    [Theory]
    [InlineData(23, 7, 23, true)]
    [InlineData(23, 7, 3, true)]
    [InlineData(23, 7, 12, false)]
    [InlineData(8, 17, 9, true)]
    [InlineData(5, 5, 5, false)]
    public void NightMode_Hours(int from, int to, int hour, bool night) => Assert.Equal(night, RgbConfig.IsNight(from, to, hour));
}

public class IniTests
{
    [Fact]
    public void Set_ReplacesExistingKey_AndKeepsRest()
    {
        var ini = IniFile.Parse("[A]\nx=1\n; Kommentar\n[B]\ny=2\n");
        ini.Set("A", "x", "5");
        ini.Set("B", "z", "3");
        ini.Set("C", "q", "9");
        var s = ini.ToString();
        Assert.Contains("x=5", s);
        Assert.Contains("; Kommentar", s);
        Assert.Contains("[B]\r\ny=2\r\nz=3", s);
        Assert.Contains("[C]\r\nq=9", s);
        Assert.Equal("5", ini.Get("A", "x"));
        Assert.Equal("9", ini.Get("c", "Q"));
    }

    [Fact]
    public void Remove_DeletesKey()
    {
        var ini = IniFile.Parse("[SystemSettings]\nr.VolumetricCloud=0\nr.Other=1");
        Assert.True(ini.Remove("SystemSettings", "r.VolumetricCloud"));
        Assert.Null(ini.Get("SystemSettings", "r.VolumetricCloud"));
        Assert.Equal("1", ini.Get("SystemSettings", "r.Other"));
    }

    [Fact]
    public void SaveLoad_KeepsUtf16Encoding()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "GameUserSettings.ini");
        File.WriteAllText(path, "[ScalabilityGroups]\r\nsg.ShadowQuality=3\r\n", System.Text.Encoding.Unicode);
        var ini = IniFile.Load(path);
        ini.Set("ScalabilityGroups", "sg.ShadowQuality", "0");
        ini.Save(path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xFF, bytes[0]); Assert.Equal(0xFE, bytes[1]);
        Assert.Equal("0", IniFile.Load(path).Get("ScalabilityGroups", "sg.ShadowQuality"));
        dir.Delete(true);
    }
}

public class SteamTests
{
    [Fact]
    public void ParsesLibraryFolders()
    {
        const string vdf = "\"libraryfolders\"\n{\n \"0\"\n {\n  \"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n }\n \"1\"\n {\n  \"path\"\t\t\"D:\\\\SteamLibrary\"\n }\n}";
        var libs = SteamLibrary.ParseLibraryFolders(vdf);
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libs);
    }

    [Fact]
    public void ParsesManifestValues()
    {
        const string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"2399830\"\n\t\"name\"\t\t\"ARK: Survival Ascended\"\n\t\"installdir\"\t\t\"ARK Survival Ascended\"\n}";
        Assert.Equal("2399830", SteamLibrary.ParseValue(acf, "appid"));
        Assert.Equal("ARK: Survival Ascended", SteamLibrary.ParseValue(acf, "name"));
    }

    [Fact]
    public void FindGames_ReadsManifests()
    {
        var root = Directory.CreateTempSubdirectory();
        var apps = Directory.CreateDirectory(Path.Combine(root.FullName, "steamapps"));
        Directory.CreateDirectory(Path.Combine(apps.FullName, "common", "ARK"));
        File.WriteAllText(Path.Combine(apps.FullName, "appmanifest_346110.acf"), "\"AppState\"{\"appid\" \"346110\" \"name\" \"ARK: Survival Evolved\" \"installdir\" \"ARK\"}");
        File.WriteAllText(Path.Combine(apps.FullName, "appmanifest_228980.acf"), "\"AppState\"{\"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"x\"}");
        var games = SteamLibrary.FindGames(new[] { root.FullName });
        Assert.Single(games);
        Assert.Equal(346110, games[0].AppId);
        root.Delete(true);
    }
}

public class TempCleanerTests
{
    [Fact]
    public async Task Clean_DeletesFiles_SkipsLocked_AndReportsProgress()
    {
        var dir = Directory.CreateTempSubdirectory();
        for (int i = 0; i < 50; i++) File.WriteAllBytes(Path.Combine(dir.FullName, $"f{i}.tmp"), new byte[1000]);
        var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "sub", "deep"));
        File.WriteAllBytes(Path.Combine(sub.FullName, "x.tmp"), new byte[500]);
        var lockedPath = Path.Combine(dir.FullName, "locked.tmp");
        File.WriteAllBytes(lockedPath, new byte[10]);
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        int reports = 0;
        var res = await TempCleaner.CleanAsync(new[] { dir.FullName }, new Progress<CleanProgress>(_ => reports++), CancellationToken.None);

        Assert.True(res.Deleted >= 51);
        Assert.True(res.FreedBytes >= 50_500);
        Assert.False(res.Cancelled);
        Assert.False(Directory.Exists(Path.Combine(dir.FullName, "sub")));
        locked.Dispose();
        dir.Delete(true);
    }

    [Fact]
    public void Clean_IsCancellable()
    {
        var dir = Directory.CreateTempSubdirectory();
        for (int i = 0; i < 200; i++) File.WriteAllBytes(Path.Combine(dir.FullName, $"f{i}.tmp"), new byte[10]);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var res = TempCleaner.Clean(new[] { dir.FullName }, null, cts.Token, TimeSpan.Zero);
        Assert.True(res.Cancelled);
        Assert.Equal(0, res.Deleted);
        dir.Delete(true);
    }

    [Fact]
    public void RefusesDangerousFolders()
    {
        Assert.True(TempCleaner.IsDangerous("/"));
        Assert.True(TempCleaner.IsDangerous(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(TempCleaner.IsDangerous(Path.Combine(Path.GetTempPath(), "nccx")));
    }

    [Fact]
    public void Fmt_Size() { Assert.Equal("1.00 GB", Fmt.Size(1L << 30).Replace(',', '.')); Assert.Equal("5 MB", Fmt.Size(5 << 20)); }
}

public class CatalogTests
{
    [Fact]
    public void Matches_WallpaperEngine_AndOldFanControl()
    {
        var m = ConflictCatalog.Match(new[] { "explorer", "wallpaper64", "NickFanControl", "svchost" });
        Assert.Contains(m, x => x.Program.Id == "wallpaperengine");
        Assert.Contains(m, x => x.Program.Id == "old-fancontrol" && x.Program.Severity == Severity.Problem);
        Assert.DoesNotContain(m, x => x.Program.Id == "icue");
    }

    [Fact]
    public void ProcessNamesAreUniqueAcrossCatalog()
    {
        var all = ConflictCatalog.Programs.SelectMany(p => p.Processes).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BoostTweaks_HaveUniqueIds_AndValidKeys()
    {
        Assert.Equal(BoostCatalog.Tweaks.Length, BoostCatalog.Tweaks.Select(t => t.Id).Distinct().Count());
        foreach (var v in BoostCatalog.Tweaks.SelectMany(t => t.Values))
            Assert.True(v.Key.StartsWith(@"HKCU\") || v.Key.StartsWith(@"HKLM\"), v.Key);
    }

    [Fact]
    public void GameCatalog_finds_games_and_presets()
    {
        Assert.Equal("valorant", GameCatalog.Find("VALORANT-Win64-Shipping.exe")?.Id);
        Assert.Equal("asa", GameCatalog.Find(null, 2399830)?.Id);
        Assert.Equal("fortnite", GameCatalog.Find(null, 0, "Fortnite")?.Id);
        Assert.Null(GameCatalog.Find("notepad"));
        Assert.Equal(GameCatalog.Games.Length, GameCatalog.Games.Select(g => g.Id).Distinct().Count());
        Assert.Equal("Palworld", GameCatalog.UnrealProjectName("Palworld-Win64-Shipping.exe"));

        var v = GameCatalog.Find("VALORANT-Win64-Shipping")!;
        Assert.Null(v.ConfigPath); // Anti-Cheat/Cloud → keine Datei-Änderungen
        var p = new GameProfile();
        GameCatalog.ApplyPreset(p, GameCatalog.Recommended(v), v, configAvailable: false, engineIniOk: false);
        Assert.Equal(GamePreset.MaxFps, p.Preset);
        Assert.Equal(2, p.Priority);
        Assert.False(p.Graphics);
        Assert.False(p.PostFxOff);

        var cp = GameCatalog.Games.First(g => g.Id == "cyberpunk");
        Assert.Equal(GamePreset.Balanced, GameCatalog.Recommended(cp));
        Assert.Empty(GameCatalog.Scalability(0));
        Assert.Equal("0", GameCatalog.Scalability(1)["sg.ShadowQuality"]);
        Assert.Equal(GameCatalog.FpsLimits.Length, GameCatalog.FpsLimitNames.Length);
        Assert.Equal(GameCatalog.ResolutionScales.Length, GameCatalog.ResolutionNames.Length);
    }

    [Fact]
    public void UserSettingsSection_detects_game_specific_section()
    {
        var ini = IniFile.Parse("[ScalabilityGroups]\nsg.ShadowQuality=3\n\n[/Script/ShooterGame.ShooterGameUserSettings]\nbUseVSync=True\nFrameRateLimit=60.000000\n");
        Assert.Equal("/Script/ShooterGame.ShooterGameUserSettings", GameCatalog.UserSettingsSection(ini));
        Assert.Equal("/Script/Engine.GameUserSettings", GameCatalog.UserSettingsSection(IniFile.Parse("[ScalabilityGroups]\n")));
    }

    [Fact]
    public void ColorOrder_maps_are_permutations()
    {
        for (int i = 1; i < LightZoneConfig.ColorOrderNames.Length; i++)
            Assert.Equal(new[] { 0, 1, 2 }, LightZoneConfig.OrderMap(i)!.OrderBy(x => x));
        Assert.Null(LightZoneConfig.OrderMap(0));
    }
}

public class SensorTests
{
    [Fact]
    public void CpuTemp_AveragesPCores_IgnoresDistance()
    {
        var t = SensorSnapshot.PickCpuTemp(new[]
        {
            ("P-Core #1", 40.0), ("P-Core #2", 50.0), ("E-Core #1", 70.0), ("P-Core #1 Distance to TjMax", 60.0), ("CPU Package", 55.0)
        });
        Assert.Equal(45, t.Value);
        Assert.Equal("Ø P-Kerne", t.Label);
    }

    [Fact]
    public void CpuTemp_AmdFallsBackToTctl()
    {
        var t = SensorSnapshot.PickCpuTemp(new[] { ("Core (Tctl/Tdie)", 62.5), ("CCD1 (Tdie)", 58.0) });
        Assert.Equal(62.5, t.Value);
    }

    [Fact]
    public void Hwinfo_ParsesBlock_AndFillsSnapshot()
    {
        var now = DateTime.UtcNow;
        var block = HwinfoSource.BuildTestBlock(now, new List<(string, uint, string, string, double)>
        {
            ("CPU [#0]: Intel Core i5-13400F", 1, "P-core 0", "°C", 40),
            ("CPU [#0]: Intel Core i5-13400F", 1, "P-core 1", "°C", 44),
            ("CPU [#0]: Intel Core i5-13400F", 1, "CPU Package", "°C", 50),
            ("CPU [#0]: Intel Core i5-13400F", 7, "Total CPU Usage", "%", 12),
            ("CPU [#0]: Intel Core i5-13400F", 5, "CPU Package Power", "W", 33),
            ("GPU [#0]: AMD Radeon RX 7800 XT", 1, "GPU Temperature", "°C", 51),
            ("GPU [#0]: AMD Radeon RX 7800 XT", 1, "GPU Hot Spot Temperature", "°C", 66),
            ("GPU [#0]: AMD Radeon RX 7800 XT", 3, "GPU Fan", "RPM", 900),
            ("S.M.A.R.T.: Samsung SSD 980", 1, "Drive Temperature", "°C", 38),
        });
        var list = HwinfoSource.Parse(block, now);
        Assert.NotNull(list);
        Assert.Equal(9, list!.Count);
        var s = new SensorSnapshot();
        HwinfoSource.Fill(s, list);
        Assert.Equal(42, s.CpuTemp);
        Assert.Equal(51, s.GpuTemp);
        Assert.Equal(66, s.GpuHotSpot);
        Assert.Equal(33, s.CpuPower);
        Assert.Equal("Intel Core i5-13400F", s.CpuName);
        Assert.Equal("AMD Radeon RX 7800 XT", s.GpuName);
        Assert.Single(s.DriveTemps);
        Assert.Contains(s.Fans, f => f.Name == "GPU Fan" || f.Name == "GPU GPU Fan");
        Assert.True(s.FromHwinfo);
    }

    [Fact]
    public void Hwinfo_StaleDataIsRejected()
    {
        var block = HwinfoSource.BuildTestBlock(DateTime.UtcNow.AddMinutes(-5), new List<(string, uint, string, string, double)> { ("CPU [#0]: X", 1, "Core 0", "°C", 40) });
        Assert.Null(HwinfoSource.Parse(block, DateTime.UtcNow));
        Assert.Equal(HwinfoSource.Status.Stale, HwinfoSource.LastStatus);
    }
}

public class StressTests
{
    [Theory]
    [InlineData(CpuStressMode.Float)]
    [InlineData(CpuStressMode.Integer)]
    [InlineData(CpuStressMode.Vector)]
    public void Kernels_AreDeterministic(CpuStressMode mode)
    {
        Assert.Equal(StressKernels.Run(mode, 3, 0), StressKernels.Run(mode, 3, 0));
        Assert.NotEqual(StressKernels.Run(mode, 3, 0), StressKernels.Run(mode, 4, 0));
    }

    [Fact]
    public void MemoryPattern_FindsNoErrorsOnHealthyRam()
    {
        using var m = new MemoryPattern(8 * 1024 * 1024);
        Assert.Equal(0, m.Pass(12345, CancellationToken.None));
    }
}

public class DriverTests
{
    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("32.0.16.2114", "621.14")]
    [InlineData("31.0.15.5222", "552.22")]
    public void NvidiaVersion(string win, string nv) => Assert.Equal(nv, DriverCheck.NvidiaVersionFromWindows(win));

    [Fact]
    public void Compare_Versions()
    {
        Assert.True(DriverCheck.Compare("621.14", "560.94") > 0);
        Assert.True(DriverCheck.Compare("26.10.2", "26.10.10") < 0);
        Assert.Equal(0, DriverCheck.Compare("1.0", "1.0.0"));
    }

    [Fact]
    public void FindsNvidiaProduct_ExactNotTi()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "nvidia-products.xml"));
        var p = DriverCheck.FindNvidiaProduct(xml, "NVIDIA GeForce RTX 4060");
        Assert.Equal((127, 1023), p);
        var ti = DriverCheck.FindNvidiaProduct(xml, "NVIDIA GeForce RTX 4060 Ti");
        Assert.NotNull(ti);
        Assert.NotEqual(1023, ti!.Value.pfid);
    }

    [Fact]
    public void ParsesNvidiaLookup()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "nvidia-studio.json"));
        var o = DriverCheck.ParseNvidiaLookup(json, "Studio");
        Assert.NotNull(o);
        Assert.Matches(@"^\d{3}\.\d{2}$", o!.Version);
        Assert.StartsWith("https://", o.DownloadUrl);
    }

    [Fact]
    public void ParsesAmdPage()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "amd-snippet.html"));
        var o = DriverCheck.ParseAmdPage(html);
        Assert.NotNull(o);
        Assert.Matches(@"^\d+\.\d+\.\d+$", o!.Version);
    }

    [Fact]
    public void Vendors()
    {
        Assert.Equal(GpuVendor.Nvidia, DriverCheck.VendorFromName("NVIDIA GeForce RTX 4060"));
        Assert.Equal(GpuVendor.Amd, DriverCheck.VendorFromName("AMD Radeon RX 7800 XT"));
        Assert.Equal(GpuVendor.Intel, DriverCheck.VendorFromName("Intel(R) Arc(TM) A770"));
    }

    [Fact]
    public void Hwinfo_picks_dedicated_gpu_over_igpu()
    {
        SensorReading R(string g, string n) => new() { Id = g + n, Group = g, Name = n, Kind = SensorKind.Temperature, Unit = "°C", Value = 50, OriginalGroup = g, OriginalName = n };
        var all = new List<SensorReading>
        {
            R("GPU [#0]: Intel UHD Graphics 770: ", "GPU Temperature"),
            R("GPU [#1]: NVIDIA GeForce RTX 4070: ", "GPU Temperature"),
        };
        Assert.Equal("GPU [#1]", HwinfoSource.PickGpuPrefix(all));
        Assert.Equal("GPU [#0]", HwinfoSource.PickGpuPrefix(new List<SensorReading>()));
    }
}

public class AdvisorTests
{
    [Fact]
    public void Tiers_and_recommendations()
    {
        Assert.Equal(1, HardwareAdvisor.GpuTier("NVIDIA GeForce RTX 4060", 8));
        Assert.Equal(3, HardwareAdvisor.GpuTier("NVIDIA GeForce RTX 4090", 24));
        Assert.Equal(2, HardwareAdvisor.GpuTier("AMD Radeon RX 7800 XT", 16));
        var pc = new PcInfo("NVIDIA GeForce RTX 4060", 8, "i5-13400F", 10, 16, 16, 1920, 1080, 144);
        var val = GameCatalog.Find("VALORANT-Win64-Shipping");
        var r = HardwareAdvisor.Recommend(pc, val, false);
        Assert.Equal(GamePreset.MaxFps, r.Preset);
        Assert.Equal(1, r.GfxQuality);
        Assert.True(r.CloseApps); // 16 GB RAM
        var cp = GameCatalog.Games.First(g => g.Id == "cyberpunk");
        var r2 = HardwareAdvisor.Recommend(pc with { Width = 2560, Height = 1440 }, cp, true);
        Assert.Equal(144, GameCatalog.FpsLimits[r2.FpsLimit]);
        Assert.Equal(0, HardwareAdvisor.NearestLimit(0));
        Assert.Equal(1, HardwareAdvisor.NearestLimit(100)); // kein passendes Limit → unbegrenzt
    }
}

public class BenchTests
{
    [Fact]
    public void Finds_hardware_and_compares()
    {
        Assert.Equal("Intel Core i5-13400F", BenchCatalog.Find(BenchCatalog.Cpus, "13th Gen Intel(R) Core(TM) i5-13400F")?.Name);
        Assert.Equal("NVIDIA GeForce RTX 4060", BenchCatalog.Find(BenchCatalog.Gpus, "NVIDIA GeForce RTX 4060")?.Name);
        Assert.Equal("NVIDIA GeForce RTX 4060 Ti", BenchCatalog.Find(BenchCatalog.Gpus, "NVIDIA GeForce RTX 4060 Ti")?.Name);
        Assert.Equal("AMD Ryzen 7 7800X3D", BenchCatalog.Find(BenchCatalog.Cpus, "AMD Ryzen 7 7800X3D 8-Core Processor")?.Name);
        var c = BenchCatalog.Compare(BenchCatalog.Gpus, "NVIDIA GeForce RTX 4060", 318);
        var me = c.Single(x => x.Mine);
        Assert.Equal(318, me.Score);
        Assert.True(c.First(x => x.Name.Contains("4090")).Score > 1000);
        Assert.Equal(1.0, c.Max(x => x.Fraction), 3);
        Assert.Empty(BenchCatalog.Compare(BenchCatalog.Gpus, "Unbekannte Karte", 100));
    }
}

public class HwinfoClockTests
{
    [Fact]
    public void CpuClock_is_highest_real_core_clock()
    {
        var now = DateTime.UtcNow;
        var block = HwinfoSource.BuildTestBlock(now, new List<(string, uint, string, string, double)>
        {
            ("CPU [#0]: Intel Core i5-13400F", 1, "P-core 0", "°C", 40),
            ("CPU [#0]: Intel Core i5-13400F", 6, "P-core 0 Clock (perf #1/2)", "MHz", 4600),
            ("CPU [#0]: Intel Core i5-13400F", 6, "P-core 1 Clock (perf #1/2)", "MHz", 800),
            ("CPU [#0]: Intel Core i5-13400F", 6, "E-core 6 Clock (perf #6)", "MHz", 800),
            ("CPU [#0]: Intel Core i5-13400F", 6, "Average Effective Clock", "MHz", 1007),
            ("CPU [#0]: Intel Core i5-13400F", 6, "P-core 0 T0 Effective Clock", "MHz", 4590),
        });
        var s = new SensorSnapshot();
        HwinfoSource.Fill(s, HwinfoSource.Parse(block, now)!);
        Assert.Equal(4600, s.CpuClock);
    }
}
