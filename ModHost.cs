using System.Diagnostics;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PineLoader;

sealed record LoadedMod(string Id, string Name, string Version);

sealed record LoadReport(bool FirstRun, bool FromCache, IReadOnlyList<LoadedMod> Loaded, IReadOnlyList<string> Errors);

sealed class ModHost : IPineHost
{
    const string ShippingSuffix = "-Win64-Shipping.exe";
    const string GameSuffix = "-Win64-Game.exe";
    const string Engine58 = "++UE5+Release-5.8";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    GameLayout? located;
    bool locatedReady;
    string? pendingNote;
    List<FileSystemWatcher>? eyes;
    readonly List<ModFolderContext> loadedContexts = [];
    readonly List<string> agentOptions = [];
    readonly List<Action<Process>> gameHooks = [];

    public string Root { get; }
    public string GameExe { get; private set; } = "";

    public ModHost(string root) => Root = root;

    public LoadReport Run()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Mods"));
        Directory.CreateDirectory(Path.Combine(Root, "Config"));
        Directory.CreateDirectory(Path.Combine(Root, "Cache"));
        Directory.CreateDirectory(Path.Combine(Root, "Logs"));
        var logPath = Path.Combine(Root, "Logs", "pineloader.txt");
        var errorPath = Path.Combine(Root, "Logs", "err.txt");
        if (!File.Exists(logPath)) File.WriteAllText(logPath, "");
        if (!File.Exists(errorPath)) File.WriteAllText(errorPath, "");
        var oldLog = Path.Combine(Root, "Logs", "pine.log");
        if (File.Exists(oldLog)) File.Delete(oldLog);
        var launchNote = Path.Combine(Root, "Steam-Startoption.txt");
        if (File.Exists(launchNote)) File.Delete(launchNote);

        Install();
        GameExe = ResolveGame();
        EnsureConfigs();

        var cachePath = Path.Combine(Root, "Cache", "state.json");
        var fingerprint = Fingerprint();
        var firstRun = !File.Exists(cachePath);
        if (!firstRun && TryReadCache(cachePath, fingerprint, out var cached))
        {
            Log("Schnellstart, Mod-Stand unverändert.");
            var activated = LoadEnabledMods();
            return activated.Errors.Count == 0 ? cached : new LoadReport(false, true, activated.Loaded, activated.Errors);
        }

