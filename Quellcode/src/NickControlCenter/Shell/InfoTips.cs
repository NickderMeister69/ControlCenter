using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NickControlCenter;

/// <summary>
/// Kleines „i“ neben jeder Überschrift (Kicker). Klick → Erklärung mit Beispielen, wofür das Feature gut ist.
/// Wird automatisch an alle Kicker-Überschriften gehängt, für die es einen Text gibt.
/// </summary>
public static class InfoTips
{
    public sealed record Info(string Text, params string[] Examples);

    public static readonly Dictionary<string, Info> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------- Dashboard
        ["DASHBOARD ANPASSEN"] = new("Hier bestimmst du, was auf der Startseite zu sehen ist und in welcher Reihenfolge.",
            "Nur GPU-Temperatur und FPS ganz oben → Kennzahlen umsortieren", "Hinweise ausblenden, wenn sie dich nerven"),
        ["WICHTIGE HINWEISE"] = new("Die wichtigsten Meldungen aus dem Problemlöser – z. B. Programme, die sich mit der App streiten, oder Treiber-Updates.",
            "„Armoury Crate läuft – kann Flackern verursachen“ → Knopf „Schließen“", "„Grafiktreiber-Update verfügbar“ → zur Treiber-Seite"),
        ["LÜFTER"] = new("Steuert die Gehäuse- und CPU-Lüfter am Mainboard nach Temperatur.",
            "Leise: Lüfter bleiben bis 78 °C langsam – fürs Arbeiten/Filme", "Leistung: früh hochdrehen – fürs Zocken oder Rendern"),
        ["BELEUCHTUNG"] = new("RGB-Beleuchtung aller unterstützten Geräte (Mainboard-Anschlüsse, Lüfter, Hubs, Razer …).",
            "Alles in einer Farbe: Effekt „Statisch“", "Lüfter zeigen die Temperatur: Effekt „Temperatur“ (blau = kühl, rot = heiß)"),
        ["FPS-BOOST"] = new("Optimiert Windows fürs Spielen – jede Änderung wird gesichert und ist mit „Alles zurücksetzen“ umkehrbar.",
            "Energieplan Höchstleistung, Spielmodus an, Xbox-Aufnahme aus", "Pro Spiel ein Profil, das beim Start automatisch aktiv wird"),
        ["GRAFIKTREIBER"] = new("Prüft, ob es für deine Grafikkarte einen neueren Treiber gibt (NVIDIA: Game Ready und Studio, sonst AMD).",
            "Neues Spiel ruckelt → neuer Game-Ready-Treiber bringt oft Fixes", "Für Videoschnitt/Blender lieber den Studio-Treiber"),
        ["TWEAKER"] = new("Übertakten/Undervolten von CPU und Grafikkarte – mit Sicherheits-Countdown.",
            "Grafikkarte +100 MHz Kern, +500 MHz Speicher = ein paar % mehr FPS", "Undervolt: gleiche Leistung, weniger Hitze und Lautstärke"),

