# PineLoader

Mod-Loader für Windows-Spiele mit Unreal Engine 5.8. PineLoader startet vor dem Spiel, ohne Steam-Startoptionen, lädt Mods aus eigenen Ordnern und lässt das Spiel auch dann starten, wenn eine einzelne Mod fehlschlägt. Ein schon eingerichtetes Spiel bleibt dieselbe `*-Win64-Game.exe`.

PineLoader is a Windows mod loader for games built with Unreal Engine 5.8. It starts before the game without Steam launch options, loads mods from their own folders, and still starts the game when one mod fails. A game that is already set up keeps the same `*-Win64-Game.exe`.

PineLoader ist kein BepInEx und kein MelonLoader. Der Aufbau dieser README folgt nur der üblichen Gliederung solcher Loader: Zweck, Voraussetzungen, Installation, Mitgelieferte Ordner, Bauen, Lizenz.

## Voraussetzungen

### Spiel starten

- Windows 10 oder neuer
- Ein Windows-Spiel, dessen Shipping-Datei die Versionszeile `++UE5+Release-5.8` trägt. Pionero Capital ist so ein Spiel.
- Die veröffentlichte `PineLoader.exe` enthält die benötigte Laufzeit. Ein separates .NET muss dafür nicht installiert werden.

### Mods und den Loader bauen

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Zielframework `net10.0-windows`
- Die Anleitung auf der Webseite nutzt [Visual Studio 2026 Community](https://visualstudio.microsoft.com/downloads/) mit der Workload .NET-Desktopentwicklung. Visual Studio 2022 baut `net10.0-windows` nicht.

## Installation

1. Lege den Ordner `PineLoader` neben die Spielinstallation. `PineLoader.exe` liegt in diesem Ordner.
2. Starte `PineLoader.exe` einmal, während das Spiel geschlossen ist.
3. Der Loader sucht neben sich eine `*-Win64-Shipping.exe` mit Unreal Engine 5.8, sichert sie als `*-Win64-Game.exe` und legt einen Hardlink mit dem Shipping-Namen auf `PineLoader.exe`. Bei Pionero Capital heißen die Dateien weiter `PioneroCapital-Win64-Shipping.exe` und `PioneroCapital-Win64-Game.exe`. Ist `config.json` schon gesetzt, bleibt genau diese Game.exe das Spiel.
4. Steam-Startoptionen bleiben leer.
5. **Spielen** in Steam startet zuerst PineLoader. Das Fenster verschwindet, das Spiel startet, und der Loader bleibt im Hintergrund, bis das Spiel endet.
6. Ein direkter Start von `PineLoader.exe` lässt das Fenster offen. **Spiel starten** macht danach denselben Ablauf wie Steam.

Wenn eine Steam-Dateiprüfung die originale Shipping-Datei zurückbringt, `PineLoader.exe` einmal erneut starten. Die gesicherte `*-Win64-Game.exe` nicht löschen und nicht durch den Loader ersetzen.

Die Webseite unter `website/` beschreibt denselben Ablauf unter `/docs/installation`.

## Ordner

```text
PineLoader/
  PineLoader.exe
  Mods/<Name>/<Name>.dll
  Config/<Name>.ini
  Cache/
  Logs/pineloader.txt
  Logs/err.txt
```

`Enabled=false` in der INI lädt die Mod nicht. Eine defekte DLL wird übersprungen und in `err.txt` vermerkt. Ordner, deren Name mit einem Punkt beginnt, werden ignoriert.

## Eine Mod bauen

Die Anleitung legt die Dateien einzeln an und erklärt jede Klasse und Methode. Sie steht unter `/docs/create-mod`. `/create-mod` führt dorthin. Das Spiel im Beispiel ist Pionero Capital. Dieselbe Notiz funktioniert ohne Java, und sie kann um einen Java-Agenten ergänzt werden.

Die öffentliche Schnittstelle ist `IPineMod` und `IPineHost` in `IPineHost.cs`. Jede Methode ist auf der Webseite unter `/docs/api` mit einem eigenen Beispiel beschrieben.

## Diesen Loader bauen

```bash
dotnet build PineLoader.csproj -c Release
```

Die einzelne veröffentlichte Datei, die neben dem Spiel liegt, entsteht mit:

```bash
dotnet publish PineLoader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
```

## Webseite

Deutsch und Englisch, Dokumentation unter `/docs/`, Changelog unter `/changelog`.

```bash
cd website
npm install
npm run dev
```

`npm run build` schreibt die fertige Seite nach `website/dist`.

Vue 3 und Vite sind nur das Gerüst der Seite. Das Aussehen ist eigenes CSS.

## Lizenz

Apache License 2.0. Der vollständige Text steht in `LICENSE`.

MelonLoader nutzt dieselbe Lizenz für einen Mod-Loader. Mods, die PineLoader nur aufrufen, können eine eigene Lizenz behalten. BepInEx steht unter LGPL-2.1; diese Lizenz wird hier nicht verwendet.

Copyright 2026 The PineLoader Authors.
