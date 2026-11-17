using PineLoader;

namespace ProbeMod;

public sealed class ProbeMod : IPineMod
{
    public ModLoad Load(IPineHost host)
    {
        var probe = host.Setting("Probe") ?? "";
        if (probe == "Off") return ModLoad.Off;
        if (probe == "Failed") return ModLoad.Failed;
        if (probe == "Read")
        {
            host.Log(host.Setting("Wort") ?? "leer");
            return ModLoad.Loaded;
        }
        if (probe == "Agent")
        {
            var folder = Path.GetDirectoryName(typeof(ProbeMod).Assembly.Location) ?? "";
            host.UseJavaAgent(Path.Combine(folder, "probe.jar"), "");
            return ModLoad.Loaded;
        }
        if (probe == "Process")
        {
            host.WhileGameRunning(_ => { });
            return ModLoad.Loaded;
        }
        if (probe == "Slow")
        {
            var mark = host.Setting("Mark") ?? "";
            Thread.Sleep(350);
            host.Log("zu spät");
            var folder = Path.GetDirectoryName(typeof(ProbeMod).Assembly.Location) ?? "";
            host.UseJavaAgent(Path.Combine(folder, "probe.jar"), "");
            if (mark.Length > 0) File.WriteAllText(mark, "ran");
            return ModLoad.Loaded;
        }
        return ModLoad.Loaded;
    }
}
