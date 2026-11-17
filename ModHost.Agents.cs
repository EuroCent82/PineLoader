using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PineLoader;

sealed partial class ModHost
{
    public void Log(string message)
    {
        if (CallClosed()) return;
        Write("pineloader.txt", message);
    }

    public void Error(string message)
    {
        if (CallClosed()) return;
        Write("err.txt", message);
    }
    public void UseJavaAgent(string jarPath, string arguments)
    {
        if (CallClosed()) return;
        RequireRight("JavaAgent", "Der Java-Agent ist nicht erlaubt.");
        if (CallClosed()) return;
        if (!File.Exists(jarPath)) throw new FileNotFoundException("Java-Agent fehlt.", jarPath);
        if (!UnderMods(jarPath)) throw new InvalidOperationException("Der Java-Agent liegt nicht unter Mods.");
        if (AgentArgumentRejected(arguments))
            throw new InvalidOperationException("Das Java-Agent-Argument darf kein Leerzeichen und keine weitere Option enthalten.");
        var option = "-javaagent:\"" + jarPath + "\"";
        if (!string.IsNullOrEmpty(arguments)) option += "=" + arguments;
        agentOptions.Add(option);
        // Die Agent-Datei ist eine Zeile, der Shim liest sie ganz. Leerzeichen im JAR-Pfad
        // bleiben darin gültig. JAVA_TOOL_OPTIONS setzt den Pfad zusätzlich in Anführungszeichen.
        var fileLine = "-javaagent:" + jarPath.Replace('\\', '/');
        if (!string.IsNullOrEmpty(arguments)) fileLine += "=" + arguments.Replace('\\', '/');
        agentFileLines.Add(fileLine);
        Log("Java-Agent: " + jarPath);
    }

    public void WhileGameRunning(Action<Process> hook)
    {
        if (CallClosed()) return;
        RequireRight("Process", "Der Spielprozess ist nicht erlaubt.");
        if (CallClosed()) return;
        var scope = CurrentLoad.Value;
        gameHooks.Add(game => RunAs(scope, () => hook(game)));
    }

    public void WhenGameExits(Action callback)
    {
        if (CallClosed()) return;
        var scope = CurrentLoad.Value;
        exitHooks.Add(() => RunAs(scope, callback));
    }

    public void GameExited()
    {
        foreach (var hook in exitHooks)
        {
            try
            {
                hook();
            }
            catch (Exception error)
            {
                Error(error.ToString());
            }
        }
    }

    public void BeginSessionTrace(LoadReport report)
    {
        var path = Path.Combine(Root, "Logs", "debug.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        Trace("Sitzung");
        Trace("Geladen: " + (report.Loaded.Count == 0
            ? "keine"
            : string.Join(", ", report.Loaded.Select(mod => mod.Name + " " + mod.Version))));
        if (report.Errors.Count > 0) Trace("Übersprungen: " + string.Join(" | ", report.Errors));
    }

    public void PrepareGame(ProcessStartInfo start)
    {
        DropStaleAgentStock();
        var live = new List<string>();
        var files = new List<string>();
        for (var index = 0; index < agentOptions.Count; index++)
        {
            if (!AgentJarExists(agentOptions[index])) continue;
            live.Add(agentOptions[index]);
            files.Add(agentFileLines[index]);
        }
        WriteAgentFile(files);
        if (live.Count == 0) return;
        start.Environment.TryGetValue("JAVA_TOOL_OPTIONS", out var existing);
        var addition = string.Join(' ', live);
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
    void DropStaleAgentStock()
    {
        var agentPath = Path.Combine(Root, "Cache", "java-agent.arg");
        if (!File.Exists(agentPath)) return;

        var missingMods = new List<string>();
        var statePath = Path.Combine(Root, "Cache", "state.json");
        string[]? loaded = null;
        if (File.Exists(statePath))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(statePath))?.Loaded;
            }
            catch
            {
                loaded = null;
            }
        }

