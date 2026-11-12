<p align="center">
  <img src="brand/NEW/pineloader-logo.png" alt="PineLoader" width="720">
</p>

<p align="center">
  <a href="#install">Install</a>
  &nbsp;·&nbsp;
  <a href="#folders">Folders</a>
  &nbsp;·&nbsp;
  <a href="#create-a-mod">Create a mod</a>
  &nbsp;·&nbsp;
  <a href="#api">API</a>
  &nbsp;·&nbsp;
  <a href="#changelog">Changelog</a>
  &nbsp;·&nbsp;
  <a href="#build">Build</a>
  &nbsp;·&nbsp;
  <a href="#license">License</a>
</p>

<p align="center">
  A Windows mod loader for games built with Unreal Engine 5.8.<br>
  It starts before the game, without Steam launch options.<br>
  One broken mod does not stop the game.
</p>

<p align="center">
  <a href="LICENSE"><img alt="Apache License 2.0" src="https://img.shields.io/badge/license-Apache%202.0-D5B41A?style=flat-square"></a>
  <img alt="Unreal Engine 5.8" src="https://img.shields.io/badge/Unreal%20Engine-5.8-04221a?style=flat-square">
  <img alt="Platform Windows" src="https://img.shields.io/badge/platform-Windows-245C48?style=flat-square">
</p>

PineLoader is not BepInEx and it is not MelonLoader. A game that is already set up keeps the same `*-Win64-Game.exe`. Pionero Capital is the example game in this guide. The same steps apply to any other Windows game whose shipping file carries `++UE5+Release-5.8`.

## Install

You need Windows 10 or newer, and a Windows game whose shipping file version contains `++UE5+Release-5.8`. The published `PineLoader.exe` brings its own runtime. Playing the game does not need a separate .NET install.

1. Put the `PineLoader` folder next to the game. `PineLoader.exe` sits in that folder.
2. Close the game, then start `PineLoader.exe` once.
3. The loader looks beside itself for a `*-Win64-Shipping.exe` built with Unreal Engine 5.8. It keeps the original file as `*-Win64-Game.exe` and points the shipping name at `PineLoader.exe` with a hard link. For Pionero Capital those names stay `PioneroCapital-Win64-Shipping.exe` and `PioneroCapital-Win64-Game.exe`. If `config.json` already names that game file, that file stays the game.
4. Leave the Steam launch options empty.
5. **Play** in Steam starts PineLoader first. The window hides, the game starts, and the loader stays until the game exits.
6. Starting `PineLoader.exe` yourself leaves the window open. **Start game** then does the same thing Steam does.

If Steam verifies the files and restores the original shipping file, start `PineLoader.exe` once more while the game is closed. Do not delete the saved `*-Win64-Game.exe`, and do not replace it with the loader.

The ready window shows how many mod folders were found, how many loaded, and how many did not. The two log buttons open `Logs/pineloader.txt` and `Logs/err.txt`. Close or Escape does not start the game.

## Folders

Everything the loader creates sits next to `PineLoader.exe`.

```text
PineLoader/
  PineLoader.exe
  Mods/<Name>/<Name>.dll
  Config/<Name>.ini
  Cache/
  Logs/pineloader.txt
  Logs/err.txt
```

Each subfolder of `Mods` is one mod. The folder name is the mod name, and the DLL uses that same name, for example `Mods/ErsteMod/ErsteMod.dll`. A folder whose name starts with a dot is ignored. A broken DLL is skipped and written to `err.txt`. The game still starts.

If the INI is missing, the loader writes `Config/<Name>.ini` with `Enabled=true`. `Enabled=false` or `Enabled=0` skips that mod and does not count it as an error. Empty lines and lines that start with `;` or `#` are ignored.

```ini
[Mod]
Enabled=true
```

`Cache` remembers the state of `Mods` and `Config`. When nothing changed, the next start is faster, and the mods are still loaded.

## Create a mod

This is not a finished mod to copy. The files are added one at a time, the way they could look. The note works without Java. The same files can then gain the Java part. The example game is Pionero Capital.

### Visual Studio