        // ---------- Lüfter / RGB
        ["CPU"] = new("Prozessor-Temperatur bzw. -Einstellungen. Standard ist der Durchschnitt der Leistungskerne – springt weniger als der heißeste Kern.",
            "Im Leerlauf 30–45 °C normal, beim Spielen 60–80 °C", "Über 90 °C → Kühler/Wärmeleitpaste prüfen"),
        ["GRAFIKKARTE"] = new("Temperatur bzw. Einstellungen der Grafikkarte.",
            "Beim Spielen 60–80 °C normal", "Hot Spot darf 10–20 °C höher sein als die GPU-Temperatur"),
        ["STATUS"] = new("Zeigt, ob die App gerade regelt und welche Sicherheitsgrenzen aktiv sind.",
            "Ab der Sicherheitsgrenze laufen alle Lüfter auf 100 %", "„BIOS“ = das Mainboard regelt selbst"),
        ["EINMALIG ZUORDNEN"] = new("Damit du weißt, welcher Anschluss welcher Lüfter ist: „Identifizieren“ dreht ihn kurz voll auf.",
            "Fan #2 dreht hoch → das ist der hintere Gehäuselüfter → umbenennen in „Hinten“"),
        ["EFFEKT FÜR ALLE GERÄTE"] = new("Der gemeinsame Lichteffekt für alle Anschlüsse, die nicht „Eigene Farben“ haben.",
            "Diashow: wechselt durch deine Farben", "Bildschirm: Licht übernimmt die Farben vom Monitor (wie Ambilight)"),
        ["ANSCHLÜSSE & GERÄTE"] = new("Jeder RGB-Anschluss einzeln: LED-Anzahl, eigene Farben, Flacker-Schutz, Farbreihenfolge.",
            "Nur ein Teil leuchtet → LED-Anzahl erhöhen", "Rot leuchtet grün → im Farbtest prüfen und „Farbreihenfolge“ umstellen"),
        ["PROFILE"] = new("Gespeicherte Einstellungen zum schnellen Umschalten.",
            "„Zocken“ = rot pulsierend, „Nacht“ = gedimmt blau"),

        // ---------- Boost
        ["BEIM BOOST AUSSERDEM"] = new("Was beim großen BOOST-Knopf zusätzlich passiert.",
            "Wiederherstellungspunkt = Windows-Sicherung vorher", "Temp-Dateien löschen und RAM freigeben"),
        ["DEINE SPIELE"] = new("Alle Spiele mit eigenem Profil. Erkannt werden Steam, Epic, Riot – oder du fügst jede exe hinzu.",
            "VALORANT hinzufügen → bekommt automatisch Max-FPS", "Startet ein bekanntes Spiel, wird es von selbst aufgenommen"),
        ["PROFIL FÜR DIESES SPIEL"] = new("Was passiert, wenn genau dieses Spiel startet. Bekannte Spiele bekommen passende Voreinstellungen.",
            "Max-FPS für Shooter: hohe Priorität, Hintergrund-Apps zu", "Ausgewogen für Story-Spiele: schöne Grafik, trotzdem flüssig",
            "„Empfohlene Einstellungen“: rechnet aus deiner Hardware das Beste aus"),
        ["BEIM SPIELSTART (WINDOWS)"] = new("Windows-Optimierungen, die nur laufen, solange das Spiel offen ist – danach wird zurückgestellt.",
            "Priorität hoch: Windows gibt dem Spiel zuerst Rechenzeit", "Timer 0,5 ms: gleichmäßigere Bildzeiten"),
        ["GRAFIK IM SPIEL"] = new("Schreibt Grafikeinstellungen direkt in die Config des Spiels (bei Unreal-Engine-Spielen wie ARK, Fortnite, Palworld). Das Original wird gesichert.",
            "Qualität „Maximale FPS“ + V-Sync aus", "FPS-Limit 144 bei einem 144-Hz-Monitor"),
        ["EMPFOHLENE STARTOPTIONEN"] = new("Zusätzliche Startparameter für Steam, die das Spiel schneller starten oder laufen lassen.",
            "ARK: -USEALLAVAILABLECORES nutzt alle CPU-Kerne"),
        ["TEMPORÄRE DATEIEN"] = new("Löscht Datenmüll im Temp-Ordner (älter als 1 Stunde). Läuft im Hintergrund, die App bleibt bedienbar.",
            "Nach Installationen sammeln sich oft mehrere GB an"),
        ["SHADER-CACHE"] = new("Gespeicherte Grafik-Bausteine der Treiber. Leeren hilft bei Rucklern nach einem Treiber-Update.",
            "Nach Treiber-Update ruckelt das Spiel am Anfang → Cache leeren, einmal neu aufbauen lassen"),
        ["ARBEITSSPEICHER"] = new("Gibt ungenutzten RAM frei und leert die Standby-Liste.",
            "Bei 16 GB RAM: weniger Nachladeruckler in großen Spielen"),
        ["NETZWERK"] = new("DNS-Cache leeren und Ping zu wichtigen Servern messen.",
            "Webseiten laden nicht → DNS leeren", "Lag im Spiel → Ping-Test zeigt, ob die Leitung schwankt"),
        ["WIEDERHERSTELLUNGSPUNKT"] = new("Eine Windows-Sicherung, zu der du zurückkehren kannst, falls etwas schiefgeht."),
        ["TIMER-AUFLÖSUNG"] = new("Der Windows-Zeitgeber tickt normal alle 15,6 ms – mit 0,5 ms reagieren Spiele gleichmäßiger.",
            "Spürbar vor allem bei hohen FPS (144+) und Shootern"),
        ["LAUFENDE HINTERGRUND-PROGRAMME"] = new("Alle Programme, die gerade laufen – das größte zuerst. Angehakte werden beim Boost/Spielstart geschlossen.",
            "Chrome mit 30 Tabs frisst 2 GB RAM → anhaken", "Discord lieber offen lassen, wenn du im Sprachchat bist"),

