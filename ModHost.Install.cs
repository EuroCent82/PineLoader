using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PineLoader;

sealed partial class ModHost
{
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

        var shippingExists = File.Exists(shipping);
        var sameLoader = shippingExists && SameFile(shipping, loader);
        if (shippingExists && IsLoaderFile(shipping) && !sameLoader)
        {
            File.Delete(shipping);
            shippingExists = false;
        }

        var replacedOfficial = false;
        switch (DecideInstall(shippingExists, sameLoader, File.Exists(game)))
        {
            case InstallAction.BackupMissing:
                if (sameLoader)
                    throw new FileNotFoundException("Die originale Spieldatei " + Path.GetFileName(game) + " fehlt.");
                throw new FileNotFoundException("Die originale Spieldatei wurde nicht gefunden.");
            case InstallAction.ShippingIsLoader:
                Note("");
                return;
            case InstallAction.MissingShipping:
                break;
            case InstallAction.ForeignShipping:
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
                break;
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

    public static int Watch(string root, int parentId)
    {
        using var gate = new Mutex(true, WatchMutex(root), out var created);
        if (!created) return 0;
        if (parentId > 0) return WatchParent(root, parentId);
        return WatchUntilSettled(root);
    }

    static int WatchParent(string root, int parentId)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            return 0;
        }
        if (parent.HasExited) return 0;
        var host = new ModHost(root);
        host.TryInstall();
        using var debounce = new System.Threading.Timer(_ => host.TryInstall(), null, Timeout.Infinite, Timeout.Infinite);
        using var eyes = WatchExes(host, () => debounce.Change(2000, Timeout.Infinite));
        using var pulse = new System.Threading.Timer(_ => host.TryInstall(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        parent.WaitForExit();
        host.TryInstall();
        return 0;
    }

    static int WatchUntilSettled(string root)
    {
        var host = WatchHostFactory?.Invoke(root) ?? new ModHost(root);
        host.TryInstall();
        if (!host.InstallWaiting()) return 0;
        host.OnWatching?.Invoke();
        using var settled = new ManualResetEventSlim(false);
        void Poke()
        {
            host.TryInstall();
            if (!host.InstallWaiting()) settled.Set();
        }
        using var debounce = new System.Threading.Timer(_ => Poke(), null, Timeout.Infinite, Timeout.Infinite);
        using var eyes = WatchExes(host, () => debounce.Change(host.WatchDebounceInterval, Timeout.InfiniteTimeSpan));
        using var pulse = new System.Threading.Timer(_ => Poke(), null, host.WatchPulseInterval, host.WatchPulseInterval);
        settled.Wait();
        return 0;
    }

    bool InstallWaiting()
    {
        if (ClearWatch?.Invoke() == true)
        {
            pendingNote = "";
            return false;
        }
        var note = pendingNote;
        return note != null
            && (note.StartsWith("Das Spiel läuft", StringComparison.Ordinal)
                || note.StartsWith("Die neue Spieldatei wird noch geschrieben", StringComparison.Ordinal));
    }

    static EyeSet WatchExes(ModHost host, Action poke)
    {
        host.eyes = [];
        foreach (var directory in host.Win64Folders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new FileSystemWatcher(directory, "*.exe")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler on = (_, _) => poke();
            watcher.Created += on;
            watcher.Changed += on;
            watcher.Renamed += (_, _) => poke();
            host.eyes.Add(watcher);
        }
        return new EyeSet(host.eyes);
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
            ArgumentList = { "--watch", Environment.ProcessId.ToString() },
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    void TryInstall()
    {
        if (HoldInstall) return;
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

    internal void RememberStartup()
    {
        try
        {
            var loader = Path.Combine(Root, "PineLoader.exe");
            if (!File.Exists(loader)) return;
            if (!AutostartOn(Root))
            {
                RunKey.Delete();
                return;
            }
            var command = "\"" + loader + "\" --watch";
            var current = RunKey.Read();
            if (string.Equals(current, command, StringComparison.Ordinal)) return;
            RunKey.Write(command);
            Log("PineLoader achtet ab der Anmeldung auf eine neue Spieldatei.");
        }
        catch (Exception error)
        {
            Log("Der Automatikstart konnte nicht eingetragen werden: " + error.Message);
        }
    }

    internal static bool AutostartOn(string root)
    {
        var value = IniValue(Path.Combine(root, "Config", "PineLoader.ini"), "Autostart");
        return value != null && value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    internal static void SaveAutostart(string root, bool on)
    {
        var path = Path.Combine(root, "Config", "PineLoader.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : ["[PineLoader]"];
        var value = "Autostart=" + (on ? "true" : "false");
        var index = lines.FindIndex(line => line.TrimStart().StartsWith("Autostart=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = value;
        else lines.Add(value);
        File.WriteAllLines(path, lines);
    }

    public void RestoreGame()
    {
        locatedReady = false;
        located = null;
        var loader = Path.Combine(Root, "PineLoader.exe");
        var layout = LocateGame();
        if (layout != null && File.Exists(loader) && File.Exists(layout.Shipping) && SameFile(layout.Shipping, loader))
        {
            var gameIsLoader = IsLoaderFile(layout.Game) || SameFile(layout.Game, loader);
            if (ShouldRestoreShipping(true, File.Exists(layout.Game), gameIsLoader))
            {
                File.Delete(layout.Shipping);
                File.Move(layout.Game, layout.Shipping);
                Log(Path.GetFileName(layout.Shipping) + " ist wieder die originale Spieldatei.");
            }
        }
        RunKey.Delete();
        RestoreJava();
    }

    internal static bool ShouldRestoreShipping(bool shippingIsLoader, bool gameExists, bool gameIsLoader)
        => shippingIsLoader && gameExists && !gameIsLoader;

    internal static InstallAction DecideInstall(bool shippingExists, bool shippingIsLoader, bool backupExists)
    {
        if (!shippingExists)
            return backupExists ? InstallAction.MissingShipping : InstallAction.BackupMissing;
        if (shippingIsLoader)
            return backupExists ? InstallAction.ShippingIsLoader : InstallAction.BackupMissing;
        return InstallAction.ForeignShipping;
    }

    internal string ShippingFileName()
    {
        locatedReady = false;
        located = null;
        var layout = LocateGame();
        return layout == null ? "Die Shipping-Datei" : Path.GetFileName(layout.Shipping);
    }

    const int JavaWalkDepth = 6;
    const int JavaWalkLimit = 4000;
    static readonly string[] JavaFirst = ["jre", "bin", "LocalServer", "Content", "Local", "Server"];

    void RestoreJava()
    {
        var seen = 0;
        if (RestoreJavaFrom(Root, 0, ref seen)) return;
        var parent = Directory.GetParent(TrimSlash(Root))?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) return;
        seen = 0;
        RestoreJavaFrom(parent, 0, ref seen);
    }

    bool RestoreJavaFrom(string directory, int depth, ref int seen)
    {
        if (string.IsNullOrWhiteSpace(directory) || depth > JavaWalkDepth || seen >= JavaWalkLimit) return false;
        if (!Directory.Exists(directory)) return false;
        seen++;
        var gameJava = Path.Combine(directory, "jre", "bin", "java-game.exe");
        if (File.Exists(gameJava))
        {
            var java = Path.Combine(directory, "jre", "bin", "java.exe");
            if (File.Exists(java) && IsPineJava(java))
            {
                File.Move(gameJava, java, overwrite: true);
                Log("java.exe ist wieder die originale Java-Datei.");
                return true;
            }
        }
        if (depth == JavaWalkDepth) return false;
        foreach (var child in JavaChildren(directory))
        {
            if (RestoreJavaFrom(child, depth + 1, ref seen)) return true;
        }
        return false;
    }

    static IEnumerable<string> JavaChildren(string directory)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch
        {
            yield break;
        }
        var list = new List<string>();
        foreach (var child in children)
        {
            if (SkipJavaDir(Path.GetFileName(child))) continue;
            list.Add(child);
        }
        list.Sort(CompareJavaDir);
        foreach (var child in list) yield return child;
    }

    static int CompareJavaDir(string left, string right)
    {
        var rank = JavaRank(Path.GetFileName(left)).CompareTo(JavaRank(Path.GetFileName(right)));
        return rank != 0 ? rank : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    static int JavaRank(string name)
    {
        for (var index = 0; index < JavaFirst.Length; index++)
        {
            if (name.Equals(JavaFirst[index], StringComparison.OrdinalIgnoreCase)) return index;
        }
        return JavaFirst.Length;
    }

    static bool SkipJavaDir(string name)
    {
        if (name.Length == 0 || name[0] == '.') return true;
        return name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Intermediate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Saved", StringComparison.OrdinalIgnoreCase)
            || name.Equals("DerivedDataCache", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Mods", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cache", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Logs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PineLoader", StringComparison.OrdinalIgnoreCase);
    }

    bool IsPineJava(string path)
    {
        if (IsPineJavaShim != null) return IsPineJavaShim(path);
        try
        {
            if (!File.Exists(path)) return false;
            return string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "PineJava", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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

    bool IsUnreal58(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = (info.ProductVersion ?? "") + "\n" + (info.FileVersion ?? "");
            return Engines().Any(engine => engine.Length > 0 && version.Contains(engine, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    string[] Engines()
    {
        if (acceptedEngines != null) return acceptedEngines;
        var path = Path.Combine(Root, "Engine.txt");
        if (!File.Exists(path)) return acceptedEngines = [Engine58];
        var list = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            list.Add(line);
        }
        return acceptedEngines = list.ToArray();
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
}
