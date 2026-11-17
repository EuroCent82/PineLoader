using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PineLoader;
using Xunit;

namespace PineLoader.Tests;

public class LoaderTests
{
    [Fact(Timeout = 10000)]
    public async Task Watch_without_parent_logs_missing_exe_and_returns()
    {
        var root = Sandbox();
        try
        {
            var code = ModHost.Watch(root, 0);
            Assert.Equal(0, code);
            var log = File.ReadAllText(Path.Combine(root, "Logs", "pineloader.txt"));
            Assert.Contains("PineLoader.exe fehlt im Loader-Ordner", log);
            await Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Dll_without_mod_class_is_an_error()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "NoInterface", Fixture("NoInterface.dll"), "[Mod]\r\nEnabled=true\r\n");
            var report = Host(root).Run();
            Assert.DoesNotContain(report.Loaded, mod => mod.Id.Equals("NoInterface", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(report.Errors, error => error.StartsWith("NoInterface:", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Disabled_mod_is_not_an_error()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "SampleMod", Fixture("SampleMod.dll"), "[Mod]\r\nEnabled=false\r\n");
            var report = Host(root).Run();
            Assert.Empty(report.Loaded);
            Assert.Empty(report.Errors);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Cache_hit_returns_the_fresh_name_and_rewrites_a_tampered_cache()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "SampleMod", Fixture("SampleMod.dll"), "[Mod]\r\nEnabled=true\r\n");
            var first = Host(root).Run();
            Assert.False(first.FromCache);
            var cachePath = Path.Combine(root, "Cache", "state.json");
            var json = File.ReadAllText(cachePath);
            Assert.Contains("SampleMod|SampleMod|", json);
            File.WriteAllText(cachePath, json.Replace("SampleMod|SampleMod|", "SampleMod|FalscherName|", StringComparison.Ordinal));
            var report = Host(root).Run();
            Assert.True(report.FromCache);
            var loaded = Assert.Single(report.Loaded);
            Assert.Equal("SampleMod", loaded.Name);
            Assert.Equal("1.2.3.0", loaded.Version);
            var fresh = File.ReadAllText(cachePath);
            Assert.DoesNotContain("FalscherName", fresh);
            Assert.Contains("SampleMod|SampleMod|1.2.3.0", fresh);
            var written = File.GetLastWriteTimeUtc(cachePath);
            Thread.Sleep(50);
            var same = Host(root).Run();
            Assert.True(same.FromCache);
            Assert.Equal("SampleMod", Assert.Single(same.Loaded).Name);
            Assert.Equal(written, File.GetLastWriteTimeUtc(cachePath));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void UseJavaAgent_rejects_spaces_and_accepts_an_empty_or_temp_argument()
    {
        var root = Sandbox();
        try
        {
            var host = Host(root);
            var folder = Path.Combine(root, "Mods", "Demo");
            Directory.CreateDirectory(folder);
            var jar = Path.Combine(folder, "agent.jar");
            File.WriteAllText(jar, "agent");
            var spaced = Path.Combine(root, "Mods", "Demo Mod", "agent.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(spaced)!);
            File.WriteAllText(spaced, "agent");
            Assert.Throws<InvalidOperationException>(() => host.UseJavaAgent(jar, "ein argument"));
            Assert.Throws<InvalidOperationException>(() => host.UseJavaAgent(jar, "wert -Xmx"));
            host.UseJavaAgent(jar, "");
            host.UseJavaAgent(spaced, "");
            var properties = Path.Combine(Path.GetTempPath(), "pine" + Guid.NewGuid().ToString("N") + ".properties");
            Assert.DoesNotContain(' ', properties);
            host.UseJavaAgent(jar, properties);
            var outside = Path.Combine(root, "ausserhalb.jar");
            File.WriteAllText(outside, "agent");
            Assert.Throws<InvalidOperationException>(() => host.UseJavaAgent(outside, ""));
            Assert.False(LogContains(root, "Erlaubniszeile fehlt"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Hash_mismatch_is_an_error_and_a_missing_hash_loads()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            var dll = Fixture("SampleMod.dll");
            PlaceMod(root, "SampleMod", dll, "[Mod]\r\nEnabled=true\r\nHash=00\r\n");
            var mismatch = Host(root).Run();
            Assert.Empty(mismatch.Loaded);
            Assert.Contains(mismatch.Errors, error => error.Contains("Hash", StringComparison.OrdinalIgnoreCase));
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
            File.WriteAllText(Path.Combine(root, "Config", "SampleMod.ini"), "[Mod]\r\nEnabled=true\r\nHash=" + actual + "\r\n");
            File.Delete(Path.Combine(root, "Cache", "state.json"));
            var matched = Host(root).Run();
            Assert.Single(matched.Loaded);
            Assert.Empty(matched.Errors);
            File.WriteAllText(Path.Combine(root, "Config", "SampleMod.ini"), "[Mod]\r\nEnabled=true\r\n");
            File.Delete(Path.Combine(root, "Cache", "state.json"));
            var open = Host(root).Run();
            var loaded = Assert.Single(open.Loaded);
            Assert.Equal("SampleMod", loaded.Id);
            Assert.Empty(open.Errors);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task After_orders_mods_and_a_cycle_does_not_hang()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            var dll = Fixture("SampleMod.dll");
            PlaceMod(root, "Beta", dll, "[Mod]\r\nEnabled=true\r\nAfter=Zebra\r\n");
            PlaceMod(root, "Zebra", dll, "[Mod]\r\nEnabled=true\r\n");
            var ordered = Host(root).Run();
            Assert.Equal(["Zebra", "Beta"], ordered.Loaded.Select(mod => mod.Id).ToArray());
            Assert.Empty(ordered.Errors);
        }
        finally
        {
            Cleanup(root);
        }

        var cycleRoot = Sandbox();
        try
        {
            StampGame(cycleRoot);
            var dll = Fixture("SampleMod.dll");
            PlaceMod(cycleRoot, "Alpha", dll, "[Mod]\r\nEnabled=true\r\nAfter=Beta\r\n");
            PlaceMod(cycleRoot, "Beta", dll, "[Mod]\r\nEnabled=true\r\nAfter=Alpha\r\n");
            var cycle = Host(cycleRoot).Run();
            Assert.Equal(["Alpha", "Beta"], cycle.Loaded.Select(mod => mod.Id).ToArray());
            Assert.Contains(cycle.Errors, error => error.Contains("Zyklus", StringComparison.Ordinal));
            await Task.CompletedTask;
        }
        finally
        {
            Cleanup(cycleRoot);
        }
    }

    [Fact]
    public void RememberStartup_follows_autostart_and_keeps_debug()
    {
        var root = Sandbox();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            var ini = Path.Combine(root, "Config", "PineLoader.ini");
            File.WriteAllText(ini, "[PineLoader]\r\nDebug=true\r\n");
            File.WriteAllText(Path.Combine(root, "PineLoader.exe"), "loader");
            Assert.False(ModHost.AutostartOn(root));
            var key = new MemoryRunKey();
            key.Seed("alt");
            var host = new ModHost(root) { RunKey = key };
            host.RememberStartup();
            Assert.Null(key.Value);
            Assert.Equal(1, key.Deletes);
            Assert.Equal(0, key.Writes);
            Assert.Contains("Debug=true", File.ReadAllText(ini));
            Assert.False(LogContains(root, "achtet ab der Anmeldung"));
            ModHost.SaveAutostart(root, true);
            var text = File.ReadAllText(ini);
            Assert.Contains("Debug=true", text);
            Assert.Contains("Autostart=true", text);
            host.RememberStartup();
            Assert.Contains("PineLoader.exe", key.Value);
            Assert.Contains("--watch", key.Value);
            Assert.Contains("Debug=true", File.ReadAllText(ini));
            Assert.True(LogContains(root, "PineLoader achtet ab der Anmeldung auf eine neue Spieldatei."));
            var log = Path.Combine(root, "Logs", "pineloader.txt");
            File.Delete(log);
            host.RememberStartup();
            Assert.False(LogContains(root, "achtet ab der Anmeldung"));
            ModHost.SaveAutostart(root, false);
            text = File.ReadAllText(ini);
            Assert.Contains("Debug=true", text);
            Assert.Contains("Autostart=false", text);
            Assert.False(ModHost.AutostartOn(Path.Combine(Path.GetTempPath(), "fehlt-" + Guid.NewGuid().ToString("N"))));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void RestoreGame_puts_the_game_file_back_without_the_real_run_key()
    {
        var root = Sandbox();
        try
        {
            var loader = Path.Combine(root, "PineLoader.exe");
            File.WriteAllBytes(loader, [1, 2, 3, 4, 5]);
            var win = Path.Combine(root, "Binaries", "Win64");
            Directory.CreateDirectory(win);
            var game = Path.Combine(win, "Demo-Win64-Game.exe");
            File.Copy(Fixture("SampleMod.dll"), game, true);
            var shipping = Path.Combine(win, "Demo-Win64-Shipping.exe");
            var backup = Path.Combine(root, "Cache", "PineLoader.backup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.WriteAllText(backup, "backup");
            var javaDir = Path.Combine(root, "Server", "Local", "jre", "bin");
            Directory.CreateDirectory(javaDir);
            var java = Path.Combine(javaDir, "java.exe");
            var javaGame = Path.Combine(javaDir, "java-game.exe");
            File.WriteAllText(java, "shim");
            File.WriteAllText(javaGame, "real-java");
            var key = new MemoryRunKey();
            key.Seed("alt");
            var linked = CreateHardLink(shipping, loader, IntPtr.Zero);
            var host = new ModHost(root)
            {
                RunKey = key,
                IsPineJavaShim = path => path.Equals(java, StringComparison.OrdinalIgnoreCase)
            };
            host.RestoreGame();
            Assert.Equal("real-java", File.ReadAllText(java));
            Assert.False(File.Exists(javaGame));
            Assert.Null(key.Value);
            if (!linked)
            {
                Assert.False(ModHost.ShouldRestoreShipping(false, true, false));
                Assert.True(ModHost.ShouldRestoreShipping(true, true, false));
                Assert.False(ModHost.ShouldRestoreShipping(true, false, false));
                Assert.False(ModHost.ShouldRestoreShipping(true, true, true));
                return;
            }
            Assert.Equal(File.ReadAllBytes(Fixture("SampleMod.dll")), File.ReadAllBytes(shipping));
            Assert.False(File.Exists(game));
            Assert.Equal("backup", File.ReadAllText(backup));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Unknown_engine_line_leaves_the_shipping_file_untouched()
    {
        var root = Sandbox();
        try
        {
            File.WriteAllText(Path.Combine(root, "Engine.txt"), "# Kommentar\r\n++UE5+Release-9.9\r\n");
            File.WriteAllText(Path.Combine(root, "PineLoader.exe"), "loader");
            var win = Path.Combine(root, "Binaries", "Win64");
            Directory.CreateDirectory(win);
            var shipping = Path.Combine(win, "Demo-Win64-Shipping.exe");
            File.Copy(Fixture("SampleMod.dll"), shipping, true);
            var before = File.ReadAllBytes(shipping);
            var error = Record.Exception(() => Host(root).Run());
            Assert.NotNull(error);
            Assert.Equal(before, File.ReadAllBytes(shipping));
            Assert.True(LogContains(root, "Kein Unreal-Engine-5.8-Spiel"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Game_exit_callbacks_are_isolated()
    {
        var root = Sandbox();
        try
        {
            var host = Host(root);
            var hit = 0;
            host.WhenGameExits(() => hit++);
            host.WhenGameExits(() => throw new InvalidOperationException("kaputt"));
            host.WhenGameExits(() => hit++);
            host.GameExited();
            Assert.Equal(2, hit);
            Assert.True(LogContains(root, "kaputt", "err.txt"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Startup_names_the_supported_game()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            Host(root).Run();
            Assert.Contains("Unterstütztes Spiel: Pionero Capital.", File.ReadAllText(Path.Combine(root, "Logs", "pineloader.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Setting_reads_the_mod_ini_only_during_load()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "ProbeMod", Fixture("ProbeMod.dll"), "[Mod]\r\nEnabled=true\r\nProbe=Read\r\nWort=Birne\r\n");
            var host = Host(root);
            var report = host.Run();
            Assert.Single(report.Loaded);
            Assert.Empty(report.Errors);
            Assert.True(LogContains(root, "Birne"));
            Assert.Null(host.Setting("Wort"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("Off", false)]
    [InlineData("Failed", true)]
    public void Load_result_is_a_quiet_skip_or_an_error(string probe, bool failed)
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "ProbeMod", Fixture("ProbeMod.dll"), "[Mod]\r\nEnabled=true\r\nProbe=" + probe + "\r\n");
            var report = Host(root).Run();
            Assert.Empty(report.Loaded);
            if (failed)
            {
                Assert.Contains(report.Errors, error => error.Contains("Laden ist fehlgeschlagen", StringComparison.Ordinal));
                Assert.True(LogContains(root, "Laden ist fehlgeschlagen", "err.txt"));
            }
            else
            {
                Assert.Empty(report.Errors);
                Assert.True(LogContains(root, "Deaktiviert: ProbeMod"));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Allow_log_only_rejects_the_java_agent()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "ProbeMod", Fixture("ProbeMod.dll"), "[Mod]\r\nEnabled=true\r\nAllow=Log\r\nProbe=Agent\r\n");
            File.WriteAllText(Path.Combine(root, "Mods", "ProbeMod", "probe.jar"), "agent");
            var report = Host(root).Run();
            Assert.Empty(report.Loaded);
            Assert.Contains(report.Errors, error => error.Contains("Java-Agent ist nicht erlaubt", StringComparison.Ordinal));
            Assert.True(LogContains(root, "Java-Agent ist nicht erlaubt", "err.txt"));
            Assert.False(LogContains(root, "Erlaubniszeile fehlt"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Missing_allow_line_keeps_the_call_and_logs_once()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "ProbeMod", Fixture("ProbeMod.dll"), "[Mod]\r\nEnabled=true\r\nProbe=Agent\r\n");
            File.WriteAllText(Path.Combine(root, "Mods", "ProbeMod", "probe.jar"), "agent");
            var report = Host(root).Run();
            Assert.Single(report.Loaded);
            Assert.Empty(report.Errors);
            var log = File.ReadAllText(Path.Combine(root, "Logs", "pineloader.txt"));
            Assert.Equal(1, CountOf(log, "Erlaubniszeile fehlt, volle Rechte."));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Allow_log_only_rejects_the_process_hook()
    {
        var root = Sandbox();
        try
        {
            StampGame(root);
            PlaceMod(root, "ProbeMod", Fixture("ProbeMod.dll"), "[Mod]\r\nEnabled=true\r\nAllow=Log\r\nProbe=Process\r\n");
            var report = Host(root).Run();
            Assert.Empty(report.Loaded);
            Assert.Contains(report.Errors, error => error.Contains("Spielprozess ist nicht erlaubt", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Late_call_after_the_load_limit_does_not_reach_the_log()
    {
        var root = Sandbox();
        var mark = Path.Combine(root, "late.txt");
        try
        {
            StampGame(root);
            PlaceMod(root, "ProbeMod", Fixture("ProbeMod.dll"), "[Mod]\r\nEnabled=true\r\nProbe=Slow\r\nMark=" + mark + "\r\n");
            File.WriteAllText(Path.Combine(root, "Mods", "ProbeMod", "probe.jar"), "agent");
            var host = Host(root);
            host.LoadLimit = TimeSpan.FromMilliseconds(150);
            var report = host.Run();
            Assert.Contains(report.Errors, error => error.Contains("Laden dauert länger als 15 Sekunden", StringComparison.Ordinal));
            Assert.Empty(report.Loaded);
            var until = DateTime.UtcNow.AddSeconds(2);
            while (!File.Exists(mark) && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.True(File.Exists(mark));
            var log = File.Exists(Path.Combine(root, "Logs", "pineloader.txt"))
                ? File.ReadAllText(Path.Combine(root, "Logs", "pineloader.txt"))
                : "";
            Assert.DoesNotContain("zu spät", log);
            var start = new ProcessStartInfo();
            host.PrepareGame(start);
            Assert.False(start.Environment.TryGetValue("JAVA_TOOL_OPTIONS", out var tools) && tools != null && tools.Contains("probe.jar", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(Path.Combine(root, "Cache", "java-agent.arg"))
                && File.ReadAllText(Path.Combine(root, "Cache", "java-agent.arg")).Contains("probe.jar", StringComparison.OrdinalIgnoreCase));
            await Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("Das Spiel läuft. Die neue Datei wird übernommen, sobald es zu ist.")]
    [InlineData("Die neue Spieldatei wird noch geschrieben.")]
    public async Task Watch_stays_while_the_note_is_set_and_returns_when_cleared(string note)
    {
        var root = Sandbox();
        var entered = new ManualResetEventSlim(false);
        var release = 0;
        ModHost.WatchHostFactory = _ =>
        {
            var host = new ModHost(root)
            {
                HoldInstall = true,
                WatchPulseInterval = TimeSpan.FromMilliseconds(40),
                OnWatching = () => entered.Set(),
                ClearWatch = () => Volatile.Read(ref release) == 1
            };
            host.SeedNote(note);
            return host;
        };
        try
        {
            var task = Task.Run(() => ModHost.Watch(root, 0));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await Task.Delay(80);
            Assert.False(task.IsCompleted);
            Volatile.Write(ref release, 1);
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(task, finished);
            Assert.Equal(0, await task);
        }
        finally
        {
            Volatile.Write(ref release, 1);
            ModHost.WatchHostFactory = null;
            entered.Dispose();
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData(false, false, true, "MissingShipping")]
    [InlineData(true, true, true, "ShippingIsLoader")]
    [InlineData(true, false, true, "ForeignShipping")]
    [InlineData(true, false, false, "ForeignShipping")]
    [InlineData(true, true, false, "BackupMissing")]
    [InlineData(false, false, false, "BackupMissing")]
    public void Install_decisions_do_not_need_a_steam_file(bool shippingExists, bool shippingIsLoader, bool backupExists, string expected)
    {
        Assert.Equal(expected, ModHost.DecideInstall(shippingExists, shippingIsLoader, backupExists).ToString());
    }

    [Fact]
    public void Loader_source_matches_the_github_tree()
    {
        var (loader, mirror) = SourceTrees();
        var names = new List<string> { "Program.cs", "MainForm.cs", "IPineHost.cs", "PineLoader.csproj" };
        names.AddRange(Directory.GetFiles(loader, "ModHost*.cs").Select(file => Path.GetFileName(file))!);
        foreach (var name in names)
        {
            var left = File.ReadAllText(Path.Combine(loader, name));
            var rightPath = Path.Combine(mirror, name);
            Assert.True(File.Exists(rightPath), rightPath);
            Assert.Equal(left, File.ReadAllText(rightPath));
        }
        var host = string.Join('\n', Directory.GetFiles(loader, "ModHost*.cs").OrderBy(file => file, StringComparer.OrdinalIgnoreCase).Select(File.ReadAllText));
        Assert.DoesNotContain("PioneroCapital", host);
    }

    static (string Loader, string Mirror) SourceTrees()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var loader = Path.Combine(dir.FullName, "PineLoader");
            var mirror = Path.Combine(dir.FullName, "GitHub", "PineLoader");
            if (File.Exists(Path.Combine(loader, "PineLoader.csproj")) && File.Exists(Path.Combine(mirror, "PineLoader.csproj")))
                return (loader, mirror);
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Die beiden PineLoader-Quellen wurden nicht gefunden.");
    }

    static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    static ModHost Host(string root) => new(root) { RunKey = new MemoryRunKey() };

    static string Sandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "PineLoaderTests", Guid.NewGuid().ToString("N"), "Loader");
        Directory.CreateDirectory(root);
        return root;
    }

    static void Cleanup(string root)
    {
        var sandbox = Directory.GetParent(root)?.FullName;
        if (string.IsNullOrEmpty(sandbox)) return;
        try
        {
            Directory.Delete(sandbox, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), path);
        return path;
    }

    static void StampGame(string root)
    {
        var dll = Fixture("SampleMod.dll");
        var info = FileVersionInfo.GetVersionInfo(dll);
        var version = (info.ProductVersion ?? "") + "\n" + (info.FileVersion ?? "");
        Assert.Contains("++UE5+Release-5.8", version);
        var dir = Path.Combine(root, "Binaries", "Win64");
        Directory.CreateDirectory(dir);
        File.Copy(dll, Path.Combine(dir, "Demo-Win64-Shipping.exe"), true);
    }

    static void PlaceMod(string root, string folder, string dll, string ini)
    {
        var dir = Path.Combine(root, "Mods", folder);
        Directory.CreateDirectory(dir);
        File.Copy(dll, Path.Combine(dir, folder + ".dll"), true);
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(root, "Config", folder + ".ini"), ini);
    }

    static bool LogContains(string root, string text, string file = "pineloader.txt")
    {
        var path = Path.Combine(root, "Logs", file);
        return File.Exists(path) && File.ReadAllText(path).Contains(text, StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    sealed class MemoryRunKey : IRunKey
    {
        public string? Value { get; private set; }
        public int Writes;
        public int Deletes;
        public string? Read() => Value;
        public void Write(string command)
        {
            Writes++;
            Value = command;
        }
        public void Delete()
        {
            Deletes++;
            Value = null;
        }
        public void Seed(string? value) => Value = value;
    }
}
