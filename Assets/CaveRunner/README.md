# CAVE-Runner – Demo-Umgebung

Grobe Test-Szene für die Spielwelt. Alles wird **per Code** gebaut (Würfel, Lichter, 3D-Text) –
es gibt noch keine Modelle oder Prefabs. Die können später Stück für Stück ersetzt werden.

## Starten

### Build and Run (Windows)

1. Projekt in Unity 2022.3.59f1 öffnen. Beim ersten Öffnen wird automatisch
   `Assets/CaveRunner/Scenes/CaveRunnerDemo.unity` erstellt und als **erste Szene** in die Build Settings eingetragen
   (siehe Console: „CaveRunner: …“).
2. **File → Build Settings…** → Plattform **Windows, Mac, Linux** wählen, Target Platform **Windows**
   (falls nicht aktiv: „Switch Platform“). Dafür muss im Unity Hub das Modul *Windows Build Support* installiert sein.
3. **File → Build And Run** (Strg+B) oder Menü **CaveRunner → Build and Run**.
   Das Spiel landet in `Builds/Windows/CaveRunnerDemo.exe` und startet direkt. **Esc** beendet das Spiel.

### Im Editor testen

**CaveRunner → Demo-Szene öffnen**, dann **Play**.
(„Demo-Szene mit CAVE-Prefab erstellen“ baut zusätzlich das CAVE-Setup aus dem Sample ein.)

Alternativ: leeres GameObject in irgendeine Szene ziehen und `CaveRunnerGame` draufziehen.

## Tasten (Test am PC)

| Taste | Aktion |
|---|---|
| Enter / Leertaste | Start, nach Ende: R = Neustart |
| ← / A | Spur links |
| → / D | Spur rechts |
| Leertaste / ↑ / W | Springen |
| ↓ / S | Ducken |
| Enter (während Lauf) | Wort „richtig gelesen“ (ersetzt Mikrofon) |
| Backspace | Wort falsch |

## Aufbau

| Datei | Inhalt |
|---|---|
| `Scripts/CaveRunnerGame.cs` | Ablauf: Start → 3,2,1 → 90 s → Gewonnen/Verloren, Wort-Tafeln, Anzeige |
| `Scripts/CaveRunnerWorld.cs` | **Umgebung**: Tunnel mit 3 Spuren, Segmente, Licht, Nebel, Hindernisse, Kollision |
| `Scripts/RunnerPlayer.cs` | Spielfigur: Spurwechsel, Sprung, Ducken |
| `Scripts/WordBoard.cs` | Eine Wort-Tafel, die auf einen zukommt |
| `Scripts/RunnerUtil.cs` | Hilfsfunktionen (Würfel, Materialien, Text) |
| `Editor/CaveRunnerSceneMenu.cs` | Menüpunkt zum Erzeugen der Demo-Szene |

**Prinzip für die CAVE:** Die Person steht still bei z = 0, die Welt bewegt sich Richtung −z auf sie zu
(Laufband). Tunnel-Segmente werden hinten wieder vorne angehängt.

Hindernisse:
- **Kiste** → Spur wechseln
- **niedriger Balken** → springen
- **Stange oben** → ducken

Die **Lesegrenze** (orange Linie bei z = 12) bewegt sich nicht. Ist eine Tafel dort und das Wort
wurde nicht gelesen, zählt ein Wortfehler. Beim 3. Fehler ist die Runde verloren.

## Schnittstellen für das Team

**Tracking (Person 1)** – statt Tastatur einfach aufrufen:

```csharp
var game = FindObjectOfType<CaveRunner.CaveRunnerGame>();
game.Player.MoveLeft();   // Körper links neigen
game.Player.MoveRight();  // Körper rechts neigen
game.Player.Jump();       // Kniebeuge + Aufrichten
game.Player.Duck();       // Ducken
```

(`keyboardInput` am Player kann man dann ausschalten.)

**Worterkennung (Person 3):**

```csharp
game.WordAppeared += wort => { /* Erkennung für dieses Wort starten */ };
game.SubmitRecognizedWord(erkanntesWort);  // true, wenn es passt
game.WordFinished += (wort, richtig) => { /* ... */ };
```

## Einstellbare Werte (im Inspector, beim Play)

- `CaveRunnerWorld`: Tempo, Abstand der Hindernisse, Tunnelbreite/-höhe, Nebel
- `CaveRunnerGame`: Rundendauer, Wörter, Tafel-Geschwindigkeit, Lesegrenze, Pausen

## Noch offen / nächste Schritte

- Echte Modelle (Höhlenwände, Kisten, Figur) statt Würfel
- Was passiert bei einem Zusammenstoß? (Aktuell: Figur blinkt rot, „Treffer“ werden nur gezählt)
- Lesezeit pro Wort testen (`wordSpeed`, `wordSpawnZ`, `readLimitZ`)
- Prüfen, wie Text und Figur auf den CAVE-Wänden wirken