        // ---------- Monitor
        ["IN-GAME-OVERLAY"] = new("Kleine Anzeige über dem Spiel: FPS, Temperaturen, Auslastung. Strg+Umschalt+O blendet sie ein/aus. Geht in Spielen mit „Randloses Fenster“.",
            "FPS + GPU-Temperatur + GPU-Auslastung oben links", "Ist die GPU-Auslastung unter 90 %, bremst eher die CPU"),
        ["MESSQUELLE"] = new("Woher die Temperaturen kommen: eigene Messung oder HWiNFO (sehr genau).",
            "Komische Werte → auf „HWiNFO bevorzugen“ stellen"),
        ["TEMPERATUR-ALARM"] = new("Meldet sich, wenn CPU oder GPU zu heiß werden.",
            "CPU über 90 °C → Hinweis unten rechts"),
        ["VERLAUF"] = new("Die Werte der letzten Minuten als Diagramm.", "Beim Zocken schauen, ob die GPU-Temperatur stetig steigt"),
        ["HWINFO"] = new("HWiNFO ist ein kostenloses Profi-Messprogramm. Die App startet es unsichtbar und liest dessen Werte mit.",
            "Gratis-Version: nach 12 Stunden startet die App HWiNFO automatisch neu"),

        // ---------- Stresstest
        ["DAUER & SICHERHEIT"] = new("Wie lange getestet wird und ab welcher Temperatur automatisch abgebrochen wird.",
            "10 Minuten reichen, um Übertaktung grob zu prüfen", "Für Stabilität sicher: 30–60 Minuten"),
        ["ERGEBNIS"] = new("Zusammenfassung des Tests: Höchsttemperaturen, Takt, Fehler.",
            "Takt fällt stark ab = Kühlung drosselt (Thermal Throttling)"),
        ["RECHENFEHLER"] = new("Der Test prüft Ergebnisse nach. Fehler bedeuten instabile Übertaktung oder RAM-Probleme.",
            "Fehler nach Undervolt → Spannung etwas zurücknehmen"),
        ["CPU-PUNKTE"] = new("Rechenleistung des Prozessors während des Tests – zum Vergleichen vorher/nachher."),
        ["GPU-PUNKTE"] = new("Rechenleistung der Grafikkarte während des Tests – zum Vergleichen vorher/nachher."),

        // ---------- Tweaker
        ["PROZESSOR"] = new("Turbo-Verhalten und Energiesparen des Prozessors.",
            "Aggressiv: maximaler Takt beim Zocken", "Effizient: kühler und leiser im Alltag"),
        ["SICHERUNG"] = new("Alle Original-Werte werden gesichert – „Zurücksetzen“ stellt sie wieder her."),
        ["PRÜFUNG"] = new("Nach dem Übernehmen hast du 20 Sekunden zum Bestätigen – sonst wird automatisch zurückgesetzt (falls das Bild hängt)."),

