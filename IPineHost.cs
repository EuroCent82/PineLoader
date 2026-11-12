using System.Diagnostics;

namespace PineLoader;

public interface IPineHost
{
    string Root { get; }

    void Log(string message);

    void Error(string message);

    void UseJavaAgent(string jarPath, string arguments);

    void WhileGameRunning(Action<Process> hook);
}

public interface IPineMod
{
    void Load(IPineHost host);
}