        if (loaded != null)
        {
            foreach (var entry in loaded)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var id = entry.Split('|', 2)[0].Trim();
                if (id.Length == 0) continue;
                if (missingMods.Exists(known => known.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                if (Directory.Exists(Path.Combine(Root, "Mods", id))) continue;
                missingMods.Add(id);
            }
        }

        var missingJars = new List<string>();
        var kept = new List<string>();
        var dropped = false;
        foreach (var raw in File.ReadAllLines(agentPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (AgentJarExists(line) && !LineForMissingMod(line, missingMods))
            {
                kept.Add(line);
                continue;
            }

            dropped = true;
            if (!TryAgentJar(line, out var jar)) continue;
            var name = Path.GetFileName(jar);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (missingJars.Exists(known => known.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            missingJars.Add(name);
        }

        if (!dropped) return;

        if (kept.Count == 0) File.Delete(agentPath);
        else File.WriteAllText(agentPath, string.Join(Environment.NewLine, kept) + Environment.NewLine);
        var reasons = new List<string>();
        if (missingMods.Count > 0) reasons.Add("weil diese Mods fehlen: " + string.Join(", ", missingMods));
        if (missingJars.Count == 1) reasons.Add("weil diese Datei fehlt: " + missingJars[0]);
        else if (missingJars.Count > 1) reasons.Add("weil diese Dateien fehlen: " + string.Join(", ", missingJars));
        var detail = reasons.Count > 0 ? string.Join(", und ", reasons) : "weil eine Zeile kein Java-Agent ist";
        Log("Altbestand verworfen, " + detail + ".");
    }

    static bool LineForMissingMod(string line, List<string> missingMods)
    {
        foreach (var id in missingMods)
        {
            if (line.Contains("/Mods/" + id + "/", StringComparison.OrdinalIgnoreCase)) return true;
            if (line.Contains("\\Mods\\" + id + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    void WriteAgentFile(List<string> lines)
    {
        var path = Path.Combine(Root, "Cache", "java-agent.arg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (lines.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            Log("Java-Agents für den Spielserver: keine.");
            Trace("Spielserver: keine Java-Agents.");
            return;
        }
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        var names = string.Join(", ", lines.Select(AgentName));
        Log("Java-Agents für den Spielserver: " + names + ".");
        Trace("Spielserver: " + names + ".");
    }

    public void Trace(string message) => Write("debug.txt", message);

    static string AgentName(string line) => TryAgentJar(line, out var jar) ? Path.GetFileName(jar) : "unbekannt";

    static bool AgentJarExists(string line) => TryAgentJar(line, out var jar) && File.Exists(jar);

    static bool TryAgentJar(string line, out string jar)
    {
        jar = "";
        var payload = line.Trim();
        const string prefix = "-javaagent:";
        if (!payload.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        payload = payload.Substring(prefix.Length).Trim();
        if (payload.StartsWith('"'))
        {
            var end = payload.IndexOf('"', 1);
            if (end <= 1) return false;
            jar = payload.Substring(1, end - 1);
            return jar.Length > 0;
        }
        var split = payload.IndexOf('=');
        jar = split >= 0 ? payload.Substring(0, split) : payload;
        return jar.Length > 0;
    }
    bool UnderMods(string jarPath)
    {
        var mods = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Root, "Mods"))) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(jarPath);
        return full.StartsWith(mods, StringComparison.OrdinalIgnoreCase);
    }

    static bool AgentArgumentRejected(string arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return false;
        return arguments.Any(char.IsWhiteSpace) || arguments.Contains(" -", StringComparison.Ordinal);
    }
    void Write(string file, string message)
    {
        try
        {
            var directory = Path.Combine(Root, "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, file);
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
            using var mutex = new Mutex(false, LogMutex(path));
            var owned = false;
            try
            {
                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
                if (!owned) return;
                File.AppendAllText(path, line);
            }
            finally
            {
                if (owned) mutex.ReleaseMutex();
            }
        }
        catch
        {
            // Ein belegtes Log darf keine Mod scheitern lassen.
        }
    }

    static string LogMutex(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        return "PineLoader.Log." + hash[..16];
    }
}
