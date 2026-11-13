namespace PineLoader;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--watch", StringComparison.OrdinalIgnoreCase)))
            return ModHost.Watch(ModHost.FindRoot());

        using var gate = new Mutex(true, ModHost.InstanceMutex(ModHost.FindRoot()), out var created);
        if (!created) return 0;
        ModHost.EnsureWatching(ModHost.FindRoot());

        var dryRun = args.Any(arg => arg == "--dry-run");
        var forwarded = args.Where(arg => arg != "--dry-run" && !arg.Equals("--watch", StringComparison.OrdinalIgnoreCase)).ToArray();
        ApplicationConfiguration.Initialize();
        var window = new MainForm(LaunchedDirectly() && !dryRun);
        var exit = 0;
        window.Shown += async (_, _) =>
        {
            try
            {
                exit = await Run(window, dryRun, forwarded);
            }
            catch (Exception error)
            {
                window.ShowStep("Fehler", error.Message);
                try
                {
                    var host = new ModHost(ModHost.FindRoot());
                    host.Log("Fehler: " + error.Message);
                    host.Error(error.ToString());
                }
                catch { }
                await Task.Delay(1600);
                exit = 1;
            }
            if (!window.IsDisposed) window.Finish();
        };
        Application.Run(window);
        return exit;
    }

    static async Task<int> Run(MainForm window, bool dryRun, string[] forwarded)
    {
        var host = new ModHost(ModHost.FindRoot());
        window.ShowStep("PineLoader", "Ordner werden geprüft");
        var report = host.Run();
        if (report.FirstRun)
        {
            window.ShowStep("Einrichtung", LaunchedDirectly() ? "Ordner stehen." : "Ordner stehen. Danach startet das Spiel.");
            await Task.Delay(1200);
        }

        var skipped = report.Errors.Count == 0 ? "keine Fehler" : report.Errors.Count + " übersprungen";
        var speed = report.FromCache ? "Schnellstart" : "Mods geladen";
        window.ShowStep(speed, report.Loaded.Count + " Mods, " + skipped);
        if (!report.FromCache) await Task.Delay(report.FirstRun ? 700 : 250);

        if (dryRun)
        {
            host.Log("Testlauf, Spiel wird nicht gestartet.");
            window.ShowStep("Testlauf", "Spiel bleibt zu");
            await Task.Delay(400);
            return 0;
        }

        if (LaunchedDirectly())
        {
            host.Log("PineLoader bleibt offen, bis Spiel starten gewählt wird.");
            var found = CountMods(host.Root);
            if (!await window.OfferStart(found, report.Loaded.Count)) return 0;
            host.Log("Spiel starten gewählt.");
        }

        var (exe, gameArgs) = GameCommand(host.GameExe, forwarded);
        host.Log("Spielstart: " + exe);
        window.StaySilent();
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute = false
        };
        foreach (var arg in gameArgs) start.ArgumentList.Add(arg);
        host.PrepareGame(start);
        var game = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Das Spiel konnte nicht gestartet werden.");
        host.GameStarted(game);
        await game.WaitForExitAsync();
        host.Log("Spiel beendet, Code " + game.ExitCode);
        return game.ExitCode;
    }

    static int CountMods(string root)
    {
        var mods = Path.Combine(root, "Mods");
        if (!Directory.Exists(mods)) return 0;
        return Directory.GetDirectories(mods).Count(dir => !Path.GetFileName(dir).StartsWith('.'));
    }

    static bool LaunchedDirectly()
    {
        var path = Environment.ProcessPath;
        return path != null && Path.GetFileName(path).Equals("PineLoader.exe", StringComparison.OrdinalIgnoreCase);
    }

    static (string Exe, string[] Arguments) GameCommand(string configured, string[] forwarded)
    {
        var arguments = forwarded.Where(arg => !ModHost.IsLoaderFile(arg)).ToArray();
        if (arguments.Length > 0 && File.Exists(arguments[0]) && !ModHost.IsLoaderFile(arguments[0]))
            return (arguments[0], arguments.Skip(1).ToArray());
        return (configured, arguments);
    }
}
