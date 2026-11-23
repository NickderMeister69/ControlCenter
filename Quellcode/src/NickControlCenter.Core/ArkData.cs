namespace NickControlCenter;

public sealed record ArkCave(string Name, string Artifact, string Boss, double Lat, double Lon, string Difficulty, string Notes, string Source)
{
    public string Coords => $"{Lat:0.0} / {Lon:0.0}";
}

public sealed record ArkBoss(string Name, string Type, string[] Artifacts, string Location, string Tips, string Reward, string Source);

public sealed record ArkResource(string Name, string Where, string Coords, string Notes);

public sealed record ArkPlace(string Name, string Coords, string Notes);

/// <summary>
/// ARK: Survival Ascended – Map „Astraeos“. Zusammengetragen aus Community-Seiten (Stand Nov. 2026), nicht im Spiel geprüft.
/// Koordinaten = GPS Breite / Länge (Lat / Lon) wie auf der Karte im Spiel.
/// Quellen: gamesomg.com (Höhlen, Bosse), layehmarket.com (Ressourcen, Tipps), sportskeeda.com.
/// </summary>
public static class ArkData
{
    public const string MapName = "Astraeos";
    public const string SourcesText = "Daten aus Community-Guides (gamesomg.com, layehmarket.com, sportskeeda.com), Stand Nov. 2026 – nicht im Spiel geprüft. Updates im Spiel können Werte ändern.";

    public static readonly ArkCave[] ArtifactCaves =
    {
        new("Brute-Höhle (Sunken Temple)", "Artefakt des Rohlings (Brute)", "Thodes", 48.6, 49.8, "Schwer", "Unter Wasser vor Therokis, langer Tauchweg, Rätsel mit Statuen/Feuer (eine Quelle nennt ein Rätsel mit 8 Knöpfen). Tauchausrüstung mitnehmen.", "gamesomg, sportskeeda, layehmarket"),
        new("Devourer-Höhle (Artemis Tomb)", "Artefakt des Verschlingers (Devourer)", "Thodes", 21.8, 43.3, "Mittel", "Eingang an der Oberfläche, Ost-Phokintos, labyrinthartig. Enterhaken mitnehmen.", "gamesomg, sportskeeda, layehmarket"),
        new("Pack-Höhle (Vulkanhöhle)", "Artefakt des Rudels (Pack)", "Thodes", 67.4, 62.9, "Schwer", "Lavahöhle im Süden von Therokis, starke Gegner (Rexe, Pyromanes).", "gamesomg, sportskeeda, layehmarket"),
        new("Massive-Höhle (Old Tomb)", "Artefakt des Massiven (Massive)", "Natrix", 37.4, 22.5, "Sehr schwer", "Mitten in Korinthos, Rätsel mit 3 Knöpfen (Enterhaken nötig), Ende unter Wasser.", "gamesomg, sportskeeda, layehmarket"),
        new("Clever-Höhle (Sunken Caverns)", "Artefakt des Klugen (Clever)", "Natrix", 24.5, 23.9, "Mittel", "Unter Wasser zwischen Korinthos und Phokintos, Felsbrocken als Wegmarke, lila leuchtende Höhle.", "gamesomg, sportskeeda, layehmarket"),
        new("Hunter-Höhle (Forgotten Temple)", "Artefakt des Jägers (Hunter)", "Natrix", 29.9, 9.5, "Schwer", "Nordwest-Korinthos bei den Zwillings-Wasserfällen, Lücken mit Enterhaken, zwei Wächter.", "gamesomg, sportskeeda, layehmarket"),
        new("Icicle Caverns", "Artefakt des Himmelsherrn (Skylord)", "Hydraskos", 2.5, 97.6, "Schwer", "Schneegebiet ganz im Nordosten. Warme Kleidung!", "gamesomg"),
        new("The Overgrown Tomb", "Artefakt des Immunen (Immune)", "Hydraskos", 30.7, 85.6, "Schwer", "Lemnokis.", "gamesomg"),
        new("Sanctum of the Horned King", "Artefakt des Starken (Strong)", "Hydraskos", 91.0, 74.0, "Sehr schwer", "Keine Höhle: Drop vom Miniboss Minotarchos (respawnt etwa stündlich).", "gamesomg"),
        new("Coral Depths", "Artefakt des Gerissenen (Cunning)", "Hydraskos", 80.8, 71.9, "Schwer", "Unter Wasser, Pyranthos – Tauchausrüstung nötig.", "gamesomg"),
        new("Crag-Höhle", "Artefakt der Klippe (Crag)", "Manticore", 93.0, 76.5, "Schwer", "Ost-Pyranthos, nahe Gatekeeper.", "gamesomg"),
        new("Gatekeeper-Höhle", "Artefakt des Torwächters (Gatekeeper)", "Manticore", 92.1, 77.6, "Schwer", "Etwa 230 m von der Crag-Höhle.", "gamesomg"),
        new("Destroyer-Höhle", "Artefakt des Zerstörers (Destroyer)", "Manticore", 95.3, 94.3, "Schwer", "Ganz im Südosten von Pyranthos.", "gamesomg"),
    };