        // ---------- Design
        ["AKZENTFARBE VON WINDOWS"] = new("Farbe für Startmenü, Taskleiste, Explorer und Fensterrahmen.",
            "Blau zum blauen Hintergrundbild", "„Automatisch“ = Farbe passend zum Hintergrundbild"),
        ["HELL / DUNKEL & EFFEKTE"] = new("Dunkler oder heller Modus, Transparenz, Akzentfarbe auf Taskleiste und Titelleisten."),
        ["THEMES – EIN KLICK: FARBE, HELL/DUNKEL UND 4K-HINTERGRUND"] = new("Fertige Kombinationen aus Farbe, Modus und Hintergrund – ein Klick ändert alles."),
        ["AKTUELLES HINTERGRUNDBILD"] = new("Dein jetziger Desktop-Hintergrund."),
        ["DIASHOW"] = new("Wechselt das Hintergrundbild automatisch aus einem Ordner.", "Alle 30 Minuten ein neues Bild aus „Bilder\\Wallpaper“"),
        ["VERLAUF-GENERATOR (4K)"] = new("Erstellt einen schlichten Farbverlauf als 4K-Hintergrund.", "Dunkelblau → Lila passt zu RGB in Lila"),

        // ---------- Kacheln
        ["DEINE KACHELN"] = new("Widgets direkt auf dem Desktop. Ziehen zum Verschieben, Rechtsklick für Optionen.",
            "Uhr + Systemwerte + Sicherheit"),
        ["KACHEL HINZUFÜGEN"] = new("Neue Kachel auswählen.", "Systemwerte: CPU/GPU-Temperatur auf einen Blick", "Notizen, Countdown, Fotos …"),
        ["NEUE KACHEL"] = new("Neue Kachel auswählen.", "Systemwerte: CPU/GPU-Temperatur auf einen Blick"),
        ["KACHEL BEARBEITEN"] = new("Größe, Titel, Werte und Farbe der gewählten Kachel. Werte mit ▲▼ sortieren – oben steht zuerst.",
            "Systemwerte: GPU-Temperatur nach oben schieben"),
        ["AUSSEHEN"] = new("Stil aller Kacheln.", "Glas: halb durchsichtig", "Akzent: in deiner App-Farbe"),
        ["SCHNELLZUGRIFF"] = new("Häufige Aktionen mit einem Klick."),
        ["STEUERUNG"] = new("Kacheln ein-/ausblenden, sperren, neu anordnen."),

