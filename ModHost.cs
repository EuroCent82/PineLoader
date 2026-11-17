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

internal enum InstallAction
{
    MissingShipping,
    ShippingIsLoader,
    ForeignShipping,
    BackupMissing
}

sealed partial class ModHost : IPineHost
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
    readonly List<string> agentFileLines = [];
    readonly List<Action<Process>> gameHooks = [];
    readonly List<Action> exitHooks = [];
    string[]? acceptedEngines;

    public string Root { get; }
    public string GameExe { get; private set; } = "";

    public ModHost(string root) => Root = root;

    internal IRunKey RunKey { get; set; } = RegistryRunKey.Shared;

    internal Func<string, bool>? IsPineJavaShim { get; set; }

    internal TimeSpan LoadLimit { get; set; } = TimeSpan.FromSeconds(15);

    internal static Func<string, ModHost>? WatchHostFactory { get; set; }

    internal bool HoldInstall { get; set; }

    internal TimeSpan WatchPulseInterval { get; set; } = TimeSpan.FromSeconds(20);

    internal TimeSpan WatchDebounceInterval { get; set; } = TimeSpan.FromSeconds(2);

    internal Action? OnWatching { get; set; }

    internal Func<bool>? ClearWatch { get; set; }

    internal void SeedNote(string message) => pendingNote = message;

    static readonly AsyncLocal<LoadTicket?> CurrentLoad = new();

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

        Log("Unterstütztes Spiel: Pionero Capital.");
        Install();
        GameExe = ResolveGame();
        EnsureConfigs();
        DropStaleAgentStock();

        var cachePath = Path.Combine(Root, "Cache", "state.json");
        var fingerprint = Fingerprint();
        var firstRun = !File.Exists(cachePath);
        if (!firstRun && TryReadCache(cachePath, fingerprint, out var cached))
        {
            Log("Schnellstart, Mod-Stand unverändert.");
            var activated = LoadEnabledMods();
            var freshLoaded = activated.Loaded.Select(mod => mod.Id + "|" + mod.Name + "|" + mod.Version).ToArray();
            var freshErrors = activated.Errors.ToArray();
            if (!SameText(freshLoaded, cached.Loaded) || !SameText(freshErrors, cached.Errors))
                File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheFile(true, fingerprint, freshLoaded, freshErrors), Json));
            return new LoadReport(false, true, activated.Loaded, activated.Errors);
        }

        if (firstRun) Log("Erster Start, Ordner werden angelegt.");
        var (loaded, errors) = LoadEnabledMods();
        var report = new LoadReport(firstRun, false, loaded, errors);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheFile(true, fingerprint, loaded.Select(mod => mod.Id + "|" + mod.Name + "|" + mod.Version).ToArray(), errors.ToArray()), Json));
        Log(loaded.Count + " Mods geladen, " + errors.Count + " übersprungen.");
        return report;
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

    bool TryReadCache(string cachePath, string fingerprint, out CacheFile cache)
    {
        cache = new CacheFile(false, "", [], []);
        try
        {
            var read = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cachePath));
            if (read is not { SetupComplete: true, Loaded: not null, Errors: not null } || read.Fingerprint != fingerprint) return false;
            cache = read;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool SameText(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        }
        return true;
    }


    public static string InstanceMutex(string root)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)));
        return "PineLoader." + Convert.ToHexString(hash)[..16];
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

    sealed class LoadTimeoutException() : InvalidOperationException("Laden dauert länger als 15 Sekunden.");

    sealed class LoadTicket(string folder)
    {
        volatile bool closed;

        public string Folder { get; } = folder;
        public bool Inside { get; set; }
        public bool Closed
        {
            get => closed;
            set => closed = value;
        }
        public bool FullRightsNoted { get; set; }
    }

    sealed class EyeSet(List<FileSystemWatcher> eyes) : IDisposable
    {
        public void Dispose()
        {
            foreach (var eye in eyes)
            {
                eye.EnableRaisingEvents = false;
                eye.Dispose();
            }
        }
    }
}

internal interface IRunKey
{
    string? Read();

    void Write(string command);

    void Delete();
}

internal sealed class RegistryRunKey : IRunKey
{
    public static readonly RegistryRunKey Shared = new();

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue("PineLoader") as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        key?.SetValue("PineLoader", command);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        key?.DeleteValue("PineLoader", false);
    }
}
