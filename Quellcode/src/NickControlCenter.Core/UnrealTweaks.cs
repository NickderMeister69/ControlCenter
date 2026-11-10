namespace NickControlCenter;

/// <summary>
/// Eine Grafik-Feineinstellung für Unreal-Engine-Spiele (Konsolenvariable / Engine.ini).
/// Look: 0 = kaum sichtbar, 1 = etwas sichtbar, 2 = deutlich sichtbar, -1 = sieht sogar besser aus.
/// </summary>
public sealed record UnrealTweak(string Id, string Title, string Description, string Gain, int Look, string Engine, (string Cvar, string Value)[] Lines, bool PvpNote = false)
{
    public string LookText => Look switch { -1 => "sieht besser aus", 0 => "kaum sichtbar", 1 => "etwas sichtbar", _ => "deutlich sichtbar" };
    public string Console => string.Join("\n", Lines.Select(l => $"{l.Cvar} {l.Value}"));
}

/// <summary>Eine Grafik-Kategorie (sg.*) in der GameUserSettings.ini.</summary>
public sealed record SgCategory(string Key, string Label, string Hint);

/// <summary>Fertige Kombination für ein Spiel („Schön &amp; flüssig“ …).</summary>
public sealed record LookPreset(string Name, string Description, Dictionary<string, int> Sg, string[] Tweaks, bool PostFxOff, bool VolumetricsOff, int FpsLimit = -1, int ResScale = 0);

public static class UnrealTweaks
{
    public static readonly string[] LevelNames = { "Wie Voreinstellung", "Niedrig", "Mittel", "Hoch", "Episch" };

    public static readonly SgCategory[] Categories =
    {
        new("sg.ViewDistanceQuality", "Sichtweite", "Wie weit Objekte detailliert gezeigt werden – kostet vor allem CPU."),
        new("sg.TextureQuality", "Texturen", "Kostet fast nur Grafikspeicher, kaum FPS. Mit 8 GB: Hoch."),
        new("sg.AntiAliasingQuality", "Kantenglättung", "Glatte Kanten. Mit DLSS/FSR fast egal."),
        new("sg.ShadowQuality", "Schatten", "Einer der größten FPS-Fresser."),
        new("sg.GlobalIlluminationQuality", "Globale Beleuchtung", "Indirektes Licht (Lumen) – sehr teuer, prägt aber die Optik."),
        new("sg.ReflectionQuality", "Reflexionen", "Spiegelungen in Wasser und Oberflächen."),
        new("sg.PostProcessQuality", "Nachbearbeitung", "Unschärfe, Bloom, Lichtstrahlen."),
        new("sg.EffectsQuality", "Effekte", "Partikel, Explosionen, Feuer."),
        new("sg.FoliageQuality", "Vegetation", "Gras, Büsche, Bäume – in ARK sehr teuer."),
        new("sg.ShadingQuality", "Shading", "Materialqualität."),
    };

    public static readonly UnrealTweak[] All =
    {
        new("vsm", "Virtuelle Schatten aus", "Ersetzt die sehr teuren „Virtual Shadow Maps“ durch klassische Schatten. Einer der größten Gewinne in UE5-Spielen.", "+10–25 %", 1, "UE5",
            new[] { ("r.Shadow.Virtual.Enable", "0") }),
        new("contact", "Kontaktschatten aus", "Winzige Schatten an Kontaktpunkten – sieht man im Spiel kaum.", "+1–3 %", 0, "both",
            new[] { ("r.ContactShadows", "0") }),
        new("ao", "Umgebungsverdeckung aus", "Dunkle Ecken/Spalten (Ambient Occlusion). Bild wirkt etwas flacher.", "+3–6 %", 1, "both",
            new[] { ("r.AmbientOcclusionLevels", "0") }),
        new("dfshadow", "Ferne Schatten vereinfachen", "Schatten weit entfernter Objekte (Distance Field Shadows) aus.", "+3–8 %", 1, "UE5",
            new[] { ("r.DistanceFieldShadowing", "0") }),
        new("vcloud", "Volumetrische Wolken aus", "Echte 3D-Wolken werden durch einfache ersetzt. Großer Gewinn bei Wetter.", "+5–15 FPS", 2, "UE5",
            new[] { ("r.VolumetricCloud", "0") }),
        new("vfog", "Volumetrischer Nebel aus", "Weniger Nebel/Lichtstrahlen – vor allem in Höhlen und nachts spürbar.", "+3–8 FPS", 2, "both",
            new[] { ("r.VolumetricFog", "0") }),
        new("lumenrefl", "Lumen-Spiegelungen aus", "Teure Echtzeit-Spiegelungen aus – hilft am Wasser und bei glänzenden Flächen.", "+5–10 %", 1, "UE5",
            new[] { ("r.Lumen.Reflections.Allow", "0") }),
        new("waterrefl", "Wasser-Spiegelungen vereinfachen", "Wasser spiegelt weniger – hilft an Küsten.", "+3–6 %", 1, "UE5",
            new[] { ("r.Water.SingleLayer.Reflection", "0") }),
        new("ssr", "Bildschirm-Spiegelungen aus", "Screen-Space-Reflections aus (glänzende Böden, Pfützen).", "+2–5 %", 1, "both",
            new[] { ("r.SSR.Quality", "0") }),
        new("nanite", "Geometrie-Detail in der Ferne senken", "Nanite zeichnet weit entfernte Objekte mit weniger Polygonen.", "+5–10 %", 0, "UE5",
            new[] { ("r.Nanite.MaxPixelsPerEdge", "2") }),
        new("gi", "Lumen-Beleuchtung vereinfachen", "Ersetzt die globale Lumen-Beleuchtung durch eine einfache Bildschirm-Variante. Riesiger Gewinn – das Licht sieht aber deutlich anders aus.", "+20–40 %", 2, "UE5",
            new[] { ("r.DynamicGlobalIlluminationMethod", "2") }),
        new("grass", "Weniger dichtes Gras", "Gras-Dichte auf 60 %. Spürbar mehr FPS in Wäldern und Wiesen.", "+5–10 %", 1, "both",
            new[] { ("grass.DensityScale", "0.6") }, PvpNote: true),
        new("sharpen", "Bild schärfen", "Schärft das Bild nach – gleicht die Unschärfe von DLSS/FSR und niedriger Auflösung aus. Kostet praktisch nichts.", "±0", -1, "both",
            new[] { ("r.Tonemapper.Sharpen", "0.6") }),
        new("lightshafts", "Lichtstrahlen aus (ASE)", "Sonnenstrahlen durch Bäume aus – ARK: Survival Evolved.", "+3–5 %", 1, "UE4",
            new[] { ("r.LightShafts", "0") }),
        new("bloom", "Leuchten/Bloom reduzieren", "Weniger Überstrahlen heller Flächen.", "+1–2 %", 0, "both",
            new[] { ("r.BloomQuality", "1") }),
    };