        if (firstRun) Log("Erster Start, Ordner werden angelegt.");
        var (loaded, errors) = LoadEnabledMods();
        var report = new LoadReport(firstRun, false, loaded, errors);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheFile(true, fingerprint, loaded.Select(mod => mod.Id + "|" + mod.Name + "|" + mod.Version).ToArray(), errors.ToArray()), Json));
        Log(loaded.Count + " Mods geladen, " + errors.Count + " übersprungen.");
        return report;
    }

    public void Log(string message) => Write("pineloader.txt", message);

    public void Error(string message) => Write("err.txt", message);

    public void UseJavaAgent(string jarPath, string arguments)
    {
        if (!File.Exists(jarPath)) throw new FileNotFoundException("Java-Agent fehlt.", jarPath);
        var option = "-javaagent:\"" + jarPath + "\"";
        if (!string.IsNullOrEmpty(arguments)) option += "=" + arguments;
        agentOptions.Add(option);
        Log("Java-Agent: " + jarPath);
    }

    public void WhileGameRunning(Action<Process> hook) => gameHooks.Add(hook);

    public void PrepareGame(ProcessStartInfo start)
    {
        if (agentOptions.Count == 0) return;
        start.Environment.TryGetValue("JAVA_TOOL_OPTIONS", out var existing);
        var addition = string.Join(' ', agentOptions);
        start.Environment["JAVA_TOOL_OPTIONS"] = string.IsNullOrWhiteSpace(existing) ? addition : existing + " " + addition;
    }

    public void GameStarted(Process game)
    {
        foreach (var hook in gameHooks)
        {
            try
            {
                hook(game);
            }
            catch (Exception error)
            {
                Error(error.ToString());
            }
        }
    }

    (List<LoadedMod> Loaded, List<string> Errors) LoadEnabledMods()
    {
        var loaded = new List<LoadedMod>();
        var errors = new List<string>();
        var modsRoot = Path.Combine(Root, "Mods");
        var directories = Directory.Exists(modsRoot)
            ? Directory.GetDirectories(modsRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
        foreach (var directory in directories)
        {
            var folder = Path.GetFileName(directory);
            if (folder.StartsWith('.')) continue;
            try
            {
                var mod = LoadMod(directory);
                if (mod == null)
                {
                    Log("Deaktiviert: " + folder);
                    continue;
                }
                loaded.Add(mod);
                Log("Geladen: " + mod.Name + " " + mod.Version);
            }
            catch (Exception error)
            {
                var message = folder + ": " + error.Message;
                errors.Add(message);
                Log("Übersprungen: " + folder);
                Error(message);
            }
        }
        return (loaded, errors);
    }

    public static bool IsLoaderFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var product = FileVersionInfo.GetVersionInfo(path).ProductName;
            if (string.Equals(product, "PineLoader", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch
        {
            // Eine Datei ohne Versionsinfo ist kein Loader.
        }
        var process = Environment.ProcessPath;
        return process != null && SameFile(path, process);
    }

    void Install()
    {
        locatedReady = false;
        var loader = Path.Combine(Root, "PineLoader.exe");
        if (!File.Exists(loader))
        {
            Log("PineLoader.exe fehlt im Loader-Ordner. Die Spieldatei bleibt unverändert.");
            return;
        }

        RememberStartup();
        RememberLoaderBackup(loader);

        var layout = LocateGame();
        if (layout == null)
        {
            Note("Kein Unreal-Engine-5.8-Spiel neben dem Loader gefunden. " + DescribeSearch());
            return;
        }

        var shipping = layout.Shipping;
        var game = layout.Game;
        if (OfficialFile(shipping) && GameIsRunning(layout))
        {
            Note("Das Spiel läuft. Die neue Datei wird übernommen, sobald es zu ist.");
            return;
        }

        // Steam kann durch den Hardlink schreiben. Dann ist die neue Spieldatei PineLoader.exe selbst.
        if (File.Exists(shipping) && SameFile(shipping, loader) && !IsLoaderFile(loader))
        {
            RestoreOverwrittenLoader(loader, shipping, game);
            Note("");
            return;
        }

        if (File.Exists(shipping) && SameFile(shipping, loader))
        {
            if (!File.Exists(game)) throw new FileNotFoundException("Die originale Spieldatei " + Path.GetFileName(game) + " fehlt.");
            Note("");
            return;
        }

        var replacedOfficial = false;
        if (File.Exists(shipping) && IsLoaderFile(shipping))
        {
            File.Delete(shipping);
        }
        else if (File.Exists(shipping))
        {
            if (!FileSettled(shipping))
            {
                Note("Die neue Spieldatei wird noch geschrieben.");
                return;
            }
            if (!IsUnreal58(shipping))
            {
                Note("Die Shipping-Datei ist kein Unreal-Engine-5.8-Spiel und bleibt liegen.");
                return;
            }
            var size = new FileInfo(shipping).Length;
            File.Move(shipping, game, overwrite: true);
            replacedOfficial = true;
            Log("Neue Spieldatei gesichert: " + Path.GetFileName(game) + " (" + size + " Bytes).");
        }

        if (!File.Exists(game)) throw new FileNotFoundException("Die originale Spieldatei wurde nicht gefunden.");
        if (!CreateHardLink(shipping, loader, IntPtr.Zero))
        {
            var code = Marshal.GetLastWin32Error();
            if (replacedOfficial && File.Exists(game) && !File.Exists(shipping)) File.Move(game, shipping, overwrite: true);
            throw new IOException("PineLoader konnte nicht vor das Spiel gelegt werden (" + code + ").");
        }
        Note("");
        Log("Spielstart öffnet zuerst PineLoader, ohne Steam-Startoptionen.");
    }

    public static int Watch(string root)
    {
        using var gate = new Mutex(true, WatchMutex(root), out var created);
        if (!created) return 0;
        var host = new ModHost(root);
        host.TryInstall();
        using var debounce = new System.Threading.Timer(_ => host.TryInstall(), null, Timeout.Infinite, Timeout.Infinite);
        host.eyes = [];
        foreach (var directory in host.Win64Folders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new FileSystemWatcher(directory, "*.exe")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler poke = (_, _) => debounce.Change(2000, Timeout.Infinite);
            watcher.Created += poke;
            watcher.Changed += poke;
            watcher.Renamed += (_, _) => debounce.Change(2000, Timeout.Infinite);
            host.eyes.Add(watcher);
        }
        using var pulse = new System.Threading.Timer(_ => host.TryInstall(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        new ManualResetEvent(false).WaitOne();
        return 0;
    }

    public static void EnsureWatching(string root)
    {
        try
        {
            using var existing = Mutex.OpenExisting(WatchMutex(root));
            return;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Noch kein Wächter.
        }
        catch (Exception)
        {
            return;
        }

        var exe = Path.Combine(root, "PineLoader.exe");
        if (!File.Exists(exe)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            ArgumentList = { "--watch" },
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    void TryInstall()
    {
        using var gate = new Mutex(false, InstallMutex(Root));
        var owned = false;
        try
        {
            try
            {
                owned = gate.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned) return;
            Install();
        }
        catch (Exception error)
        {
            Log("Die Spielzuordnung bleibt: " + error.Message);
            Error(error.ToString());
        }
        finally
        {
            if (owned) gate.ReleaseMutex();
        }
    }

    string DescribeSearch()
    {
        var notes = new List<string> { "Wurzel " + Root };
        var configPath = Path.Combine(Root, "config.json");
        notes.Add(File.Exists(configPath) ? "config da" : "config fehlt");
        foreach (var win64 in Win64Folders())
        {
            notes.Add(win64);
            try
            {
                foreach (var file in Directory.EnumerateFiles(win64, "*.exe"))
                {
                    var name = Path.GetFileName(file);
                    if (!name.EndsWith(ShippingSuffix, StringComparison.OrdinalIgnoreCase) && !name.EndsWith(GameSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    notes.Add(name + " loader=" + IsLoaderFile(file) + " ue=" + IsUnreal58(file));
                }
            }
            catch (Exception error)
            {
                notes.Add(error.Message);
            }
        }
        return string.Join("; ", notes);
    }

    static bool OfficialFile(string path) => File.Exists(path) && !IsLoaderFile(path);

    bool GameIsRunning(GameLayout layout)
    {
        if (MatchesGame(layout, layout.Shipping)) return true;
        return !string.Equals(Path.GetFileName(layout.Shipping), Path.GetFileName(layout.Game), StringComparison.OrdinalIgnoreCase)
            && MatchesGame(layout, layout.Game);
    }

    static bool MatchesGame(GameLayout layout, string expected)
    {
        var name = Path.GetFileNameWithoutExtension(expected);
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || IsLoaderFile(path)) continue;
                    var full = Path.GetFullPath(path);
                    if (full.Equals(Path.GetFullPath(layout.Shipping), StringComparison.OrdinalIgnoreCase)) return true;
                    if (full.Equals(Path.GetFullPath(layout.Game), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch
                {
                    // Auf fremde Prozesse ohne Modulzugriff wird nicht gewartet.
                }
            }
        }
        return false;
    }

    static bool FileSettled(string path)
    {
        var first = new FileInfo(path).Length;
        if (first <= 0) return false;
        Thread.Sleep(800);
        return File.Exists(path) && new FileInfo(path).Length == first;
    }

    void RememberLoaderBackup(string loader)
    {
        if (!IsLoaderFile(loader)) return;
        var backup = LoaderBackup();
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if (File.Exists(backup) && new FileInfo(backup).Length == new FileInfo(loader).Length && IsLoaderFile(backup)) return;
        File.Copy(loader, backup, true);
    }

    void RestoreOverwrittenLoader(string loader, string shipping, string game)
    {
        var backup = LoaderBackup();
        if (!File.Exists(backup) || !IsLoaderFile(backup))
            throw new IOException("Steam hat PineLoader überschrieben, und Cache\\PineLoader.backup.exe fehlt.");
        var size = new FileInfo(loader).Length;
        File.Copy(loader, game, true);
        File.Copy(backup, loader, true);
        if (!SameFile(shipping, loader))
        {
            if (File.Exists(shipping)) File.Delete(shipping);
            if (!CreateHardLink(shipping, loader, IntPtr.Zero))
            {
                var code = Marshal.GetLastWin32Error();
                throw new IOException("PineLoader konnte nicht vor das Spiel gelegt werden (" + code + ").");
            }
        }
        Log("Neue Spieldatei gesichert: " + Path.GetFileName(game) + " (" + size + " Bytes).");
        Log("Spielstart öffnet zuerst PineLoader, ohne Steam-Startoptionen.");
    }

    string LoaderBackup() => Path.Combine(Root, "Cache", "PineLoader.backup.exe");

    void RememberStartup()
    {
        try
        {
            var loader = Path.Combine(Root, "PineLoader.exe");
            if (!File.Exists(loader)) return;
            var command = "\"" + loader + "\" --watch";
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var current = key.GetValue("PineLoader") as string;
            if (string.Equals(current, command, StringComparison.Ordinal)) return;
            key.SetValue("PineLoader", command);
            Log("PineLoader achtet ab der Anmeldung auf eine neue Spieldatei.");
        }
        catch (Exception error)
        {
            Log("Der Automatikstart konnte nicht eingetragen werden: " + error.Message);
        }
    }

    void Note(string message)
    {
        if (pendingNote == message) return;
        pendingNote = message;
        if (message.Length > 0) Log(message);
    }

    public static string WatchMutex(string root) => InstanceMutex(root) + ".Watch";

    static string InstallMutex(string root) => InstanceMutex(root) + ".Install";

    LoadedMod? LoadMod(string directory)
    {
        var folder = Path.GetFileName(directory);
        var ini = Path.Combine(Root, "Config", folder + ".ini");
        if (!Enabled(ini)) return null;

        var dll = EntryDll(directory, folder);
        var context = new ModFolderContext(dll);
        try
        {
            var assembly = context.LoadFromAssemblyPath(dll);
            var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
            loadedContexts.Add(context);
            Start(assembly);
            return new LoadedMod(folder, folder, version);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    void EnsureConfigs()
    {
        var modsRoot = Path.Combine(Root, "Mods");
        if (!Directory.Exists(modsRoot)) return;
        foreach (var directory in Directory.GetDirectories(modsRoot))
        {
            var folder = Path.GetFileName(directory);
            if (folder.StartsWith('.')) continue;
            var ini = Path.Combine(Root, "Config", folder + ".ini");
            if (!File.Exists(ini)) File.WriteAllText(ini, "[Mod]\r\nEnabled=true\r\n");
        }
    }

    void Start(Assembly assembly)
    {
        Type? entry = null;
        try
        {
            entry = ModType(assembly.GetTypes());
        }
        catch (ReflectionTypeLoadException error)
        {
            entry = ModType(error.Types);
        }
        if (entry == null) return;
        var mod = (IPineMod)Activator.CreateInstance(entry)!;
        mod.Load(this);
    }

    static Type? ModType(IEnumerable<Type?> types)
    {
        return types.FirstOrDefault(type => type is { IsAbstract: false, IsInterface: false }
            && typeof(IPineMod).IsAssignableFrom(type)
            && type.GetConstructor(Type.EmptyTypes) != null);
    }

    static string EntryDll(string directory, string folder)
    {
        var named = Path.Combine(directory, folder + ".dll");
        if (File.Exists(named)) return named;
        var dlls = Directory.GetFiles(directory, "*.dll");
        if (dlls.Length == 1) return dlls[0];
        if (dlls.Length == 0) throw new FileNotFoundException("Keine DLL im Mod-Ordner.");
        throw new InvalidDataException(folder + ".dll fehlt.");
    }

    static bool Enabled(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            var split = line.Split('=', 2);
            if (split.Length == 2 && split[0].Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            {
                var value = split[1].Trim();
                return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
            }
        }
        return true;
    }

    string Fingerprint()
    {
        var builder = new StringBuilder();
        foreach (var folder in new[] { "Mods", "Config" })
        {
            var path = Path.Combine(Root, folder);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                builder.Append(info.FullName).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }
        var body = builder.ToString();
        return body.Length == 0 ? "empty" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    }

    bool TryReadCache(string cachePath, string fingerprint, out LoadReport report)
    {
        report = new LoadReport(false, true, [], []);
        try
        {
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath));
            if (cache is not { SetupComplete: true, Loaded: not null, Errors: not null } || cache.Fingerprint != fingerprint) return false;
            var loaded = cache.Loaded.Select(row =>
            {
                var parts = row.Split('|');
                return new LoadedMod(parts[0], parts.Length > 1 ? parts[1] : parts[0], parts.Length > 2 ? parts[2] : "");
            }).ToArray();
            report = new LoadReport(false, true, loaded, cache.Errors);
            return true;
        }
        catch
        {
            return false;
        }
    }

    string ResolveGame()
    {
        var layout = LocateGame();
        if (layout != null && File.Exists(layout.Game))
        {
            File.WriteAllText(Path.Combine(Root, "config.json"), JsonSerializer.Serialize(new ConfigFile(layout.Game), Json));
            return layout.Game;
        }
        if (layout != null && File.Exists(layout.Shipping) && !IsLoaderFile(layout.Shipping)) return layout.Shipping;

        var configPath = Path.Combine(Root, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var config = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(configPath));
                if (!string.IsNullOrWhiteSpace(config?.GameExe) && File.Exists(config.GameExe) && !IsLoaderFile(config.GameExe) && IsUnreal58(config.GameExe)) return config.GameExe;
            }
            catch
            {
                Log("config.json ist ungültig und wird ignoriert.");
            }
        }
        throw new FileNotFoundException("Kein Unreal-Engine-5.8-Spiel gefunden. Erwartet wird eine Datei *-Win64-Shipping.exe mit der Versionszeile " + Engine58 + ".");
    }

    GameLayout? LocateGame()
    {
        if (locatedReady) return located;
        locatedReady = true;
        var saved = SavedLayout();
        if (saved != null)
        {
            located = saved;
            return located;
        }
        var matches = new List<GameLayout>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var win64 in Win64Folders())
        {
            if (!seen.Add(Path.GetFullPath(win64))) continue;
            Dictionary<string, (string? Shipping, string? Game)> projects;
            try
            {
                projects = ProjectsIn(win64);
            }
            catch (Exception error)
            {
                Log("Spielordner nicht lesbar: " + win64 + " (" + error.Message + ")");
                continue;
            }

            foreach (var (project, pair) in projects)
            {
                var real = RealGameFile(pair.Game, pair.Shipping);
                if (real == null || !IsUnreal58(real)) continue;
                matches.Add(new GameLayout(
                    win64,
                    project,
                    pair.Shipping ?? Path.Combine(win64, project + ShippingSuffix),
                    pair.Game ?? Path.Combine(win64, project + GameSuffix)));
            }
        }

        if (matches.Count == 1)
        {
            located = matches[0];
            return located;
        }
        if (matches.Count > 1) Log("Mehrere Unreal-Engine-5.8-Spiele gefunden. Es wird keines verändert: " + string.Join(", ", matches.Select(item => item.Shipping)));
        return null;
    }

    GameLayout? SavedLayout()
    {
        var configPath = Path.Combine(Root, "config.json");
        if (!File.Exists(configPath)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(configPath));
            var game = config?.GameExe;
            if (string.IsNullOrWhiteSpace(game) || !File.Exists(game) || IsLoaderFile(game) || !IsUnreal58(game)) return null;
            var name = Path.GetFileName(game);
            if (!name.EndsWith(GameSuffix, StringComparison.OrdinalIgnoreCase)) return null;
            var project = name[..^GameSuffix.Length];
            if (string.IsNullOrWhiteSpace(project)) return null;
            var win64 = Path.GetDirectoryName(game);
            if (string.IsNullOrWhiteSpace(win64)) return null;
            var known = new HashSet<string>(Win64Folders().Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            if (!known.Contains(Path.GetFullPath(win64))) return null;
            return new GameLayout(win64, project, Path.Combine(win64, project + ShippingSuffix), game);
        }
        catch
        {
            return null;
        }
    }

    static string? RealGameFile(string? game, string? shipping)
    {
        if (!string.IsNullOrWhiteSpace(game) && File.Exists(game) && !IsLoaderFile(game)) return game;
        if (!string.IsNullOrWhiteSpace(shipping) && File.Exists(shipping) && !IsLoaderFile(shipping)) return shipping;
        return null;
    }

    static bool IsUnreal58(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = (info.ProductVersion ?? "") + "\n" + (info.FileVersion ?? "");
            return version.Contains(Engine58, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static Dictionary<string, (string? Shipping, string? Game)> ProjectsIn(string win64)
    {
        var projects = new Dictionary<string, (string? Shipping, string? Game)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(win64, "*.exe"))
        {
            var name = Path.GetFileName(file);
            string? project = null;
            var shipping = false;
            if (name.EndsWith(ShippingSuffix, StringComparison.OrdinalIgnoreCase))
            {
                project = name[..^ShippingSuffix.Length];
                shipping = true;
            }
            else if (name.EndsWith(GameSuffix, StringComparison.OrdinalIgnoreCase))
            {
                project = name[..^GameSuffix.Length];
            }
            if (string.IsNullOrWhiteSpace(project)) continue;
            projects.TryGetValue(project, out var pair);
            if (shipping) pair.Shipping = file;
            else pair.Game = file;
            projects[project] = pair;
        }
        return projects;
    }

    IEnumerable<string> Win64Folders()
    {
        var parent = Directory.GetParent(TrimSlash(Root))?.FullName;
        var starts = new[] { Root, parent }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var start in starts)
        {
            var direct = Path.Combine(start!, "Binaries", "Win64");
            if (Directory.Exists(direct)) yield return direct;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(start!);
            }
            catch
            {
                continue;
            }
            foreach (var child in children)
            {
                var nested = Path.Combine(child, "Binaries", "Win64");
                if (Directory.Exists(nested)) yield return nested;
            }
        }
    }

    public static string InstanceMutex(string root)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)));
        return "PineLoader." + Convert.ToHexString(hash)[..16];
    }

    void Write(string file, string message)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
        File.AppendAllText(Path.Combine(Root, "Logs", file), line);
    }

    static bool SameFile(string left, string right)
    {
        if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var leftHandle = File.OpenHandle(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rightHandle = File.OpenHandle(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(leftHandle.DangerousGetHandle(), out var leftInfo)) return false;
            if (!GetFileInformationByHandle(rightHandle.DangerousGetHandle(), out var rightInfo)) return false;
            return leftInfo.VolumeSerialNumber == rightInfo.VolumeSerialNumber
                && leftInfo.FileIndexHigh == rightInfo.FileIndexHigh
                && leftInfo.FileIndexLow == rightInfo.FileIndexLow;
        }
        catch
        {
            return false;
        }
    }

    public static string FindRoot()
    {
        var processDirectory = Environment.ProcessPath == null ? null : Path.GetDirectoryName(Environment.ProcessPath);
        var starts = new[] { AppContext.BaseDirectory, processDirectory }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var start in starts)
        {
            for (var dir = new DirectoryInfo(start!); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "brand", "logo.jpg"))) return TrimSlash(dir.FullName);
                var nested = Path.Combine(dir.FullName, "PineLoader");
                if (File.Exists(Path.Combine(nested, "brand", "logo.jpg"))) return TrimSlash(nested);
            }
        }
        return TrimSlash(AppContext.BaseDirectory);
    }

    static string TrimSlash(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(IntPtr handle, out BY_HANDLE_FILE_INFORMATION information);

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    sealed class ModFolderContext(string entryDll) : AssemblyLoadContext(isCollectible: true)
    {
        readonly AssemblyDependencyResolver resolver = new(entryDll);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "PineLoader", StringComparison.OrdinalIgnoreCase)) return typeof(ModHost).Assembly;
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
    }

    sealed record CacheFile(bool SetupComplete, string Fingerprint, string[] Loaded, string[] Errors);
    sealed record ConfigFile(string GameExe);
    sealed record GameLayout(string Win64, string Project, string Shipping, string Game);
}