Use Visual Studio 2026, Community edition. The version number starts with 18. Visual Studio 2022 stops at .NET 9 and cannot build `net10.0-windows`. Build Tools have no editor. The Java part waits until file 3, and Visual Studio has no package for it.

- [Visual Studio 2026 Community](https://visualstudio.microsoft.com/downloads/)
- [.NET 10 needs Visual Studio 2026](https://learn.microsoft.com/dotnet/core/install/windows)
- [.NET desktop development workload](https://learn.microsoft.com/visualstudio/install/workload-component-id-vs-professional?view=visualstudio)

1. On the download page choose **Community**. Not Professional, not Build Tools, and not Visual Studio 2022.
2. In the installer, open **Workloads** and check **.NET desktop development**. That is `Microsoft.VisualStudio.Workload.ManagedDesktop`. Desktop development with C++ and ASP.NET and web development do not replace it.
3. In the installation details on the right, leave these checked: **.NET 10.0 Runtime (Long Term Support)** and **.NET SDK**. A Windows SDK is a C++ package and does not build this mod.
4. GitHub Copilot can be cleared. It is not part of the mod.
5. Install. Open Visual Studio. **Help → About Microsoft Visual Studio**: the version has to start with 18.
6. **File → Open → Folder**. Choose the source folder that contains `PineLoader.csproj`.
7. **View → Terminal**. Run `mkdir examples\ErsteMod`. File 1 and file 2 go in that folder. `PineLoader.csproj` sits two levels above it, or the `ProjectReference` points nowhere.
8. Do not use **File → New → Project**. The Windows Forms App template is its own program, not a mod.

### File 1, the project

In Solution Explorer, right-click `examples/ErsteMod`, then **Add → New File**, and name it `ErsteMod.csproj`. This is not a mod yet. It only tells the compiler what the DLL will be. Comments between `<!--` and `-->` are ignored.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Windows and .NET 10, the same runtime as PineLoader. -->
    <TargetFramework>net10.0-windows</TargetFramework>
    <!-- Short type names such as Path work without an extra using. -->
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- The DLL name. The folder later has to match. -->
    <AssemblyName>ErsteMod</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- So IPineMod and IPineHost are known. -->
    <ProjectReference Include="..\..\PineLoader.csproj">
      <!-- false: do not copy a second PineLoader.dll next to the mod. -->
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
```

- `Project Sdk` is the shell of every .NET project. Without it the compiler does not know what to build.
- `TargetFramework` `net10.0-windows` selects Windows and .NET 10. PineLoader uses the same.
- `ImplicitUsings` allows short names such as `Path`, without writing `using System.IO` at the top.
- `AssemblyName` is the DLL file name, without the extension. `Mods/ErsteMod/ErsteMod.dll` has to match it.
- `ProjectReference` points at `PineLoader.csproj`, two folders up. That is how this file knows the interfaces.
- `Private` `false` stops the build from placing a copy of `PineLoader.dll` in the mod folder. The mod runs inside the loader and should not bring the loader's output along.

### File 2, the class

Add `ErsteMod.cs` next to the project file. `IPineMod` requires `Load`, so the empty class below does not build yet. The next blocks fill it in. This is still only a shape, not an installed mod.

```csharp
using PineLoader;

namespace ErsteMod;

// Public, so the loader can see it.
public sealed class ErsteMod : IPineMod
{
}
```

- `using PineLoader` makes the names `IPineMod` and `IPineHost` known in this file.
- `namespace ErsteMod` only groups the source. The loader looks for the folder name, not the namespace.
- `class ErsteMod` is the type. The name may differ from the DLL. The loader takes the first public, non-abstract class with a public constructor that has no parameters.
- `sealed` means another class may not inherit from it. For this note that is a choice, not a requirement.
- `: IPineMod` says this class is a mod. Without it the loader finds no matching class and skips the folder.

`Load` is the only method the loader calls. Once, in the loader process, before Pionero Capital starts. `host` is that running loader.

```csharp
public void Load(IPineHost host)
{
    // One line in Logs/pineloader.txt.
    host.Log("ErsteMod ist geladen.");
    // Call this only once the game process exists.
    host.WhileGameRunning(game => host.Log("Pionero läuft. Prozess " + game.Id));
}
```

`host.Log` writes one sentence to the loader log. The note already works with that, and it does not need Java. For Pionero Capital the line appears in `Logs/pineloader.txt` as soon as PineLoader has loaded the mod.

`WhileGameRunning` runs later, still in the loader, not in Unreal. `game` is the Pionero Capital process, and `game.Id` is its number. Without Java the note is finished here.

The whole file, after those steps:

```csharp
using PineLoader;

namespace ErsteMod;

public sealed class ErsteMod : IPineMod
{
    // Once, before Pionero Capital starts.
    public void Load(IPineHost host)
    {
        // One line in Logs/pineloader.txt.
        host.Log("ErsteMod ist geladen.");
        // Call this only once the game process exists.
        host.WhileGameRunning(game => host.Log("Pionero läuft. Prozess " + game.Id));
    }
}
```

### Check it on Pionero Capital

1. **View → Terminal**. Change into the project folder with `cd examples\ErsteMod`.
2. `dotnet build ErsteMod.csproj -c Release`
3. Copy the DLL from `bin/Release/net10.0-windows/ErsteMod.dll` to `Mods/ErsteMod/ErsteMod.dll`.
4. Pionero Capital is closed. Start `PineLoader.exe` directly and choose **Start game**.
5. `Logs/pineloader.txt` shows `ErsteMod ist geladen` first. Then `Pionero läuft`, with the process number.
6. The loader creates `Config/ErsteMod.ini` itself. `Enabled=false` does not load the note on the next start.

### The same note with Java

After the game starts, Pionero Capital starts its own Java server. The C# note above stays and keeps working even if you skip the next two files. With those files the same note can also write a line from the Java process.

Visual Studio has no Java workload. JavaScript in the installer is not Java. The two files only need the [Microsoft Build of OpenJDK 21](https://learn.microsoft.com/java/openjdk/download). On that page choose Windows, x64, and the MSI. Leave PATH and `JAVA_HOME` selected when the installer offers them. Then close the terminal in Visual Studio and open it again with **View → Terminal**. `java -version` and `javac -version` have to show 21. Do not use the `jre` folder inside the game. That is only the game runtime, and it has no `javac`.

File 3 is `manifest.txt`, next to the C# files. Java reads it to learn which class runs when the agent starts. These lines are the instruction. There are no `//` comments.

```text
Premain-Class: pine.note.Agent
Can-Redefine-Classes: false
```

`Premain-Class` names the class that contains `premain`. The name has to match the package in file 4. `Can-Redefine-Classes: false` says this agent does not rebuild classes that are already loaded. The note only writes a line.

File 4 is `Agent.java`. `premain` is the fixed method name Java looks for on an agent. `arguments` is the text after the equals sign of `-javaagent`. `instrumentation` is the Java contract an agent can use to see classes. This note does not use it. It only writes to the file whose path is in `arguments`.

```java
package pine.note;

import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;

public final class Agent {
    private Agent() {
    }

    // Java calls this before the program in the server JAR starts.
    public static void premain(String arguments, Instrumentation instrumentation) throws Exception {
        var line = "ErsteMod, from Java." + System.lineSeparator();
        Files.writeString(Path.of(arguments), line, StandardOpenOption.CREATE, StandardOpenOption.APPEND);
    }
}
```

One line in `Load`, under the log call. The JAR must already sit next to the DLL, or `UseJavaAgent` throws and the loader skips this mod. The two lines that work without Java stay, and they still work.

```csharp
var folder = Path.GetDirectoryName(typeof(ErsteMod).Assembly.Location) ?? "";
var jar = Path.Combine(folder, "note-agent.jar");
var log = Path.Combine(host.Root, "Logs", "erste-mod-java.txt");
// Remember the JAR. The text log arrives at premain as arguments.
host.UseJavaAgent(jar, log);
```

1. **View → Terminal**, in `examples\ErsteMod`: `javac --release 17 -d out Agent.java`
2. Then: `jar cfm note-agent.jar manifest.txt -C out .`
3. Place `note-agent.jar` in `Mods/ErsteMod/`, next to `ErsteMod.dll`.
4. Build the C# file again and copy the DLL again.

Without file 3 and file 4 the note writes two lines to `pineloader.txt`. With the JAR and `UseJavaAgent` those same two lines still appear, and the loader records the JAR. The line from `premain` appears when Java actually loads the agent. `UseJavaAgent` puts that agent on the process PineLoader starts itself. Pionero Capital starts its Java server afterwards with its own command, so this call alone does not inject the note into that server.

## API

The whole public surface lives in `IPineHost.cs`. Two interfaces, no attributes, no further class. The example game is Pionero Capital.

### IPineMod

The interface of a mod. The loader looks for one public class that implements it, with a public constructor that has no parameters. There is exactly one method.

```csharp
// The loader takes the first matching class.
public interface IPineMod
{
    void Load(IPineHost host);
}
```

### Load

Called once, in the loader process, before Pionero Capital starts. `host` is the running loader. If `Load` throws, only this mod is skipped. The game still starts.

```csharp
public void Load(IPineHost host)
{
    // host is PineLoader, not the game.
    host.Log("Load was called.");
}
```

### IPineHost

What the mod is allowed to use from the loader. No method here runs inside Unreal. `Root` is a property. The others are methods.

### Root

The folder that contains `PineLoader.exe`. For Pionero Capital that is the `PineLoader` folder next to the game. `Mods`, `Config`, `Cache`, and `Logs` live in it.

```csharp
public void Load(IPineHost host)
{
    host.Log(host.Root);
}
```

### Log

Writes one line to `Logs/pineloader.txt`, with the time. A note can work with this alone, without Java. The same line is shown in the PineLoader log window.

```csharp
host.Log("A note from the mod.");
```

### Error

Writes one line to `Logs/err.txt`. That alone does not mark the mod as failed. The mod fails only when `Load` throws, or when the DLL cannot be loaded.

```csharp
host.Error("A note. The mod keeps running.");
```

### WhileGameRunning

Stores a function. The loader calls it after it has started the game process. `game` is that process, and `game.Id` is its number. For Pionero Capital that process is `Game.exe`. The function runs in the loader.

```csharp
host.WhileGameRunning(game => host.Log("Pionero läuft. Prozess " + game.Id));
```

### UseJavaAgent

Registers a JAR as a Java agent. `jarPath` has to point at a file that exists, or the method throws and this mod is skipped. `arguments` is the text Java passes to `premain`. The loader puts the agent into `JAVA_TOOL_OPTIONS` of the process it starts itself.

Pionero Capital starts its Java server later, on its own. This method alone does not put the agent into that server. The C# note still works with or without the call.

```csharp
host.UseJavaAgent(jarPath, logPath);
```

### What is not there

No attributes on the class, no `OnUpdate` method, and no access to Unreal objects. The loader does not patch game methods, and it does not load BepInEx plugins or MelonLoader mods. Anything that has to happen inside the game is built in the mod.

## Changelog

### 1.0.0

2026-11-11

- Starts before the game without Steam launch options. A game that is already set up keeps the same `*-Win64-Game.exe`. A new game is recognized by the version line `++UE5+Release-5.8`. For Pionero Capital the file name stays `PioneroCapital-Win64-Game.exe`.
- Creates `Mods`, `Config`, `Cache`, and `Logs`. Broken mods are skipped. Disabled mods are left out quietly.
- An unchanged mod set starts faster through the cache. The mods are still loaded.
- A window without the Windows frame. Through Steam the window hides once the game starts. The loader stays until the game exits.
- Starting `PineLoader.exe` directly shows **Start game**, the mod counts, and both log windows.
- Logo, wordmark, and pear icon. Background `#04221a`.

## Build

The published `PineLoader.exe` already contains the runtime. Building the loader from this source needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and `net10.0-windows`.

```bash
dotnet build PineLoader.csproj -c Release
```

The single file that sits next to the game is produced with:

```bash
dotnet publish PineLoader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
```

The site is published separately, in German and English, at PineLoader.dev or PineLoader.de. The same guide is there under `/docs/`, and the changelog under `/changelog`.

## License

Apache License 2.0. The full text is in [LICENSE](LICENSE).

MelonLoader uses the same license for a mod loader. Mods that only call PineLoader can keep their own license. BepInEx is under LGPL-2.1. That license is not used here.

Copyright 2026 The PineLoader Authors.