    public static IEnumerable<UnrealTweak> For(string engine) =>
        All.Where(t => t.Engine == "both" || t.Engine == engine || (engine is not ("UE4" or "UE5")));

    /// <summary>Alle Konsolenvariablen, die von der App verwaltet werden (zum Entfernen abgewählter Einstellungen).</summary>
    public static IEnumerable<string> ManagedCvars => All.SelectMany(t => t.Lines.Select(l => l.Cvar)).Distinct();

    // Stufen: Niedrig=0, Mittel=1, Hoch=2, Episch=3 (UI: 1…4)
    private static Dictionary<string, int> Sg(int view, int tex, int aa, int shadow, int gi, int refl, int pp, int fx, int foliage, int shading) => new()
    {
        ["sg.ViewDistanceQuality"] = view, ["sg.TextureQuality"] = tex, ["sg.AntiAliasingQuality"] = aa, ["sg.ShadowQuality"] = shadow,
        ["sg.GlobalIlluminationQuality"] = gi, ["sg.ReflectionQuality"] = refl, ["sg.PostProcessQuality"] = pp, ["sg.EffectsQuality"] = fx,
        ["sg.FoliageQuality"] = foliage, ["sg.ShadingQuality"] = shading
    };

    /// <summary>Fertige Kombinationen. Für ARK speziell abgestimmt, für andere Unreal-Spiele allgemein.</summary>
    public static LookPreset[] Presets(string? knownId)
    {
        if (knownId == "asa")
            return new[]
            {
                new LookPreset("Schön & flüssig", "Texturen, Sichtweite und Kantenglättung hoch (das sieht man am meisten), teure Schatten-/Licht-Technik vereinfacht, Bild geschärft. Dazu im Spiel: DLSS „Qualität“ (+ Frame Generation bei RTX 40).",
                    Sg(3, 3, 3, 1, 2, 1, 2, 2, 1, 2), new[] { "vsm", "contact", "nanite", "sharpen" }, true, false),
                new LookPreset("Ausgewogen", "Mittlere Details, Wolken bleiben, Schatten und Spiegelungen vereinfacht.",
                    Sg(2, 2, 2, 1, 1, 1, 1, 1, 1, 1), new[] { "vsm", "contact", "dfshadow", "lumenrefl", "waterrefl", "nanite", "sharpen" }, true, false),
                new LookPreset("Max-FPS", "Alles für Bildrate: niedrige Details, keine Volumen-Wolken/-Nebel, einfache Beleuchtung. Sieht deutlich schlichter aus.",
                    Sg(1, 2, 1, 0, 0, 0, 0, 0, 0, 0), new[] { "vsm", "contact", "ao", "dfshadow", "vcloud", "vfog", "lumenrefl", "waterrefl", "ssr", "nanite", "gi", "grass", "sharpen" }, true, true),
            };
        if (knownId == "ase")
            return new[]
            {
                new LookPreset("Schön & flüssig", "Texturen und Sichtweite hoch, Schatten und Lichtstrahlen vereinfacht.",
                    Sg(2, 3, 2, 1, 2, 2, 1, 2, 1, 2), new[] { "contact", "lightshafts", "sharpen" }, true, false),
                new LookPreset("Max-FPS", "Niedrige Details, keine Lichtstrahlen, weniger Gras.",
                    Sg(1, 2, 0, 0, 0, 0, 0, 0, 0, 0), new[] { "contact", "ao", "lightshafts", "bloom", "ssr", "grass", "sharpen" }, true, false),
            };
        return new[]
        {
            new LookPreset("Schön & flüssig", "Texturen und Sichtweite hoch, teure Effekte vereinfacht, Bild geschärft.",
                Sg(3, 3, 2, 1, 2, 1, 2, 2, 2, 2), new[] { "vsm", "contact", "sharpen" }, true, false),
            new LookPreset("Max-FPS", "Niedrige Details und alle teuren Effekte aus.",
                Sg(1, 2, 1, 0, 0, 0, 0, 0, 0, 0), new[] { "vsm", "contact", "ao", "vfog", "lumenrefl", "ssr", "nanite", "sharpen" }, true, true),
        };
    }
}