        // ---------- Treiber / Problemlöser / Einstellungen
        ["ERKANNTE HARDWARE"] = new("Welche Grafikkarte erkannt wurde und welcher Treiber installiert ist."),
        ["WEITERE LINKS"] = new("Offizielle Hersteller-Seiten, falls du lieber manuell lädst."),
        ["VOM PROBLEMLÖSER DEAKTIVIERTE DIENSTE"] = new("Dienste, die du über den Problemlöser abgeschaltet hast – hier wieder einschaltbar."),
        ["AKZENTFARBE DER APP"] = new("Farbe der App selbst (Knöpfe, Diagramme, Overlay)."),
        ["START & SCHLIESSEN"] = new("Mit Windows starten und ob X die App beendet oder nur ins Symbol neben der Uhr legt.",
            "Autostart an → Lüfter und RGB laufen gleich nach dem Hochfahren"),
        ["VERKNÜPFUNGEN"] = new("Desktop- und Startmenü-Symbol der App."),
        ["ANZEIGE"] = new("Einheiten und Messquelle.", "°F statt °C"),
        ["ORDNER"] = new("Wo Logs und Einstellungen liegen – Logs brauchst du, wenn etwas nicht geht."),
        ["ALTE EINZEL-APPS"] = new("Deine früheren Einzel-Apps sollten nicht gleichzeitig laufen – sonst steuern zwei Programme dieselbe Hardware."),
        ["ÜBER"] = new("Version und verwendete Bibliotheken."),
    };

    /// <summary>Erklärungen für einzelne Einstellungen (Beschriftung bzw. Schalter-Text).</summary>
    public static readonly Dictionary<string, Info> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------- Tweaker: Prozessor
        ["Vorschläge"] = new("Fertige Einstellungen für den Prozessor – ein Klick setzt alle Regler darunter. Danach „Anwenden“.",
            "Windows-Standard: alles wie ab Werk", "Leise & kühl: Takt etwas begrenzt – ideal zum Arbeiten, Lüfter bleiben ruhig",
            "Effizient: fast volle Leistung, deutlich weniger Strom und Hitze", "Leistung: maximaler Takt, Kerne schlafen nie – fürs Zocken"),
        ["Eigenen Energieplan verwenden"] = new("Die App legt einen eigenen Windows-Energieplan an und stellt dort die Werte unten ein. Dein normaler Plan bleibt unangetastet und kommt mit „Werkseinstellung“ zurück.",
            "Aus = Windows-Energieplan wie bisher, die Regler wirken dann nicht"),
        ["Turbo-Modus"] = new("Wie stark die CPU über ihren Basistakt hochtaktet (Turbo Boost / Precision Boost).",
            "Turbo aus: z. B. 2,5 statt 4,6 GHz – sehr kühl und leise, aber deutlich langsamer", "Normal: wie Windows es macht",
            "Aggressiv: taktet sofort und dauerhaft hoch – max. FPS, etwas wärmer", "Effizient: hoch nur wenn wirklich nötig – guter Kompromiss"),
        ["Max. Takt"] = new("Obergrenze für den CPU-Takt. Weniger Takt braucht weniger Spannung – das wirkt wie ein Undervolt: kühler und leiser.",
            "100 % = keine Grenze", "z. B. 4,0 GHz statt 4,6 GHz: kaum langsamer im Alltag, 10–15 °C kühler unter Last"),
        ["Min. Leistung"] = new("Wie weit die CPU im Leerlauf heruntertakten darf (in % des Maximums).",
            "5 % = spart Strom im Leerlauf (Standard)", "100 % = immer voller Takt – minimal schnellere Reaktion, aber wärmer und mehr Strom"),
        ["Max. Leistung"] = new("Höchste erlaubte Leistung in %. Unter 100 % schaltet bei vielen CPUs den Turbo komplett ab.",
            "99 % = Turbo aus → sehr kühl (gut für Laptops/leise Büro-PCs)", "100 % = volle Leistung"),
        ["Energie-Präferenz"] = new("Sagt der CPU, ob sie eher auf Leistung oder auf Sparen achten soll (Intel Speed Shift / AMD CPPC, kurz EPP).",
            "0 = immer volle Leistung (beste Reaktionszeit)", "33 = ausgewogen", "80+ = stark sparen (Laptop im Akkubetrieb)"),
        ["Kern-Parking"] = new("Mindestanteil der Kerne, die wach bleiben. Geparkte Kerne sparen Strom, brauchen aber kurz zum Aufwachen.",
            "100 % = kein Parken – weniger Mikroruckler in Spielen", "10 % = Windows darf fast alle Kerne schlafen legen (Stromsparen)"),
        ["Echten Turbo-Takt messen"] = new("Belastet einen Kern 5 Sekunden und misst den höchsten Takt – so siehst du, ob der Turbo wirklich greift.",
            "i5-13400F: ca. 4,6 GHz erwartet", "Deutlich darunter → Turbo-Modus/Energieplan prüfen oder CPU wird zu heiß"),

        // ---------- Tweaker: Grafikkarte
        ["Vorschläge (auf diese Karte abgestimmt)"] = new("Fertige Werte für deine Grafikkarte – sicher gewählt. Danach „Anwenden“; 20 Sekunden zum Bestätigen, sonst Rücksetzung.",
            "Werkseinstellung: alles ab Werk", "Undervolt: gleiche FPS, 5–10 °C kühler und leiser",
            "Leichtes OC: ca. +3–5 % FPS, sehr sicher", "Maximales OC: mehr FPS, unbedingt mit Stabilitätstest prüfen"),
        ["Leistungslimit"] = new("Wie viel Strom die Grafikkarte maximal ziehen darf (in % vom Standard).",
            "80 %: oft nur 3–5 % weniger FPS, aber deutlich kühler und leiser", "Höher als 100 % geht nur, wenn die Karte es erlaubt – bringt Spielraum fürs Übertakten"),
        ["Kern-Takt"] = new("Takt-Versatz für den Grafikchip in MHz (Übertakten). Wirkt auf alle Boost-Stufen.",
            "+100 MHz: meistens problemlos, ein paar % mehr FPS", "Bildfehler, Abstürze oder Treiber-Neustart → Wert verringern"),
        ["Speicher-Takt"] = new("Takt-Versatz für den Grafikspeicher (VRAM) in MHz. Bringt vor allem bei hohen Auflösungen etwas.",
            "+500 MHz: bei RTX 40 meist sicher", "Bunte Pixel/Artefakte → Wert verringern"),
        ["Takt-Obergrenze"] = new("Begrenzt den höchsten Boost-Takt (NVIDIA). Weil hohe Takte viel Spannung brauchen, wirkt das wie ein Undervolt.",
            "Ganz links = aus (keine Grenze)", "z. B. 2600 MHz bei einer RTX 4060: kaum weniger FPS, deutlich kühler"),
        ["Spannung (Undervolt)"] = new("Spannungs-Versatz in mV (AMD RX 7000/9000). Weniger Spannung = weniger Hitze bei gleicher Leistung.",
            "−50 mV: meist stabil", "−100 mV oder mehr: unbedingt Stabilitätstest machen"),
        ["Werte beim Windows-Start anwenden (mit Absturz-Schutz)"] = new("Setzt deine Werte automatisch nach jedem Hochfahren. Stürzt Windows danach ab, wendet die App sie beim nächsten Start NICHT nochmal an.",
            "Erst ein paar Tage testen, dann einschalten"),
    };

    private static readonly DependencyProperty AttachedProp = DependencyProperty.RegisterAttached("InfoAttached", typeof(bool), typeof(InfoTips));

    /// <summary>Allen Kicker-Überschriften unterhalb von root ein „i“ geben.</summary>
    public static void Attach(DependencyObject root)
    {
        if (Application.Current?.TryFindResource("Kicker") is not Style kicker) return;
        foreach (var tb in Descendants(root).OfType<TextBlock>())
        {
            if ((bool)tb.GetValue(AttachedProp)) continue;
            var key = tb.Text?.Trim() ?? "";
            // Überschriften (Kicker) und einzelne Einstellungen (Beschriftung oder Schalter-Text)
            Info? info = null;
            if (tb.Style == kicker) Texts.TryGetValue(key, out info);
            else if (Settings.TryGetValue(key, out var si))
            {
                // Text in einem Knopf: „i“ rechts neben den Knopf (nur bei Knöpfen, die nicht die ganze Breite füllen)
                if (FindButton(tb) is { } btn)
                {
                    if (btn.HorizontalAlignment == HorizontalAlignment.Stretch || (bool)btn.GetValue(AttachedProp)) continue;
                    var bl = AdornerLayer.GetAdornerLayer(btn);
                    if (bl == null) continue;
                    btn.SetValue(AttachedProp, true);
                    tb.SetValue(AttachedProp, true);
                    bl.Add(new InfoAdorner(btn, key, si, () => btn.ActualWidth));
                    continue;
                }
                info = si;
            }
            if (info == null) continue;
            var layer = AdornerLayer.GetAdornerLayer(tb);
            if (layer == null) continue;
            tb.SetValue(AttachedProp, true);
            var ad = new InfoAdorner(tb, key, info, null);
            layer.Add(ad);
        }
    }

    /// <summary>Text in einem normalen Knopf? Dort kein „i“ (würde über den Knopf hinausragen).</summary>
    private static ButtonBase? FindButton(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is ToggleButton) return null; // Schalter/Häkchen: „i“ direkt hinter den Text
            if (p is ButtonBase b) return b;
            if (p is UserControl) break;
        }
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        int n;
        try { n = VisualTreeHelper.GetChildrenCount(d); } catch { yield break; }
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    private static string Pretty(string kicker) =>
        CultureInfo.GetCultureInfo("de-DE").TextInfo.ToTitleCase(kicker.ToLowerInvariant());

    /// <summary>Erklärung als kleines Fenster unter dem „i“.</summary>
    public static void Show(UIElement target, string key, Info info)
    {
        var sp = new StackPanel { MaxWidth = 380 };
        sp.Children.Add(new TextBlock { Text = Pretty(key), FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = info.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(225, 230, 238)) });
        if (info.Examples.Length > 0)
        {
            sp.Children.Add(new TextBlock { Text = info.Examples.Length == 1 ? "BEISPIEL" : "BEISPIELE", FontSize = 10.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 3),
                                            Foreground = (Brush)Application.Current.Resources["AccentBrush"] });
            foreach (var ex in info.Examples)
                sp.Children.Add(new TextBlock { Text = "•  " + ex, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(190, 198, 210)) });
        }
        var popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 26, 34)), BorderBrush = (Brush)Application.Current.Resources["AccentDimBrush"],
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 4, 8, 8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.5 },
                Child = sp
            }
        };
        popup.IsOpen = true;
    }
}

