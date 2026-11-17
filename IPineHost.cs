using System.Diagnostics;

namespace PineLoader;

public enum ModLoad
{
    Loaded,
    Off,
    Failed
}

public interface IPineHost
{
    string Root { get; }

    void Log(string message);

    void Error(string message);

    void UseJavaAgent(string jarPath, string arguments);

    void WhileGameRunning(Action<Process> hook);

    void WhenGameExits(Action callback);

    string? Setting(string key);

    bool Allowed(string right);
}

public interface IPineMod
{
    ModLoad Load(IPineHost host);
}