    public static readonly ArkBoss[] Bosses =
    {
        new("Thodes", "Zyklop (Hauptboss)", new[] { "Brute", "Devourer", "Pack" }, "Arena laut Quellen nicht angegeben – im Wiki/KI nachschauen",
            "Viel Leben, wenig Schaden, schwacher Flächenangriff. Eine Armee aus Rexen oder Deinosuchus funktioniert gut.",
            "Element (laut einer Quelle bis ca. 750 auf Alpha – unbestätigt)", "gamesomg, layehmarket"),
        new("Natrix", "Gorgone/Medusa (Hauptboss)", new[] { "Massive", "Clever", "Hunter" }, "Arena laut Quellen nicht angegeben – im Wiki/KI nachschauen",
            "Wirft dich vom Reittier, saugt Leben, Tollwut, Schaden geht durch Sättel. Megatherien empfohlen (töten die Spinnen/Schlangen und erreichen die Fledermäuse).",
            "Element (laut einer Quelle bis ca. 600) – unbestätigt", "gamesomg, layehmarket"),
        new("Hydraskos der Ungebrochene", "Fünfköpfige Hydra (Hauptboss)", new[] { "Skylord", "Immune", "Strong", "Cunning" }, "Laut einer Quelle am roten Obelisken",
            "Starke Kampftiere und volle Rüstung mitbringen.", "Schaltet Drachen-TEK-Baupläne frei", "gamesomg, layehmarket"),
        new("Manticore", "Hauptboss (Pyranthos)", new[] { "Crag", "Gatekeeper", "Destroyer" }, "Laut einer Quelle am roten Obelisken",
            "Starke Kampftiere, volle Rüstung und Heilung mitnehmen – Details im Wiki/KI nachschauen.", "–", "gamesomg, layehmarket"),
        new("Minotarchos", "Miniboss", Array.Empty<string>(), "Sanctum of the Horned King, ca. 91 / 74",
            "Kampf-Reittier, starke Waffen, volle Rüstung. Respawnt etwa jede Stunde.", "Artefakt des Starken (für Hydraskos)", "gamesomg"),
    };

    public static readonly ArkResource[] Resources =
    {
        new("Metall", "Dichtestes Feld (auch Kristall/Obsidian)", "58.6 / 16.9", "Zweites Feld bei der Boss-Statue: 38.3 / 15.7"),
        new("Kristall", "Korinthos", "33.1 / 12.9", ""),
        new("Obsidian", "Beim Metallfeld", "40.5 / 15.7", ""),
        new("Öl", "Reine Ölquelle", "65.1 / 14.1", "Gemischt (Silica, schwarze Perlen, Astral-Mosas): 48.6 / 29"),
        new("Silica & schwarze Perlen", "Unter Wasser", "47.8 / 28.8", "Anglerfisch empfohlen – Vorsicht vor Aalen und Mosas"),
        new("Zementpaste", "Biberdämme / Chitin-Höhle", "4.2 / 9.3", "Chitin-Höhle: 29.9 / 9.5"),
        new("Polymer & Honig", "Bienenhöhle", "46.1 / 11.3", ""),
        new("Schwefel", "Grund des Wyvern-Grabens", "–", "Im Wasser können Wyvern dich nicht treffen"),
    };

    public static readonly ArkPlace[] Places =
    {
        new("Startlager (gratis Metallwerkzeug)", "61.9 / 23.1", "Alternativ 61.1 / 24.3"),
        new("Gemüsebeet mit Starterhäusern", "24.4 / 74.2", ""),
        new("Blue Zone (Aberration-artig)", "≈ 6.5 / 15.5", "Umweltschaden – Hazard Suit mitnehmen"),
        new("Maeguana (Astraeos-Reittier)", "Korinthos", "Zähmen: mit 4–5 Holz-Plakatwänden einsperren, Dach drauf, mit Langhals/Armbrust betäuben"),
    };

    /// <summary>Kurzfassung aller Daten als Text – geht als Wissen mit in die KI-Fragen.</summary>
    public static string AsContext()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Map {MapName} (ARK: Survival Ascended). Koordinaten = GPS Lat / Lon.");
        sb.AppendLine("Artefakt-Höhlen:");
        foreach (var c in ArtifactCaves) sb.AppendLine($"- {c.Name}: {c.Artifact} für {c.Boss}, Eingang {c.Coords}, {c.Difficulty}. {c.Notes}");
        sb.AppendLine("Bosse:");
        foreach (var b in Bosses) sb.AppendLine($"- {b.Name} ({b.Type}): Artefakte {string.Join(", ", b.Artifacts)}. Ort: {b.Location}. Tipps: {b.Tips} Belohnung: {b.Reward}");
        sb.AppendLine("Ressourcen:");
        foreach (var r in Resources) sb.AppendLine($"- {r.Name}: {r.Where} {r.Coords} {r.Notes}");
        foreach (var p in Places) sb.AppendLine($"- {p.Name}: {p.Coords} {p.Notes}");
        return sb.ToString();
    }
}
