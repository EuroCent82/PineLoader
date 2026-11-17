using System.Reflection;
using System.Security.Cryptography;

namespace PineLoader;

sealed partial class ModHost
{
    public string? Setting(string key)
    {
        var scope = CurrentLoad.Value;
        if (scope == null || scope.Closed || !scope.Inside) return null;
        if (string.IsNullOrWhiteSpace(key)) return null;
        return IniValue(Path.Combine(Root, "Config", scope.Folder + ".ini"), key.Trim());
    }

    public bool Allowed(string right)
    {
        if (string.IsNullOrWhiteSpace(right) || !KnownRight(right)) return false;
        var scope = CurrentLoad.Value;
        if (scope == null || scope.Closed) return false;
        var allow = IniValue(Path.Combine(Root, "Config", scope.Folder + ".ini"), "Allow");
        if (allow == null)
        {
            NoteFullRights(scope);
            return true;
        }
        return RightListed(allow, right);
    }
    (List<LoadedMod> Loaded, List<string> Errors) LoadEnabledMods()
    {
        var loaded = new List<LoadedMod>();
        var errors = new List<string>();
        var modsRoot = Path.Combine(Root, "Mods");
        var directories = Directory.Exists(modsRoot)
            ? OrderFolders(Directory.GetDirectories(modsRoot), errors)
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
    LoadedMod? LoadMod(string directory)
    {
        var folder = Path.GetFileName(directory);
        var ini = Path.Combine(Root, "Config", folder + ".ini");
        if (!Enabled(ini)) return null;

        var dll = EntryDll(directory, folder);
        VerifyHash(ini, dll);
        var context = new ModFolderContext(dll);
        try
        {
            var assembly = context.LoadFromAssemblyPath(dll);
            var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
            loadedContexts.Add(context);
            if (!Start(assembly, folder))
            {
                loadedContexts.Remove(context);
                try
                {
                    context.Unload();
                }
                catch
                {
                    // Aus ist ein stilles Überspringen. Der Kontext bleibt, wenn er noch läuft.
                }
                return null;
            }
            return new LoadedMod(folder, folder, version);
        }
        catch (LoadTimeoutException)
        {
            throw;
        }
        catch
        {
            loadedContexts.Remove(context);
            try
            {
                context.Unload();
            }
            catch
            {
                // Läuft Load noch, bleibt der Kontext. Die ursprüngliche Meldung gilt.
            }
            throw;
        }
    }

    void VerifyHash(string ini, string dll)
    {
        var expected = IniValue(ini, "Hash");
        if (expected == null) return;
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
        var wanted = expected.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (!actual.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der Hash stimmt nicht.");
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

    bool Start(Assembly assembly, string folder)
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
        if (entry == null) throw new InvalidOperationException("Keine Mod-Klasse in " + folder + ".");
        var mod = (IPineMod)Activator.CreateInstance(entry)!;
        var scope = new LoadTicket(folder);
        var agents = agentOptions.Count;
        var hooks = gameHooks.Count;
        var exits = exitHooks.Count;
        var load = Task.Run(() =>
        {
            CurrentLoad.Value = scope;
            scope.Inside = true;
            try
            {
                return mod.Load(this);
            }
            finally
            {
                scope.Inside = false;
            }
        });
        try
        {
            if (!load.Wait(LoadLimit))
            {
                // Der Thread läuft weiter. Ab hier ändern seine Host-Aufrufe nichts mehr.
                scope.Closed = true;
                load.ContinueWith(task => { var _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new LoadTimeoutException();
            }
            if (load.Result != ModLoad.Loaded)
            {
                Trim(agentOptions, agents);
                Trim(agentFileLines, agents);
                Trim(gameHooks, hooks);
                Trim(exitHooks, exits);
            }
            return load.Result switch
            {
                ModLoad.Off => false,
                ModLoad.Failed => throw new InvalidOperationException("Laden ist fehlgeschlagen."),
                _ => true
            };
        }
        catch (AggregateException error)
        {
            throw error.InnerException ?? error;
        }
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
        if (!File.Exists(path)) return true;
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

    static string? IniValue(string path, string key)
    {
        if (!File.Exists(path)) return null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            var split = line.Split('=', 2);
            if (split.Length == 2 && split[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return split[1].Trim();
        }
        return null;
    }

    List<string> OrderFolders(IEnumerable<string> directories, List<string> errors)
    {
        var names = new List<string>();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.') || byName.ContainsKey(name)) continue;
            byName[name] = name;
            names.Add(name);
        }

        var after = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var ini = Path.Combine(Root, "Config", name + ".ini");
            if (!Enabled(ini)) continue;
            var other = IniValue(ini, "After");
            if (string.IsNullOrWhiteSpace(other) || !byName.TryGetValue(other.Trim(), out var target)) continue;
            if (!Enabled(Path.Combine(Root, "Config", target + ".ini"))) continue;
            after[name] = target;
        }

        var indegree = names.ToDictionary(name => name, _ => 0, StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in after)
        {
            indegree[pair.Key]++;
            if (pair.Key.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (!dependents.TryGetValue(pair.Value, out var waiting))
            {
                waiting = [];
                dependents[pair.Value] = waiting;
            }
            waiting.Add(pair.Key);
        }

        var ready = new SortedSet<string>(names.Where(name => indegree[name] == 0), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            if (next == null) break;
            ready.Remove(next);
            ordered.Add(next);
            placed.Add(next);
            if (!dependents.TryGetValue(next, out var waiting)) continue;
            foreach (var child in waiting)
            {
                indegree[child]--;
                if (indegree[child] == 0) ready.Add(child);
            }
        }

        if (ordered.Count < names.Count)
        {
            var rest = names.Where(name => !placed.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            ordered.AddRange(rest);
            errors.Add("Ladereihenfolge: Zyklus zwischen " + string.Join(", ", rest) + ". Es bleibt beim Alphabet.");
        }

        var modsRoot = Path.Combine(Root, "Mods");
        return ordered.Select(name => Path.Combine(modsRoot, name)).ToList();
    }

    internal IReadOnlyList<string> ModLines(LoadReport report)
    {
        var lines = new List<string>();
        var modsRoot = Path.Combine(Root, "Mods");
        if (!Directory.Exists(modsRoot)) return lines;
        foreach (var directory in Directory.GetDirectories(modsRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(directory);
            if (folder.StartsWith('.')) continue;
            var ini = Path.Combine(Root, "Config", folder + ".ini");
            var note = IniValue(ini, "Hash") == null ? ", unsigniert" : "";
            if (File.Exists(ini) && !Enabled(ini))
            {
                lines.Add(folder + ": aus" + note);
                continue;
            }
            if (report.Loaded.Any(mod => mod.Id.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(folder + ": geladen" + note);
                continue;
            }
            var error = report.Errors.FirstOrDefault(item => item.StartsWith(folder + ":", StringComparison.OrdinalIgnoreCase));
            if (error == null)
            {
                lines.Add(folder + ": nicht geladen" + note);
                continue;
            }
            var detail = error[(folder.Length + 1)..].Trim();
            lines.Add(folder + ": " + FirstSentence(detail) + note);
        }
        return lines;
    }

    static string FirstSentence(string message)
    {
        if (message.Length == 0) return message;
        var dot = message.IndexOf('.');
        if (dot < 0) return message;
        return message[..(dot + 1)];
    }
    bool CallClosed() => CurrentLoad.Value is { Closed: true };

    void RequireRight(string right, string message)
    {
        if (CallClosed()) return;
        if (HasRight(right)) return;
        throw new InvalidOperationException(message);
    }

    bool HasRight(string right)
    {
        var scope = CurrentLoad.Value;
        if (scope == null) return true;
        if (scope.Closed) return false;
        var allow = IniValue(Path.Combine(Root, "Config", scope.Folder + ".ini"), "Allow");
        if (allow == null)
        {
            NoteFullRights(scope);
            return true;
        }
        return RightListed(allow, right);
    }

    void NoteFullRights(LoadTicket scope)
    {
        if (scope.FullRightsNoted) return;
        scope.FullRightsNoted = true;
        Write("pineloader.txt", "Erlaubniszeile fehlt, volle Rechte.");
    }

    static bool RightListed(string allow, string right)
    {
        foreach (var part in allow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static bool KnownRight(string right)
        => right.Equals("Log", StringComparison.OrdinalIgnoreCase)
            || right.Equals("JavaAgent", StringComparison.OrdinalIgnoreCase)
            || right.Equals("Process", StringComparison.OrdinalIgnoreCase);

    static void RunAs(LoadTicket? scope, Action call)
    {
        if (scope is { Closed: true }) return;
        var previous = CurrentLoad.Value;
        if (scope != null) CurrentLoad.Value = scope;
        try
        {
            call();
        }
        finally
        {
            if (scope != null) CurrentLoad.Value = previous;
        }
    }

    static void Trim<T>(List<T> list, int count)
    {
        if (list.Count > count) list.RemoveRange(count, list.Count - count);
    }
}
