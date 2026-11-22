<div align="center">

# 🎛️ Control Center

**Alles für deinen Gaming-PC in einer App** – Lüfter, RGB, FPS-Boost, Overlay, Monitoring, Treiber, Updates und mehr.

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![Version](https://img.shields.io/badge/dynamic/regex?url=https%3A%2F%2Fraw.githubusercontent.com%2FNickderMeister69%2FControlCenter%2Fmain%2Fversion.txt&search=(.%2B)&replace=v%241&label=Version&color=d000ff)

[**⬇️ Download**](https://github.com/NickderMeister69/ControlCenter/raw/main/ControlCenter.exe) · [Funktionen](#-funktionen) · [Installation](#-installation) · [Updates](#-updates) · [FAQ](#-faq)

</div>

---

## ✨ Funktionen

| | Bereich | Was es kann |
|---|---|---|
| 📊 | **Dashboard** | Temperaturen, Lüfter, Beleuchtung, Stromverbrauch & Kosten und Schnellaktionen auf einen Blick |
| 📈 | **Monitor & Overlay** | Live-Werte aller Sensoren mit Verlauf, FPS-Overlay im Spiel (auf Wunsch nur in Spielen), Aufzeichnung als CSV |
| 🚀 | **FPS-Boost** | 1-Klick-Boost für Windows, eigenes Profil pro Spiel (Steam, Epic, Riot, jede exe), Auto-Boost beim Spielstart, RAM freigeben |
| 🦖 | **Unreal-Engine-Feineinstellungen** | Jede Grafik-Kategorie einzeln, Profi-Einstellungen (z. B. für ARK: Survival Ascended) und alle Befehle mit einem Klick für die Ingame-Konsole kopieren |
| 🎯 | **Zielen & Maus** | Input-Lag-Check, Empfindlichkeit zwischen Spielen umrechnen, Maus-Abtastrate messen |
| 🌀 | **Lüfter** | Jeder Lüfter einzeln: Minimum, Temperaturkurve, fest oder BIOS – mit Sicherheitsmodus |
| 🌈 | **Beleuchtung** | RGB für Mainboard, Lüfter-Hubs und Razer – ohne Zusatzprogramm, Farbabgleich pro Gerät, eigene Farbe beim Zocken |
| ⚙️ | **Tweaker** | CPU und Grafikkarte feinjustieren (Intel, AMD, NVIDIA) mit Sicherheitsrückfrage |
| 🔥 | **Stresstest** | CPU, Grafikkarte und Arbeitsspeicher auf Stabilität prüfen |
| 🧰 | **Werkzeuge** | Programme & Steam-Spiele aktualisieren, Downloads aufräumen, „Was kann weg?“ für volle Laufwerke, automatisch ausschalten, globale Tastenkürzel |
| 🛡️ | **VPN & Werbeblocker** | Werbung und Tracker für den ganzen PC blocken, VPN (WireGuard) mit einem Klick verbinden |
| 🩺 | **Problemlöser** | Findet Programme, die sich in die Quere kommen (RGB-Software, Wallpaper Engine …), und behebt sie per Knopfdruck |
| 🖥️ | **Treiber** | Grafiktreiber (NVIDIA, AMD, Intel) und alle anderen Treiber prüfen und installieren |
| 🎨 | **Design & Kacheln** | Akzentfarbe, Hell/Dunkel, Hintergrund, Live-Widgets auf dem Desktop |
| 🚦 | **Autostart** | Zeigt bei jedem Programm, ob es aus kann oder an bleiben sollte |

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
<summary><b>Der Werbeblocker blockt YouTube-Werbung nicht</b></summary>

YouTube-Werbung kommt vom selben Server wie die Videos – das kann kein DNS-Filter blocken. Dafür im Browser einen Werbeblocker nutzen (Opera GX: eingebaut, sonst uBlock Origin).
</details>

<details>
<summary><b>Kann ich alles rückgängig machen?</b></summary>

Ja. Boost- und Design-Änderungen werden vorher gesichert – *Alles zurücksetzen* stellt den Originalzustand her. GPU-Übertaktung setzt sich nach 20 Sekunden automatisch zurück, wenn du nicht „Behalten“ klickst.
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