/// <summary>Das kleine runde „i“ rechts neben einer Überschrift.</summary>
internal sealed class InfoAdorner : Adorner
{
    private readonly VisualCollection _visuals;
    private readonly Border _btn;
    private readonly FrameworkElement _el;
    private readonly Func<double>? _x;

    public InfoAdorner(FrameworkElement adorned, string key, InfoTips.Info info, Func<double>? x) : base(adorned)
    {
        _el = adorned;
        _x = x;
        _btn = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Background = Brushes.Transparent,
            ToolTip = "Was ist das? (mit Beispielen)",
            Child = new TextBlock { Text = "i", FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) }
        };
        _btn.SetResourceReference(Border.BorderBrushProperty, "AccentDimBrush");
        ((TextBlock)_btn.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _btn.MouseEnter += (_, _) => _btn.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        _btn.MouseLeave += (_, _) => _btn.Background = Brushes.Transparent;
        _btn.MouseLeftButtonUp += (_, e) => { InfoTips.Show(_btn, key, info); e.Handled = true; };
        _visuals = new VisualCollection(this) { _btn };
        Visibility = adorned.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        adorned.IsVisibleChanged += (_, _) => Visibility = adorned.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint) { _btn.Measure(new Size(15, 15)); return AdornedElement.RenderSize; }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double w, h = _el.ActualHeight;
        if (_x != null) w = _x();
        else if (_el is TextBlock tb)
        {
            // Breite des Texts (nicht der ganzen Zeile) → „i“ direkt hinter den Text
            try
            {
                var ft = new FormattedText(tb.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White, VisualTreeHelper.GetDpi(tb).PixelsPerDip);
                w = ft.WidthIncludingTrailingWhitespace + (tb.Text?.Length ?? 0) * 0.6; // Kicker haben etwas Zeichenabstand
            }
            catch { w = tb.ActualWidth; }
            w = Math.Min(w, Math.Max(0, tb.ActualWidth + 40)) + tb.Padding.Left;
        }
        else w = _el.ActualWidth;
        _btn.Arrange(new Rect(w + 6, Math.Max(0, (h - 15) / 2), 15, 15));
        return finalSize;
    }
}
