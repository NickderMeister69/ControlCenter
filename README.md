<div align="center">

# 🎛️ Control Center

**Alles für deinen Gaming-PC in einer App** – Lüfter, RGB, FPS-Boost, Overlay, Monitoring, Treiber, Updates, ARK-Begleiter mit KI und mehr.

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![Version](https://img.shields.io/badge/dynamic/regex?url=https%3A%2F%2Fraw.githubusercontent.com%2FNickderMeister69%2FControlCenter%2Fmain%2Fversion.txt&search=(.%2B)&replace=v%241&label=Version&color=d000ff)

[**⬇️ Download**](https://github.com/NickderMeister69/ControlCenter/raw/main/ControlCenter.exe) · [Funktionen](#-funktionen) · [ARK & KI](#-ark-begleiter--ki) · [Dateifreigabe](#-dateifreigabe-download-center) · [Installation](#-installation) · [Updates](#-updates) · [FAQ](#-faq)

</div>

---

## ✨ Funktionen

| | Bereich | Was es kann |
|---|---|---|
| 📊 | **Dashboard** | Temperaturen, Lüfter, Beleuchtung, Stromverbrauch & Kosten und Schnellaktionen auf einen Blick |
| 📈 | **Monitor & Overlay** | Live-Werte aller Sensoren mit Verlauf, FPS-Overlay im Spiel (auf Wunsch nur in Spielen), Aufzeichnung als CSV |
| 🚀 | **FPS-Boost** | 1-Klick-Boost für Windows, eigenes Profil pro Spiel (Steam, Epic, Riot, jede exe), Auto-Boost beim Spielstart, RAM freigeben |
| 🦖 | **Unreal-Engine-Feineinstellungen** | Jede Grafik-Kategorie einzeln, Profi-Einstellungen (z. B. für ARK: Survival Ascended) und alle Befehle mit einem Klick für die Ingame-Konsole kopieren |
| 🗺️ | **ARK-Begleiter** | Interaktive Karte jeder ASA-Map, Höhlen & Artefakte, Bosse, Ressourcen, Wiki – und die Karte als Overlay im Spiel |
| 🤖 | **KI-Assistent** | Fragen zu ARK auf Deutsch, die KI sucht selbst im Wiki und zeigt die Quellen. Läuft lokal (Ollama) oder über die KI eines Freundes |
| 🎯 | **Zielen & Maus** | Input-Lag-Check, Empfindlichkeit zwischen Spielen umrechnen, Maus-Abtastrate messen |
| 🌀 | **Lüfter** | Jeder Lüfter einzeln: Minimum, Temperaturkurve, fest oder BIOS – mit Sicherheitsmodus |
| 🌈 | **Beleuchtung** | RGB für Mainboard, Lüfter-Hubs und Razer – ohne Zusatzprogramm, Farbabgleich pro Gerät, eigene Farbe beim Zocken |
| ⚙️ | **Tweaker** | CPU und Grafikkarte feinjustieren (Intel, AMD, NVIDIA) mit Sicherheitsrückfrage |
| 🔥 | **Stresstest** | CPU, Grafikkarte und Arbeitsspeicher auf Stabilität prüfen |
| 🧰 | **Werkzeuge** | Programme & Steam-Spiele aktualisieren, Downloads aufräumen, „Was kann weg?“ für volle Laufwerke, automatisch ausschalten, globale Tastenkürzel |
| 🛡️ | **VPN & Werbeblocker** | Werbung und Tracker für den ganzen PC blocken, VPN (WireGuard) mit einem Klick verbinden |
| 📤 | **Dateifreigabe** | Eigener File Share für Freunde: Dateien und Ordner bis 100 GB direkt in der App hoch- und herunterladen – Freunde verbinden sich per Anfrage, ohne Code. Mit Tempo-Anzeige, fortsetzbar |
| 🩺 | **Problemlöser** | Findet Programme, die sich in die Quere kommen (RGB-Software, Wallpaper Engine …), und behebt sie per Knopfdruck |
| 🖥️ | **Treiber** | Grafiktreiber (NVIDIA, AMD, Intel) und alle anderen Treiber prüfen und installieren |
| 🎨 | **Design & Kacheln** | Akzentfarbe, Hell/Dunkel, Hintergrund, Live-Widgets auf dem Desktop |
| 🚦 | **Autostart** | Zeigt bei jedem Programm, ob es aus kann oder an bleiben sollte |

---

## 🗺️ ARK-Begleiter & KI

**Karte & Infos** – Seite *ARK*: Map oben auswählen (Astraeos, The Island, Scorched Earth, The Center, Aberration, Extinction, Ragnarok, Valguero, Lost Colony). Eigene Markierungen (Basis, Tames …) anlegen, Höhlen/Bosse/Ressourcen nachschlagen, Wiki direkt in der App – Werbung wird ausgeblendet.

**Karte im Spiel** – ARK auf *Vollbild-Fenster* stellen, dann:

| Taste | Funktion |
|---|---|
| `Strg + Alt + A` | Karte über dem Spiel ein/aus |
| `Strg + Alt + J` | Karte durchklickbar ein/aus (Klicks gehen ans Spiel) |

Position, Größe, Bildschirm, Durchsichtigkeit und Zoom stellst du im Tab *Karte im Spiel* ein (Schnellwahl: Minimap, Seitenleiste, groß). Die Karte ist ein normales Fenster – **es wird nichts aus dem Spiel gelesen oder eingeschleust** (BattlEye-sicher). Mit dem Schalter *Spiel-Anbindung* lässt sich alles abschalten.

**KI-Assistent** – Tab *KI-Assistent*, zwei Möglichkeiten:

- **Eigene KI:** [Ollama](https://ollama.com/download) installieren, in der App „Empfohlenes Modell laden“ (ca. 5 GB). Läuft komplett auf deinem PC, startet von selbst bei der ersten Frage und geht nach 10 Minuten ohne Frage in Standby (Grafikkarte & RAM wieder frei).
- **KI eines Freundes:** *KI eines Freundes* wählen, den **Einladungs-Code** (`CCKI-…`) einfügen, *Verbinden* – fertig, dauerhaft. Kein Ollama, keine starke Grafikkarte nötig. Dort steht dann „KI läuft auf dem PC von …“.

<details>
<summary><b>Eigene KI für Freunde freigeben</b></summary>

*KI-Assistent → Meine KI für Freunde freigeben* einschalten, Namen eintragen, Einladungs-Code kopieren und verschicken.

- Verbindung übers Internet per **Cloudflare-Tunnel** (https, keine Router-Einstellung, deine IP bleibt verborgen). Die App lädt ihn beim ersten Mal selbst herunter.
- Die aktuelle Tunnel-Adresse steht in [`ki-adresse.txt`](ki-adresse.txt) – die Apps der Freunde lesen sie dort automatisch, der Code bleibt also gültig.
- Ohne den Schlüssel aus dem Code kommt niemand rein. Freunde können nur Fragen stellen – keine Modelle löschen, nichts ausschalten. *Neuen Code erzeugen* sperrt alle alten.
- Die KI des Control Centers läuft als **eigene Ollama-Instanz** (Port 11436) – getrennt von einem Ollama, das du für andere Projekte nutzt.
</details>

---

## 📤 Dateifreigabe (Download Center)

Dein eigener File Share direkt im Control Center – deine Freunde laden Dateien und ganze Ordner bei dir hoch und runter, ohne Browser und ohne Cloud-Abo.

**Einrichten (einmal):** Seite *Dateifreigabe → Meine Freigabe* → unten zwei Passwörter festlegen (eins für Freunde im Browser, eins nur für dich als Admin) → *Speichern*. Unter *Freunde mit Zugang* noch eintragen, wie du bei deinen Freunden heißt.

**So verbinden sich deine Freunde – ohne Code:**
1. Freund öffnet in seinem Control Center *Dateifreigabe → Bei einem Freund*. Dein Download Center wird automatisch gefunden.
2. Er klickt auf **Verbinden**.
3. Du bekommst eine Windows-Benachrichtigung „… möchte sich verbinden“ → auf der Seite **Erlauben** (oder *Ablehnen*).
4. Fertig – er ist dauerhaft verbunden, auch nach Neustarts. Unter *Freunde mit Zugang* kannst du jeden jederzeit wieder entfernen.

**Was man damit machen kann (bei dir und bei deinen Freunden):**

| | |
|---|---|
| ⬆️ **Hochladen ▾** | Dateien oder ganze Ordner (Ordner werden als `.zip` mit allen Unterordnern geteilt) – oder einfach ins Fenster ziehen. Kategorie frei eintippen oder auswählen. |
| ⬇️ **Herunterladen** | In den eigenen Download-Ordner (einstellbar). |
| 📊 **Live-Anzeige** | Tempo in **MB/s**, **„3,2 GB von 40,0 GB“**, Prozent und Restzeit. Fertige Übertragungen verschwinden nach ein paar Sekunden von selbst. |
| 🔁 **Fortsetzen** | Bricht die Verbindung ab, geht es an der Stelle weiter. Pausierte Uploads: dasselbe nochmal hochladen. |
| 🗑️ **Löschen** | Du: jede Datei einzeln oder *Alles löschen*. Freunde: nur ihre eigenen Uploads (einzeln oder *Alles löschen*). |

- **Große Dateien:** bis 100 GB pro Datei (einstellbar). Übertragen wird in 64-MB-Teilen direkt auf die Platte – braucht kaum Arbeitsspeicher.
- **Tempo:** Wenn Freunde herunterladen, schickt dein PC die Dateien – es zählt also **dein Upload** (bzw. ihr Download, falls der langsamer ist). Die Seite rechnet mit deinem letzten Speedtest aus, wie lange 1/10/40/100 GB dauern.
- **Verbindung:** übers Internet per **Cloudflare-Tunnel** (https, keine Router-Einstellung, deine IP bleibt verborgen), über Tailscale oder im Heimnetz. Die wechselnde Internet-Adresse veröffentlicht die App in [`datei-adresse.txt`](datei-adresse.txt) – daran finden die Apps deiner Freunde dich (die Commits „Datei-Adresse aktualisiert“ macht die App selbst).
- **Speicherort:** standardmäßig `Dokumente\Download Center Dateien` (einstellbar). Was Freunde hochladen, belegt Platz bei dir.
- **Updates warten**, bis gerade nichts hoch- oder heruntergeladen wird – es bricht nichts mittendrin ab.

<details>
<summary><b>Alternativen: Einladungs-Code und Browser</b></summary>

- **Einladungs-Code** (`CCDC-…`): unter *Meine Freigabe → Alternativ: Einladungs-Code* kopieren, der Freund fügt ihn bei *Bei einem Freund → Stattdessen einen Einladungs-Code eingeben* ein. Enthält das Passwort – nur an Freunde schicken.
- **Freunde ohne Control Center:** Schalter *Auch im Browser erreichbar* einschalten und den Link schicken – dort mit Namen und Passwort anmelden. Hoch- und Herunterladen geht auch im Browser.
- Die alte eigenständige *FileShareApp* wird beim Start automatisch beendet, aus dem Autostart genommen und gelöscht – sie würde sonst denselben Port belegen. Vorhandene Dateien werden übernommen.
</details>

---

## 📥 Installation

1. **[`ControlCenter.exe` herunterladen](https://github.com/NickderMeister69/ControlCenter/raw/main/ControlCenter.exe)** – mehr brauchst du nicht.
2. In einen eigenen Ordner legen, z. B. `C:\Programme\ControlCenter` oder auf den Desktop.
3. Doppelklicken. Die App fragt nach **Admin-Rechten** – die braucht sie für Lüfter, Sensoren und Tweaks.
4. Fertig. Die App legt Verknüpfungen an und startet auf Wunsch mit Windows.

> **Windows SmartScreen** meldet sich evtl. beim ersten Start („Unbekannter Herausgeber“), weil die App nicht signiert ist: **Weitere Informationen → Trotzdem ausführen**.

**Voraussetzungen:** Windows 10 oder 11 (64 Bit). Nichts weiter installieren – .NET ist schon eingebaut.
Optional: [HWiNFO](https://www.hwinfo.com/download/) für noch genauere Temperaturen, [PawnIO](https://pawnio.eu) für die Lüftersteuerung (die App sagt dir Bescheid, wenn etwas fehlt).

---

## 🔄 Updates

Die App sucht selbst nach neuen Versionen. Gibt es eine, erscheint unten links:

> **⬇️ Neue Version v2.x verfügbar**

Ein Klick – sie lädt die neue Version, startet kurz neu, fertig. **Alle Einstellungen bleiben erhalten.**
Manuell prüfen: *Einstellungen → App-Version & Updates → Nach Updates suchen*.

> Hast du eine ganz alte Version ohne Update-Knopf? Einfach die App beenden (Rechtsklick aufs Symbol unten rechts → Beenden) und die `ControlCenter.exe` durch die neue ersetzen.

---

## 💾 Einstellungen & Sicherungen

- Alle Einstellungen liegen in `%AppData%\NickControlCenter` – **nicht** im App-Ordner. Ein Update oder Austausch der exe ändert daran nichts.
- ⚠️ **Diesen Ordner nicht löschen** (auch nicht beim Aufräumen von AppData) – sonst sind alle Einstellungen *und* die Sicherungen weg. Aus Versehen gelöscht? Im Papierkorb nachsehen und wiederherstellen (App vorher beenden).
- Automatische Sicherung **einmal am Tag** und **vor jedem Update** (die letzten 15 Stände).
  Zurückholen unter *Einstellungen → Einstellungen sichern*.

---

## ❓ FAQ

<details>
<summary><b>Die Beleuchtung flackert oder zeigt falsche Farben</b></summary>

Meist läuft noch ein zweites RGB-Programm (Armoury Crate, iCUE, RGB Fusion / Gigabyte Control Center, Wallpaper Engine …). Der **Problemlöser** zeigt das an und kann es beenden, aus dem Autostart nehmen oder deinstallieren. Bei Gigabyte-Mainboards zusätzlich im BIOS den „GIGABYTE Utilities Downloader“ ausschalten – sonst installiert das BIOS die Software immer wieder neu.
</details>

<details>
<summary><b>Ist das Overlay / die FPS-Messung Anti-Cheat-sicher?</b></summary>

Ja. Die FPS werden über eine offizielle Windows-Schnittstelle (ETW) gemessen – es wird nichts in Spiele eingeschleust. Es gibt in dieser App keine Cheats.
</details>

<details>
<summary><b>Die KI findet nichts / antwortet falsch</b></summary>

Die KI sucht zu jeder Frage passende Seiten im [ARK-Wiki](https://ark.wiki.gg) und zeigt sie unter der Antwort als Quellen. KI kann sich trotzdem irren – wichtige Dinge über die Quellen-Links prüfen. Frag möglichst konkret („Welcher Dino farmt am besten Metall?“ statt „Metall?“).
</details>

<details>
<summary><b>Der Werbeblocker blockt YouTube-Werbung nicht</b></summary>

YouTube-Werbung kommt vom selben Server wie die Videos – das kann kein DNS-Filter blocken. Dafür im Browser einen Werbeblocker nutzen (Opera GX: eingebaut, sonst uBlock Origin).
</details>

<details>
<summary><b>Kann ich alles rückgängig machen?</b></summary>

Ja. Boost- und Design-Änderungen werden vorher gesichert – *Alles zurücksetzen* stellt den Originalzustand her. GPU-Übertaktung setzt sich nach 20 Sekunden automatisch zurück, wenn du nicht „Behalten“ klickst.
</details>

<details>
<summary><b>Sehen meine Freunde meine Einstellungen oder Dateien, wenn ich auf GitHub pushe?</b></summary>

Nein. Auf GitHub liegt nur das Programm. Deine Einstellungen (Passwörter, Freunde mit Zugang, Lüfter, RGB …) liegen in `%AppData%\NickControlCenter`, deine geteilten Dateien in `Dokumente\Download Center Dateien` – beides ist nicht im Repo. Freunde bekommen deine Dateien nur über die Verbindung zur Dateifreigabe, und nur wenn du sie erlaubt hast.
</details>

<details>
<summary><b>Wozu ist der Port 8080 in der Dateifreigabe?</b></summary>

Darüber reden die Apps miteinander (wie bei Steam oder Discord) – du musst nichts damit machen. Nur ändern, wenn ein anderes Programm ihn schon benutzt.
</details>

<details>
<summary><b>Eine Seite zeigt etwas Veraltetes an</b></summary>

Oben rechts auf **⟳ Neu laden** klicken (oder **F5** / **Strg + R**) – die Seite wird neu aufgebaut, laufende Übertragungen laufen weiter.
</details>

<details>
<summary><b>Etwas funktioniert nicht – was soll ich schicken?</b></summary>

Den Ordner **`Logs`** neben der exe. Dort protokolliert die App alles, was sie tut.
</details>

---

## 🛠️ Selbst bauen

Der Quellcode liegt im Ordner [`Quellcode`](Quellcode) (.NET 8, WPF).

```bash
dotnet publish Quellcode/src/NickControlCenter -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

<div align="center">

Gemacht von **[NickderMeister69](https://github.com/NickderMeister69)** · Nutzung auf eigene Verantwortung – vor allem beim Übertakten und bei der Lüftersteuerung

</div>
